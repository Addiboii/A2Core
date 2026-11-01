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
    [property: JsonPropertyName("points")] double[][] Points,
    /// <summary>Subzone.dat UseRide: when false the client refuses CharGetOnRide locally.</summary>
    [property: JsonPropertyName("use_ride")] bool UseRide = true)
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

/// <summary>World-map fog (the clouds on the M map): which group a position uncovers, by map (embedded subzones.json).</summary>
public static class Subzones
{
    public const double HeightSlack = 2000;

    sealed record File([property: JsonPropertyName("groups")] int[] Groups,
                       [property: JsonPropertyName("maps")] Dictionary<string, SubzoneArea[]> Maps);

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

    /// <summary>Field and instance maps that define a subzone by name (EnterSubZone objectives and quest teleports).</summary>
    public static IEnumerable<int> MapsNamed(string name) =>
        data.Value.Maps.Where(p => p.Value.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The fog group uncovered at a position: the highest-priority area containing it, else none.</summary>
    public static int? GroupAt(int mapId, Vector3d at)
    {
        SubzoneArea? best = null;
        foreach (var area in OnMap(mapId))
            if (area.Group > 0 && (best is null || area.Priority > best.Priority) && area.Contains(at)) best = area;
        return best?.Group;
    }

    /// <summary>The internal name of the highest-priority subzone containing a position, if any.</summary>
    public static string? AreaAt(int mapId, Vector3d at) =>
        AreaContaining(mapId, at)?.Name;

    /// <summary>The highest-priority subzone area at a position, if any.</summary>
    public static SubzoneArea? AreaContaining(int mapId, Vector3d at) =>
        OnMap(mapId).Where(a => a.Contains(at)).MaxBy(a => a.Priority);

    /// <summary>Subzone table id at a position (MyCharAppear _subzone_id), else 0.</summary>
    public static int SubzoneIdAt(int mapId, Vector3d at) => AreaContaining(mapId, at)?.Subzone ?? 0;

    /// <summary>Whether Subzone.dat allows mounting at this position (defaults true when unset in export).</summary>
    public static bool AllowsRide(int mapId, Vector3d at) => AreaContaining(mapId, at)?.UseRide ?? true;

    /// <summary>Whether a position lies in a named subzone (EnterSubZone objectives).</summary>
    public static bool InSubzone(int mapId, string name, Vector3d at) =>
        OnMap(mapId).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) && a.Contains(at));
    /// <summary>
    /// A map event's EnterSubZone objective. InstanceLayer_*_Subzone names the whole instance layer the event runs on
    /// (World_D_A_Layer_HQ001_MapEvent on 111025, HQ002, HQ015, PrisonCamp): its volume lives in the layer level, not in
    /// MapData.dat SubzoneVolumeInfoMap, so subzones.json has no polygon for it and the event stayed at stage 1 forever
    /// (HQ2102030 "Crash Site Rendezvous": 0%, Drakana fruit not usable). A run only exists on its own map, so standing on
    /// the layer is being inside it. Exported areas still decide when present.
    /// </summary>
    public static bool InMapEventSubzone(int mapId, string name, Vector3d at) =>
        OnMap(mapId).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            ? InSubzone(mapId, name, at)
            : name.StartsWith("InstanceLayer_", StringComparison.OrdinalIgnoreCase);
}
