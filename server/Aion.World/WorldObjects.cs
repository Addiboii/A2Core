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
    // An exit door (EnvObjData Usage MoveMap): the MapData teleport point it leads to (tools/world_objects.py).
    [property: JsonPropertyName("move_to")] string? MoveTo = null,
    // Its EnvObjData name, when the spawn entry is named after another object (tools/world_objects.py).
    [property: JsonPropertyName("env_name")] string? EnvName = null,
    // EnvObj_Destroy objects: the hits that break one (EnvObjData HitLimit; 0/1: one) and the skills that count (empty: any).
    [property: JsonPropertyName("hit_limit")] int HitLimit = 0,
    [property: JsonPropertyName("hit_skills")] int[]? HitSkills = null,
    // A carried-item box (BombBox): InteractConditionList NotHaveAbnormalGroup, bDespawn, FinishDuration (tools/world_objects.py).
    [property: JsonPropertyName("carry")] CarryBox? Carry = null,
    // EnvObjData CombatPower and its Level / ItemLevel (portals gated for level 45).
    [property: JsonPropertyName("required_combat_power")] int RequiredCombatPower = 0,
    [property: JsonPropertyName("min_level")] int MinLevel = 0,
    [property: JsonPropertyName("item_level")] int ItemLevel = 0,
    // A field-event treasure box: the loot.json reward table opening it pays (tools/world_objects.py; CANDIDATE link by the
    // table name reward_manual_FieldEvent_<x>_TreasureBox_<EnvObjData id>).
    [property: JsonPropertyName("loot")] string? Loot = null)
{
    public Vector3d At => new(Position[0], Position[1], Position[2]);
    /// <summary>EnvObj_Destroy (ERelationshipEntity 15): broken by hits rather than used.</summary>
    public bool Destructible => Relationship == 15;
    public int HitsToBreak => Math.Max(1, HitLimit);
    public bool CountsHit(int skill) => HitSkills is not { Length: > 0 } skills || skills.Contains(skill);
}

/// <summary>A carried-item box: the abnormal group it gives / refuses, whether it hides after use and for how long (ms).</summary>
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

    public static IReadOnlyList<WorldObject> ObjectsOn(int mapId) => Map(mapId)?.Objects ?? [];
    public static IReadOnlyList<TriggerArea> TriggersOn(int mapId) => Map(mapId)?.Triggers ?? [];

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int[]> longRange = new();

    /// <summary>Indexes of the map's long-range objects (sent at entry on instance maps).</summary>
    public static IReadOnlyList<int> LongRangeOn(int mapId) => longRange.GetOrAdd(mapId, id =>
        ObjectsOn(id).Select((o, i) => (o, i)).Where(x => x.o.LongRange).Select(x => x.i).ToArray());
}
