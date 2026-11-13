using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// The client's waypoint actions (tools/route_actions.py -> tools/data/route-actions.json, from MapData.dat WaypointsDataMap
/// Actions): which scripted routes end with the NPC vanishing, and the social actions played at route points.
/// </summary>
public sealed class RouteActions
{
    public sealed record Route(
        [property: JsonPropertyName("points")] int Points,
        [property: JsonPropertyName("despawn")] bool Despawn = false,
        [property: JsonPropertyName("social")] int[][]? Social = null);

    sealed record File([property: JsonPropertyName("maps")] Dictionary<string, Dictionary<string, Route>> Maps,
        [property: JsonPropertyName("persistent_social")] int[]? PersistentSocial = null);

    readonly Dictionary<int, Dictionary<string, Route>> maps;
    readonly HashSet<int> persistentSocial;

    public RouteActions(IReadOnlyDictionary<string, Dictionary<string, Route>> source, IEnumerable<int>? persistent = null)
    {
        maps = source.ToDictionary(m => int.Parse(m.Key, System.Globalization.CultureInfo.InvariantCulture),
            m => new Dictionary<string, Route>(m.Value, StringComparer.OrdinalIgnoreCase));
        persistentSocial = [.. persistent ?? []];
    }

    static readonly Lazy<RouteActions> embedded = new(() =>
    {
        using var stream = typeof(RouteActions).Assembly.GetManifestResourceStream("route-actions.json")
            ?? throw new InvalidOperationException("route-actions.json is not embedded");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("route-actions.json");
        return new RouteActions(file.Maps, file.PersistentSocial);
    });

    public static RouteActions Embedded => embedded.Value;

    public Route? Of(int mapId, string route) => maps.TryGetValue(mapId, out var routes) ? routes.GetValueOrDefault(route) : null;

    /// <summary>
    /// The NPC that walks this route to its end vanishes there (0x3642 act 7). OBSERVED: Elvan (HQ001 Waypoints_1), Alsena (HQ002
    /// Waypoints_1) and the Seal_Altgard_013 villager vanished 50-100 ms after their last stop with no despawn directive in their
    /// scripts; the client data marks exactly those routes (a default action at the last point).
    /// </summary>
    public bool DespawnsAtEnd(int mapId, string route) => Of(mapId, route)?.Despawn == true;

    /// <summary>The delay between the arrival's stop and the despawn: OBSERVED 50 ms (Elvan, Salros' scripted one), 60 ms, 100 ms.</summary>
    public const int DespawnDelayMs = 50;

    /// <summary>A SocialAction.dat pose the NPC holds (type Persistant: sitting, lying, sick); stopping it means getting up first.</summary>
    public bool Persistent(int socialAction) => persistentSocial.Contains(socialAction);

    /// <summary>
    /// How long an NPC takes to get up from a held pose before it can walk: the time between its 0x9508 stop and its next 0x371b.
    /// OBSERVED per pose (2026-11-06/07): 1195 AD_Sick_L_02 3.80 s (Elvan, HQ001 #21.436 -> #25.238), 1075 AD_Sit_L_05 5.23 s,
    /// 1162 AD_LieDown_H_02 5.39 s, 1164 AD_LieDown_L_02 5.10 s, 1165 AD_LieDown_L_03 4.75 s (the Seal_Altgard_013 villagers),
    /// 1072 AD_Sit_L_02 4.20 s (a Fugitive Village crowd NPC). CANDIDATE for the other held poses: 4.9 s, the middle of those six
    /// (the get-up animation's own length is in the client's act assets, not its tables); other social actions: no wait.
    /// </summary>
    public int GetUpMs(int socialAction) => socialAction switch
    {
        1195 => 3800, 1075 => 5230, 1162 => 5390, 1164 => 5100, 1165 => 4750, 1072 => 4200,
        _ => Persistent(socialAction) ? 4900 : 0,
    };
}
