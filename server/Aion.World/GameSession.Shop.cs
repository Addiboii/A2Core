using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>NPC shops from tools/data/shops.json (tools/shop_data.py): merchant -> shop, goods per race, Kina prices, sell prices.</summary>
public sealed class ShopTables
{
    sealed record ShopFile(
        [property: JsonPropertyName("npcs")] Dictionary<string, string> Npcs,
        [property: JsonPropertyName("shops")] Dictionary<string, Dictionary<string, string[]>> Shops,
        [property: JsonPropertyName("goods")] Dictionary<string, long[]> Goods,
        [property: JsonPropertyName("sell")] Dictionary<string, long> Sell);

    readonly ShopFile file;
    ShopTables(ShopFile file) => this.file = file;

    static readonly Lazy<ShopTables> embedded = new(() =>
    {
        using var stream = typeof(ShopTables).Assembly.GetManifestResourceStream("shops.json") ?? throw new InvalidOperationException("Missing embedded game data shops.json");
        return new ShopTables(JsonSerializer.Deserialize<ShopFile>(stream) ?? throw new InvalidOperationException("Empty shops.json"));
    });

    public static ShopTables Embedded => embedded.Value;
    static string Key(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsMerchant(int npcId) => file.Npcs.ContainsKey(Key(npcId));

    public sealed record GoodsRow(int ItemId, int Bundle, long Price, int Level, int Currency, int Limit, int LimitTarget, int LimitPeriod);

    /// <summary>A goods row the merchant sells to a race; null otherwise.</summary>
    public GoodsRow? Goods(int npcId, int race, int goodsId) =>
        file.Npcs.TryGetValue(Key(npcId), out var shop) && file.Shops.TryGetValue(shop, out var races) && races.TryGetValue(Key(race), out var list)
        && list.Contains(Key(goodsId)) && file.Goods.TryGetValue(Key(goodsId), out var g) && g.Length >= 8
            ? new GoodsRow((int)g[0], (int)g[1], g[2], (int)g[3], (int)g[4], (int)g[5], (int)g[6], (int)g[7]) : null;

    /// <summary>The limited goods a merchant sells to a race.</summary>
    public IEnumerable<(int GoodsId, GoodsRow Row)> LimitedGoods(int npcId, int race) =>
        file.Npcs.TryGetValue(Key(npcId), out var shop) && file.Shops.TryGetValue(shop, out var races) && races.TryGetValue(Key(race), out var list)
            ? list.Select(id => (Id: int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), Row: Goods(npcId, race, int.Parse(id, System.Globalization.CultureInfo.InvariantCulture))))
                .Where(x => x.Row is { Limit: > 0 }).Select(x => (x.Id, x.Row!))
            : [];

    /// <summary>
    /// The reset period a purchase counts in (GlobalSetting npc_shop_limited_goods_reset_time 57600 s KST = 07:00 UTC,
    /// weekly on reset_week 3 = Wednesday CANDIDATE, monthly on day 1): "all", a day, "w" + the week's first day, or a month.
    /// </summary>
    public static string Period(int kind, DateTimeOffset now)
    {
        var day = now.UtcDateTime.AddHours(-7).Date;
        return kind switch
        {
            1 => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            2 => "w" + day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            3 => day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            _ => "all",
        };
    }

    /// <summary>Kina per unit for selling an item, or null when it cannot be sold.</summary>
    public long? SellPrice(int itemId) => file.Sell.TryGetValue(Key(itemId), out var price) ? price : null;
}

// NPC shops. The client builds the shop window from its own tables (NpcData FuncValues -> GameShop); the server
// answers the open (0x8c00 -> 0x8c01, OBSERVED official reply: result, NPC key varint, purchased limited goods
// map {i32 goods, i32 count} CANDIDATE widths, server time), buys (0x8c02 -> 0x8c03) and sells (0x8c04 -> 0x8c05).
// The 0x8c run: catalogue index + 0x88de (OBSERVED 0x8c00/0x8c01). Buy/sell key widths are not observed: the requests
// are read in the layout that consumes them exactly (varint or i32 keys), and the replies echo the request's lists.
// Prices are in the goods' currency item (Kina, Abyss points, coins, medals: Item.dat CurrencyCategory items).
// Sold items wait for buyback (RestorableItemList_NT 0xe342 OBSERVED empty at entry; ItemRestore 0xe344/0xe345 by
// position): restored for what they sold for (CANDIDATE cost), 30 per year (ItemRestorePolicy NpcShopSell).
public sealed partial class GameSession
{
    public const ushort ShopOpenRq = 0x8c00, ShopOpenRs = 0x8c01, ShopBuyRq = 0x8c02, ShopBuyRs = 0x8c03, ShopSellRq = 0x8c04, ShopSellRs = 0x8c05,
        RestorableItemsNt = 0xe342, ItemRestoreRq = 0xe344, ItemRestoreRs = 0xe345;
    const byte IncreaseShopBuy = 19, IncreaseShopSell = 20, DecreaseShopBuy = 15, DecreaseShopSell = 16;   // EIncreaseReason / EDecreaseReason
    const byte IncreaseItemRestore = 87, DecreaseItemRestore = 102;
    public static readonly TimeSpan BuybackKept = TimeSpan.FromHours(72);
    List<ItemState> buyback = [];

    /// <summary>Stacks of an item in the bag covering <paramref name="count"/> (largest first), or null when short.</summary>
    List<(Guid Item, long Revision, int Count)>? CostStacks(int itemId, long count, Guid? except = null)
    {
        var costs = new List<(Guid, long, int)>();
        foreach (var stack in storedItems.Values.Where(i => i.TemplateId == itemId && i.Location == "bag" && i.InstanceId != except).OrderByDescending(i => i.Quantity))
        {
            if (count <= 0) break;
            var n = (int)Math.Min(Math.Min(stack.Quantity, count), int.MaxValue);
            costs.Add((stack.InstanceId, stack.Revision, n));
            count -= n;
        }
        return count <= 0 ? costs : null;
    }

    static long SoldValue(ItemState item, string field) =>
        item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty(field, out var v) && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>RestorableItemInfo: key, item, count, type 1 (NpcShopSell), sold at, expiry, cost {Kina: sold for}, no additions.</summary>
    static void WriteRestorable(PacketWriter w, ItemState item)
    {
        var soldAt = SoldValue(item, "sold_at");
        w.I64(item.WireId).I32(item.TemplateId).I64(item.Quantity).U8(1).I64(soldAt).I64(soldAt + (long)BuybackKept.TotalMilliseconds);
        var price = SoldValue(item, "sold_for");
        if (price > 0) w.Varint(1).I32(KinaItemId).I64(price); else w.Varint(0);
        w.Varint(0);
    }

    byte[] RestorableItems()
    {
        var w = PacketWriter.Opcode(RestorableItemsNt).Varint(buyback.Count);
        foreach (var item in buyback) WriteRestorable(w, item);
        return w.ToArray();
    }

    async Task LoadBuybackAsync(INativeCharacterStore store, Guid character, CancellationToken ct) =>
        buyback = (await store.BuybackAsync(character, ct)).OrderBy(i => SoldValue(i, "sold_at")).ToList();
    int? openShop;
    static ShopTables Shops => ShopTables.Embedded;

    /// <summary>0x8c00 NpcShopOpen_RQ {NPC key varint}: a merchant here opens its shop, with what was bought of its limited goods.</summary>
    async Task<List<byte[]>> OpenShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        Target? npc = null;
        var ok = targets?.TryGetValue((int)key, out npc) == true && Shops.IsMerchant(npc!.NpcId);
        openShop = ok ? (int)key : null;
        var bought = new List<(int Goods, int Count)>();
        if (ok && Store is { } store && CharacterId is { } character)
        {
            var limited = Shops.LimitedGoods(npc!.NpcId, profileRace).ToList();
            if (limited.Count > 0)
            {
                IReadOnlyList<(int Target, int GoodsId, string Period, int Count)> rows = [];
                await Stored(async () => { rows = await store.ShopPurchasesAsync(character, ct); return []; });
                var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
                foreach (var (goods, row) in limited)
                    if (rows.FirstOrDefault(r => r.GoodsId == goods && r.Target == row.LimitTarget && r.Period == ShopTables.Period(row.LimitPeriod, at)) is { Count: > 0 } r)
                        bought.Add((goods, r.Count));
            }
        }
        var w = PacketWriter.Opcode(ShopOpenRs).U16(ok ? 0 : 1).Varint(key).Varint(bought.Count);
        foreach (var (goods, count) in bought) w.I32(goods).I32(count);
        return [w.I64(now).ToArray()];
    }

    /// <summary>The first layout that reads the whole body: (NPC key, entries) or null.</summary>
    static (int Npc, List<(long A, long B)> Entries)? ReadShopRequest(byte[] plain, bool withCharKey, bool wideEntries)
    {
        foreach (var varintKeys in new[] { true, false })
            foreach (var varintEntries in new[] { false, true })
            {
                try
                {
                    var r = new PacketReader(plain) { Offset = 2 };
                    long ReadKey() => varintKeys ? r.Varint() : r.I32();
                    if (withCharKey) ReadKey();
                    var npc = (int)ReadKey();
                    var count = r.Varint();
                    var entries = new List<(long, long)>();
                    for (var i = 0; i < count && i < 128; i++)
                        entries.Add(varintEntries ? (r.Varint(), r.Varint()) : wideEntries ? ((long)r.U64(), (long)r.U64()) : (r.I32(), r.I32()));
                    if (r.End && entries.Count == count) return (npc, entries);
                }
                catch (ProtocolException) { }
            }
        return null;
    }

    /// <summary>0x8c02 NpcShopBuyGoods_RQ {char key, NPC key, array {goods id, count}}: Kina paid, items granted, answered 0x8c03.</summary>
    async Task<List<byte[]>> BuyGoodsAsync(byte[] plain, long now, CancellationToken ct)
    {
        var request = ReadShopRequest(plain, withCharKey: true, wideEntries: false);
        var entries = request?.Entries ?? [];
        byte[] Reply(int result, int npc)
        {
            var w = PacketWriter.Opcode(ShopBuyRs).U16(result).Varint(npc).Varint(entries.Count);
            foreach (var (goods, count) in entries) w.I32((int)goods).I32((int)count);
            return w.ToArray();
        }
        if (request is not { } req) return [Reply(1, openShop ?? 0)];
        var npcKey = targets?.ContainsKey(req.Npc) == true ? req.Npc : openShop ?? req.Npc;
        if (Store is not { } store || CharacterId is not { } character || targets?.TryGetValue(npcKey, out var npc) != true || entries.Count == 0)
            return [Reply(1, npcKey)];
        var rewards = new List<(int, long)>();
        var prices = new Dictionary<int, long>();
        var limits = new List<(int, int, string, int, int)>();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        foreach (var (goodsId, count) in entries)
        {
            if (count < 1 || count > 10_000 || Shops.Goods(npc!.NpcId, profileRace, (int)goodsId) is not { } g || level < g.Level) return [Reply(1, npcKey)];
            rewards.Add((g.ItemId, g.Bundle * count));
            prices[g.Currency] = prices.GetValueOrDefault(g.Currency) + g.Price * count;
            if (g.Limit > 0) limits.Add((g.LimitTarget, (int)goodsId, ShopTables.Period(g.LimitPeriod, at), (int)count, g.Limit));
        }
        var costs = new List<(Guid, long, int)>();
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (CostStacks(currency, price) is not { } stacks) return [Reply(2, npcKey)];
            costs.AddRange(stacks);
        }
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, rewards, limits, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(limits.Count > 0 ? 3 : 1, npcKey)];
        return [Reply(0, npcKey), .. await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy)];
    }

    /// <summary>0x8c04 NpcShopSellItem_RQ {NPC key, map item key -> count}: items sold for their Item.dat SellPrice in Kina, answered 0x8c05.</summary>
    async Task<List<byte[]>> SellItemsAsync(byte[] plain, CancellationToken ct)
    {
        var request = ReadShopRequest(plain, withCharKey: false, wideEntries: true);
        var entries = request?.Entries ?? [];
        byte[] Reply(int result, int npc)
        {
            var w = PacketWriter.Opcode(ShopSellRs).U16(result).Varint(npc).Varint(entries.Count);
            foreach (var (key, count) in entries) w.I64(key).I64(count);
            return w.ToArray();
        }
        if (request is not { } req) return [Reply(1, openShop ?? 0)];
        var npcKey = targets?.ContainsKey(req.Npc) == true ? req.Npc : openShop ?? req.Npc;
        if (Store is not { } store || CharacterId is not { } character || targets?.TryGetValue(npcKey, out var npc) != true || !Shops.IsMerchant(npc!.NpcId) || entries.Count == 0)
            return [Reply(1, npcKey)];
        foreach (var (key, count) in entries)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity || Shops.SellPrice(row.TemplateId) is not { } price)
                return [Reply(1, npcKey), .. await InventoryChangesAsync(ct, IncreaseShopSell, DecreaseShopSell)];
            var sold = await Stored(async () => await store.SellToMerchantAsync(character, row.InstanceId, row.Revision, (int)count, price * count, ct) ? [[]] : []);
            if (sold.Count == 0) return [Reply(1, npcKey), .. await InventoryChangesAsync(ct, IncreaseShopSell, DecreaseShopSell)];
        }
        var replies = new List<byte[]> { Reply(0, npcKey) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseShopSell, DecreaseShopSell));
        await Stored(async () => { await LoadBuybackAsync(store, character, ct); return []; });
        replies.Add(RestorableItems());
        return replies;
    }

    /// <summary>0xe344 ItemRestore_RQ {array i64 item keys}: sold items bought back for their price (0xe345, then the bag and the list).</summary>
    async Task<List<byte[]>> RestoreItemsAsync(byte[] plain, CancellationToken ct)
    {
        var keys = new List<long>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            for (var i = 0; i < n && i < 50; i++) keys.Add((long)r.U64());
        }
        catch (ProtocolException) { return []; }
        var restored = new List<ItemState>();
        var result = 0;
        if (Store is { } store && CharacterId is { } character)
        {
            foreach (var key in keys)
            {
                if (buyback.FirstOrDefault(b => b.WireId == key) is not { } item) { result = 1; break; }
                var price = SoldValue(item, "sold_for");
                if ((price > 0 ? CostStacks(KinaItemId, price) : []) is not { } costs) { result = 2; break; }
                var outcome = "failed";
                await Stored(async () => { outcome = await store.RestoreItemAsync(character, item.InstanceId, costs, ct); return []; });
                if (outcome != "ok") { result = outcome == "limit" ? 3 : 1; break; }
                restored.Add(item);
                await InventoryChangesAsync(ct, IncreaseItemRestore, DecreaseItemRestore);   // refresh stacks for the next price
            }
            await Stored(async () => { await LoadBuybackAsync(store, character, ct); return []; });
        }
        else result = 1;
        var w = PacketWriter.Opcode(ItemRestoreRs).U16(result).Varint(keys.Count);
        foreach (var key in keys) w.I64(key);
        w.Varint(restored.Count);
        foreach (var item in restored) WriteRestorable(w, item);
        List<byte[]> replies = [w.ToArray(), .. await InventoryChangesAsync(ct, IncreaseItemRestore, DecreaseItemRestore), RestorableItems()];
        foreach (var _ in restored) replies.Add(await CountRestoreAsync(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct));
        return replies;
    }
}
