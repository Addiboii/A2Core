using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Gear enhancement tables from the client (tools/data/enhance.json, tools/enhance_data.py): amplification (ExceedEnchant),
/// Enhance Potential (ItemSurpass), their AdditionalStat lines, manastone rolls (ImprintMagicStoneProb), succession
/// (ItemSuccession, ItemSuccessionProb), soul add (ItemSoulAdd) and godstones (GodstoneData, GodstoneCombine, GodstoneGacha).
/// </summary>
public sealed class EnhanceTables
{
    /// <summary>One try from a level: chance and failure correction out of 10000, levels lost on a failure, Kina, items, and the
    /// AdditionalStat name of that level (what the item has while at it).</summary>
    public sealed record Step(int Success, int FailCorrection, int FailPenalty, long Kina, IReadOnlyList<(int ItemId, int Count)> Costs, string Stats);
    public sealed record StoneStat(string Stat, int Weight, IReadOnlyList<(int Value, int Chance)> Grades);
    public sealed record SuccessionCost(int Count, long Kina, IReadOnlyList<(int ItemId, int Count)> Costs);
    public sealed record SoulAdd(int Success, long Kina, int ItemId, int Count);
    public sealed record Godstone(int Id, string Category, string Grade, int Abnormal, int Type, bool Combinable);

    readonly Dictionary<string, List<Step>> exceed = [], surpass = [];
    readonly Dictionary<string, List<(string Stat, int Value)>> additional = [];
    readonly Dictionary<string, List<StoneStat>> imprint = [];
    readonly Dictionary<string, Dictionary<string, List<SuccessionCost>>> succession = [];
    readonly Dictionary<string, List<int>> successionProb = [];
    readonly Dictionary<string, SoulAdd> soulAdd = [];
    readonly Dictionary<int, Godstone> godstones = [];
    readonly Dictionary<string, List<(int Id, int Weight)>> combine = [], gacha = [];
    readonly Dictionary<int, (string Succession, string SoulAdd)> items = [];

    static readonly Lazy<EnhanceTables> embedded = new(() =>
    {
        using var stream = typeof(EnhanceTables).Assembly.GetManifestResourceStream("enhance.json")
            ?? throw new InvalidOperationException("Missing embedded game data enhance.json");
        return new EnhanceTables(JsonDocument.Parse(stream).RootElement);
    });

    public static EnhanceTables Embedded => embedded.Value;

    static List<(int, int)> Costs(JsonElement e) => e.EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32())).ToList();

    EnhanceTables(JsonElement root)
    {
        foreach (var g in root.GetProperty("exceed").EnumerateObject())
            exceed[g.Name] = g.Value.EnumerateArray().Select(r => new Step(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt64(), Costs(r[4]), r[5].GetString()!)).ToList();
        foreach (var g in root.GetProperty("surpass").EnumerateObject())
            surpass[g.Name] = g.Value.EnumerateArray().Select(r => new Step(r[0].GetInt32(), r[1].GetInt32(), 0, r[2].GetInt64(), Costs(r[3]), r[4].GetString()!)).ToList();
        foreach (var a in root.GetProperty("additional").EnumerateObject())
            additional[a.Name] = a.Value.EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetInt32())).ToList();
        foreach (var g in root.GetProperty("imprint").EnumerateObject())
            imprint[g.Name.ToLowerInvariant()] = g.Value.EnumerateArray().Select(s => new StoneStat(s[0].GetString()!, s[1].GetInt32(),
                s[2].EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToList())).ToList();
        foreach (var g in root.GetProperty("succession").EnumerateObject())
            succession[g.Name] = g.Value.EnumerateObject().ToDictionary(s => s.Name,
                s => s.Value.EnumerateArray().Select(r => new SuccessionCost(r[0].GetInt32(), r[1].GetInt64(), Costs(r[2]))).ToList());
        foreach (var t in root.GetProperty("succession_prob").EnumerateObject())
            successionProb[t.Name] = t.Value.EnumerateArray().Select(v => v.GetInt32()).ToList();
        foreach (var g in root.GetProperty("soul_add").EnumerateObject())
            soulAdd[g.Name] = new SoulAdd(g.Value[0].GetInt32(), g.Value[1].GetInt64(), g.Value[2].GetInt32(), g.Value[3].GetInt32());
        foreach (var g in root.GetProperty("godstones").EnumerateObject())
        {
            var id = int.Parse(g.Name, System.Globalization.CultureInfo.InvariantCulture);
            godstones[id] = new Godstone(id, g.Value[0].GetString()!, g.Value[1].GetString()!, g.Value[2].GetInt32(), g.Value[3].GetInt32(), g.Value[4].GetBoolean());
        }
        foreach (var g in root.GetProperty("godstone_combine").EnumerateObject())
            combine[g.Name] = g.Value.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToList();
        foreach (var g in root.GetProperty("godstone_gacha").EnumerateObject())
            gacha[g.Name] = g.Value.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToList();
        foreach (var i in root.GetProperty("items").EnumerateObject())
            items[int.Parse(i.Name, System.Globalization.CultureInfo.InvariantCulture)] = (i.Value[0].GetString()!, i.Value[1].GetString()!);
    }

    /// <summary>The amplification try from exceed level <paramref name="level"/> (0 = the first past the enchant maximum); null at the top.</summary>
    public Step? Exceed(string? group, int level) => At(exceed, group, level) is { Success: > 0 } s ? s : null;
    /// <summary>Every ExceedEnchant group name.</summary>
    public IEnumerable<string> ExceedGroups => exceed.Keys;
    /// <summary>The highest exceed level of a group (the row whose success is 0), 0 when the group is unknown.</summary>
    public int ExceedMax(string? group) => group is not null && exceed.TryGetValue(group, out var rows) ? rows.Count - 1 : 0;
    /// <summary>The stats of an item at an exceed level (AdditionalStat of that level's row).</summary>
    public IReadOnlyList<(string Stat, int Value)> ExceedStats(string? group, int level) =>
        level > 0 && group is not null && exceed.TryGetValue(group, out var rows) ? Stats(rows[Math.Min(level, rows.Count - 1)].Stats) : [];

    public bool HasSurpass(string? group) => group is not null && surpass.ContainsKey(group);
    public Step? Surpass(string? group, int level) => At(surpass, group, level) is { Success: > 0 } s ? s : null;
    public IReadOnlyList<(string Stat, int Value)> SurpassStats(string? group, int level) =>
        level > 0 && group is not null && surpass.TryGetValue(group, out var rows) ? Stats(rows[Math.Min(level, rows.Count - 1)].Stats) : [];

    public IReadOnlyList<(string Stat, int Value)> Stats(string name) => additional.TryGetValue(name, out var s) ? s : [];

    /// <summary>The manastone pool of an ImprintGroupName (case-insensitive), null when unknown.</summary>
    public IReadOnlyList<StoneStat>? Imprint(string group) => imprint.GetValueOrDefault(group.ToLowerInvariant());

    /// <summary>The succession groups of an item (Item.dat SuccessionGroup, SoulAddGroup), null when it has none.</summary>
    public string? SuccessionGroupOf(int itemId) => items.TryGetValue(itemId, out var g) && g.Succession != "None" ? g.Succession : null;
    public string? SoulAddGroupOf(int itemId) => items.TryGetValue(itemId, out var g) && g.SoulAdd != "None" ? g.SoulAdd : null;

    /// <summary>The cost of a succession into <paramref name="group"/> from <paramref name="source"/> at a character's succession count
    /// (the last row covers higher counts); null when the source group cannot pass into the target.</summary>
    public SuccessionCost? Succession(string group, string source, int count) =>
        succession.TryGetValue(group, out var sources) && sources.TryGetValue(source, out var rows) && rows.Count > 0
            ? rows.LastOrDefault(r => r.Count <= count) ?? rows[0] : null;

    public int SuccessionChance(string type, int count) =>
        successionProb.TryGetValue(type, out var p) && p.Count > 0 ? p[Math.Clamp(count, 0, p.Count - 1)] : 10_000;

    public SoulAdd? SoulAddOf(string? group) => group is not null ? soulAdd.GetValueOrDefault(group) : null;

    public Godstone? GodstoneOf(int id) => godstones.GetValueOrDefault(id);
    public IReadOnlyList<(int Id, int Weight)> GodstoneGacha(string group) => gacha.GetValueOrDefault(group) ?? [];
    /// <summary>The combine pool for materials of a grade (EItemGrade name: Rare, Legend, Unique).</summary>
    public IReadOnlyList<(int Id, int Weight)> GodstoneCombine(string grade) => combine.GetValueOrDefault(grade) ?? [];

    static Step? At(Dictionary<string, List<Step>> table, string? group, int level) =>
        group is not null && level >= 0 && table.TryGetValue(group, out var rows) && level < rows.Count ? rows[level] : null;

    /// <summary>A weighted pick (weights &gt; 0) with a roll source; default when the list is empty.</summary>
    public static T Pick<T>(IReadOnlyList<T> list, Func<T, int> weight, Random rng)
    {
        var total = list.Sum(x => (long)Math.Max(0, weight(x)));
        if (total <= 0) return list.Count > 0 ? list[rng.Next(list.Count)] : default!;
        var roll = rng.NextInt64(total);
        foreach (var x in list)
        {
            long w = Math.Max(0, weight(x));
            if (roll < w) return x;
            roll -= w;
        }
        return list[^1];
    }
}
