using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>
/// Collection stats: wings from the client tables (tools/data/gear-effects.json, GearEffects: the worn wing's Wing.dat EquipStatList,
/// every owned wing's WingEnchantEffect at its enchant level), the skin collection's category steps (the client's SkinCollection.dat,
/// tools/data/closet.json) and the PROVISIONAL pet collection and all-owned skin effect (gamedata/provisional/collection-stats.xml).
/// Loaded once; stats by EStat name.
/// </summary>
public sealed class CollectionStats
{
    public sealed record WingEffect(IReadOnlyDictionary<string, int> Equipped, IReadOnlyDictionary<string, int> Owned);
    readonly Dictionary<int, WingEffect> wings;
    readonly List<(int Level, Dictionary<string, int> Stats)> petLevels;
    readonly Dictionary<string, int> skinStats;
    /// <summary>A skin category: its full collection's points, its stats (the first step's value) and the SkinCollection steps.</summary>
    public sealed record SkinCategory(string Name, int MaxPoints, IReadOnlyList<(string Stat, int PerPoint)> Stats,
        IReadOnlyList<(int Point, IReadOnlyList<(string Stat, int Value)> Stats)> Steps)
    {
        /// <summary>The stats at a number of owned points: every step at or below it.</summary>
        public Dictionary<string, int> At(int points)
        {
            var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (point, lines) in Steps)
            {
                if (point > points) break;
                foreach (var (stat, value) in lines) stats[stat] = stats.GetValueOrDefault(stat) + value;
            }
            return stats;
        }
    }
    readonly List<SkinCategory> skinCategories;
    CollectionStats(Dictionary<int, WingEffect> wings, List<(int, Dictionary<string, int>)> petLevels, Dictionary<string, int> skinStats, List<SkinCategory> skinCategories) =>
        (this.wings, this.petLevels, this.skinStats, this.skinCategories) = (wings, petLevels, skinStats, skinCategories);

    public IReadOnlyList<SkinCategory> SkinCategories => skinCategories;

    static readonly Lazy<CollectionStats> embedded = new(() =>
    {
        XDocument Load(string name)
        {
            using var stream = typeof(CollectionStats).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded game data {name}");
            return XDocument.Load(stream);
        }
        return Parse(GearEffects.Embedded, Load("provisional/collection-stats.xml"));
    });

    public static CollectionStats Embedded => embedded.Value;

    /// <summary>
    /// Every Wing.dat wing: worn = its EquipStatList, owned = its EffectGroup's WingEnchantEffect at level 0 (no Wing.dat row has an
    /// EnchantGroup, so every wing stays at level 0; WingList_NT sends enchant 0). OFFICIAL client data. Until 2026-11-11 the stats
    /// came from the item descriptions (gamedata/wing-effects.xml): 50 wings only, Flight Power 100x too small (+2,000 / +200 where
    /// the table has FPMax 200000 / 20000) and 22 wings with other owned or worn lines than the table.
    /// </summary>
    static Dictionary<int, WingEffect> Wings(GearEffects gear)
    {
        static Dictionary<string, int> Lines(IEnumerable<(string Stat, int Value)> lines) =>
            lines.GroupBy(l => l.Stat, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(l => l.Value), StringComparer.OrdinalIgnoreCase);
        var wings = new Dictionary<int, WingEffect>();
        foreach (var id in gear.WingIds)
            if (gear.WingOf(id) is { } w) wings[id] = new WingEffect(Lines(w.Worn), Lines(gear.WingOwned(id)));
        return wings;
    }

    public static CollectionStats Parse(GearEffects gear, XDocument collections)
    {
        var wings = Wings(gear);
        var root = collections.Root ?? throw new InvalidDataException("collection-stats: empty");
        var pets = (root.Element("pets")?.Elements("level") ?? []).Select(l => (int.Parse((string)l.Attribute("n")!, CultureInfo.InvariantCulture),
            l.Elements("stat").ToDictionary(s => (string)s.Attribute("name")!, s => int.Parse((string)s.Attribute("value")!, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => p.Item1).ToList();
        var skins = root.Element("skins");
        var skinStats = skins?.Element("all_owned")?.Elements("stat").ToDictionary(s => (string)s.Attribute("name")!, s => int.Parse((string)s.Attribute("value")!, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase) ?? [];
        // SkinCollection.dat: one row per collect point reached (500 per category), each adding its stats (10 = 0.1% today).
        var maxPoints = SkinTables.Embedded.MaxPointsByCategory;
        var categories = SkinTables.Closet.Collection.Where(c => maxPoints.GetValueOrDefault(c.Key) > 0)
            .Select(c => new SkinCategory(c.Key, maxPoints[c.Key], c.Value.SelectMany(r => r.Stats).GroupBy(s => s.Stat).Select(g => (g.Key, g.First().Value)).ToList(), c.Value))
            .ToList();
        foreach (var name in wings.Values.SelectMany(w => w.Equipped.Keys.Concat(w.Owned.Keys)).Concat(pets.SelectMany(p => p.Item2.Keys)).Concat(skinStats.Keys)
                     .Concat(categories.SelectMany(c => c.Stats.Select(s => s.Stat))))
            if (StatIds.Of(name) is null) throw new InvalidDataException($"collection stats: unknown stat {name}");
        return new CollectionStats(wings, pets, skinStats, categories);
    }

    public WingEffect? Wing(int wingId) => wings.GetValueOrDefault(wingId);
    public int WingCount => wings.Count;

    /// <summary>One owned pet's stats at its level (the highest listed level at or below it).</summary>
    public IReadOnlyDictionary<string, int> Pet(int level) => petLevels.LastOrDefault(p => p.Level <= level).Stats ?? [];

    /// <summary>
    /// The skin collection's stats for <paramref name="points"/> of <paramref name="maxPoints"/> collection points: the full
    /// collection's stats (the client window at 100%) times the share owned, rounded down.
    /// </summary>
    public IReadOnlyDictionary<string, int> Skins(int points, int maxPoints) =>
        maxPoints <= 0 || points <= 0 ? [] : skinStats.ToDictionary(p => p.Key, p => (int)(p.Value * Math.Min(1.0, points / (double)maxPoints)), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The whole skin collection's stats: the all-owned effect by the share of every point (PROVISIONAL), plus each category's
    /// SkinCollection steps up to its owned points (<paramref name="owned"/> and <paramref name="obtainable"/> are points per
    /// category name; the points are the client's own, so no scaling).
    /// </summary>
    public IReadOnlyDictionary<string, int> Skins(IReadOnlyDictionary<string, int> owned, IReadOnlyDictionary<string, int> obtainable)
    {
        var stats = new Dictionary<string, int>(Skins(owned.Values.Sum(), obtainable.Values.Sum()), StringComparer.OrdinalIgnoreCase);
        foreach (var category in skinCategories)
        {
            var mine = owned.GetValueOrDefault(category.Name);
            if (mine <= 0) continue;
            foreach (var (stat, value) in category.At(mine)) stats[stat] = stats.GetValueOrDefault(stat) + value;
        }
        return stats;
    }
}
