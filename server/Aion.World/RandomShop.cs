using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The Black Cloud Merchants (the client's random shop, String_UI_SHOP_RANDOMSHOP_TITLE) from tools/data/randomshop.json
/// (tools/random_shop_data.py): per race an "All" shop (skins, pets, theostones), a skin shop and a pet shop, the goods pool
/// of each (RandomShopGoods), the paid refresh costs (RandomShopRefreshCost) and each goods' item kind, grade and SellPrice.
/// </summary>
public sealed class RandomShopTables
{
    public const int Skin = 0, Pet = 1, Theostone = 2, Other = 3;
    public static readonly string[] KindNames = ["skin", "pet", "theostone", "other"];
    public static readonly string[] GradeNames = ["Common", "Rare", "Legend", "Unique", "Epic"];
    /// <summary>ERandomShopCategory per race in usmap order (L_All .. L_Etc02, D_All .. D_Etc02).</summary>
    static readonly string[] CategoryOrder = ["All", "Consume", "Skin", "Pet", "Etc01", "Etc02"];

    public sealed record Shop(int Id, string Category, int Race, string Group, string RefreshGroup, int LevelMin, int LevelMax, bool OnSale, int Order)
    {
        /// <summary>The category on the wire: race x 100 + its place in the race's list (OBSERVED D_All 201, D_Skin 203, D_Pet 204).</summary>
        public int Wire => WireCategory(Category);
    }

    public sealed record Goods(int Id, int ItemId, int Kind, int Grade, long SellPrice, int LevelMin, int LevelMax);

    sealed record Data(
        [property: JsonPropertyName("shops")] List<JsonElement[]> Shops,
        [property: JsonPropertyName("groups")] Dictionary<string, List<long[]>> Groups,
        [property: JsonPropertyName("refresh")] Dictionary<string, long[]> Refresh);

    readonly Dictionary<int, Shop> shops = [];
    readonly Dictionary<string, List<Goods>> pools = [];
    readonly Dictionary<int, Goods> goods = [];
    readonly Dictionary<string, long[]> refresh;

    RandomShopTables(Data file)
    {
        foreach (var s in file.Shops)
        {
            var shop = new Shop(s[0].GetInt32(), s[1].GetString()!, s[2].GetInt32(), s[3].GetString()!, s[4].GetString()!, s[5].GetInt32(), s[6].GetInt32(), s[7].GetInt32() != 0, s[8].GetInt32());
            if (shop.Wire == 0) throw new InvalidDataException($"randomshop.json: unknown category {shop.Category}");
            shops[shop.Wire] = shop;
        }
        foreach (var (group, rows) in file.Groups)
        {
            var list = rows.Select(r => new Goods((int)r[0], (int)r[1], (int)r[2], (int)r[3], r[4], (int)r[5], (int)r[6])).ToList();
            pools[group] = list;
            foreach (var g in list) goods[g.Id] = g;
        }
        refresh = file.Refresh;
    }

    static readonly Lazy<RandomShopTables> embedded = new(() =>
    {
        using var stream = typeof(RandomShopTables).Assembly.GetManifestResourceStream("randomshop.json")
            ?? throw new InvalidOperationException("Missing embedded game data randomshop.json");
        return new RandomShopTables(JsonSerializer.Deserialize<Data>(stream) ?? throw new InvalidOperationException("Empty randomshop.json"));
    });

    public static RandomShopTables Embedded => embedded.Value;

    /// <summary>"D_Skin" -> 203; 0 for an unknown name.</summary>
    public static int WireCategory(string category)
    {
        var race = category.StartsWith("L_", StringComparison.Ordinal) ? 1 : category.StartsWith("D_", StringComparison.Ordinal) ? 2 : 0;
        var place = Array.IndexOf(CategoryOrder, race == 0 ? "" : category[2..]);
        return race == 0 || place < 0 ? 0 : race * 100 + place + 1;
    }

    public IEnumerable<Shop> Shops => shops.Values.OrderBy(s => s.Id);
    public Shop? ShopOf(int wire) => shops.GetValueOrDefault(wire);
    public IReadOnlyList<Goods> Pool(string group) => pools.GetValueOrDefault(group) ?? [];
    public Goods? GoodsOf(int id) => goods.GetValueOrDefault(id);
    public int GoodsCount => goods.Count;
    /// <summary>Kina for paid refresh step 1, 2, .. of a cost group (RandomShopRefreshCost, GoldCombined).</summary>
    public IReadOnlyList<long> RefreshCosts(string group) => refresh.GetValueOrDefault(group) ?? [];
}

/// <summary>A rolled list of one shop for one character: the goods, their discount and whether bought; stored as JSON (migration 086).</summary>
public sealed record RandomShopOffer(
    [property: JsonPropertyName("until")] long Until,
    [property: JsonPropertyName("goods")] List<int[]> Goods,
    [property: JsonPropertyName("day")] string Day,
    [property: JsonPropertyName("refreshes")] int Refreshes)
{
    /// <summary>[goods id, discount index, bought 0/1] of a slot.</summary>
    public int[]? Slot(int goodsId) => Goods.FirstOrDefault(g => g.Length == 3 && g[0] == goodsId);
    public string ToJson() => JsonSerializer.Serialize(this);
    public static RandomShopOffer? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<RandomShopOffer>(json) is { Goods: not null, Day: not null } o && o.Goods.All(g => g.Length == 3) ? o : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The Black Cloud Merchants' rules. OBSERVED (official 20261106-115817 and -144624, 6 lists of 10 goods): 10 goods per list, all
/// discounted, paid in Kina (ECurrencyCategory 93 GoldCombined), one item each; the list renews at the next full hour
/// (next_refresh_time 13:00 at 12:05, 15:00 at 14:48 UTC); price_index 0-3 = 90/80/70/50 % of the normal price; the normal
/// price = the item's SellPrice x 100 for skins, x 50 for pets, x 200 for theostones (all 59 goods exact).
/// CANDIDATE (estimated from those 59 goods, editable by staff): how often each discount comes (32/41/24/3), the share of each
/// kind in the "All" list (pets 35, skins 35, theostones 25 of 20 seen) and the per-item weight by grade (skins Common 90 /
/// Rare 27 / Legend 3: 52 % / 37 % / 11 % of the skins seen, which these weights reproduce). Unknown: whether the list renews
/// every hour or every 2 hours (both fit), the paid refresh beyond step 10 (the last step's cost again) and its daily reset
/// (07:00 UTC like the shop limits).
/// </summary>
public sealed record RandomShopRules(bool Enabled, int Slots, int RefreshMinutes, IReadOnlyList<RandomShopRules.Discount> Discounts,
    IReadOnlyList<RandomShopRules.Kind> Kinds)
{
    public sealed record Discount([property: JsonPropertyName("percent")] int Percent, [property: JsonPropertyName("weight")] int Weight);
    /// <summary>A kind's share in a mixed list, its price factor (x SellPrice) and the weight of one item per grade (Common, Rare, Legend, Unique, Epic).</summary>
    public sealed record Kind([property: JsonPropertyName("share")] int Share, [property: JsonPropertyName("priceFactor")] long PriceFactor,
        [property: JsonPropertyName("grades")] int[] Grades);

    public const int MaxSlots = 30, MaxWeight = 1_000_000;
    public const long MaxFactor = 1_000_000;
    /// <summary>ECurrencyCategory GoldCombined (Kina) as the official sends it.</summary>
    public const byte CurrencyKina = 93;

    public static RandomShopRules Default { get; } = new(true, 10, 60,
        [new(90, 32), new(80, 41), new(70, 24), new(50, 3)],
        [new(35, 100, [90, 27, 3, 1, 1]), new(35, 50, [1, 1, 1, 1, 1]), new(25, 200, [60, 60, 40, 40, 40]), new(0, 100, [1, 1, 1, 1, 1])]);

    public Kind KindOf(int kind) => Kinds[Math.Clamp(kind, 0, Kinds.Count - 1)];
    public long NormalPrice(RandomShopTables.Goods g) => Math.Min(g.SellPrice * KindOf(g.Kind).PriceFactor, 1_000_000_000_000);
    public long Price(RandomShopTables.Goods g, int discount) => NormalPrice(g) * Discounts[Math.Clamp(discount, 0, Discounts.Count - 1)].Percent / 100;

    /// <summary>When the list rolled now renews: the next multiple of RefreshMinutes since the epoch (60: the next full hour).</summary>
    public long NextRefresh(long nowMs)
    {
        var step = RefreshMinutes * 60_000L;
        return (nowMs / step + 1) * step;
    }

    /// <summary>A new list: Slots different goods the character's level may use, the kind first (by share, among the kinds left),
    /// then one item of it by its grade's weight; each with a discount by weight.</summary>
    public List<int[]> Roll(RandomShopTables.Shop shop, int level, Random rng)
    {
        var left = RandomShopTables.Embedded.Pool(shop.Group)
            .Where(g => (g.LevelMin == 0 || level >= g.LevelMin) && (g.LevelMax == 0 || level <= g.LevelMax)).ToList();
        var list = new List<int[]>();
        while (list.Count < Slots && left.Count > 0)
        {
            var kinds = left.Select(g => g.Kind).Distinct().OrderBy(k => k).ToList();
            var shares = kinds.Select(k => (long)KindOf(k).Share).ToList();
            var kind = kinds[Pick(shares.Sum() > 0 ? shares : kinds.Select(_ => 1L).ToList(), rng)];
            var candidates = left.Where(g => g.Kind == kind).ToList();
            var grades = KindOf(kind).Grades;
            var weights = candidates.Select(g => (long)Math.Max(0, grades[Math.Clamp(g.Grade, 0, grades.Length - 1)])).ToList();
            var chosen = candidates[Pick(weights.Sum() > 0 ? weights : candidates.Select(_ => 1L).ToList(), rng)];
            left.Remove(chosen);
            list.Add([chosen.Id, Pick(Discounts.Select(d => (long)d.Weight).ToList(), rng), 0]);
        }
        return list;
    }

    static int Pick(IReadOnlyList<long> weights, Random rng)
    {
        var total = weights.Sum();
        if (total <= 0) return 0;
        var roll = rng.NextInt64(total);
        for (var i = 0; i < weights.Count; i++)
            if ((roll -= weights[i]) < 0) return i;
        return weights.Count - 1;
    }

    /// <summary>&lt;random_shop enabled slots refresh_minutes&gt; with &lt;discount percent weight/&gt; and &lt;kind name share price_factor grades/&gt;.</summary>
    public static RandomShopRules Parse(XElement? e)
    {
        if (e is null) return Default;
        static int Int(XElement x, string name, int fallback) => (string?)x.Attribute(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
        var discounts = e.Elements("discount").Select(d => new Discount(Int(d, "percent", 100), Int(d, "weight", 1))).ToList();
        var kinds = Default.Kinds.ToList();
        foreach (var k in e.Elements("kind"))
        {
            var index = Array.IndexOf(RandomShopTables.KindNames, (string?)k.Attribute("name") ?? "");
            if (index < 0) throw new InvalidDataException($"server-rules.xml: random_shop kind {(string?)k.Attribute("name")} is not one of {string.Join(", ", RandomShopTables.KindNames)}");
            var grades = (string?)k.Attribute("grades") is { } g ? g.Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray() : kinds[index].Grades;
            kinds[index] = new Kind(Int(k, "share", kinds[index].Share), (string?)k.Attribute("price_factor") is { } f ? long.Parse(f, CultureInfo.InvariantCulture) : kinds[index].PriceFactor, grades);
        }
        var rules = new RandomShopRules((string?)e.Attribute("enabled") is { } on ? System.Xml.XmlConvert.ToBoolean(on) : true, Int(e, "slots", Default.Slots),
            Int(e, "refresh_minutes", Default.RefreshMinutes), discounts.Count > 0 ? discounts : Default.Discounts, kinds);
        return rules.Checked("server-rules.xml: random_shop ");
    }

    /// <summary>The rules if every number is in range, else an InvalidDataException naming the first bad one.</summary>
    public RandomShopRules Checked(string where = "")
    {
        if (Slots is < 1 or > MaxSlots) throw new InvalidDataException($"{where}slots must be 1-{MaxSlots}");
        if (RefreshMinutes is < 1 or > 10080) throw new InvalidDataException($"{where}refresh minutes must be 1-10080");
        if (Discounts.Count is < 1 or > 4) throw new InvalidDataException($"{where}needs 1-4 discounts (the client shows 4 tags)");
        if (Discounts.Any(d => d.Percent is < 1 or > 100 || d.Weight is < 0 or > MaxWeight) || Discounts.All(d => d.Weight == 0))
            throw new InvalidDataException($"{where}discount percent must be 1-100 and weights 0-{MaxWeight}, not all 0");
        if (Kinds.Count != RandomShopTables.KindNames.Length) throw new InvalidDataException($"{where}needs {RandomShopTables.KindNames.Length} kinds");
        if (Kinds.Any(k => k.Share is < 0 or > MaxWeight || k.PriceFactor is < 0 or > MaxFactor || k.Grades.Length != RandomShopTables.GradeNames.Length
                || k.Grades.Any(w => w is < 0 or > MaxWeight)))
            throw new InvalidDataException($"{where}kind shares and grade weights must be 0-{MaxWeight} ({RandomShopTables.GradeNames.Length} grades), price factors 0-{MaxFactor}");
        return this;
    }
}

/// <summary>The staff panel's Black Cloud Merchants settings (server setting "random_shop", JSON); missing fields keep server-rules.xml.</summary>
public sealed record RandomShopSettings(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("slots")] int? Slots,
    [property: JsonPropertyName("refreshMinutes")] int? RefreshMinutes,
    [property: JsonPropertyName("discounts")] List<RandomShopRules.Discount>? Discounts,
    [property: JsonPropertyName("kinds")] Dictionary<string, RandomShopRules.Kind>? Kinds)
{
    public static RandomShopSettings Parse(string json)
    {
        var s = JsonSerializer.Deserialize<RandomShopSettings>(json) ?? throw new InvalidDataException("empty settings");
        foreach (var name in s.Kinds?.Keys.AsEnumerable() ?? [])
            if (Array.IndexOf(RandomShopTables.KindNames, name) < 0) throw new InvalidDataException($"unknown kind {name}");
        s.Over(RandomShop.Defaults);
        return s;
    }

    public RandomShopRules Over(RandomShopRules defaults)
    {
        var kinds = defaults.Kinds.ToList();
        foreach (var (name, kind) in Kinds ?? []) kinds[Array.IndexOf(RandomShopTables.KindNames, name)] = kind with { Grades = kind.Grades ?? [] };
        return new RandomShopRules(Enabled ?? defaults.Enabled, Slots ?? defaults.Slots, RefreshMinutes ?? defaults.RefreshMinutes,
            Discounts ?? defaults.Discounts, kinds).Checked();
    }
}

/// <summary>The Black Cloud Merchants' rules in force: server-rules.xml, overridden live by the staff panel.</summary>
public static class RandomShop
{
    static RandomShopRules? live;

    public static RandomShopRules Defaults => ServerRules.Embedded.RandomShop;
    public static RandomShopRules Current => Volatile.Read(ref live) ?? Defaults;

    /// <summary>The staff setting (JSON) over the defaults; null: the defaults alone. False when the JSON was refused.</summary>
    public static bool Apply(string? json)
    {
        if (json is null) { Volatile.Write(ref live, null); return true; }
        try { Volatile.Write(ref live, RandomShopSettings.Parse(json).Over(Defaults)); return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or OverflowException or NullReferenceException) { return false; }
    }

    /// <summary>One goods as RandomShopInfo_NT carries it.</summary>
    public readonly record struct WireGoods(int Id, int ItemId, long Count, byte PriceIndex, byte Currency, long Normal, long Discounted, bool IsDiscounted, bool Purchased);

    public static WireGoods Wire(RandomShopRules rules, RandomShopTables.Goods g, int discount, bool purchased) =>
        new(g.Id, g.ItemId, 1, (byte)Math.Clamp(discount, 0, 255), RandomShopRules.CurrencyKina, rules.NormalPrice(g), rules.Price(g, discount), true, purchased);

    /// <summary>
    /// RandomShopInfo_NT 0x5691 {category u8, varint count, goods {id i32, item i32, count i64, price index u8, currency u8,
    /// normal i64, discounted i64, is discounted + is purchased: 2 bits}, next refresh time i64, user refresh count i32}. OBSERVED
    /// (official 359-byte lists): the bools are packed 2 bits per goods, 4 goods per byte, the byte written where the first of
    /// its bools falls (after goods 0, 4, 8: 0x55 = four discounted goods, 0x5f = two of them also bought).
    /// </summary>
    public static byte[] InfoNt(int category, IReadOnlyList<WireGoods> goods, long nextRefreshMs, int refreshCount)
    {
        var w = PacketWriter.Opcode(GameSession.RandomShopInfoNt).U8((byte)category).Varint(goods.Count);
        for (var i = 0; i < goods.Count; i++)
        {
            var g = goods[i];
            w.I32(g.Id).I32(g.ItemId).I64(g.Count).U8(g.PriceIndex).U8(g.Currency).I64(g.Normal).I64(g.Discounted);
            if (i % 4 != 0) continue;
            var bits = 0;
            for (var j = i; j < Math.Min(i + 4, goods.Count); j++)
                bits |= ((goods[j].IsDiscounted ? 1 : 0) | (goods[j].Purchased ? 2 : 0)) << (2 * (j - i));
            w.U8((byte)bits);
        }
        return w.I64(nextRefreshMs).I32(refreshCount).ToArray();
    }
}
