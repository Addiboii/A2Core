using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The character list's location (LobbyCharDesc _zone_id): a Subzone.dat ID, shown by the lobby as that subzone's name.
/// OBSERVED (9 official records, 2026-11-06/07): the subzone of the last saved spot, e.g. 1067 HunterCamp_Subzone (Guide Rest Stop)
/// and 1004 CheckpointofGarolik_Subzone on Altgard 1110, 3011 L_Starter_Subzone_Base on Poeta 1000, 10070 Dungeon_L_0016_Subzone in
/// its dungeon; each is the highest-priority subzone area holding the saved position (tools/data/subzones.json). A map with no
/// area there (the instance layers: their volumes are not extracted; official 3040 / 4025 on layers 100001 / 110001) shows its
/// Map.dat default subzone (tools/data/lobby-zones.json, CANDIDATE: the layer's own sub-area is not known to us).
/// </summary>
public static class LobbyZones
{
    sealed record File([property: JsonPropertyName("defaults")] Dictionary<string, int> Defaults);

    static readonly Lazy<Dictionary<int, int>> defaults = new(() =>
    {
        using var stream = typeof(LobbyZones).Assembly.GetManifestResourceStream("lobby-zones.json")
            ?? throw new InvalidOperationException("lobby-zones.json is not embedded");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("lobby-zones.json");
        return file.Defaults.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value);
    });

    /// <summary>A map's default subzone (Map.dat DefaultSubzoneID), 0 when none.</summary>
    public static int DefaultOf(int mapId) => defaults.Value.GetValueOrDefault(mapId);

    /// <summary>
    /// The lobby location of a saved spot: the highest-priority subzone area containing it, else the map's default subzone, else
    /// the race's home map default (Poeta 3000 / Ishalgen 4000) so the list never shows a stale placeholder.
    /// </summary>
    public static int ZoneOf(int? mapId, Vector3d? at, int race = 1)
    {
        if (mapId is { } map && map > 0)
        {
            if (at is { } spot && spot.IsFinite
                && Subzones.OnMap(map).Where(a => a.Subzone > 0 && a.Contains(spot)).MaxBy(a => a.Priority) is { } area)
                return area.Subzone;
            if (DefaultOf(map) is var fallback and > 0) return fallback;
        }
        return DefaultOf(race == 2 ? 1100 : 1000);
    }
}
