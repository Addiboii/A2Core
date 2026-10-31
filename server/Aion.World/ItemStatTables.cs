using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>EStat wire map and combat stat lists (tools/data/item-stats.json, tools/item_stat_data.py).</summary>
public sealed class ItemStatTables
{
    sealed record File(
        [property: JsonPropertyName("stat_wire")] Dictionary<string, string> StatWire,
        [property: JsonPropertyName("combat_stats")] string[] CombatStats,
        [property: JsonPropertyName("percent_stats")] string[] PercentStats,
        [property: JsonPropertyName("aliases")] Dictionary<string, string>? Aliases,
        [property: JsonPropertyName("rolled_fields")] string[]? RolledFields);

    readonly File file;
    readonly Dictionary<string, int> nameToWire;
    readonly Dictionary<string, string> wireToName;
    readonly HashSet<string> percent;
    readonly Dictionary<string, string> aliasToCanonical;

    ItemStatTables(File file)
    {
        this.file = file;
        wireToName = file.StatWire;
        nameToWire = file.StatWire.ToDictionary(p => p.Value, p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture));
        percent = file.PercentStats.ToHashSet(StringComparer.Ordinal);
        aliasToCanonical = file.Aliases ?? [];
    }

    static readonly Lazy<ItemStatTables> embedded = new(() =>
    {
        using var stream = typeof(ItemStatTables).Assembly.GetManifestResourceStream("item-stats.json")
            ?? throw new InvalidOperationException("Missing embedded game data item-stats.json");
        return new ItemStatTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty item-stats.json"));
    });

    public static ItemStatTables Embedded => embedded.Value;

    public IReadOnlyList<string> CombatStats => file.CombatStats;

    public bool IsPercent(string stat) => percent.Contains(stat);

    public string Canonical(string stat) => aliasToCanonical.GetValueOrDefault(stat, stat);

    public int? WireOf(string stat)
    {
        stat = Canonical(stat);
        return nameToWire.TryGetValue(stat, out var wire) ? wire : null;
    }

    public string? NameOfWire(int wire) => wireToName.GetValueOrDefault(wire.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public IEnumerable<string> KeysFor(string stat)
    {
        stat = Canonical(stat);
        yield return stat;
        foreach (var (alias, canonical) in aliasToCanonical)
            if (canonical == stat) yield return alias;
    }

    /// <summary>Per-instance stat rolls (rolled_stats / upgrades), not Item.dat MainStats.</summary>
    public int SumInstance(JsonElement rolled, JsonElement upgrades, string stat)
    {
        stat = Canonical(stat);
        var total = 0;
        if (rolled.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in file.RolledFields ?? [])
                if (rolled.TryGetProperty(field, out var node)) total += SumNode(node, stat);
            total += SumFlatMap(rolled, stat);
        }
        if (upgrades.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in file.RolledFields ?? [])
                if (upgrades.TryGetProperty(field, out var node)) total += SumNode(node, stat);
            if (upgrades.TryGetProperty("magicstones", out var stones)) total += SumNode(stones, stat);
        }
        return total;
    }

    int SumFlatMap(JsonElement obj, string stat)
    {
        if (!obj.TryGetProperty(stat, out var direct) || !direct.TryGetInt32(out var value)) return 0;
        return value;
    }

    int SumNode(JsonElement node, string stat)
    {
        return node.ValueKind switch
        {
            JsonValueKind.Array when node.GetArrayLength() > 0 && node[0].ValueKind == JsonValueKind.String
                => SumPairs(node, stat),
            JsonValueKind.Array => SumPairs(node, stat),
            JsonValueKind.Object => SumFlatMap(node, stat),
            _ => 0,
        };
    }

    int SumPairs(JsonElement array, string stat)
    {
        var total = 0;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2)
            {
                var (name, value) = ParsePair(entry[0], entry[1]);
                if (name is not null && Canonical(name) == stat) total += value;
                continue;
            }
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("stat", out var s) && entry.TryGetProperty("value", out var v)
                && v.TryGetInt32(out var n))
            {
                var name = s.GetString();
                if (name is not null && Canonical(name) == stat) total += n;
            }
        }
        return total;
    }

    (string? Name, int Value) ParsePair(JsonElement key, JsonElement value)
    {
        if (!value.TryGetInt32(out var n)) return (null, 0);
        if (key.ValueKind == JsonValueKind.String) return (key.GetString(), n);
        if (key.ValueKind == JsonValueKind.Number && key.TryGetInt32(out var wire))
            return (NameOfWire(wire), n);
        return (null, 0);
    }
}
