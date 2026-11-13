using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Monster loot from tools/data/loot.json (tools/loot_data.py: RewardData, RewardItem, RewardPackage,
/// RewardPackageItem). An NPC (RewardData ID = NpcData ID) has reward tables; every table entry is an
/// independent chance of 1e8 (items with a count range, packages that pick one item by weight).
/// CANDIDATE roll reading (tables often sum above 1e8). Prologue monsters (Poeta) have no tables.
/// </summary>
public sealed class LootTables
{
    sealed record Table(
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("items")] long[][] Items,
        [property: JsonPropertyName("packages")] JsonElement[][] Packages);

    sealed record LootFile(
        [property: JsonPropertyName("npcs")] Dictionary<string, string[]> Npcs,
        [property: JsonPropertyName("tables")] Dictionary<string, Table> Tables,
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages,
        [property: JsonPropertyName("levelscale")] Dictionary<string, JsonElement[]>? LevelScale = null,
        [property: JsonPropertyName("groups")] Dictionary<string, JsonElement[][]>? Groups = null,
        [property: JsonPropertyName("world_level_unlock")] int WorldLevelUnlock = 0,
        [property: JsonPropertyName("drop_radius")] Dictionary<string, int[]>? DropRadius = null,
        [property: JsonPropertyName("ascension")] Dictionary<string, AscensionRow[]>? Ascension = null);

    /// <summary>AscensionReward: a grade's reward for a race (1 Light, 2 Dark) and class: exp and items [[item ID, count]].</summary>
    public sealed record AscensionRow(
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("class")] string Class,
        [property: JsonPropertyName("exp")] long Exp,
        [property: JsonPropertyName("items")] long[][] Items);

    /// <summary>A level-scaled NPC (RewardData GroupLevelscaleRewardName): its group, the fixed table at its own level, and its
    /// NpcData LevelScaleType / WorldLevelScaleType (0 None, 1 Exceed, 2 All).</summary>
    public sealed record LevelScaled(string Group, string Fixed, int ScaleType, int WorldScaleType);

    public const int ScaleNone = 0, ScaleExceed = 1, ScaleAll = 2;

    readonly LootFile file;
    LootTables(LootFile file) => this.file = file;

    static readonly Lazy<LootTables> embedded = new(() =>
    {
        using var stream = typeof(LootTables).Assembly.GetManifestResourceStream("loot.json")
            ?? throw new InvalidOperationException("Missing embedded game data loot.json");
        var file = JsonSerializer.Deserialize<LootFile>(stream) ?? throw new InvalidOperationException("Empty loot.json");
        // Reward tables kept outside loot.json: the hidden cubes' key rewards (world-pools.json "tables", tools/world_objects.py pools).
        using var pools = typeof(LootTables).Assembly.GetManifestResourceStream("world-pools.json");
        if (pools is not null && JsonSerializer.Deserialize<ExtraFile>(pools)?.Tables is { } extra)
            foreach (var (name, table) in extra) file.Tables.TryAdd(name, table);
        return new LootTables(file);
    });

    sealed record ExtraFile([property: JsonPropertyName("tables")] Dictionary<string, Table>? Tables);

    /// <summary>Whether a reward table is known (loot.json or world-pools.json).</summary>
    public bool HasTable(string name) => file.Tables.ContainsKey(name);

    public static LootTables Embedded => embedded.Value;

    public bool HasLoot(int npcId) => file.Npcs.ContainsKey(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The items one kill (or chest, Empyrean Trace, object reward) gives a character of a race (1 Light, 2 Dark), merged by item.
    /// Only that faction's rewards (FactionRewards.cs): a table's race rows replace its merged packages (the treasure chests
    /// reward_manual_TreasureBox_*: RewardPackage Light / Dark rows), an item of the other faction becomes its twin or is left out
    /// before the roll (a package's remaining weights are its new total; an independent chance of a left-out item never hits).
    /// </summary>
    public List<(int ItemId, long Count)> Roll(int npcId, int race, Random rng) => Roll(npcId, race, rng, 0);

    /// <summary>
    /// <see cref="Roll(int, int, Random)"/> for a killer of a level: a level-scaled NPC (<see cref="ScaledLevel"/>) rolls its
    /// group's table at the scaled level (LevelScaleDropGroup) in place of the fixed table at its own level; 0 = no killer level.
    /// </summary>
    public List<(int ItemId, long Count)> Roll(int npcId, int race, Random rng, int killerLevel) =>
        RollTables(TablesFor(npcId, killerLevel), race, rng);

    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The reward tables a kill of this NPC rolls for a killer of a level (0: the fixed tables).</summary>
    public IReadOnlyList<string> TablesFor(int npcId, int killerLevel = 0)
    {
        var names = file.Npcs.GetValueOrDefault(Key(npcId)) ?? [];
        if (killerLevel <= 0 || LevelScaleOf(npcId) is not { } scaled || ScaledLevel(npcId, killerLevel) is not { } at
            || GroupTable(scaled.Group, at) is not { } table || table == scaled.Fixed)
            return names;
        // RewardData: BasicRewardName = the group's table at the NPC's level + LevelscaleBasicRewardName (tools/loot_data.py checks
        // it), so a scaled kill swaps that one table and keeps the rest (RewardName, the _key tables).
        return [.. names.Where(n => n != scaled.Fixed), table];
    }

    /// <summary>The level-scaled drop data of an NPC (RewardData GroupLevelscaleRewardName), or null.</summary>
    public LevelScaled? LevelScaleOf(int npcId) =>
        file.LevelScale?.GetValueOrDefault(Key(npcId)) is [var group, var fixedTable, var type, var world]
            ? new LevelScaled(group.GetString()!, fixedTable.GetString()!, type.GetInt32(), world.GetInt32()) : null;

    /// <summary>The NPC's own level, read from its fixed table's suffix (group_level; equal to NpcData Level for all 1,058).</summary>
    public int? NpcLevel(int npcId) =>
        LevelScaleOf(npcId) is { } s && int.TryParse(s.Fixed[(s.Group.Length + 1)..], out var level) ? level : null;

    /// <summary>World Level (ContentsUnlock WorldLevelScale) opens at this character level (45 in the 11-09 data).</summary>
    public int WorldLevelUnlock => file.WorldLevelUnlock;

    /// <summary>
    /// The level a kill's basic drops are scaled to, or null when they stay at the NPC's own level. CANDIDATE reading of the
    /// client data: the NPC's LevelScaleType applies always; its WorldLevelScaleType once the killer has World Level
    /// (ContentsUnlock WorldLevelScale, level 45). Exceed: the killer's level when above the NPC's; All: the killer's level.
    /// OBSERVED once (rec 20261107-212241): a level-45 character killing the level-27 2400336 (World Exceed) got
    /// equip_ar_Helmet_D_c_drop_003a_s, which reward_basic_D_N3 has at levels 10-20 and 45+ but not at 21-44.
    /// </summary>
    public int? ScaledLevel(int npcId, int killerLevel)
    {
        if (LevelScaleOf(npcId) is not { } s || NpcLevel(npcId) is not { } own) return null;
        var type = s.ScaleType != ScaleNone ? s.ScaleType
            : WorldLevelUnlock > 0 && killerLevel >= WorldLevelUnlock ? s.WorldScaleType : ScaleNone;
        var at = type switch { ScaleAll => killerLevel, ScaleExceed => Math.Max(own, killerLevel), _ => own };
        return at == own ? null : at;
    }

    /// <summary>A level-scale group's reward table for a level (LevelScaleDropGroup LevelMin..LevelMax), clamped to its range.</summary>
    public string? GroupTable(string group, int level)
    {
        if (file.Groups?.GetValueOrDefault(group) is not { Length: > 0 } rows) return null;
        foreach (var row in rows)
            if (row is [var min, var max, var name] && level >= min.GetInt32() && level <= max.GetInt32()) return name.GetString();
        var last = rows.MaxBy(r => r[1].GetInt32())!;
        var first = rows.MinBy(r => r[0].GetInt32())!;
        return (level > last[1].GetInt32() ? last : first)[2].GetString();
    }

    /// <summary>The NPC's drop spread (NpcData DropRadiusMin / DropRadiusMax, cm), or null when the data has none.</summary>
    public (int Min, int Max)? DropRadius(int npcId) =>
        file.DropRadius?.GetValueOrDefault(Key(npcId)) is [var min, var max] && max > 0 ? (Math.Min(min, max), max) : null;

    /// <summary>AscensionReward for reaching a grade (2..6) as a race (1 Light, 2 Dark) and class (Gladiator, ...), or null.</summary>
    public AscensionRow? AscensionReward(int grade, int race, string? className) =>
        className is null ? null
            : file.Ascension?.GetValueOrDefault(Key(grade))?.FirstOrDefault(r => r.Race == race && string.Equals(r.Class, className, StringComparison.OrdinalIgnoreCase));

    /// <summary>The items named reward tables give (as <see cref="Roll"/>; a hidden cube's RewardName and BasicKeyRewards tables).</summary>
    public List<(int ItemId, long Count)> RollTables(IEnumerable<string> names, int race, Random rng)
    {
        var faction = FactionRewards.Embedded;
        var drops = new Dictionary<int, long>();
        void Add(long item, long count) { if (count > 0) drops[(int)item] = drops.GetValueOrDefault((int)item) + count; }
        long Count(long min, long max) => min >= max ? min : min + (long)(rng.NextDouble() * (max - min + 1));
        bool Hit(long chance) => rng.NextDouble() * 100_000_000 < chance;
        foreach (var name in names)
        {
            if (!file.Tables.TryGetValue(name, out var table) || table.Race != 0 && table.Race != race) continue;
            var listed = table.Items.Where(i => i.Length >= 4).Select(i => (int)i[0]).ToHashSet();
            foreach (var item in table.Items)
            {
                if (item.Length < 4) continue;
                var kept = faction.ForRace((int)item[0], race, listed);
                // The chance is rolled either way, so the faction's other entries keep their exact odds.
                if (Hit(item[1]) && kept is { } id) Add(id, Count(item[2], item[3]));
            }
            foreach (var package in faction.LootPackages(name, race) ?? table.Packages)
            {
                if (package.Length < 3 || !Hit(package[1].GetInt64())) continue;
                var packageName = package[0].GetString()!;
                var entries = faction.ForRace((IEnumerable<long[]>?)faction.Package(packageName) ?? file.Packages.GetValueOrDefault(packageName) ?? [], race);
                if (entries.Count == 0) continue;
                var total = entries.Sum(e => e.Weight);
                for (var n = 0; n < Math.Max(1, package[2].GetInt32()); n++)
                {
                    var pick = rng.NextDouble() * total;
                    foreach (var e in entries)
                    {
                        if ((pick -= e.Weight) >= 0) continue;
                        Add(e.ItemId, Count(e.Min, e.Max));
                        break;
                    }
                }
            }
        }
        return drops.Select(d => (d.Key, d.Value)).ToList();
    }
}
