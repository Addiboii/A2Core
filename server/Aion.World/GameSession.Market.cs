using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// The market (the client's Trade window; Trade* messages, the 0x3bxx run at dev + 0xa6fb). Split between two channels on the
// official server: the BROWSE side (search, item price lists, my sales, earnings) is the 8087 web API (trade/v1.0/policies,
// trade/v1.0/me/earnings, ... - host log 2026-11-07); its JSON shapes are not known (the client binary is packed, the official
// HTTPS is not recorded), so it is NOT served: the window's lists stay empty. The GAME side is served here, saved in
// game.market_sales (migration 088):
//   TradeSell_RQ 0xe264 {item key i64, count i64, ECurrencyCategory u8, price i64, fee rate i64, fee min i64, trade server}
//     -> TradeSell_RS 0xe265 in the OBSERVED form (recording 20261106-115817, 5 sales): `0000 | sale GUID (36 chars, upper case)
//     | 41 zero bytes | trade server u16` - the official reply leaves the item, count, price and period fields at 0.
//   TradeSold_NT 0xe26e OBSERVED `sale GUID | item i32 | count i64 | trade server u16` (20261106-144624: Material_Part_Gemstone x1),
//     sent to the seller when online.
//   TradeDeliveryComplete_RQ 0xe26f {delivery GUID, item i32, server} -> RS 0xe270 OBSERVED `0000 | GUID | server u16` (20261107-210132 x3).
//   CANDIDATE (usmap fields, never recorded): TradeCancelSale 0xe266/0xe267, TradeRenewSale 0xe268/0xe269, TradeExpireSale
//     0xe26a/0xe26b, TradePurchase 0xe26c/0xe26d, TradeGetSummary 0xe271/0xe272. Every request's widths are unrecorded (the
//     client's requests are encrypted in the recordings); the trade server id is read as u16 (as in every observed reply) or i32,
//     whichever makes the body exact. Results are EResult u16 (Trade_SystemUnavailable 9984 for any refusal: the Trade_* codes
//     the client knows are only those three).
// Rules from GlobalSetting: listings per character trade_sale_count 0 / trade_sale_count_subscribe 10 (subscription free mode
// -> 10), the price in non-binding Kina (trade_buy_cost_currency_type GoldNonbinding; the earnings too), the sale fee in any
// Kina (trade_sale_fee_tax_currency_type GoldCombined) charged at listing: max(fee min, price x fee rate / 100) (CANDIDATE: the
// rate's unit and the official fee are unrecorded). Listing period 7 days (CANDIDATE). Only Item.dat ExchangeType Exchangeable
// items. A purchase buys the whole sale; the item goes into the buyer's bag at once and the seller's earnings come by mail
// (Post.dat 106) - the official delivery box is part of the unserved web API.
public sealed partial class GameSession
{
    public const ushort TradeSellRq = 0xe264, TradeSellRs = 0xe265, TradeCancelRq = 0xe266, TradeCancelRs = 0xe267, TradeRenewRq = 0xe268,
        TradeRenewRs = 0xe269, TradeExpireRq = 0xe26a, TradeExpireRs = 0xe26b, TradePurchaseRq = 0xe26c, TradePurchaseRs = 0xe26d, TradeSoldNt = 0xe26e,
        TradeDeliveryRq = 0xe26f, TradeDeliveryRs = 0xe270, TradeSummaryRq = 0xe271, TradeSummaryRs = 0xe272;
    /// <summary>GlobalSetting trade_sale_count / trade_sale_count_subscribe.</summary>
    public const int MarketSalesFree = 0, MarketSalesSubscribed = 10;
    public static readonly TimeSpan MarketListingPeriod = TimeSpan.FromDays(7);
    const byte IncreaseTradeCancel = 26, IncreaseTradeExpire = 27, IncreaseTradePurchase = 29, DecreaseTradeSell = 26, DecreaseTradePurchase = 27;   // EIncrease/EDecreaseReason

    static bool IsMarketRequest(ushort opcode) => opcode is TradeSellRq or TradeCancelRq or TradeRenewRq or TradeExpireRq or TradePurchaseRq or TradeDeliveryRq or TradeSummaryRq;

    /// <summary>The trade server id at the end of a request: u16 when that makes the body exact, else i32.</summary>
    static int TradeServerAt(byte[] plain, int offset) =>
        plain.Length - offset == 2 ? BitConverter.ToUInt16(plain, offset) : plain.Length - offset >= 4 ? BitConverter.ToInt32(plain, offset) : 0;

    static PacketWriter TradeServer(PacketWriter w, int server) => w.U16(server);

    async Task<List<byte[]>?> MarketRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var opcode = Opcode(plain);
        if (!IsMarketRequest(opcode) || resources is null) return null;
        try
        {
            return opcode switch
            {
                TradeSellRq => await MarketSellAsync(plain, ct),
                TradeCancelRq or TradeExpireRq => await MarketTakeBackAsync(plain, opcode == TradeExpireRq, ct),
                TradeRenewRq => await MarketRenewAsync(plain, ct),
                TradePurchaseRq => await MarketPurchaseAsync(plain, ct),
                TradeDeliveryRq => await MarketDeliveryAsync(plain, ct),
                _ => await MarketSummaryAsync(plain, ct),
            };
        }
        catch (ProtocolException) { return []; }
    }

    /// <summary>The fee of a listing (CANDIDATE): the client's minimum, or its rate (per cent) of the price when that is more; never above the price.</summary>
    public static long MarketFee(long price, long rate, long minimum) =>
        Math.Clamp(Math.Max(Math.Max(0, minimum), rate is > 0 and <= 100 ? price / 100 * rate + price % 100 * rate / 100 : 0), 0, price);

    static int MarketLimit => ServerRules.Embedded.SubscriptionFree ? MarketSalesSubscribed : MarketSalesFree;

    static string SaleText(Guid sale) => sale.ToString("D").ToUpperInvariant();

    async Task<List<byte[]>> MarketSellAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var itemKey = r.I64();
        var count = r.I64();
        var currency = r.U8();
        var price = r.I64();
        var rate = r.I64();
        var minimum = r.I64();
        var server = TradeServerAt(plain, r.Offset);
        Guid? listed = null;
        byte[] Reply(int result) => TradeServer(PacketWriter.Opcode(TradeSellRs).U16(result).String(listed is { } s ? SaleText(s) : "").Zeros(41), server).ToArray();
        if (Store is not { } store || CharacterId is not { } character || dead) return [Reply(EResult.Trade_SystemUnavailable)];
        if (!storedItems.TryGetValue(itemKey, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity
            || price < 1 || price > 99_999_999_999L || ItemDB.Embedded.Of(row.TemplateId) is not { Tradable: true })
            return [Reply(EResult.Trade_SystemUnavailable)];
        var fee = MarketFee(price, rate, minimum);
        var feeCosts = fee > 0 ? CostStacks(KinaItemId, fee, except: row.InstanceId) : [];
        if (feeCosts is null) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];
        var outcome = await StoredValue(() => store.MarketSellAsync(character, row.InstanceId, row.Revision, count, price, fee, feeCosts, server,
            MarketLimit, MarketListingPeriod, ct), new MarketOutcome("failed"));
        if (outcome is not { Result: "ok", Sale: { } sale })
            return [Reply(outcome.Result == "money" ? EResult.Item_Common_Fail_NotEnoughCountMoney : EResult.Trade_SystemUnavailable),
                .. await InventoryChangesAsync(ct, IncreaseTradeCancel, DecreaseTradeSell)];
        listed = sale.SaleId;
        Note($"market: listed {row.TemplateId} x{count} for {price} (fee {fee}, currency {currency})");
        return [Reply(0), .. await InventoryChangesAsync(ct, IncreaseTradeCancel, DecreaseTradeSell)];
    }

    /// <summary>A request that starts with a sale GUID string.</summary>
    static Guid? SaleOf(string text) => Guid.TryParse(text, out var id) ? id : null;

    async Task<List<byte[]>> MarketTakeBackAsync(byte[] plain, bool expire, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        var itemId = r.I32();
        var count = r.I64();
        var server = TradeServerAt(plain, r.Offset);
        var reply = expire ? TradeExpireRs : TradeCancelRs;
        byte[] Reply(int result, long key) => TradeServer(PacketWriter.Opcode(reply).U16(result).String(text).I64(key).I32(itemId).I64(count), server).ToArray();
        if (Store is not { } store || CharacterId is not { } character || SaleOf(text) is not { } sale) return [Reply(EResult.Trade_SystemUnavailable, 0)];
        var outcome = await StoredValue(() => store.MarketTakeBackAsync(character, sale, expire, ct), new MarketOutcome("failed"));
        if (outcome is not { Result: "ok", Instance: { } instance }) return [Reply(EResult.Trade_SystemUnavailable, 0)];
        var changes = await InventoryChangesAsync(ct, expire ? IncreaseTradeExpire : IncreaseTradeCancel, DecreaseTradeSell);
        var key = storedItems.Values.FirstOrDefault(i => i.InstanceId == instance)?.WireId ?? 0;
        Note($"market: {(expire ? "expired" : "cancelled")} sale {text}");
        return [Reply(0, key), .. changes];
    }

    async Task<List<byte[]>> MarketRenewAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        r.I32();   // item id
        // {server, fee rate i64, fee min i64, price i64}: the server id's width from what is left (u16: 26 bytes, i32: 28).
        var server = plain.Length - r.Offset == 26 ? r.U16() : r.I32();
        var rate = r.I64();
        var minimum = r.I64();
        var price = r.I64();
        byte[] Reply(int result) => TradeServer(PacketWriter.Opcode(TradeRenewRs).U16(result).String(text), server).ToArray();
        if (Store is not { } store || CharacterId is not { } character || SaleOf(text) is not { } sale || price < 1 || price > 99_999_999_999L)
            return [Reply(EResult.Trade_SystemUnavailable)];
        var fee = MarketFee(price, rate, minimum);
        var feeCosts = fee > 0 ? CostStacks(KinaItemId, fee) : [];
        if (feeCosts is null) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];
        var outcome = await StoredValue(() => store.MarketRenewAsync(character, sale, price, fee, feeCosts, MarketListingPeriod, ct), new MarketOutcome("failed"));
        return [Reply(outcome.Result == "ok" ? 0 : outcome.Result == "money" ? EResult.Item_Common_Fail_NotEnoughCountMoney : EResult.Trade_SystemUnavailable),
            .. await InventoryChangesAsync(ct, IncreaseTradeCancel, DecreaseTradeSell)];
    }

    async Task<List<byte[]>> MarketPurchaseAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        var itemId = r.I32();
        var count = r.I64();
        var server = TradeServerAt(plain, r.Offset);
        byte[] Reply(int result) => TradeServer(PacketWriter.Opcode(TradePurchaseRs).U16(result).String(text).I32(itemId).I64(count), server).ToArray();
        if (Store is not { } store || CharacterId is not { } character || SaleOf(text) is not { } sale || dead) return [Reply(EResult.Trade_SystemUnavailable)];
        // The price as stored now (the costs must match it exactly): one look, then the purchase transaction re-checks it.
        var price = (await StoredValue(() => store.MarketSaleAsync(sale, ct), null))?.Price ?? 0;
        if (price <= 0) return [Reply(EResult.Trade_SystemUnavailable)];
        if (CostStacks(NonBindingKinaItemId, price) is not { } costs) return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];
        var outcome = await StoredValue(() => store.MarketPurchaseAsync(character, sale, count, costs, NonBindingKinaItemId, ct), new MarketOutcome("failed"));
        var changes = await InventoryChangesAsync(ct, IncreaseTradePurchase, DecreaseTradePurchase);
        if (outcome is not { Result: "ok", Sale: { } sold }) return [Reply(outcome.Result == "money" ? EResult.Item_Common_Fail_NotEnoughCountMoney : EResult.Trade_SystemUnavailable), .. changes];
        // The seller, when online on this host, hears of it (OBSERVED form) and gets the earnings mail in the post list.
        if (World?.FindOnline(sold.Seller) is { } seller)
            seller.Send(TradeServer(PacketWriter.Opcode(TradeSoldNt).String(text).I32(sold.TemplateId).I64(sold.Quantity), sold.TradeServerId).ToArray());
        Note($"market: bought sale {text} ({sold.TemplateId} x{sold.Quantity}) for {sold.Price}");
        return [Reply(0), .. changes];
    }

    async Task<List<byte[]>> MarketDeliveryAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var text = r.String();
        r.I32();   // item id
        var server = TradeServerAt(plain, r.Offset);
        byte[] Reply(int result) => TradeServer(PacketWriter.Opcode(TradeDeliveryRs).U16(result).String(text), server).ToArray();
        if (Store is not { } store || CharacterId is not { } character || SaleOf(text) is not { } sale) return [Reply(EResult.Trade_SystemUnavailable)];
        var outcome = await StoredValue(() => store.MarketDeliveredAsync(character, sale, ct), new MarketOutcome("failed"));
        return [Reply(outcome.Result == "ok" ? 0 : EResult.Trade_SystemUnavailable)];
    }

    /// <summary>TradeGetSummary_RS {result, sales i32, world sales i32, group sales map, earnings i32, world earnings i32, group earnings map, server} (CANDIDATE).</summary>
    async Task<List<byte[]>> MarketSummaryAsync(byte[] plain, CancellationToken ct)
    {
        var server = TradeServerAt(plain, 2);
        var sales = Store is { } store && CharacterId is { } character ? await StoredValue(() => store.MarketSalesAsync(character, ct), []) : [];
        int selling = sales.Count(s => s.State == "selling"), earned = sales.Count(s => s.State == "sold");
        return [TradeServer(PacketWriter.Opcode(TradeSummaryRs).U16(0).I32(selling).I32(selling).Varint(0).I32(earned).I32(earned).Varint(0), server).ToArray()];
    }
}
