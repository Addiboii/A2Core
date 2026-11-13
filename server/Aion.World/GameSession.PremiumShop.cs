using Aion.Protocol.Native;

namespace Aion.World;

// The Black Cloud market (the client's BM shop: BMShopCategory / BMShopGoods), free to play (user 2026-11-09): every goods is
// bought with Kina (PremiumShop.cs: the rules and prices). The client builds the market from its own tables; the server answers
//   NPShopGameMoneyGoodsLimit_RQ 0x568d {array i32 goods} -> _RS 0x568e {result u16, array {i32 goods, i32 bought, i64 reset time}}
//     OBSERVED (official 20261106-105448 #19670: 12 goods, count 0, the next weekly reset 2026-11-11 07:00 UTC, and
//     2099-12-31 15:00 UTC for a limit that never resets); the purchase counts per limit owner and period.
//   NPShopPurchase_RQ 0x568b {i32 goods, i32 quantity} -> _RS 0x568c {result u16, i32 goods, i32 quantity}, then the bag
//     (EIncreaseReason 47 / EDecreaseReason 66 kNPShopPurchaseWithGameMoney), the Quna shown, and for a limited goods
//     NPShopGameMoneyGoodsLimit_NT 0x568f {i32 goods, i32 bought, i64 reset time}.
// The opcodes: usmap catalogue 0x37cd-0x37d1 + 0x1ebe, the offset of the item run (ItemUse_RQ 0x3754 -> 0x5612, OBSERVED 0x568e,
// 0x5689/0x568a TakeAchievementRewardBatch right before); request bodies from the usmap (CANDIDATE: no official request decoded).
// Official goods bought with Quna or money land in the market's warehouse (WarehouseGoodsDelivery_NT, kWarehouseTakeItem);
// here every goods goes straight to the bag, as the official Kina goods do. A Quna pack (GetGameCash item) credits the
// account's Quna instead of an item.
// Quna shown (mirror_quna): the client checks the Quna balance itself before it asks to buy, so GameCash (7) in RefreshCurrency_NT
// is the account's Quna plus the bag's Kina / KinaPerQuna; the account's own Quna is spent first, Kina pays the rest.
// Tabs (cash-shop agent 2026-11-11): the client groups goods by BMShopCategory CategoryGroup (PremiumShopTables.TabOf: Atelier,
// Event, Quna Shop, Tsenka Merchants, Wind Breeze Merchants, A2 Mileage, Storage). FINDING: which goods a tab lists does NOT come
// from this protocol. Every official recording has 0x568d (51 bytes, 12 goods incl. 1254/1262/1001xx that are not in the
// client's BMShopGoods) soon after login or a shop visit; on our server the client sent TrustLimitInfo_RQ 0xe260 (shop opened)
// but never 0x568d in 123 traces. The usmap holds BMShopExportGoods/Limit (goods_id, sale_status, prices, limits: the table
// exported to NC's platform store), NcPaymentV2API and BMShop_WarehouseServerLoadComplete: the catalogue, balances and the
// Storage tab come from the NC platform HTTP API (api_gate on port 8087), never recorded (TLS to NC) and answered 503 here.
// The host now logs those requests (logs/platform-unanswered.jsonl) so one shop visit shows the routes to serve next.
public sealed partial class GameSession
{
    public const ushort NpShopPurchaseRq = 0x568b, NpShopPurchaseRs = 0x568c, NpShopGoodsLimitRq = 0x568d, NpShopGoodsLimitRs = 0x568e, NpShopGoodsLimitNt = 0x568f;
    const byte IncreasePremiumShop = 47, DecreasePremiumShop = 66;   // kNPShopPurchaseWithGameMoney
    /// <summary>The reset time the official sends for a limit that never resets (2099-12-31 15:00 UTC = 2100-01-01 KST).</summary>
    public const long NoResetMs = 4102412400000;
    /// <summary>Limit periods of the market are kept apart from the NPC shops' (same table, own period keys).</summary>
    const string PremiumPeriodPrefix = "bm:";

    /// <summary>The account currencies as last read from the store (the Quna mirror adds the bag's Kina to them).</summary>
    IReadOnlyDictionary<int, long>? storedCurrencies;

    long BagKina() => storedItems.Values.Where(i => i.Location == "bag" && i.TemplateId is KinaItemId or NonBindingKinaItemId).Sum(i => i.Quantity);

    /// <summary>The balances RefreshCurrency_NT shows: the stored Quna, GameCash plus the Kina-bought Quna when mirrored.</summary>
    Dictionary<int, long> CurrencyBalances(IReadOnlyDictionary<int, long> stored)
    {
        var quna = stored.GetValueOrDefault(GamePackets.CurrencyQuna);
        var bound = stored.GetValueOrDefault(GamePackets.CurrencyQunaBound);
        var rules = PremiumShop.Current;
        var mirrored = rules.Enabled && rules.MirrorQuna ? BagKina() / rules.KinaPerQuna : 0;
        var shown = new Dictionary<int, long>
        {
            [GamePackets.CurrencyQuna] = quna + bound + mirrored,
            [GamePackets.CurrencyQunaTradable] = quna,
            [GamePackets.CurrencyQunaBound] = bound,
            [GamePackets.CurrencyQunaActivating] = stored.GetValueOrDefault(GamePackets.CurrencyQunaActivating),
            [GamePackets.CurrencyQunaPending] = stored.GetValueOrDefault(GamePackets.CurrencyQunaPending),
            // A2 Mileage (CANDIDATE: no official RefreshCurrency_NT recorded), with the bag's Kina mirrored like the Quna.
            [GamePackets.CurrencyMileage] = stored.GetValueOrDefault(GamePackets.CurrencyMileage) + (rules.Enabled && rules.MirrorMileage ? rules.MileageOfKina(BagKina()) : 0),
        };
        // The other account currencies held (Mileage 6, Cash 9: Currencies.cs) are shown as stored.
        foreach (var (category, balance) in stored) shown.TryAdd(category, balance);
        return shown;
    }

    /// <summary>RefreshCurrency_NT when the shown balances differ from the last ones sent (null: never sent, nothing yet).</summary>
    List<byte[]> SendCurrencies(Dictionary<int, long> balances)
    {
        if (shownCurrencies is not null && shownCurrencies.Count == balances.Count && balances.All(p => shownCurrencies.GetValueOrDefault(p.Key) == p.Value)) return [];
        shownCurrencies = balances;
        return CurrencyGroups.Select(g => GamePackets.RefreshCurrency(g, balances)).ToList();
    }

    /// <summary>After a bag change: the mirrored Quna follows the Kina (no store read; only once the currencies were shown).</summary>
    List<byte[]> QunaMirrorChanges() =>
        shownCurrencies is null || storedCurrencies is null || !PremiumShop.Current.MirrorQuna ? [] : SendCurrencies(CurrencyBalances(storedCurrencies));

    /// <summary>When a limit's current period ends (ms): the next 07:00 UTC day, Wednesday week or month; NoResetMs when it never resets.</summary>
    public static long PeriodEndMs(int kind, long nowMs)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime.AddHours(-7).Date;
        DateTime? end = kind switch
        {
            1 => day.AddDays(1),
            2 => day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7)).AddDays(7),
            3 => new DateTime(day.Year, day.Month, 1).AddMonths(1),
            _ => null,
        };
        return end is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e.AddHours(7), DateTimeKind.Utc)).ToUnixTimeMilliseconds() : NoResetMs;
    }

    static string PremiumPeriod(int kind, long nowMs) => PremiumPeriodPrefix + ShopTables.Period(kind, DateTimeOffset.FromUnixTimeMilliseconds(nowMs));

    /// <summary>How many of a goods the character (or its account) bought in the current period.</summary>
    static int Bought(IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> rows, PremiumShopTables.Goods g, long nowMs) =>
        g.LimitTarget == 0 || g.Limit <= 0 ? 0
            : rows.Where(r => r.GoodsId == g.Id && r.Target == g.LimitTarget && r.Period == PremiumPeriod(g.LimitPeriod, nowMs)).Sum(r => r.Count);

    /// <summary>0x568d NPShopGameMoneyGoodsLimit_RQ {array i32 goods}: what was bought of each in its period, and when that resets.</summary>
    async Task<List<byte[]>> PremiumGoodsLimitAsync(byte[] plain, long now, CancellationToken ct)
    {
        var ids = new List<int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            if (n > 1024) return [];
            for (var i = 0; i < n; i++) ids.Add(r.I32());
            if (!r.End) return [];
        }
        catch (ProtocolException) { return []; }
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> rows = [];
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { rows = await store.ShopPurchasesAsync(character, ct); return []; });
        var w = PacketWriter.Opcode(NpShopGoodsLimitRs).U16(0).Varint(ids.Count);
        foreach (var id in ids)
        {
            var g = PremiumShopTables.Embedded.Of(id);
            w.I32(id).I32(g is null ? 0 : Bought(rows, g, now)).I64(PeriodEndMs(PremiumShopTables.Embedded.LimitPeriodOf(id), now));
        }
        return [w.ToArray()];
    }

    /// <summary>0x568b NPShopPurchase_RQ {i32 goods, i32 quantity}: the goods bought with Kina (and the account's Quna), answered 0x568c.</summary>
    async Task<List<byte[]>> PremiumShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        int goodsId, quantity;
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            goodsId = r.I32();
            quantity = r.I32();
            if (!r.End) return [];
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(NpShopPurchaseRs).U16(result).I32(goodsId).I32(quantity).ToArray();
        var rules = PremiumShop.Current;
        if (Store is not { } store || CharacterId is not { } character || PremiumShopTables.Embedded.Of(goodsId) is not { } g)
            return [Reply(EResult.NPShopPurchase_InvalidGoods)];
        if (!rules.Sells(g) || !rules.Available(g, profileRace, now)) return [Reply(EResult.NPShopPurchase_NotForSale)];
        if (quantity < 1 || quantity > 999 || g.MaxPerOnce > 0 && quantity > g.MaxPerOnce) return [Reply(EResult.NPShopPurchase_GoodsCondition)];
        if (g.LevelMin > 0 && level < g.LevelMin || g.LevelMax > 0 && level > g.LevelMax) return [Reply(EResult.NPShopPurchase_InvalidCharacterLevel)];

        // The price: Kina, of which the account's own currency pays its share first: Mileage for a Mileage goods, Quna for a
        // Quna or real-money goods (Kina goods: Kina only).
        var kinaTotal = (Int128)rules.KinaPrice(g) * quantity;
        var mileageGoods = g.Currency == PremiumShopTables.Mileage;
        var shareTotal = mileageGoods ? (Int128)g.Price * quantity : (Int128)rules.QunaValue(g) * quantity;
        long shareUsed = 0;
        if (shareTotal > 0)
        {
            IReadOnlyDictionary<int, long> stored = new Dictionary<int, long>();
            await Stored(async () => { stored = await store.AccountCurrenciesAsync(character, ct); return []; });
            storedCurrencies = stored;
            var held = mileageGoods ? stored.GetValueOrDefault(GamePackets.CurrencyMileage)
                : stored.GetValueOrDefault(GamePackets.CurrencyQuna) + stored.GetValueOrDefault(GamePackets.CurrencyQunaBound);
            shareUsed = (long)Int128.Min(shareTotal, Math.Max(0, held));
        }
        var qunaUsed = mileageGoods ? 0 : shareUsed;
        var mileageUsed = mileageGoods ? shareUsed : 0;
        var kina = shareTotal > 0 ? (kinaTotal * (shareTotal - shareUsed) + shareTotal - 1) / shareTotal : kinaTotal;
        if (kina > PremiumShopRules.MaxPrice * 999L) return [Reply(EResult.NPShopPurchase_NotEnoughMoney)];
        var costs = kina > 0 ? CostStacks(KinaItemId, (long)kina) : [];
        if (costs is null) return [Reply(EResult.NPShopPurchase_NotEnoughMoney)];

        var limits = new List<(int, int, string, int, int)>();
        if (g.LimitTarget > 0 && g.Limit > 0)
        {
            if (quantity > g.Limit) return [Reply(EResult.NPShopPurchase_OverPurchaseLimit)];
            limits.Add((g.LimitTarget, g.Id, PremiumPeriod(g.LimitPeriod, now), quantity, g.Limit));
        }
        var qunaPack = PremiumShopTables.Embedded.QunaPerItem(g.ItemId);
        var qunaGranted = qunaPack > 0 ? (long)qunaPack * g.Count * quantity : 0;
        List<(int, long)> items = qunaPack > 0 ? [] : [(g.ItemId, (long)g.Count * quantity)];
        var mileageGranted = rules.MileageEarned(g, quantity);
        var audit = new { goods = g.Id, name = g.Name, quantity, currency = g.Currency, price = g.Price, kina = (long)kina, quna = qunaUsed, mileage = mileageUsed,
            mileageEarned = mileageGranted, kinaPerQuna = rules.KinaPerQuna, offSale = !PremiumShopTables.OnSaleFor(g, profileRace, now) };
        if (items.Count > 0 && !BagRoomFor(items)) return [Reply(EResult.NPShopPurchase_NotEnoughInventorySlot)];   // GameSession.BagRoom.cs
        var outcome = "failed";
        await Stored(async () => { outcome = await store.PremiumShopBuyAsync(character, costs, qunaUsed, items, qunaGranted, mileageUsed, mileageGranted, limits, audit, ct); return []; });
        storedCurrencies = null;   // the Quna changed: the bag update shows no stale balance, CurrencyChangesAsync reads it fresh
        if (outcome != "ok")
        {
            // Stale stacks or Quna: the client gets the fresh bag and balances with the refusal.
            List<byte[]> refused = [Reply(outcome switch
            {
                "limit" => EResult.NPShopPurchase_OverPurchaseLimit,
                "money" => EResult.NPShopPurchase_NotEnoughMoney,
                _ => EResult.NPShopPurchase_NPPurchaseTimeout,
            })];
            refused.AddRange(await InventoryChangesAsync(ct, IncreasePremiumShop, DecreasePremiumShop));
            refused.AddRange(await CurrencyChangesAsync(ct));
            return refused;
        }
        var replies = new List<byte[]> { Reply(0) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreasePremiumShop, DecreasePremiumShop));
        replies.AddRange(await CurrencyChangesAsync(ct));
        if (limits.Count > 0)
        {
            IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> rows = [];
            await Stored(async () => { rows = await store.ShopPurchasesAsync(character, ct); return []; });
            replies.Add(PacketWriter.Opcode(NpShopGoodsLimitNt).I32(g.Id).I32(Bought(rows, g, now)).I64(PeriodEndMs(g.LimitPeriod, now)).ToArray());
        }
        return replies;
    }
}
