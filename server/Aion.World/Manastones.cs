using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Manastone imprinting (tools/data/manastones.json: ImprintMagicStoneProb.dat and Item.dat of build 26262663): the stones and
/// their imprint groups, the items with stone slots (count, Kinah cost, accepted stone groups) and per group the stats a slot
/// can roll, with their weights and per-grade chances (/10000) and values.
/// </summary>
public sealed class ManastoneTables
{
    sealed record File(
        [property: JsonPropertyName("groups")] Dictionary<string, JsonElement[][]> Groups,
        [property: JsonPropertyName("stones")] Dictionary<string, string[]> Stones,
        [property: JsonPropertyName("items")] Dictionary<string, JsonElement[]> Items);

    public sealed record Grade(int Value, int Chance, int Amount);
    public sealed record StatRoll(int Stat, string Name, int Weight, IReadOnlyList<Grade> Grades);
    public sealed record Slots(int Count, long Kina, IReadOnlyList<string> Groups);

    readonly Dictionary<string, List<StatRoll>> groups = [];
    readonly Dictionary<int, (string Group, string EquipGroup)> stones = [];
    readonly Dictionary<int, Slots> items = [];
    readonly Dictionary<int, string> statNames = [];

    ManastoneTables(File file)
    {
        foreach (var (name, rows) in file.Groups)
        {
            var list = groups[name] = [];
            foreach (var r in rows)
            {
                var grades = r[3].EnumerateArray().Select(g => new Grade(g[0].GetInt32(), g[1].GetInt32(), g[2].GetInt32())).ToList();
                list.Add(new StatRoll(r[0].GetInt32(), r[1].GetString()!, r[2].GetInt32(), grades));
                statNames[r[0].GetInt32()] = r[1].GetString()!;
            }
        }
        foreach (var (id, s) in file.Stones) stones[int.Parse(id, CultureInfo.InvariantCulture)] = (s[0], s[1]);
        foreach (var (id, s) in file.Items)
            items[int.Parse(id, CultureInfo.InvariantCulture)] = new Slots(s[0].GetInt32(), s[1].GetInt64(), s[3].EnumerateArray().Select(g => g.GetString()!).ToList());
    }

    static readonly Lazy<ManastoneTables> embedded = new(() =>
    {
        using var stream = typeof(ManastoneTables).Assembly.GetManifestResourceStream("manastones.json")
            ?? throw new InvalidOperationException("Missing embedded game data manastones.json");
        return new ManastoneTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("manastones.json"));
    });

    public static ManastoneTables Embedded => embedded.Value;

    /// <summary>A stone's imprint group and the equipment stone group it fits, else null.</summary>
    public (string Group, string EquipGroup)? Stone(int itemId) => stones.TryGetValue(itemId, out var s) ? s : null;
    /// <summary>An item's stone slots, Kinah per imprint and accepted stone groups, else null.</summary>
    public Slots? Item(int itemId) => items.GetValueOrDefault(itemId);
    /// <summary>The stats a slot can roll in a group.</summary>
    public IReadOnlyList<StatRoll> Group(string name) => groups.TryGetValue(name, out var g) ? g : [];
    /// <summary>The EStat name of a stat id the tables know, else null.</summary>
    public string? StatName(int stat) => statNames.GetValueOrDefault(stat);
    /// <summary>A rolled slot's stat amount (the group's value for that grade), 0 when unknown.</summary>
    public int Amount(int stone, int stat, int grade) =>
        Stone(stone) is { } s ? Group(s.Group).FirstOrDefault(r => r.Stat == stat)?.Grades.FirstOrDefault(g => g.Value == grade)?.Amount ?? 0 : 0;

    /// <summary>
    /// One slot's roll: a stat by its weight, then a grade by its chance. <paramref name="roll"/> returns 0..n-1. Each slot rolls on
    /// its own (OBSERVED: the same stat on two slots in 2026-11-04 163050 i4553, i4870, i5344, i5500).
    /// </summary>
    public (int Stat, int Grade) Roll(string group, Func<int, int> roll)
    {
        var stats = Group(group);
        var pick = roll(stats.Sum(s => s.Weight));
        var stat = stats.First();
        foreach (var s in stats)
        {
            if (pick < s.Weight) { stat = s; break; }
            pick -= s.Weight;
        }
        var options = stat.Grades.Where(g => g.Chance > 0).ToList();
        var at = roll(options.Sum(g => g.Chance));
        foreach (var g in options)
        {
            if (at < g.Chance) return (stat.Stat, g.Value);
            at -= g.Chance;
        }
        return (stat.Stat, options.Last().Value);
    }
}
