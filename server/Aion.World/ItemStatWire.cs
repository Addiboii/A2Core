using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>EStat name ↔ wire id (tools/data/item-stat-wire.json, tools/gear_stat_wire.py).</summary>
public sealed class ItemStatWire
{
    sealed record File([property: JsonPropertyName("by_name")] Dictionary<string, int> ByName);

    readonly Dictionary<string, int> byName;
    readonly Dictionary<int, string> byWire;

    ItemStatWire(File file)
    {
        byName = file.ByName;
        byWire = file.ByName.ToDictionary(p => p.Value, p => p.Key);
    }

    static readonly Lazy<ItemStatWire> embedded = new(() =>
    {
        using var stream = typeof(ItemStatWire).Assembly.GetManifestResourceStream("item-stat-wire.json")
            ?? throw new InvalidOperationException("Missing embedded game data item-stat-wire.json");
        return new ItemStatWire(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty item-stat-wire.json"));
    });

    public static ItemStatWire Embedded => embedded.Value;

    public int WireOf(string stat) => byName.GetValueOrDefault(stat);

    public string? NameOf(int wire) => byWire.GetValueOrDefault(wire);

    public bool Known(string stat) => byName.ContainsKey(stat);
}
