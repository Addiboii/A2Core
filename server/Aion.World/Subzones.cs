using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A subzone area (tools/subzones.py): a 2D polygon between two heights and the world-map fog group it uncovers.</summary>
public sealed record SubzoneArea(
    [property: JsonPropertyName("subzone")] int Subzone,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("z")] double[] Z,
    [property: JsonPropertyName("points")] double[][] Points)
{
    /// <summary>Whether a position lies in the area (even-odd polygon test; heights with some slack).</summary>
    public bool Contains(Vector3d at)
    {
        if (Z.Length == 2 && (at.Z < Z[0] - Subzones.HeightSlack || at.Z > Z[1] + Subzones.HeightSlack)) return false;
        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            double xi = Points[i][0], yi = Points[i][1], xj = Points[j][0], yj = Points[j][1];
            if (yi > at.Y != yj > at.Y && at.X < (xj - xi) * (at.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// A map's default subzone (tools/subzones.py): Map.dat DefaultSubzoneID with its Subzone.dat Name and fog group (OBSERVED,
/// build 26262663 tables). Used for a position no subzone volume contains (CANDIDATE: what the field's name suggests).
/// </summary>
public sealed record DefaultSubzone(
    [property: JsonPropertyName("subzone")] int Subzone,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("group")] int Group);

/// <summary>World-map fog (the clouds on the M map): which group a position uncovers, by map (embedded subzones.json).</summary>
public static class Subzones
{
    public const double HeightSlack = 2000;

    sealed record File([property: JsonPropertyName("groups")] int[] Groups,
                       [property: JsonPropertyName("maps")] Dictionary<string, SubzoneArea[]> Maps,
                       [property: JsonPropertyName("defaults")] Dictionary<string, DefaultSubzone>? Defaults = null);

    static readonly Lazy<File> data = new(() =>
    {
        using var stream = typeof(Subzones).Assembly.GetManifestResourceStream("subzones.json")
            ?? throw new InvalidOperationException("subzones.json is not embedded");
        return JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("subzones.json");
    });

    /// <summary>Every fog group any subzone uncovers (GM RevealMap).</summary>
    public static IReadOnlyList<int> AllGroups => data.Value.Groups;

    public static IReadOnlyList<SubzoneArea> OnMap(int mapId) =>
        data.Value.Maps.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];

    /// <summary>The map's default subzone (Map.dat DefaultSubzoneID), if the map is in Map.dat.</summary>
    public static DefaultSubzone? DefaultOf(int mapId) =>
        data.Value.Defaults?.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Whether no subzone volume of the map contains a position (then the map's default subzone applies).</summary>
    static bool Outside(int mapId, Vector3d at) => !OnMap(mapId).Any(a => a.Contains(at));

    /// <summary>The fog group uncovered at a position: the highest-priority area containing it with a group; outside every
    /// area, the default subzone's group (CANDIDATE); else none.</summary>
    public static int? GroupAt(int mapId, Vector3d at)
    {
        SubzoneArea? best = null;
        var any = false;
        foreach (var area in OnMap(mapId))
        {
            if (!area.Contains(at)) continue;
            any = true;
            if (area.Group > 0 && (best is null || area.Priority > best.Priority)) best = area;
        }
        if (best is not null) return best.Group;
        return !any && DefaultOf(mapId) is { Group: > 0 } fallback ? fallback.Group : null;
    }

    /// <summary>The internal name of the highest-priority subzone containing a position; outside every area, the map's
    /// default subzone's name; else none.</summary>
    public static string? AreaAt(int mapId, Vector3d at) =>
        OnMap(mapId).Where(a => a.Contains(at)).MaxBy(a => a.Priority)?.Name ?? DefaultOf(mapId)?.Name;

    /// <summary>Whether a position lies in a named subzone (EnterSubZone objectives, SubZone conditions): a volume of that
    /// name contains it; or no volume does and the name is the map's default subzone (CANDIDATE; the Asmodian layers'
    /// InstanceLayer_World_D_A_Layer_HQ001_Subzone has no volume); or, for an instance layer, an area of its base map it overlays
    /// (World_L_A_Layer_District_EP3_5 waits for Verteron's VerteronCitadelRuin_Subzone).</summary>
    public static bool InSubzone(int mapId, string name, Vector3d at)
    {
        static bool Named(SubzoneArea a, string n) => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase);
        var areas = OnMap(mapId);
        if (areas.Any(a => Named(a, name) && a.Contains(at))) return true;
        if (DefaultOf(mapId) is { Name: { } fallback } && string.Equals(fallback, name, StringComparison.OrdinalIgnoreCase) && Outside(mapId, at)) return true;
        return !areas.Any(a => Named(a, name)) && MapEventTables.Embedded.Volume(mapId) is { } layer
            && OnMap(layer.Base).Any(a => Named(a, name) && a.Contains(at));
    }

    /// <summary>The map a named subzone is on and a point inside it (its polygon centre), so a quest "move" can lead there; null if
    /// unknown. Used for EnterSubZone quest objectives whose subzone is on another map (e.g. DreamCrossroad_Subzone on map 200001).</summary>
    public static (int Map, Vector3d Center)? Locate(string name)
    {
        foreach (var (mapKey, areas) in data.Value.Maps)
            foreach (var a in areas)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(mapKey, System.Globalization.CultureInfo.InvariantCulture, out var map))
                {
                    double cx = a.Points.Length > 0 ? a.Points.Average(p => p[0]) : 0;
                    double cy = a.Points.Length > 0 ? a.Points.Average(p => p[1]) : 0;
                    double cz = a.Z.Length == 2 ? (a.Z[0] + a.Z[1]) / 2 : 0;
                    return (map, new Vector3d(cx, cy, cz));
                }
        return null;
    }
}
