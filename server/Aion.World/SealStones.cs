using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Seal stones (tools/data/sealstones.json: SealStone.dat and Item.dat of build 26262663): the stats a registered stone adds.</summary>
public sealed class SealStoneTables
{
    sealed record File(
        [property: JsonPropertyName("effects")] Dictionary<string, JsonElement[][]> Effects,
        [property: JsonPropertyName("stones")] Dictionary<string, string> Stones);

    readonly Dictionary<int, IReadOnlyList<(int Stat, string Name, int Value)>> stones = [];

    SealStoneTables(File file)
    {
        foreach (var (item, effect) in file.Stones)
            if (file.Effects.TryGetValue(effect, out var rows))
                stones[int.Parse(item, CultureInfo.InvariantCulture)] = rows.Select(r => (r[0].GetInt32(), r[1].GetString()!, r[2].GetInt32())).ToList();
    }

    static readonly Lazy<SealStoneTables> embedded = new(() =>
    {
        using var stream = typeof(SealStoneTables).Assembly.GetManifestResourceStream("sealstones.json")
            ?? throw new InvalidOperationException("Missing embedded game data sealstones.json");
        return new SealStoneTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("sealstones.json"));
    });

    public static SealStoneTables Embedded => embedded.Value;

    /// <summary>Is this item a seal stone?</summary>
    public bool IsStone(int itemId) => stones.ContainsKey(itemId);
    /// <summary>The stats a stone adds (EStat id, name, value), empty for any other item.</summary>
    public IReadOnlyList<(int Stat, string Name, int Value)> Stats(int itemId) => stones.TryGetValue(itemId, out var s) ? s : [];
    /// <summary>One stat of a stone by EStat name, 0 when it has none.</summary>
    public int Stat(int itemId, string name) => Stats(itemId).Where(s => s.Name == name).Sum(s => s.Value);
}
