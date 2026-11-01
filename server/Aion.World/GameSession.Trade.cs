using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Trade server / broker (0x3B69–0x3B77, TradeServerInfo 0xe263). Listings persist in Postgres (051+) or FakeCharacterStore in tests.</summary>
public sealed partial class GameSession
{
    public const ushort TradeSellRq = 0x3B69, TradeSellRs = 0x3B6A, TradeCancelSaleRq = 0x3B6B, TradeCancelSaleRs = 0x3B6C,
        TradePurchaseRq = 0x3B71, TradePurchaseRs = 0x3B72, TradeSoldNt = 0x3B73, TradeDeliveryCompleteRq = 0x3B74,
        TradeDeliveryCompleteRs = 0x3B75, TradeGetSummaryRq = 0x3B76, TradeGetSummaryRs = 0x3B77;

    static TradeTables TradeConfig => TradeTables.Embedded;

    async Task<List<byte[]>> TradeRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var op = Opcode(plain);
        return op switch
        {
            TradeSellRq when plain.Length >= 40 => await TradeSellAsync(plain, now, ct),
            TradeCancelSaleRq when plain.Length >= 20 => await TradeCancelAsync(plain, ct),
            TradePurchaseRq when plain.Length >= 20 => await TradePurchaseAsync(plain, ct),
            TradeDeliveryCompleteRq when plain.Length >= 11 => await TradeDeliveryCompleteAsync(plain, ct),
            TradeGetSummaryRq when plain.Length >= 6 => await TradeSummaryAsync(plain, ct),
            _ => [],
        };
    }

    static long ParseSaleId(string text) => long.TryParse(text, out var id) ? id : 0;

    async Task<List<byte[]>> TradeSellAsync(byte[] plain, long nowMs, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var itemKey = (long)r.U64();
        var count = (long)r.U64();
        r.I32(); // money type (kina)
        var price = (long)r.U64();
        r.I32(); r.I32(); // fee rate/min from client — server uses trade.json
        var tradeServer = r.I32();
        byte[] Reply(int result, TradeListingRecord? listing) =>
            PacketWriter.Opcode(TradeSellRs).U16(result).String(listing?.SaleId.ToString() ?? "0").I64(itemKey).I64(count)
                .I32(GameSession.KinaItemId).I64(price).I64(listing?.ListedAtMs ?? nowMs).I64(listing?.ExpiresAtMs ?? nowMs)
                .I32(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count) || price < 1 || price > PacketSecurity.MaxTradeUnitPrice
            || !PacketSecurity.TryMultiply(price, count, out _))
            return [Reply(1, null)];
        var (result, listing) = await StoredValue(() => store.TradeListItemAsync(character, itemKey, count, price, tradeServer, nowMs, ct), ("failed", null));
        if (listing is null) return [Reply(result == "ok" ? 0 : 1, null), .. await InventoryChangesAsync(ct, 0, 0)];
        return [Reply(0, listing), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    async Task<List<byte[]>> TradeCancelAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var saleId = ParseSaleId(r.String());
        var template = r.I32();
        var count = (long)r.U64();
        var tradeServer = r.I32();
        byte[] Reply(int result) => PacketWriter.Opcode(TradeCancelSaleRs).U16(result).String(saleId.ToString()).I64(0).I32(template).I64(count).I32(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, _) = await StoredValue(() => store.TradeCancelListingAsync(character, saleId, template, count, tradeServer, ct), ("failed", null));
        return [Reply(result == "ok" ? 0 : 1), .. result == "ok" ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    async Task<List<byte[]>> TradePurchaseAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var saleId = ParseSaleId(r.String());
        var template = r.I32();
        var count = (long)r.U64();
        var tradeServer = r.I32();
        byte[] Reply(int result) => PacketWriter.Opcode(TradePurchaseRs).U16(result).String(saleId.ToString()).I32(template).I64(count).I32(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, deliveryId) = await StoredValue(() => store.TradePurchaseAsync(character, saleId, template, count, tradeServer, ct), ("failed", 0L));
        var replies = new List<byte[]> { Reply(result == "ok" ? 0 : 1) };
        if (result == "ok") replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
        if (deliveryId > 0) replies.Add(PacketWriter.Opcode(TradeSoldNt).String(saleId.ToString()).I32(template).I64(count).I32(tradeServer).ToArray());
        return replies;
    }

    async Task<List<byte[]>> TradeDeliveryCompleteAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var deliveryId = ParseSaleId(r.String());
        r.I32();
        var tradeServer = r.I32();
        byte[] Reply(int result) => PacketWriter.Opcode(TradeDeliveryCompleteRs).U16(result).String(deliveryId.ToString()).I32(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var ok = await StoredValue(() => store.TradeCompleteDeliveryAsync(character, deliveryId, tradeServer, ct), false);
        return [Reply(ok ? 0 : 1), .. ok ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    async Task<List<byte[]>> TradeSummaryAsync(byte[] plain, CancellationToken ct)
    {
        var tradeServer = BitConverter.ToInt32(plain, 2);
        if (Store is not { } store || CharacterId is not { } character)
            return [PacketWriter.Opcode(TradeGetSummaryRs).U16(1).I32(0).I32(0).I32(0).I32(0).I32(tradeServer).ToArray()];
        var summary = await StoredValue(() => store.TradeSummaryAsync(character, tradeServer, ct), new TradeSummaryRecord(0, 0, 0));
        return [PacketWriter.Opcode(TradeGetSummaryRs).U16(0).I32(summary.ActiveSales).I32(summary.ActiveSales).I32(summary.PendingDeliveries)
            .I32(summary.PendingEarningsKina).I32(summary.PendingEarningsKina).I32(tradeServer).ToArray()];
    }
}
