using System.Text.Json;

namespace Aion.World;

/// <summary>The client's EStat ids by name (tools/data/stat-ids.json from the usmap enum, tools/stat_ids.py), loaded once.</summary>
public static class StatIds
{
    sealed record File(Dictionary<string, int> Stats);

    static readonly Lazy<IReadOnlyDictionary<string, int>> ids = new(() =>
    {
        using var stream = typeof(StatIds).Assembly.GetManifestResourceStream("stat-ids.json")
            ?? throw new InvalidOperationException("Missing embedded game data stat-ids.json");
        return JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Stats
            ?? throw new InvalidDataException("stat-ids.json");
    });

    public static int? Of(string name) => ids.Value.TryGetValue(name, out var id) ? id : null;
    public static int Count => ids.Value.Count;

    static readonly Lazy<Dictionary<int, string>> names = new(() => ids.Value.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key));

    /// <summary>The stat name of an EStat id (the first name when several share it), or null.</summary>
    public static string? Name(int id) => names.Value.GetValueOrDefault(id);
}
