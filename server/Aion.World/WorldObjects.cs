using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A placed quest object (tools/world_objects.py): EnvObjData id, MapData placement, interaction from EnvObjData.</summary>
public sealed record WorldObject(
    [property: JsonPropertyName("env")] int Env,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unique_key")] int UniqueKey,
    [property: JsonPropertyName("position")] double[] Position,
    [property: JsonPropertyName("yaw")] double Yaw,
    [property: JsonPropertyName("interact_time")] int InteractTime,
    [property: JsonPropertyName("interact_range")] int InteractRange,
    [property: JsonPropertyName("relationship")] int Relationship,
    [property: JsonPropertyName("gather")] long[]? Gather = null,
    [property: JsonPropertyName("portal")] int[]? Portal = null,
    [property: JsonPropertyName("cube")] bool Cube = false,
    [property: JsonPropertyName("long_range")] bool LongRange = false,
    [property: JsonPropertyName("flight_ring")] bool FlightRing = false,
    [property: JsonPropertyName("move_to")] string? MoveTo = null,
    [property: JsonPropertyName("env_name")] string? EnvName = null,
    [property: JsonPropertyName("hit_limit")] int HitLimit = 0,
    [property: JsonPropertyName("hit_skills")] int[]? HitSkills = null,
    [property: JsonPropertyName("carry")] CarryBox? Carry = null)
{
    public Vector3d At => new(Position[0], Position[1], Position[2]);

    /// <summary>
    /// ERelationshipEntity EnvObj_Destroy: the client targets and attacks it as an enemy and never sends InteractEnvObj_RQ;
    /// hits break it (EnvObjData HitLimit, HitSkillIdList). On a map with carried-item boxes it takes the carried bomb instead.
    /// </summary>
    public bool Destructible => Relationship == WorldObjects.DestroyRelationship;
    public int HitsToBreak => Math.Max(1, HitLimit);
    public bool CountsHit(int skill) => HitSkills is not { Length: > 0 } || HitSkills.Contains(skill);
}

/// <summary>
/// A box whose use gives the player a carried item (EnvObjData InteractConditionList NotHaveAbnormalGroup: the BombBox's bomb,
/// group 1196). Despawn and FinishDuration (ms): the box is gone after its use and back once that time has passed.
/// </summary>
public sealed record CarryBox(
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("despawn")] bool Despawn,
    [property: JsonPropertyName("finish_duration")] int FinishDuration);

/// <summary>A quest trigger area (MapData TriggerActorDataMap): a rotated box or a sphere with a height.</summary>
public sealed record TriggerArea(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("position")] double[] Position,
    [property: JsonPropertyName("direction")] double Direction,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("box")] double[] Box,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("id")] long Id = 0,
    [property: JsonPropertyName("env")] int Env = 0)
{
    public Vector3d At => new(Position[0], Position[1], Position[2]);

    /// <summary>
    /// Whether a position is inside: the box (BoxSize taken as half extents, rotated by Direction; CANDIDATE) or the
    /// sphere's radius, within Height above or below; <paramref name="extra"/> widens it (the objective's value).
    /// </summary>
    public bool Contains(Vector3d at, double extra = 0)
    {
        double dx = at.X - Position[0], dy = at.Y - Position[1], dz = at.Z - Position[2];
        if (Math.Abs(dz) > Math.Max(Height, 500) + extra) return false;
        if (Shape == "Sphere" || Box.Length < 2 || Box[0] <= 0 && Box[1] <= 0)
            return Math.Sqrt(dx * dx + dy * dy) <= Math.Max(Radius, 100) + extra;
        var radians = -Direction * Math.PI / 180;
        double lx = dx * Math.Cos(radians) - dy * Math.Sin(radians), ly = dx * Math.Sin(radians) + dy * Math.Cos(radians);
        return Math.Abs(lx) <= Box[0] + extra && Math.Abs(ly) <= Box[1] + extra;
    }
}

/// <summary>Quest objects and trigger areas by map (embedded world-objects.json).</summary>
public static class WorldObjects
{
    /// <summary>Entity keys of quest objects: FirstKey + index on the map (Kibelisks use 900000+).</summary>
    public const int FirstKey = 910_000;
    /// <summary>Entity keys of trigger actors: FirstTriggerKey + index in the map's trigger list.</summary>
    public const int FirstTriggerKey = 950_000;
    /// <summary>Outside instance maps, trigger actors (EnvObjData VisibleRangeType LongRange) are spawned within this (OURS).</summary>
    public const double TriggerRange = 20000;
    /// <summary>Objects are spawned for a player within this distance and despawned beyond it (cm).</summary>
    public const double VisibleRange = 6000;
    /// <summary>Slack on EnvObjData InteractRange for latency and collision radii.</summary>
    public const double InteractSlack = 400;
    /// <summary>ERelationshipEntity::EnvObj_Destroy (usmap).</summary>
    public const int DestroyRelationship = 15;

    sealed record MapEntry([property: JsonPropertyName("objects")] WorldObject[] Objects,
                           [property: JsonPropertyName("triggers")] TriggerArea[] Triggers);
    sealed record File([property: JsonPropertyName("maps")] Dictionary<string, MapEntry> Maps);

    static readonly Lazy<Dictionary<string, MapEntry>> maps = new(() =>
    {
        using var stream = typeof(WorldObjects).Assembly.GetManifestResourceStream("world-objects.json")
            ?? throw new InvalidOperationException("world-objects.json is not embedded");
        return (JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("world-objects.json")).Maps;
    });

    static MapEntry? Map(int mapId) => maps.Value.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    sealed record BaseFile([property: JsonPropertyName("bases")] Dictionary<string, int> Bases);

    /// <summary>
    /// Field maps include quest gimmicks from Map.dat submaps (map-bases.json): E_L1_UgarosInsDoor_01 lives on overlay
    /// 800002 while the player stays on world map 1010; instance dungeon maps keep their own object lists only.
    /// </summary>
    static readonly Lazy<(Dictionary<int, WorldObject[]> Objects, Dictionary<int, TriggerArea[]> Triggers)> fieldComposed = new(BuildFieldComposed);

    static (Dictionary<int, WorldObject[]> Objects, Dictionary<int, TriggerArea[]> Triggers) BuildFieldComposed()
    {
        using var stream = typeof(WorldObjects).Assembly.GetManifestResourceStream("map-bases.json")
            ?? throw new InvalidOperationException("map-bases.json is not embedded");
        var bases = (JsonSerializer.Deserialize<BaseFile>(stream) ?? throw new InvalidDataException("map-bases.json")).Bases;
        var overlaysByBase = new Dictionary<int, List<int>>();
        foreach (var (key, baseMap) in bases)
            if (int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var overlay)
                && overlay != baseMap && FieldGimmickOverlay(overlay))
                (overlaysByBase.TryGetValue(baseMap, out var list) ? list : overlaysByBase[baseMap] = []).Add(overlay);
        var objects = new Dictionary<int, WorldObject[]>();
        var triggers = new Dictionary<int, TriggerArea[]>();
        foreach (var (key, entry) in maps.Value)
        {
            if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var mapId)) continue;
            if (InstanceInteriorMap(mapId)) continue;
            var objList = new List<WorldObject>(entry.Objects);
            var trgList = new List<TriggerArea>(entry.Triggers);
            if (overlaysByBase.TryGetValue(mapId, out var overlays))
                foreach (var overlayId in overlays.OrderBy(x => x))
                    if (Map(overlayId) is { } overlayEntry)
                    {
                        objList.AddRange(overlayEntry.Objects);
                        trgList.AddRange(overlayEntry.Triggers);
                    }
            objects[mapId] = objList.ToArray();
            triggers[mapId] = trgList.ToArray();
        }
        return (objects, triggers);
    }

    /// <summary>Map.dat 800xxx district overlays (doors, triggers) merged onto the parent field map; not instance interiors.</summary>
    static bool FieldGimmickOverlay(int mapId) => mapId is >= 800_000 and < 900_000;

    /// <summary>Served instance/ dungeon maps use their native spawns only (153001 Garrison H1 interior, etc.).</summary>
    static bool InstanceInteriorMap(int mapId) => servedDungeonMaps.Value.Contains(mapId);

    sealed record ServedMapsFile([property: JsonPropertyName("served_maps")] int[] ServedMaps);

    static readonly Lazy<HashSet<int>> servedDungeonMaps = new(() =>
    {
        using var stream = typeof(WorldObjects).Assembly.GetManifestResourceStream("map-events.json")
            ?? throw new InvalidOperationException("map-events.json is not embedded");
        return (JsonSerializer.Deserialize<ServedMapsFile>(stream) ?? throw new InvalidDataException("map-events.json")).ServedMaps
            .Where(id => !FieldGimmickOverlay(id)).ToHashSet();
    });

    public static IReadOnlyList<WorldObject> ObjectsOn(int mapId) =>
        fieldComposed.Value.Objects.TryGetValue(mapId, out var composed) ? composed : Map(mapId)?.Objects ?? [];

    public static IReadOnlyList<TriggerArea> TriggersOn(int mapId) =>
        fieldComposed.Value.Triggers.TryGetValue(mapId, out var composed) ? composed : Map(mapId)?.Triggers ?? [];
    internal static IEnumerable<(int Map, WorldObject Object)> AllObjects =>
        maps.Value.SelectMany(m => m.Value.Objects.Select(o => (int.Parse(m.Key, System.Globalization.CultureInfo.InvariantCulture), o)));

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int[]> longRange = new();

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> carryMaps = new();

    /// <summary>Whether the map has carried-item boxes: its EnvObj_Destroy objects then need the item (the BombBox's towers).</summary>
    public static bool HasCarryBoxes(int mapId) => carryMaps.GetOrAdd(mapId, id => ObjectsOn(id).Any(o => o.Carry is not null));

    static readonly Lazy<HashSet<int>> carryGroups = new(() =>
        maps.Value.Values.SelectMany(m => m.Objects).Where(o => o.Carry is not null).Select(o => o.Carry!.Group).ToHashSet());

    /// <summary>Whether an abnormal group is a carry box's item (group 1196): it never outlives the session or the map copy.</summary>
    public static bool IsCarryGroup(int group) => carryGroups.Value.Contains(group);

    /// <summary>Indexes of the map's long-range objects (sent at entry on instance maps).</summary>
    public static IReadOnlyList<int> LongRangeOn(int mapId) => longRange.GetOrAdd(mapId, id =>
        ObjectsOn(id).Select((o, i) => (o, i)).Where(x => x.o.LongRange).Select(x => x.i).ToArray());
}
