using System.Text.Json;

namespace Aion.World;

/// <summary>Substance morph recipes and Arcana tables from tools/data/systems3.json (tools/systems3_data.py, client tables).</summary>
public sealed class Systems3Tables
{
    public sealed record Conversion(int Id, int Race, int Product, int ProductCount, int Success, int Critical, int CriticalProduct, int CriticalCount,
        int Fail, int FailProduct, int FailCount, string RegisterType, int RegisterValue, int LimitCharacter, int LimitAccount, int LimitServer, int ResetPeriod,
        IReadOnlyList<(int Item, int Count)> Components)
    {
        /// <summary>Every ingredient (plain, enchanted, replaceable, combined or multi-select): the items the client may give for it.</summary>
        public IReadOnlyList<ConversionSlot> Slots { get; init; } = [];
    }
    /// <summary>One ingredient: its component item (0 for a multi-select equipment slot), count and enchant, and the accepted
    /// items with their own count and minimum enchant (the component itself, replacements, combined items).</summary>
    public sealed record ConversionSlot(int Origin, int Count, int Enchant, IReadOnlyList<(int Item, int Count, int Enchant)> Accepted);
    public sealed record ArcanaCreate(int Id, string Category, string Grade, int Item, int MainMax, int SubMax, int Material, int MaterialCount,
        int MaterialPerMain, int MaterialPerSub, long Kina, long KinaPerMain, long KinaPerSub);
    public sealed record ArcanaItem(int Id, string EnchantGroup, string EffectGroup, IReadOnlyList<(string Stat, int Value)> MainStats, int SubStats, string Grade, string Category);
    public sealed record EnchantStep(int NeedExp, int GetExp, long Kina);

    readonly Dictionary<int, Conversion> conversions = [];
    readonly Dictionary<int, ArcanaCreate> creates = [];
    readonly Dictionary<int, ArcanaItem> arcana = [];
    readonly Dictionary<string, List<EnchantStep>> enchant = [];
    readonly Dictionary<string, Dictionary<int, List<(string Stat, int Value)>>> mainEffect = [];
    readonly List<(string Category, string Class, int Kind, string Stat, int Id)> filters = [];
    public int ConversionTimeMs { get; }
    public int ConversionMaxCount { get; }
    public int EnchantMaterial { get; }
    public int CreateCooltimeMinutes { get; }

    static int I(JsonElement e) => e.GetInt32();

    Systems3Tables(JsonElement root)
    {
        foreach (var p in root.GetProperty("conversions").EnumerateObject())
        {
            var v = p.Value.EnumerateArray().ToArray();
            conversions[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = new(int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                I(v[0]), I(v[1]), I(v[2]), I(v[3]), I(v[4]), I(v[5]), I(v[6]), I(v[7]), I(v[8]), I(v[9]), v[10].GetString() ?? "", I(v[11]), I(v[12]), I(v[13]), I(v[14]),
                I(v[15]), v[16].EnumerateArray().Select(c => (I(c[0]), I(c[1]))).ToList())
            {
                Slots = v.Length > 17 ? v[17].EnumerateArray().Select(s => new ConversionSlot(I(s[0]), I(s[1]), I(s[2]),
                    s[3].EnumerateArray().Select(a => (I(a[0]), I(a[1]), I(a[2]))).ToList())).ToList() : [],
            };
        }
        ConversionTimeMs = root.GetProperty("conversion_time_ms").GetInt32();
        ConversionMaxCount = root.GetProperty("conversion_max_count").GetInt32();
        var a = root.GetProperty("arcana");
        foreach (var p in a.GetProperty("create").EnumerateObject())
        {
            var v = p.Value.EnumerateArray().ToArray();
            creates[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = new(int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                v[0].GetString()!, v[1].GetString()!, I(v[2]), I(v[3]), I(v[4]), I(v[5]), I(v[6]), I(v[7]), I(v[8]), v[9].GetInt64(), v[10].GetInt64(), v[11].GetInt64());
        }
        foreach (var p in a.GetProperty("items").EnumerateObject())
        {
            var v = p.Value.EnumerateArray().ToArray();
            arcana[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = new(int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                v[0].GetString()!, v[1].GetString()!, v[2].EnumerateArray().Select(s => (s[0].GetString()!, I(s[1]))).ToList(), I(v[3]), v[4].GetString()!, v[5].GetString()!);
        }
        foreach (var p in a.GetProperty("enchant").EnumerateObject())
            enchant[p.Name] = p.Value.EnumerateArray().Select(s => new EnchantStep(I(s[0]), I(s[1]), s[2].GetInt64())).ToList();
        foreach (var p in a.GetProperty("main_effect").EnumerateObject())
            mainEffect[p.Name] = p.Value.EnumerateObject().ToDictionary(l => int.Parse(l.Name, System.Globalization.CultureInfo.InvariantCulture),
                l => l.Value.EnumerateArray().Select(s => (s[0].GetString()!, I(s[1]))).ToList());
        foreach (var f in a.GetProperty("stat_filter").EnumerateArray())
            filters.Add((f[0].GetString()!, f[1].GetString()!, I(f[2]), f[3].GetString()!, I(f[4])));
        EnchantMaterial = a.GetProperty("material").GetInt32();
        CreateCooltimeMinutes = a.GetProperty("create_cooltime_min").GetInt32();
    }

    static readonly Lazy<Systems3Tables> embedded = new(() =>
    {
        using var stream = typeof(Systems3Tables).Assembly.GetManifestResourceStream("systems3.json") ?? throw new InvalidOperationException("systems3.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new Systems3Tables(doc.RootElement);
    });
    public static Systems3Tables Embedded => embedded.Value;

    public Conversion? ConversionOf(int id) => conversions.GetValueOrDefault(id);
    public IEnumerable<Conversion> Conversions => conversions.Values;
    public ArcanaCreate? CreateOf(int id) => creates.GetValueOrDefault(id);
    public ArcanaItem? ArcanaOf(int itemId) => arcana.GetValueOrDefault(itemId);
    /// <summary>An enchant group's step at a level (ArcanaEnchant CurrentLevel), or null past its end.</summary>
    public EnchantStep? Step(string group, int level) => enchant.TryGetValue(group, out var steps) && level >= 0 && level < steps.Count ? steps[level] : null;
    public int MaxLevel(string group) => enchant.TryGetValue(group, out var steps) ? steps.Count : 0;
    public IReadOnlyList<(string Stat, int Value)> MainEffect(string group, int level) =>
        mainEffect.TryGetValue(group, out var levels) && levels.TryGetValue(level, out var list) ? list : [];
    /// <summary>A stat a creation may pick (ArcanaStatFilter): its row id for the category (1 main, 2 sub) and class (None = every class).</summary>
    public bool Selectable(string category, int kind, int statId) =>
        filters.Any(f => f.Id == statId && f.Category == category && f.Kind == kind);
}
