using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Coin shops (GameShop ShopType CoinShop) from tools/data/coin-shops.json (tools/coin_shop_data.py): goods per race.</summary>
public sealed class CoinShopTables
{
    sealed record File([property: JsonPropertyName("shops")] Dictionary<string, Dictionary<string, JsonElement>> Shops,
        [property: JsonPropertyName("goods")] Dictionary<string, long[]> Goods);

    readonly Dictionary<int, Dictionary<int, HashSet<int>>> shops = [];
    readonly Dictionary<int, string> names = [];
    readonly Dictionary<int, ShopTables.GoodsRow> goods = [];

    CoinShopTables(File file)
    {
        foreach (var (key, g) in file.Goods)
            if (g.Length >= 8) goods[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = new ShopTables.GoodsRow((int)g[0], (int)g[1], g[2], (int)g[3], (int)g[4], (int)g[5], (int)g[6], (int)g[7]);
        foreach (var (key, shop) in file.Shops)
        {
            var id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
            var races = new Dictionary<int, HashSet<int>>();
            foreach (var (field, value) in shop)
                if (field == "name") names[id] = value.GetString() ?? "";
                else if (int.TryParse(field, System.Globalization.CultureInfo.InvariantCulture, out var race) && value.ValueKind == JsonValueKind.Array)
                    races[race] = value.EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
            shops[id] = races;
        }
    }

    static readonly Lazy<CoinShopTables> embedded = new(() =>
    {
        using var stream = typeof(CoinShopTables).Assembly.GetManifestResourceStream("coin-shops.json") ?? throw new InvalidOperationException("coin-shops.json is not embedded");
        return new CoinShopTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("coin-shops.json"));
    });
    public static CoinShopTables Embedded => embedded.Value;

    public bool Exists(int shopId) => shops.ContainsKey(shopId);
    public string NameOf(int shopId) => names.GetValueOrDefault(shopId) ?? "";

    /// <summary>A goods row the coin shop sells to a race, or null.</summary>
    public ShopTables.GoodsRow? Goods(int shopId, int race, int goodsId) =>
        shops.TryGetValue(shopId, out var races) && races.TryGetValue(race, out var list) && list.Contains(goodsId) ? goods.GetValueOrDefault(goodsId) : null;

    public IEnumerable<(int GoodsId, ShopTables.GoodsRow Row)> LimitedGoods(int shopId, int race) =>
        shops.TryGetValue(shopId, out var races) && races.TryGetValue(race, out var list)
            ? list.Where(id => goods.TryGetValue(id, out var g) && g.Limit > 0).Select(id => (id, goods[id])) : [];
}

// Coin shops: the Abyss (990001), Nightmare (990003), battle pass (990004/990005), arena (990006), Shugo festa (990007),
// season (990008) and battlefield (990009) shops, opened from the menu, paid in Abyss points, coins and medals (Item.dat
// currency items). The 0x8c run (index + 0x52d2, OBSERVED official replies, recording 20261106-115817 and 145442):
// CoinShopOpen_RQ 0x8c06 {shop i32} -> CoinShopOpen_RS 0x8c07 {u16 0, shop i32, purchased limited goods map varint
// {goods i32, count i32}, server time i64} (`0000 371b0f00 03 {40eb191e 0a000000}... 11a75eb1a1010000`);
// CoinShopBuyGoods_RQ 0x8c08 {shop i32, goods list {goods, count}} (widths unrecorded: the layout that reads the whole body)
// -> CoinShopBuyGoods_RS 0x8c09 {u16 result, shop i32, list varint {goods i32, count i32}} with result 1536
// NpcShop_BuyGoods_Success (OBSERVED `0006 371b0f00 01 40eb191e 0a000000`: the goods and the count bought now). Limits count
// per goods and period with the NPC shops' purchases (ShopTables.Period: daily/weekly/monthly at 07:00 UTC).
public sealed partial class GameSession
{
    public const ushort CoinShopBuyRq = 0x8c08, CoinShopBuyRs = 0x8c09;
    static CoinShopTables CoinShops => CoinShopTables.Embedded;

    /// <summary>CoinShopOpen_RQ {shop i32}: the shop with what was bought of its limited goods in their periods.</summary>
    async Task<List<byte[]>> OpenCoinShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var shopId = BitConverter.ToInt32(plain, 2);
        var ok = CoinShops.Exists(shopId) && resources is not null;
        var bought = new List<(int Goods, int Count)>();
        if (ok && Store is { } store && CharacterId is { } character)
        {
            var limited = CoinShops.LimitedGoods(shopId, profileRace).ToList();
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
        var w = PacketWriter.Opcode(CoinShopOpenRs).U16(ok ? 0 : 1).I32(shopId).Varint(bought.Count);
        foreach (var (goods, count) in bought) w.I32(goods).I32(count);
        return [w.I64(now).ToArray()];
    }

    /// <summary>The buy request in the first layout that reads the whole body (i32 or varint pairs).</summary>
    static (int Shop, List<(long Goods, long Count)> Entries)? ReadCoinShopBuy(byte[] plain)
    {
        if (plain.Length < 7) return null;
        foreach (var varintEntries in new[] { false, true })
        {
            try
            {
                var r = new PacketReader(plain) { Offset = 2 };
                var shop = r.I32();
                var count = r.Varint();
                var entries = new List<(long, long)>();
                for (var i = 0; i < count && i < 128; i++) entries.Add(varintEntries ? (r.Varint(), r.Varint()) : (r.I32(), r.I32()));
                if (r.End && entries.Count == count) return (shop, entries);
            }
            catch (ProtocolException) { }
        }
        return null;
    }

    /// <summary>CoinShopBuyGoods_RQ: the price in the goods' currency paid, the items granted, the limits counted.</summary>
    async Task<List<byte[]>> BuyCoinShopAsync(byte[] plain, long now, CancellationToken ct)
    {
        var request = ReadCoinShopBuy(plain);
        var shopId = request?.Shop ?? (plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0);
        var entries = request?.Entries ?? [];
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(CoinShopBuyRs).U16(result).I32(shopId).Varint(result == EResult.NpcShop_BuyGoods_Success ? entries.Count : 0);
            if (result == EResult.NpcShop_BuyGoods_Success) foreach (var (goods, count) in entries) w.I32((int)goods).I32((int)count);
            return w.ToArray();
        }
        if (request is null || entries.Count == 0 || !CoinShops.Exists(shopId) || Store is not { } store || CharacterId is not { } character || resources is null)
            return [Reply(EResult.NpcShop_BuyGoods_Fail)];
        var rewards = new List<(int, long)>();
        var prices = new Dictionary<int, long>();
        var limits = new List<(int, int, string, int, int)>();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        foreach (var (goodsId, count) in entries)
        {
            if (count < 1 || count > 10_000 || goodsId is < 0 or > int.MaxValue || CoinShops.Goods(shopId, profileRace, (int)goodsId) is not { } g || level < g.Level)
                return [Reply(EResult.NpcShop_BuyGoods_Fail)];
            if (g.Limit > 0 && count > g.Limit) return [Reply(EResult.NpcShop_BuyGoods_Fail_NotEnoughLimitedGoodsCount)];
            rewards.Add((g.ItemId, g.Bundle * count));
            prices[g.Currency] = prices.GetValueOrDefault(g.Currency) + g.Price * count;
            if (g.Limit > 0) limits.Add((g.LimitTarget, (int)goodsId, ShopTables.Period(g.LimitPeriod, at), (int)count, g.Limit));
        }
        var costs = new List<(Guid, long, int)>();
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (CostStacks(currency, price) is not { } stacks) return [Reply(currency == KinaItemId ? EResult.Item_Common_Fail_NotEnoughCountMoney : EResult.Item_Common_Fail_NotEnoughCountItem)];
            costs.AddRange(stacks);
        }
        if (!BagRoomFor(rewards)) return [Reply(EResult.Item_Common_Fail_NotEnoughSlotCountContainer)];   // GameSession.BagRoom.cs
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, rewards, limits, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(limits.Count > 0 ? EResult.NpcShop_BuyGoods_Fail_NotEnoughLimitedGoodsCount : EResult.NpcShop_BuyGoods_Fail)];
        Note($"coin shop {CoinShops.NameOf(shopId)}: bought {string.Join(", ", entries.Select(e => $"{e.Goods} x{e.Count}"))}");
        return [Reply(EResult.NpcShop_BuyGoods_Success), .. await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy)];
    }
}
