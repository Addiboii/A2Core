using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Monolith.dat rows and env→monolith_id map (tools/data/monolith-table.json, tools/monolith_data.py).</summary>
public sealed class MonolithTables
{
    sealed record Row(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("race")] int Race);

    sealed record File(
        [property: JsonPropertyName("monoliths")] Row[] Monoliths,
        [property: JsonPropertyName("env_to_monolith_id")] Dictionary<string, int>? EnvToMonolithId);

    readonly File file;
    readonly Dictionary<int, int> envToId;
    readonly Dictionary<int, int[]> idsByRace;

    MonolithTables(File file)
    {
        this.file = file;
        envToId = new Dictionary<int, int>();
        foreach (var (key, id) in file.EnvToMonolithId ?? [])
            if (int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var env))
                envToId[env] = id;
        idsByRace = file.Monoliths.GroupBy(m => m.Race).ToDictionary(g => g.Key, g => g.Select(m => m.Id).OrderBy(x => x).ToArray());
    }

    static readonly Lazy<MonolithTables> embedded = new(() =>
    {
        using var stream = typeof(MonolithTables).Assembly.GetManifestResourceStream("monolith-table.json")
            ?? throw new InvalidOperationException("monolith-table.json is not embedded");
        return new MonolithTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("monolith-table.json"));
    });

    public static MonolithTables Embedded => embedded.Value;

    /// <summary>Client Monolith.dat id for an EnvObjData id (falls back to env when unmapped).</summary>
    public int MonolithIdForEnv(int envId) => envToId.GetValueOrDefault(envId, envId);

    public IReadOnlyList<int> IdsForRace(int race) => idsByRace.GetValueOrDefault(race, []);
}
