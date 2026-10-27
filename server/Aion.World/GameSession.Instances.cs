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
    List<byte[]> InstanceTrigger(Vector3d at, long now)
    {
        if (reloading || resources is null || dead) return [];
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
    List<byte[]> LeaveInstance(long now, string mode = "LastPosition")
    {
        if (resources is null || instanceKey == 0) return [];
        var hand = Instances.Of(worldMap);
        var volume = MapEventData.Volume(worldMap);
        int map; Vector3d exit;
        if (hand is not null) (map, exit) = (hand.Exit.Map, new Vector3d(hand.Exit.Position[0], hand.Exit.Position[1], hand.Exit.Position[2]));
        else if (instanceReturn is { } back) (map, exit) = back;
        else (map, exit) = (volume?.Base ?? 1000, position ?? spawn?.Position ?? default);
        if (hand is null && volume is { } overlay && overlay.Base == map && string.Equals(mode, "CurrentPosition", StringComparison.OrdinalIgnoreCase) && position is { } here)
            exit = here;
        var leaving = (CopyOwner ?? this).mapEventRuns.Select(r => GamePackets.MapEventLeave(r.Id)).ToList();
        HandOverCopy();
        mapEventRuns.Clear();
        pendingExit = null;
        instanceKey = 0;
        instanceExitAt = null;
        instanceReturn = null;
        return [.. InstanceMove(map, exit, now), .. leaving];
    }

    /// <summary>A kill in an instance: the boss clears its map event and starts the way out.</summary>
    void InstanceKill(Target target, long now)
    {
        if (instanceKey == 0 || Instances.Of(worldMap) is not { } instance || !instance.Boss.Contains(target.NpcId) || instanceExitAt is not null) return;
        clearedEvents.Add(instance.MapEvent);
        instanceExitAt = now + InstanceExitDelayMs;
    }

    List<byte[]> InstanceTick(long now) => instanceExitAt is { } due && now >= due && !reloading ? LeaveInstance(now) : [];

    /// <summary>After an instance map load: MapEnvState_NT {map, u8 0, i64 0, enter 1, no states}, MapEnvSetting_NT {map, no settings, enter 1}.</summary>
    List<byte[]> MapEnvironment(int mapId) =>
    [
        PacketWriter.Opcode(0x4001).I32(mapId).U8(0).I64(0).U8(1).Varint(0).ToArray(),
        PacketWriter.Opcode(0x4002).I32(mapId).Varint(0).U8(1).ToArray(),
    ];
}
