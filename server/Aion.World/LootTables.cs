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
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages);

    readonly LootFile file;
    LootTables(LootFile file) => this.file = file;

    static readonly Lazy<LootTables> embedded = new(() =>
    {
        using var stream = typeof(LootTables).Assembly.GetManifestResourceStream("loot.json")
            ?? throw new InvalidOperationException("Missing embedded game data loot.json");
        return new LootTables(JsonSerializer.Deserialize<LootFile>(stream) ?? throw new InvalidOperationException("Empty loot.json"));
    });

    public static LootTables Embedded => embedded.Value;

    public bool HasLoot(int npcId) => file.Npcs.ContainsKey(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The items one kill drops for a character of a race (1 Light, 2 Dark), merged by item.</summary>
    public List<(int ItemId, long Count)> Roll(int npcId, int race, Random rng)
    {
        var drops = new Dictionary<int, long>();
        void Add(long item, long count) { if (count > 0) drops[(int)item] = drops.GetValueOrDefault((int)item) + count; }
        long Count(long min, long max) => min >= max ? min : min + (long)(rng.NextDouble() * (max - min + 1));
        bool Hit(long chance) => rng.NextDouble() * 100_000_000 < chance;
        foreach (var name in file.Npcs.GetValueOrDefault(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [])
        {
            if (!file.Tables.TryGetValue(name, out var table) || table.Race != 0 && table.Race != race) continue;
            foreach (var item in table.Items)
                if (item.Length >= 4 && Hit(item[1])) Add(item[0], Count(item[2], item[3]));
            foreach (var package in table.Packages)
            {
                if (package.Length < 3 || !Hit(package[1].GetInt64()) || !file.Packages.TryGetValue(package[0].GetString()!, out var entries) || entries.Length == 0) continue;
                for (var n = 0; n < Math.Max(1, package[2].GetInt32()); n++)
                {
                    var pick = rng.NextDouble() * entries.Sum(e => e[1]);
                    foreach (var e in entries)
                    {
                        if ((pick -= e[1]) >= 0) continue;
                        Add(e[0], Count(e[2], e[3]));
                        break;
                    }
                }
            }
        }
        return drops.Select(d => (d.Key, d.Value)).ToList();
    }
}
