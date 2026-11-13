using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>
/// The Black Cloud market (the client's BM shop, BMShopGoods) from tools/data/bmshop.json (tools/bmshop_data.py): goods, the
/// item each one gives, its official price and currency (Kina, Quna, real money, Mileage), purchase limits and sale window.
/// </summary>
public sealed class PremiumShopTables
{
    public const int Kina = 0, Quna = 1, Cash = 2, Mileage = 3;

    public sealed record Goods(int Id, int ItemId, int Count, int Currency, long Price, int MaxPerOnce, int LimitTarget, int Limit, int LimitPeriod,
        int LevelMin, int LevelMax, int Races, bool OnSale, long SaleStartMs, long SaleEndMs, string Name, string CategoryLight, string CategoryDark);

    /// <summary>A BMShopCategory row (by category or sub-category name): its tab group (EBMShopCategoryGroup), race (0 all,
    /// 1 Elyos, 2 Asmodians), order, whether the client shows it (bIsShow) and the currency it shows (ECurrencyCategory).</summary>
    public sealed record Category(string Group, int Race, int Order, bool Shown, string Currency);

    sealed record Data(
        [property: JsonPropertyName("goods")] Dictionary<string, long[]> Goods,
        [property: JsonPropertyName("names")] Dictionary<string, string[]> Names,
        [property: JsonPropertyName("quna_items")] Dictionary<string, int> QunaItems,
        [property: JsonPropertyName("categories")] Dictionary<string, JsonElement[]>? Categories = null,
        [property: JsonPropertyName("tabs")] Dictionary<string, string>? Tabs = null);

    readonly Dictionary<int, Goods> goods;
    readonly Dictionary<int, int> qunaItems;
    readonly Dictionary<string, Category> categories;
    readonly Dictionary<string, string> tabs;

    PremiumShopTables(Data file)
    {
        goods = [];
        foreach (var (key, g) in file.Goods)
        {
            if (g.Length < 14) throw new InvalidDataException($"bmshop.json: goods {key} has {g.Length} fields");
            var id = int.Parse(key, CultureInfo.InvariantCulture);
            var names = file.Names.GetValueOrDefault(key) ?? [];
            goods[id] = new Goods(id, (int)g[0], (int)g[1], (int)g[2], g[3], (int)g[4], (int)g[5], (int)g[6], (int)g[7], (int)g[8], (int)g[9], (int)g[10],
                g[11] != 0, g[12], g[13], names.ElementAtOrDefault(0) ?? "", names.ElementAtOrDefault(1) ?? "None", names.ElementAtOrDefault(2) ?? "None");
        }
        qunaItems = file.QunaItems.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
        categories = [];
        foreach (var (name, c) in file.Categories ?? [])
            categories[name] = c.Length >= 5 ? new Category(c[0].GetString() ?? "None", c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32() != 0, c[4].GetString() ?? "None")
                : throw new InvalidDataException($"bmshop.json: category {name} has {c.Length} fields");
        tabs = file.Tabs ?? [];
    }

    static readonly Lazy<PremiumShopTables> embedded = new(() =>
    {
        using var stream = typeof(PremiumShopTables).Assembly.GetManifestResourceStream("bmshop.json")
            ?? throw new InvalidOperationException("Missing embedded game data bmshop.json");
        return new PremiumShopTables(JsonSerializer.Deserialize<Data>(stream) ?? throw new InvalidOperationException("Empty bmshop.json"));
    });

    public static PremiumShopTables Embedded => embedded.Value;

    public Goods? Of(int id) => goods.GetValueOrDefault(id);
    public IEnumerable<Goods> All => goods.Values.OrderBy(g => g.Id);

    /// <summary>
    /// Goods the official server keeps limits for although the client's BMShopGoods has no row for them (OBSERVED official
    /// 20261106-105448 #19670 0x568e: their reset times): goods id -> limit period (0 never, 2 weekly, 3 monthly). Not sold here.
    /// </summary>
    static readonly Dictionary<int, int> recordedPeriods = new() { [1254] = 0, [1262] = 3, [100100] = 2, [100134] = 2, [100135] = 2, [100141] = 2, [100173] = 2 };

    /// <summary>The limit period of a goods: its BMShopGoods row, else the one the official server showed, else 0 (never resets).</summary>
    public int LimitPeriodOf(int id) => Of(id)?.LimitPeriod ?? recordedPeriods.GetValueOrDefault(id);

    /// <summary>Quna per item of a Quna pack (item effect GetGameCash), 0 for other items.</summary>
    public int QunaPerItem(int itemId) => qunaItems.GetValueOrDefault(itemId);

    /// <summary>The BMShopCategory row of a category or sub-category name (Shop_Special, D_SkinShop_Pet), null when unknown.</summary>
    public Category? CategoryOf(string name) => categories.GetValueOrDefault(name);

    /// <summary>
    /// The shop tab a goods is listed under for a race (1 Elyos: CategoryLight, 2 Asmodians: CategoryDark): the client's own name
    /// of its category group (BMShopCategory CategoryGroup + Table_L10N_en-US UI_SHOP_ENUM_CATEGORY_&lt;GROUP&gt;): Atelier, Event,
    /// Quna Shop, Tsenka Merchants, Wind Breeze Merchants, A2 Mileage, Founder's Pack, Subscribe; "" when the race has none.
    /// </summary>
    public string TabOf(Goods g, int race) =>
        (race == 2 ? g.CategoryDark : g.CategoryLight) is var name && name != "None" && CategoryOf(name) is { } c ? TabName(c.Group) : "";

    /// <summary>The client's English name of a tab group (Skin = Atelier, Warehouse = Storage ...), the group itself when it has none.</summary>
    public string TabName(string group) => tabs.GetValueOrDefault(group) ?? group;

    /// <summary>The tab groups in the client's order (the lowest category order of each group).</summary>
    public IEnumerable<string> TabGroups => categories.Values.GroupBy(c => c.Group).OrderBy(g => g.Min(c => c.Order)).Select(g => g.Key);

    /// <summary>Whether the race has a category for the goods (CategoryLight / CategoryDark not None).</summary>
    public static bool ForRace(Goods g, int race) => (g.Races & (race == 2 ? 2 : 1)) != 0;

    /// <summary>Whether the client shows the goods to a race now: bOnSale, the race has a category for it, inside the sale window.</summary>
    public static bool OnSaleFor(Goods g, int race, long nowMs) =>
        g.OnSale && (g.Races & (race == 2 ? 2 : 1)) != 0 && (g.SaleStartMs == 0 || g.SaleStartMs <= nowMs) && (g.SaleEndMs == 0 || nowMs <= g.SaleEndMs);
}

/// <summary>
/// Free-to-play rules for the Black Cloud market (user 2026-11-09: everything sold for Quna or real money is bought with Kina).
/// Defaults from gamedata/server-rules.xml &lt;premium_shop&gt;; the staff panel's choice (server setting "premium_shop") wins.
/// Prices: a Quna price costs <see cref="KinaPerQuna"/> Kina per Quna; a real-money price (KRW) is first turned into Quna at
/// <see cref="CashPerQuna"/> KRW per Quna (the official top-up: 1,000 Quna for 21,500 KRW), a Mileage price at
/// <see cref="MileagePerQuna"/>; Kina goods keep their Kina price. A per-goods Kina price replaces the computed one.
/// Mileage (A2 Mileage, ECurrencyCategory 6, account): a Mileage-priced goods spends the account's Mileage first and Kina for the
/// rest; buying a Quna or real-money goods earns its Quna value / <see cref="QunaPerMileage"/> Mileage and a Black Cloud Merchants
/// goods its Kina / <see cref="RandomKinaPerMileage"/> (GlobalSetting bmshop_mileage_accrue_ratio 10, random_shop_mileage_accrue_ratio
/// 200: the values are the client's, reading them as "per 1 Mileage" is CANDIDATE). <see cref="MirrorMileage"/> shows the bag's Kina
/// as Mileage too (Kina / (KinaPerQuna / MileagePerQuna)), so the client lets a player press buy. <see cref="SellOffSale"/>: goods
/// the client table has switched off or out of their sale window are sold as well (user 2026-11-10: "show them all").
/// </summary>
public sealed record PremiumShopRules(bool Enabled, long KinaPerQuna, decimal CashPerQuna, decimal MileagePerQuna, bool MirrorQuna,
    IReadOnlyDictionary<int, bool> GoodsEnabled, IReadOnlyDictionary<int, long> KinaPrices, IReadOnlySet<string> DisabledCategories,
    bool SellOffSale = true, bool MirrorMileage = true, long QunaPerMileage = 10, long RandomKinaPerMileage = 200)
{
    public const long MaxKinaPerQuna = 100_000_000, MaxPrice = 1_000_000_000_000;

    public static PremiumShopRules Default { get; } = new(true, 1000, 21.5m, 40m, true, new Dictionary<int, bool>(), new Dictionary<int, long>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Daily_RechargeQuna" });

    /// <summary>The category without the Asmodian "D_" prefix (Daily_RechargeQuna / D_Daily_RechargeQuna are one category).</summary>
    public static string Category(string category) => category.StartsWith("D_", StringComparison.Ordinal) ? category[2..] : category;

    /// <summary>Sold here: the market is on and neither the goods nor its category is switched off.</summary>
    public bool Sells(PremiumShopTables.Goods g) => Enabled && (GoodsEnabled.TryGetValue(g.Id, out var on)
        ? on : !DisabledCategories.Contains(Category(g.CategoryLight)) && !DisabledCategories.Contains(Category(g.CategoryDark)));

    /// <summary>What the goods is worth in Quna (0 for Kina goods): Quna as is, real money and Mileage converted, rounded up.</summary>
    public long QunaValue(PremiumShopTables.Goods g) => g.Currency switch
    {
        PremiumShopTables.Quna => g.Price,
        PremiumShopTables.Cash => (long)Math.Ceiling(g.Price / CashPerQuna),
        PremiumShopTables.Mileage => (long)Math.Ceiling(g.Price / MileagePerQuna),
        _ => 0,
    };

    /// <summary>The Kina price of one purchase: the staff's own price, else the Kina price, else the Quna value in Kina.</summary>
    public long KinaPrice(PremiumShopTables.Goods g) =>
        KinaPrices.TryGetValue(g.Id, out var own) ? own : g.Currency == PremiumShopTables.Kina ? g.Price : Math.Min(MaxPrice, QunaValue(g) * KinaPerQuna);

    /// <summary>Whether a race may buy the goods now: shown by the client now, or (SellOffSale) any goods of the race's categories.</summary>
    public bool Available(PremiumShopTables.Goods g, int race, long nowMs) =>
        PremiumShopTables.OnSaleFor(g, race, nowMs) || SellOffSale && PremiumShopTables.ForRace(g, race);

    /// <summary>Mileage earned by buying a goods (quantity included): Quna and real-money goods only.</summary>
    public long MileageEarned(PremiumShopTables.Goods g, int quantity) =>
        QunaPerMileage > 0 && g.Currency is PremiumShopTables.Quna or PremiumShopTables.Cash ? QunaValue(g) * quantity / QunaPerMileage : 0;

    /// <summary>Kina the bag's Kina is worth in Mileage for the mirror: Kina / (KinaPerQuna / MileagePerQuna).</summary>
    public long MileageOfKina(long kina) => (long)(kina * MileagePerQuna / KinaPerQuna);

    /// <summary>&lt;premium_shop enabled kina_per_quna cash_per_quna mileage_per_quna mirror_quna&gt; with &lt;category name enabled/&gt; and &lt;goods id enabled kina/&gt;.</summary>
    public static PremiumShopRules Parse(XElement? e)
    {
        if (e is null) return Default;
        static bool Flag(XElement x, string name, bool fallback) => (string?)x.Attribute(name) is { } v ? System.Xml.XmlConvert.ToBoolean(v) : fallback;
        var rate = long.Parse((string?)e.Attribute("kina_per_quna") ?? "1000", CultureInfo.InvariantCulture);
        var cash = decimal.Parse((string?)e.Attribute("cash_per_quna") ?? "21.5", CultureInfo.InvariantCulture);
        var mileage = decimal.Parse((string?)e.Attribute("mileage_per_quna") ?? "40", CultureInfo.InvariantCulture);
        if (rate is < 1 or > MaxKinaPerQuna) throw new InvalidDataException($"server-rules.xml: premium_shop kina_per_quna must be 1-{MaxKinaPerQuna}");
        if (cash <= 0 || mileage <= 0) throw new InvalidDataException("server-rules.xml: premium_shop cash_per_quna and mileage_per_quna must be above 0");
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in e.Elements("category"))
            if (!Flag(c, "enabled", true)) categories.Add(Category((string?)c.Attribute("name") ?? throw new InvalidDataException("server-rules.xml: premium_shop category without name")));
        var enabled = new Dictionary<int, bool>();
        var prices = new Dictionary<int, long>();
        foreach (var g in e.Elements("goods"))
        {
            var id = int.Parse((string?)g.Attribute("id") ?? throw new InvalidDataException("server-rules.xml: premium_shop goods without id"), CultureInfo.InvariantCulture);
            if (PremiumShopTables.Embedded.Of(id) is null) throw new InvalidDataException($"server-rules.xml: premium_shop goods {id} is not in BMShopGoods");
            if ((string?)g.Attribute("enabled") is not null) enabled[id] = Flag(g, "enabled", true);
            if ((string?)g.Attribute("kina") is { } k)
                prices[id] = long.Parse(k, CultureInfo.InvariantCulture) is var p && p is >= 0 and <= MaxPrice ? p
                    : throw new InvalidDataException($"server-rules.xml: premium_shop goods {id} kina must be 0-{MaxPrice}");
        }
        var perMileage = long.Parse((string?)e.Attribute("quna_per_mileage") ?? "10", CultureInfo.InvariantCulture);
        var randomPerMileage = long.Parse((string?)e.Attribute("random_shop_kina_per_mileage") ?? "200", CultureInfo.InvariantCulture);
        if (perMileage < 0 || randomPerMileage < 0) throw new InvalidDataException("server-rules.xml: premium_shop mileage ratios must be 0 or more (0 = none earned)");
        return new PremiumShopRules(Flag(e, "enabled", true), rate, cash, mileage, Flag(e, "mirror_quna", true), enabled, prices, categories,
            Flag(e, "sell_off_sale", true), Flag(e, "mirror_mileage", true), perMileage, randomPerMileage);
    }
}

/// <summary>
/// The staff panel's market settings (server setting "premium_shop", JSON): on/off, Kina per Quna, goods switched on or off and
/// own Kina prices. Missing fields keep the server-rules.xml value.
/// </summary>
public sealed record PremiumShopSettings(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("kinaPerQuna")] long? KinaPerQuna,
    [property: JsonPropertyName("goods")] Dictionary<string, bool>? Goods,
    [property: JsonPropertyName("prices")] Dictionary<string, long>? Prices,
    [property: JsonPropertyName("sellOffSale")] bool? SellOffSale = null,
    [property: JsonPropertyName("mirrorMileage")] bool? MirrorMileage = null,
    [property: JsonPropertyName("qunaPerMileage")] long? QunaPerMileage = null,
    [property: JsonPropertyName("randomKinaPerMileage")] long? RandomKinaPerMileage = null)
{
    /// <summary>The settings, checked (unknown goods, a rate or price out of range: an InvalidDataException).</summary>
    public static PremiumShopSettings Parse(string json)
    {
        var s = JsonSerializer.Deserialize<PremiumShopSettings>(json) ?? throw new InvalidDataException("empty settings");
        if (s.KinaPerQuna is < 1 or > PremiumShopRules.MaxKinaPerQuna) throw new InvalidDataException("kinaPerQuna out of range");
        foreach (var key in (s.Goods?.Keys.AsEnumerable() ?? []).Concat(s.Prices?.Keys.AsEnumerable() ?? []))
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || PremiumShopTables.Embedded.Of(id) is null)
                throw new InvalidDataException($"unknown goods {key}");
        if (s.Prices?.Values.Any(p => p is < 0 or > PremiumShopRules.MaxPrice) == true) throw new InvalidDataException("price out of range");
        if (s.QunaPerMileage is < 0 or > 1_000_000 || s.RandomKinaPerMileage is < 0 or > 100_000_000) throw new InvalidDataException("mileage ratio out of range");
        return s;
    }

    public PremiumShopRules Over(PremiumShopRules defaults)
    {
        var enabled = new Dictionary<int, bool>(defaults.GoodsEnabled);
        foreach (var (key, on) in Goods ?? []) enabled[int.Parse(key, CultureInfo.InvariantCulture)] = on;
        var prices = Prices is null ? defaults.KinaPrices : Prices.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
        return defaults with { Enabled = Enabled ?? defaults.Enabled, KinaPerQuna = KinaPerQuna ?? defaults.KinaPerQuna, GoodsEnabled = enabled, KinaPrices = prices,
            SellOffSale = SellOffSale ?? defaults.SellOffSale, MirrorMileage = MirrorMileage ?? defaults.MirrorMileage,
            QunaPerMileage = QunaPerMileage ?? defaults.QunaPerMileage, RandomKinaPerMileage = RandomKinaPerMileage ?? defaults.RandomKinaPerMileage };
    }
}

/// <summary>The rules in force: server-rules.xml, overridden live by the staff panel (the host applies the stored setting).</summary>
public static class PremiumShop
{
    static PremiumShopRules? live;

    public static PremiumShopRules Defaults => ServerRules.Embedded.PremiumShop;
    public static PremiumShopRules Current => Volatile.Read(ref live) ?? Defaults;

    /// <summary>The staff setting (JSON) over the defaults; null or unreadable: the defaults alone. False when the JSON was refused.</summary>
    public static bool Apply(string? json)
    {
        if (json is null) { Volatile.Write(ref live, null); return true; }
        try { Volatile.Write(ref live, PremiumShopSettings.Parse(json).Over(Defaults)); return true; }
        catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or OverflowException) { return false; }
    }
}
