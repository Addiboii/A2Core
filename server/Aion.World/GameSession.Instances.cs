using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Instance layers the server runs, from tools/data/instances.json: entry trigger, arrival, exit, boss.</summary>
public sealed class InstanceTables
{
    public sealed record Entry([property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("center")] double[] Center,
        [property: JsonPropertyName("size")] double[] Size);
    public sealed record Exit([property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("position")] double[] Position);
    public sealed record Instance(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("map_event")] string MapEvent,
        [property: JsonPropertyName("entry")] Entry Entry,
        [property: JsonPropertyName("arrival")] double[] Arrival,
        [property: JsonPropertyName("exit")] Exit Exit,
        [property: JsonPropertyName("quest")] string? Quest,
        [property: JsonPropertyName("boss")] int[] Boss)
    {
        public int MapId { get; set; }
    }

    sealed record InstanceFile([property: JsonPropertyName("instances")] Dictionary<string, Instance> Instances);

    readonly Dictionary<int, Instance> byMap;
    InstanceTables(InstanceFile file) =>
        byMap = file.Instances.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => { p.Value.MapId = int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture); return p.Value; });

    static readonly Lazy<InstanceTables> embedded = new(() =>
    {
        using var stream = typeof(InstanceTables).Assembly.GetManifestResourceStream("instances.json") ?? throw new InvalidOperationException("Missing embedded game data instances.json");
        return new InstanceTables(JsonSerializer.Deserialize<InstanceFile>(stream) ?? throw new InvalidOperationException("Empty instances.json"));
    });

    public static InstanceTables Embedded => embedded.Value;
    public Instance? Of(int mapId) => byMap.GetValueOrDefault(mapId);
    internal IEnumerable<Instance> All => byMap.Values;

    /// <summary>The field map and spot of the entry trigger of the layer running the map event, or null.</summary>
    public (int Map, Vector3d At)? EntryOfEvent(string mapEvent) =>
        byMap.Values.FirstOrDefault(i => string.Equals(i.MapEvent, mapEvent, StringComparison.OrdinalIgnoreCase)) is { } layer
            ? (layer.Entry.Map, new Vector3d(layer.Entry.Center[0], layer.Entry.Center[1], layer.Entry.Center[2])) : null;

    /// <summary>The instance whose entry trigger (a box on the field map) contains the position.</summary>
    public Instance? EntryAt(int mapId, Vector3d at) => byMap.Values.FirstOrDefault(i => i.Entry.Map == mapId
        && Math.Abs(at.X - i.Entry.Center[0]) <= i.Entry.Size[0] / 2 && Math.Abs(at.Y - i.Entry.Center[1]) <= i.Entry.Size[1] / 2
        && Math.Abs(at.Z - i.Entry.Center[2]) <= i.Entry.Size[2] / 2);
}

// Instance layers (a dungeon copy per player). Entering: the field map's EnterInstanceLayer trigger box, while its quest
// is active (staff always). The official move (capture 20261013-144836, map 1000 -> 100010 and back): 0x3647 for the
// player's own key with reason 12 (InstanceLayer) and teleport effect 0x41, then 0x3621 with reason 12 and effect
// 0x41; after the load MapEnvState_NT 0x4001 / MapEnvSetting_NT 0x4002 (0x40 run, catalogue index + 0x3ef9). Killing
// the boss clears the map event (the quest's ClearMapEvent objective); the player leaves to the exit point 10 s later.
// The MapEvent progress messages (0x922d/0x922e/0x922f) are not sent yet.
public sealed partial class GameSession
{
    public const byte InstanceReason = 12, InstanceEffect = 0x41;
    public const long InstanceExitDelayMs = 10_000;
    static InstanceTables Instances => InstanceTables.Embedded;

    /// <summary>The instance copy this player is in (its own key), or 0 on the shared maps.</summary>
    int instanceKey;
    long? instanceExitAt;
    readonly List<string> clearedEvents = [];
    public int InstanceKey => instanceKey;

    /// <summary>The player walked into an instance's entry trigger: the move into its own copy.</summary>
    List<byte[]> InstanceTrigger(Vector3d at, int moveOpcode, long now)
    {
        if (reloading || resources is null || dead) return [];
        if (WalkInLayerTrigger(moveOpcode, at, now) is { Count: > 0 } walkIn) return walkIn;
        if (Instances.EntryAt(worldMap, at) is not { } instance) return DungeonTrigger(at, now);
        // The quest gate applies to stored characters (development logins keep no quest log).
        if (instance.Quest is { } code && !IsAdmin && questLog is not null && questLog.Get(code) is not { Status: QuestLog.Incomplete }) return [];
        instanceKey = playerKey;
        instanceExitAt = null;
        instanceReturn = (worldMap, at);
        var arrival = new Vector3d(instance.Arrival[0], instance.Arrival[1], instance.Arrival[2]);
        return InstanceMove(instance.MapId, arrival, now);
    }

    /// <summary>
    /// Back to the field map: the hand-entered exit point, else where the player came in (LastPosition), or the same spot
    /// under an instance layer that overlays the base map (CurrentPosition, the HQ001 way out). The events' 0x922f follow
    /// the move (OBSERVED after 0x3621).
    /// </summary>
    List<byte[]> LeaveInstance(long now, string mode = "LastPosition", (int Map, Vector3d At)? to = null, byte? teleportEffect = null)
    {
        if (resources is null || instanceKey == 0) return [];
        var hand = Instances.Of(worldMap);
        var volume = MapEventData.Volume(worldMap);
        int map; Vector3d exit;
        if (to is { } given) (map, exit) = given;
        else if (hand is not null) (map, exit) = (hand.Exit.Map, new Vector3d(hand.Exit.Position[0], hand.Exit.Position[1], hand.Exit.Position[2]));
        else if (instanceReturn is { } back) (map, exit) = back;
        else if (volume is not null) (map, exit) = (volume.Base, position ?? spawn?.Position ?? default);
        // No way out recorded: the race's starting map (Poeta for Light, Ishalgen for Dark) at its start point.
        else (map, exit) = (HomeMap, data.MapStart(HomeMap)?.Position ?? GamePackets.SpawnPosition);
        if (to is null && hand is null && volume is { } overlay && overlay.Base == map && string.Equals(mode, "CurrentPosition", StringComparison.OrdinalIgnoreCase) && position is { } here)
            exit = here;
        var leaving = (CopyOwner ?? this).mapEventRuns.Select(r => GamePackets.MapEventLeave(r.Id)).ToList();
        HandOverCopy();
        mapEventRuns.Clear();
        pendingExit = null;
        clearExit = null;
        instanceKey = 0;
        instanceExitAt = null;
        instanceReturn = null;
        // Back outside: the entry triggers there wait until the player has stepped out of them (a way out that lands in or
        // next to an entry area pulled the player straight back in, 2026-10-30 HQ003 14:11:47-49).
        dungeonTriggerNeedsOutside = true;
        return [.. InstanceMove(map, exit, now, teleportEffect: teleportEffect), .. leaving];
    }

    /// <summary>
    /// An exit door used (EnvObjData Usage MoveMap, UsageValue the MapData teleport point: E_D0_Starter_HQ003_Warehouse_002 ->
    /// World_D_Starter_HQ003_Exit on Ishalgen). Inside a layer or dungeon copy: out to that point (the base map's point first);
    /// on a shared map: a teleport to it. An unknown point leaves the copy the usual way.
    /// </summary>
    List<byte[]> MoveMapDoor(string point, long now)
    {
        if (resources is null || reloading || dead) return [];
        var preferred = instanceReturn?.Map ?? MapEventData.Volume(worldMap)?.Base ?? worldMap;
        (int Map, Vector3d At)? dest = data.MapPoints(preferred).TryGetValue(point, out var p) ? (preferred, new Vector3d(p[0], p[1], p[2])) : null;
        if (dest is null)
            foreach (var (name, map, at) in data.Destinations())
                if (string.Equals(name, point, StringComparison.OrdinalIgnoreCase)) { dest = (map, at); break; }
        // A door out is the PCTeleport counterpart of the way in (reason 2, SeamlessDoorTeleport_heavy_out = TeleportEffect.dat 41);
        // the seamless layer move (12 / 0x41) left the client inside the storage while the server put it on Ishalgen (2026-10-30).
        if (instanceKey != 0) return LeaveInstance(now, to: dest, teleportEffect: DoorExitTeleportEffect);
        return dest is { } d ? Teleport(d.Map, d.At, now) : [];
    }

    /// <summary>A door's move out of its layer: SeamlessDoorTeleport_heavy_out.</summary>
    const byte DoorExitTeleportEffect = 41;

    /// <summary>A kill in an instance: the boss clears its map event and starts the way out.</summary>
    void InstanceKill(Target target, long now)
    {
        if (instanceKey == 0 || Instances.Of(worldMap) is not { } instance || !instance.Boss.Contains(target.NpcId) || instanceExitAt is not null) return;
        clearExit ??= ClearExitOf(instance.MapEvent);
        clearedEvents.Add(instance.MapEvent);
        instanceExitAt = now + InstanceExitDelayMs;
    }

    List<byte[]> InstanceTick(long now) => instanceExitAt is { } due && now >= due && !reloading ? LeaveInstance(now, to: clearExit) : [];

    /// <summary>After an instance map load: MapEnvState_NT {map, u8 0, i64 0, enter 1, no states}, MapEnvSetting_NT {map, no settings, enter 1}.</summary>
    List<byte[]> MapEnvironment(int mapId) =>
    [
        PacketWriter.Opcode(0x4001).I32(mapId).U8(0).I64(0).U8(1).Varint(0).ToArray(),
        PacketWriter.Opcode(0x4002).I32(mapId).Varint(0).U8(1).ToArray(),
    ];
}
