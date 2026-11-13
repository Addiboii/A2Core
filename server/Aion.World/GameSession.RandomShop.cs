using Aion.Protocol.Native;

namespace Aion.World;

// The Black Cloud Merchants (the client's random shop; RandomShop.cs: tables, rules, the list packet). Per character and shop
// a list of goods is rolled and kept (game.character_random_shops, migration 086) until its refresh time.
//   RandomShopInfo_RQ 0x5692 {category u8} -> _RS 0x5693 {result u16, category u8} then _NT 0x5691 (the list)   OBSERVED replies
//   RandomShopPurchase_RQ 0x5694 {category u8, goods i32} -> the bag (EIncreaseReason 48 / EDecreaseReason 67 kRandomShopPurchase),
//     AchievementCountUpdate_NT (RandomShopBuy) and _RS 0x5695 {result u16, category u8, goods i32}            OBSERVED replies
//   RandomShopGoodsListRefresh_RQ 0x5696 {category} -> Kina paid (RandomShopRefreshCost step, EDecreaseReason 68), a new list:
//     _RS 0x5697 {result u16, category u8} then _NT                                                           CANDIDATE
//   RandomShopGoodsListRefreshCheck_RQ 0x5698 {category} -> _RS 0x5699 {result u16, category u8} then _NT (renewed when due)  CANDIDATE
// Opcodes: usmap 0x37d3-0x37db + 0x1ebe (the BM shop run's offset). Request bodies from the usmap (client requests are encrypted
// in the recordings: CANDIDATE); the result is a u16 on the wire like every other reply (OBSERVED "93560000c9").
public sealed partial class GameSession
{
    public const ushort RandomShopInfoNt = 0x5691, RandomShopInfoRq = 0x5692, RandomShopInfoRs = 0x5693, RandomShopPurchaseRq = 0x5694, RandomShopPurchaseRs = 0x5695,
        RandomShopRefreshRq = 0x5696, RandomShopRefreshRs = 0x5697, RandomShopRefreshCheckRq = 0x5698, RandomShopRefreshCheckRs = 0x5699;
    const byte IncreaseRandomShop = 48, DecreaseRandomShop = 67, DecreaseRandomShopRefresh = 68;   // kRandomShopPurchase / kRandomShopGoodsListRefresh

    /// <summary>The character's lists by wire category with their stored revision (0: not stored yet).</summary>
    readonly Dictionary<int, (long Revision, RandomShopOffer Offer)> randomShops = [];
    bool randomShopsLoaded;

    static string ShopDay(long nowMs) => ShopTables.Period(1, DateTimeOffset.FromUnixTimeMilliseconds(nowMs));

    /// <summary>The shop of a request's category byte when the character may use it (its race, the shops open), else null.</summary>
    RandomShopTables.Shop? RandomShopFor(byte[] plain, out int category, int length = 3)
    {
        category = plain.Length > 2 ? plain[2] : 0;
        if (plain.Length != length || !RandomShop.Current.Enabled) return null;
        return RandomShopTables.Embedded.ShopOf(category) is { OnSale: true } shop && shop.Race == (profileRace == 2 ? 2 : 1) ? shop : null;
    }

    async Task LoadRandomShopsAsync(CancellationToken ct)
    {
        if (randomShopsLoaded || Store is not { } store || CharacterId is not { } character) return;
        IReadOnlyList<(int Category, long Revision, string State)> rows = [];
        await Stored(async () => { rows = await store.RandomShopOffersAsync(character, ct); return []; });
        foreach (var (category, revision, state) in rows)
            if (RandomShopOffer.Parse(state) is { } offer) randomShops[category] = (revision, offer);
            else randomShops[category] = (revision, new RandomShopOffer(0, [], "", 0));   // unreadable: rolled again over the same row
        randomShopsLoaded = true;
    }

    /// <summary>Stores a list (with what it costs and grants) over the revision it was read at: "ok", "stale" or "failed".</summary>
    async Task<string> SaveRandomShopAsync(int category, RandomShopOffer offer, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, object? audit, CancellationToken ct)
    {
        var revision = randomShops.TryGetValue(category, out var held) ? held.Revision : 0;
        if (Store is not { } store || CharacterId is not { } character)
        {
            randomShops[category] = (revision + 1, offer);   // a development login keeps its lists in memory
            return costs.Count == 0 && goods.Count == 0 ? "ok" : "failed";
        }
        var outcome = "failed";
        await Stored(async () => { outcome = await store.RandomShopSaveAsync(character, category, revision, offer.ToJson(), costs, goods, audit, ct); return []; });
        if (outcome == "ok") randomShops[category] = (revision + 1, offer);
        else if (outcome == "stale") { randomShopsLoaded = false; randomShops.Clear(); }   // another login changed it: read again next time
        return outcome;
    }

    /// <summary>The shop's list now: the stored one while it lasts, else a new roll (stored at once so it does not change on reopening).</summary>
    async Task<RandomShopOffer?> CurrentRandomShopAsync(RandomShopTables.Shop shop, long now, CancellationToken ct)
    {
        await LoadRandomShopsAsync(ct);
        var rules = RandomShop.Current;
        var day = ShopDay(now);
        var previous = randomShops.TryGetValue(shop.Wire, out var held) ? held.Offer : null;
        if (previous is not null && now < previous.Until && previous.Goods.Count > 0)
            return previous.Day == day ? previous : previous with { Day = day, Refreshes = 0 };
        // The paid refresh count lasts the day (07:00 UTC, CANDIDATE), across the hourly lists.
        var offer = new RandomShopOffer(rules.NextRefresh(now), rules.Roll(shop, level, rng), day, previous?.Day == day ? previous.Refreshes : 0);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var outcome = await SaveRandomShopAsync(shop.Wire, offer, [], [], null, ct);
            if (outcome == "ok") return offer;
            if (outcome != "stale") return null;
            await LoadRandomShopsAsync(ct);
            if (randomShops.TryGetValue(shop.Wire, out held) && now < held.Offer.Until && held.Offer.Goods.Count > 0) return held.Offer;
        }
        return null;
    }

    byte[] RandomShopList(RandomShopTables.Shop shop, RandomShopOffer offer)
    {
        var rules = RandomShop.Current;
        var goods = offer.Goods.Select(s => RandomShopTables.Embedded.GoodsOf(s[0]) is { } g ? RandomShop.Wire(rules, g, s[1], s[2] != 0) : (RandomShop.WireGoods?)null)
            .OfType<RandomShop.WireGoods>().ToList();
        return RandomShop.InfoNt(shop.Wire, goods, offer.Until, offer.Refreshes);
    }

    static byte[] RandomShopReply(ushort opcode, int result, int category) => PacketWriter.Opcode(opcode).U16(result).U8((byte)category).ToArray();

    /// <summary>0x5692 RandomShopInfo_RQ {category}: 0x5693 then the list 0x5691 (as official).</summary>
    async Task<List<byte[]>> RandomShopInfoAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop) return [RandomShopReply(RandomShopInfoRs, EResult.RandomShop_Purchase_GoodsCondition, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [RandomShopReply(RandomShopInfoRs, EResult.RandomShop_Purchase_ExpiredGoodsList, category)];
        return [RandomShopReply(RandomShopInfoRs, 0, category), RandomShopList(shop, offer)];
    }

    /// <summary>0x5698 RandomShopGoodsListRefreshCheck_RQ {category}: 0x5699, then the list (a new one when its time has come).</summary>
    async Task<List<byte[]>> RandomShopRefreshCheckAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop) return [RandomShopReply(RandomShopRefreshCheckRs, EResult.RandomShop_Refresh_Expired, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [RandomShopReply(RandomShopRefreshCheckRs, EResult.RandomShop_Refresh_Expired, category)];
        return [RandomShopReply(RandomShopRefreshCheckRs, 0, category), RandomShopList(shop, offer)];
    }

    /// <summary>0x5694 RandomShopPurchase_RQ {category, goods}: Kina paid, the item to the bag, the slot marked bought; answered 0x5695.</summary>
    async Task<List<byte[]>> RandomShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        var goodsId = plain.Length == 7 ? BitConverter.ToInt32(plain, 3) : 0;
        byte[] Reply(int result, int category) => PacketWriter.Opcode(RandomShopPurchaseRs).U16(result).U8((byte)category).I32(goodsId).ToArray();
        if (RandomShopFor(plain, out var category, 7) is not { } shop || Store is null) return [Reply(EResult.RandomShop_Purchase_InvalidGoods, category)];
        await LoadRandomShopsAsync(ct);
        if (!randomShops.TryGetValue(category, out var held) || held.Offer.Goods.Count == 0) return [Reply(EResult.RandomShop_Purchase_InvalidGoods, category)];
        if (now >= held.Offer.Until)
        {
            // The list ran out while the window was open: refused, and the new list follows.
            List<byte[]> expired = [Reply(EResult.RandomShop_Purchase_ExpiredGoodsList, category)];
            if (await CurrentRandomShopAsync(shop, now, ct) is { } renewed) expired.Add(RandomShopList(shop, renewed));
            return expired;
        }
        var offer = held.Offer;
        if (offer.Slot(goodsId) is not { } slot || RandomShopTables.Embedded.GoodsOf(goodsId) is not { } g) return [Reply(EResult.RandomShop_Purchase_InvalidGoods, category)];
        if (slot[2] != 0) return [Reply(EResult.RandomShop_Purchase_AlreadyPurchased, category)];
        if (g.LevelMin > 0 && level < g.LevelMin || g.LevelMax > 0 && level > g.LevelMax) return [Reply(EResult.RandomShop_Purchase_GoodsCondition, category)];
        var rules = RandomShop.Current;
        var price = rules.Price(g, slot[1]);
        var costs = price > 0 ? CostStacks(KinaItemId, price) : [];
        if (costs is null) return [Reply(EResult.RandomShop_Purchase_NotEnoughMoney, category)];
        if (!BagRoomFor([(g.ItemId, 1)])) return [Reply(EResult.Item_Common_Fail_NotEnoughSlotCountContainer, category)];   // GameSession.BagRoom.cs
        var bought = offer with { Goods = offer.Goods.Select(s => s[0] == goodsId ? new[] { s[0], s[1], 1 } : s).ToList() };
        var audit = new { shop = category, goods = g.Id, item = g.ItemId, discount = rules.Discounts[Math.Clamp(slot[1], 0, rules.Discounts.Count - 1)].Percent, kina = price };
        var outcome = await SaveRandomShopAsync(category, bought, costs, [(g.ItemId, 1)], audit, ct);
        if (outcome != "ok")
            return [Reply(outcome == "stale" ? EResult.RandomShop_Purchase_ExpiredGoodsList : EResult.RandomShop_Purchase_NotEnoughMoney, category),
                .. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShop)];
        // A2 Mileage earned: Kina / random_shop_mileage_accrue_ratio (GlobalSetting 200; CANDIDATE reading, PremiumShopRules).
        var ratio = PremiumShop.Current.RandomKinaPerMileage;
        var mileage = ratio > 0 ? price / ratio : 0;
        if (mileage > 0 && Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.AddAccountCurrencyAsync(character, GamePackets.CurrencyMileage, mileage, ct); return []; });
        // As official: the bag, the achievement count, then the reply.
        return [.. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShop), .. Achieve("RandomShopBuy", 1, now: now),
            .. (mileage > 0 ? await CurrencyChangesAsync(ct) : new List<byte[]>()), Reply(0, category)];
    }

    /// <summary>0x5696 RandomShopGoodsListRefresh_RQ {category}: the next paid step's Kina, a new list (same refresh time); 0x5697 then the list.</summary>
    async Task<List<byte[]>> RandomShopRefreshAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop || Store is null) return [RandomShopReply(RandomShopRefreshRs, EResult.RandomShop_Refresh_Expired, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [RandomShopReply(RandomShopRefreshRs, EResult.RandomShop_Refresh_Expired, category)];
        var steps = RandomShopTables.Embedded.RefreshCosts(shop.RefreshGroup);
        var cost = steps.Count == 0 ? 0 : steps[Math.Min(offer.Refreshes, steps.Count - 1)];
        var costs = cost > 0 ? CostStacks(KinaItemId, cost) : [];
        if (costs is null) return [RandomShopReply(RandomShopRefreshRs, EResult.RandomShop_Refresh_NotEnoughMoney, category)];
        var renewed = new RandomShopOffer(offer.Until, RandomShop.Current.Roll(shop, level, rng), ShopDay(now), offer.Refreshes + 1);
        var outcome = await SaveRandomShopAsync(category, renewed, costs, [], new { shop = category, refresh = renewed.Refreshes, kina = cost }, ct);
        if (outcome != "ok")
            return [RandomShopReply(RandomShopRefreshRs, outcome == "stale" ? EResult.RandomShop_Refresh_Expired : EResult.RandomShop_Refresh_NotEnoughMoney, category),
                .. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShopRefresh)];
        return [.. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShopRefresh), RandomShopReply(RandomShopRefreshRs, 0, category), RandomShopList(shop, renewed)];
    }
}
