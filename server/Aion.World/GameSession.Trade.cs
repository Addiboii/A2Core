using System.Globalization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Broker and gift box (opcodes in GameSession.Economy.cs). Rewritten from A2Core PR #4 (Artezianec) with live opcodes and
// these fixes: a partial stack is split into its own escrow row (the bag keeps only the rest), the price is per unit and
// a purchase pays unit price x count, a cancel needs the seller's own listing that is neither sold nor cancelled, expired
// listings come back through ExpireSale, bound and untradable items are refused, and earnings stay i64 Kina.
public sealed partial class GameSession
{
    // Broker reasons, EIncreaseReason / EDecreaseReason read from the usmap of client 2.0.6 (no recording): increase 26
    // kTradeCancelSale, 27 kTradeExpireSale, 28 kTradeEarnings, 29 kTradePurchase; decrease 26 kTradeSell, 27 kTradePurchase.
    const byte IncreaseTradeCancelSale = 26, IncreaseTradeExpireSale = 27, IncreaseTradeEarnings = 28, IncreaseTradePurchase = 29,
        DecreaseTradeSell = 26, DecreaseTradePurchase = 27;

    const byte CurrencyKina = 1;   // ECurrencyCategory (CANDIDATE: 1 = game money)

    static string SaleId(long id) => id.ToString(CultureInfo.InvariantCulture);
    static long? ParseSaleId(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    async Task<List<byte[]>?> TradeAsync(ushort op, PacketReader r, long now, CancellationToken ct)
    {
        var economy = Economy;
        switch (op)
        {
            // TradeSell_RQ {item_key, item_count, money type, price (per unit, CANDIDATE), fee rate, fee min (ignored), server}.
            case TradeSellRq:
            {
                var (key, count, money, price) = (r.I64(), r.I64(), r.U8(), r.I64());
                r.I64(); r.I64();
                var server = r.I32();
                byte[] Reply(int result, TradeListing? l) => PacketWriter.Opcode(TradeSellRs).U16(result).String(l is null ? "" : SaleId(l.SaleId)).I64(key).I64(count)
                    .U8(money).I64(price).I64(l?.Listed.ToUnixTimeMilliseconds() ?? 0).I64(l?.Expires.ToUnixTimeMilliseconds() ?? 0).I32(server).ToArray();
                if (economy is null || !storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity) return [Reply(1, null)];
                if (money != CurrencyKina || price < 1 || price > TradeMaxPrice) return [Reply(2, null)];
                if (!ItemRules.Embedded.Tradable(row.TemplateId) || SoulBindCount(row) > 0) return [Reply(6, null)];
                long total;
                try { total = checked(price * count); } catch (OverflowException) { return [Reply(2, null)]; }
                var fee = Math.Max(1, total / 1000 * TradeFeePermille);
                if (CostStacks(KinaItemId, fee, row.InstanceId) is not { } costs) return [Reply(3, null)];
                TradeListing? listing = null;
                var expires = DateTimeOffset.FromUnixTimeMilliseconds(now).AddDays(TradeListingDays);
                await Stored(async () => { listing = await economy.TradeSellAsync(Me, row.InstanceId, row.Revision, count, price, costs, expires, ct); return []; });
                if (listing is null) return [Reply(1, null)];
                return [Reply(0, listing), .. await InventoryChangesAsync(ct, 0, DecreaseTradeSell)];
            }
            // TradeCancelSale_RQ / TradeExpireSale_RQ {sale_id str, item_id, item_count, server}: the listed item comes back.
            case TradeCancelRq or TradeExpireRq:
            {
                var (sale, itemId, count, server) = (r.String(), r.I32(), r.I64(), r.I32());
                byte[] Reply(int result, TradeListing? l) => PacketWriter.Opcode(op == TradeCancelRq ? TradeCancelRs : TradeExpireRs).U16(result).String(sale)
                    .I64(l is null ? 0 : storedItems.FirstOrDefault(i => i.Value.InstanceId == l.Item).Key).I32(itemId).I64(l?.Quantity ?? count).I32(server).ToArray();
                if (economy is null || ParseSaleId(sale) is not { } id) return [Reply(1, null)];
                if (op == TradeExpireRq)
                {
                    TradeListing? current = null;
                    await Stored(async () => { current = await economy.TradeListingAsync(id, ct); return []; });
                    if (current is not { State: "expired" }) return [Reply(2, current)];
                }
                TradeListing? listing = null;
                await Stored(async () => { listing = await economy.TradeCancelAsync(Me, id, ct); return []; });
                if (listing is null) return [Reply(1, null)];
                var changes = await InventoryChangesAsync(ct, op == TradeCancelRq ? IncreaseTradeCancelSale : IncreaseTradeExpireSale, 0);
                return [Reply(0, listing), .. changes];
            }
            // TradeRenewSale_RQ: not built (result 1); an expired listing is taken back and listed again.
            case TradeRenewRq:
            {
                var sale = r.String();
                return [PacketWriter.Opcode(TradeRenewRs).U16(1).String(sale).ToArray()];
            }
            // TradePurchase_RQ {sale_id str, item_id, item_count, server}: pays unit price x count to a delivery for the seller.
            case TradePurchaseRq:
            {
                var (sale, itemId, count, server) = (r.String(), r.I32(), r.I64(), r.I32());
                byte[] Reply(int result) => PacketWriter.Opcode(TradePurchaseRs).U16(result).String(sale).I32(itemId).I64(count).I32(server).ToArray();
                if (economy is null || ParseSaleId(sale) is not { } id || count < 1) return [Reply(1)];
                TradeListing? listing = null;
                await Stored(async () => { listing = await economy.TradeListingAsync(id, ct); return []; });
                if (listing is not { State: "active" } || listing.TemplateId != itemId || listing.Seller == CharacterId) return [Reply(2)];
                if (count > listing.Quantity) return [Reply(4)];
                long total;
                try { total = checked(listing.UnitPrice * count); } catch (OverflowException) { return [Reply(3)]; }
                if (CostStacks(KinaItemId, total) is not { } costs) return [Reply(3)];
                (string Result, TradeListing? Listing) outcome = ("none", null);
                await Stored(async () => { outcome = await economy.TradePurchaseAsync(Me, id, count, listing.UnitPrice, costs, ct); return []; });
                if (outcome.Result != "ok") return [Reply(outcome.Result switch { "money" => 3, "count" => 4, _ => 1 })];
                if (options.World?.FindOnline(listing.Seller) is { } seller)
                    seller.Send([PacketWriter.Opcode(TradeSoldNt).String(sale).I32(itemId).I64(count).I32(server).ToArray()]);
                return [Reply(0), .. await InventoryChangesAsync(ct, IncreaseTradePurchase, DecreaseTradePurchase)];
            }
            // TradeDeliveryComplete_RQ {delivery_id str, item_id, server}: the seller collects a sale's Kina.
            case TradeDeliveryRq:
            {
                var (delivery, _, server) = (r.String(), r.I32(), r.I32());
                byte[] Reply(int result) => PacketWriter.Opcode(TradeDeliveryRs).U16(result).String(delivery).I32(server).ToArray();
                if (economy is null || ParseSaleId(delivery) is not { } id) return [Reply(1)];
                long? kina = null;
                await Stored(async () => { kina = await economy.CompleteTradeDeliveryAsync(Me, id, ct); return []; });
                return kina is null ? [Reply(1)] : [Reply(0), .. await InventoryChangesAsync(ct, IncreaseTradeEarnings, 0)];
            }
            // TradeGetSummary_RQ {server}: own active listings and uncollected sales (no group or world counts).
            case TradeSummaryRq:
            {
                var server = r.I32();
                (IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries) summary = ([], []);
                if (economy is not null) await Stored(async () => { summary = await economy.TradeSummaryAsync(Me, ct); return []; });
                return [PacketWriter.Opcode(TradeSummaryRs).U16(economy is null ? 1 : 0).I32(summary.Listings.Count).I32(0).Varint(0)
                    .I32(summary.Deliveries.Count).I32(0).Varint(0).I32(server).ToArray()];
            }
        }
        return null;
    }

    // ---------- Gift box ----------

    /// <summary>RetrievePushRewardInfo_RS {result, next request time, [type, id, seq, sender, title, body, sent, expire, item, count]}.</summary>
    async Task<List<byte[]>> PushRewardInfoAsync(long now, CancellationToken ct)
    {
        IReadOnlyList<PushRewardRecord> list = [];
        if (Economy is { } economy)
            await Stored(async () => { list = await economy.PushRewardsAsync(Me, DateTimeOffset.FromUnixTimeMilliseconds(now), ct); return []; });
        var open = list.Where(p => !p.Taken).ToList();
        var w = PacketWriter.Opcode(PushRewardInfoRs).U16(0).I64(now + 3_600_000).Varint(open.Count);
        foreach (var p in open)
            // Sequence 1 in every official entry (9 lists, 2026-11-04 to 06; 183516: sender, title, body, sent, expire, item, count).
            w.U8(p.Type).I32(p.Id).U8(1).String(p.Sender).String(p.Title).String(p.Body)
                .I64(p.Sent.ToUnixTimeMilliseconds()).I64(p.Expire.ToUnixTimeMilliseconds()).I32(p.TemplateId).I64(p.Count);
        return [w.ToArray()];
    }

    /// <summary>TakePushReward_RQ {type, id, target sequence}: the reward's item, once per character.</summary>
    async Task<List<byte[]>> TakePushRewardAsync(byte type, int id, byte sequence, long now, CancellationToken ct)
    {
        PushRewardRecord? reward = null;
        if (Economy is { } economy)
            await Stored(async () => { reward = await economy.TakePushRewardAsync(Me, id, DateTimeOffset.FromUnixTimeMilliseconds(now), ct); return []; });
        var rs = PacketWriter.Opcode(PushRewardTakeRs).U16(reward is null ? 1 : 0).U8(type).I32(id).U8(sequence).I32(reward?.TemplateId ?? 0).I64(reward?.Count ?? 0).ToArray();
        // The bag change first with increase reason 22, then the reply; no 0xe34a follows (OBSERVED 2026-11-06 234912 i124322-124323,
        // 8 takes).
        return reward is null ? [rs] : [.. await InventoryChangesAsync(ct, 22, 0), rs];
    }
}
