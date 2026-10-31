using System.Collections.Concurrent;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The world every game connection on this host shares (multiplayer): one <see cref="MapInstance"/>
/// per map with its NPCs, their AI and respawns, and the players in it. Game sessions run their message
/// handling and ticks under <see cref="Lock"/>: world work exclusively (one at a time, so world state and
/// other sessions may be touched directly), map work (movement, most ticks) shared together with the map's
/// own lock, so maps run in parallel and map work touches only its own map and players. Messages for
/// another player go to that player's <see cref="WorldPlayer.Outbox"/> (thread-safe), sent on its next tick.
/// </summary>
public sealed class SharedWorld(GameData data)
{
    /// <summary>Player entity keys: unique per host, clear of NPCs (2000+), the dummy (1000) and Kibelisks (900000+).</summary>
    public const int FirstPlayerKey = 100_000;
    /// <summary>Players see each other within this distance (cm), as NPCs stay active within NpcAi.ActiveRange.</summary>
    public const double VisibleRange = 8000;

    readonly Dictionary<long, MapInstance> maps = [];
    int lastKey;

    public WorldLock Lock { get; } = new();

    /// <summary>The server's daily gather counter (1,000 successful gathers per game day since Oct 28 2026).</summary>
    public GatherDailyLimiter GatherLimiter { get; } = new();
    public GameData Data => data;

    public int AllocateKey() => FirstPlayerKey + Interlocked.Increment(ref lastKey);

    /// <summary>
    /// The shared instance of a map, or with <paramref name="instance"/> (the owner's key) a private copy of an instance
    /// layer (created with its MapData NPCs on first use). Call under the gate.
    /// </summary>
    public MapInstance Map(int mapId, int instance = 0)
    {
        var key = (long)instance << 32 | (uint)mapId;
        if (!maps.TryGetValue(key, out var map))
        {
            maps[key] = map = new MapInstance(mapId, Combat.MapTargets(data, mapId), new NpcAi(new Random(), data)) { Instance = instance };
            if (instance != 0 && MapEventTables.Embedded.DungeonOfMap(mapId) is { } room)
                Patch20261028.ScaleBossHp(map.Targets.Values, MapEventTables.Embedded.DungeonIdOfMap(mapId), room);
        }
        return map;
    }

    /// <summary>Drops a private copy nobody is in any more. Call under the gate.</summary>
    public void Drop(MapInstance map)
    {
        if (map.Instance != 0 && map.Players.Count == 0) maps.Remove((long)map.Instance << 32 | (uint)map.MapId);
    }

    public IEnumerable<MapInstance> Maps => maps.Values;

    /// <summary>The shared map if someone loaded it (staff tools do not create maps). Call under the lock held exclusively.</summary>
    public MapInstance? LoadedMap(int mapId) => maps.GetValueOrDefault((uint)mapId);

    double expRate = 1, dropRate = 1;
    /// <summary>Event boosts (scheduled events): multipliers for quest experience on this host.</summary>
    public double ExpRate { get => Volatile.Read(ref expRate); set => Volatile.Write(ref expRate, Math.Clamp(value, 0.1, 10)); }
    public double DropRate { get => Volatile.Read(ref dropRate); set => Volatile.Write(ref dropRate, Math.Clamp(value, 0.1, 10)); }

    /// <summary>Parties, invitations, trades and duels (use under the gate).</summary>
    public SocialState Social { get; } = new();

    /// <summary>Fortress siege schedule, applications, live battle and ownership (AirspaceBattle).</summary>
    public AirspaceSiege Airspace { get; } = new();

    /// <summary>Daily/weekly reset cursors and world-scoped rollover (legion attendance, weekly contribution).</summary>
    public ContentResetCoordinator Resets { get; } = new();
    public bool ResetsLoaded { get; set; }

    /// <summary>Every player in the world on this host (any map), by entity key. Use under the gate.</summary>
    public Dictionary<int, WorldPlayer> Online { get; } = [];

    public WorldPlayer? FindOnline(string name) =>
        Online.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public WorldPlayer? FindOnline(Guid character) => Online.Values.FirstOrDefault(p => p.CharacterId == character);

    /// <summary>Notifies every connected player that the push-reward list changed (0xe34a, no body).</summary>
    public void BroadcastPushRewardChanged()
    {
        var packet = GameSession.PushRewardChangedPacket();
        foreach (var player in Online.Values) player.Send([packet]);
    }
}

/// <summary>A player as the shared world knows it. Fields are read and written under the world gate; the outbox is thread-safe.</summary>
public sealed class WorldPlayer(GameSession session, int key)
{
    public GameSession Session { get; } = session;
    public int Key { get; } = key;
    /// <summary>The stored character (null on development logins) and its stored name.</summary>
    public Guid? CharacterId { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; } = 1;
    public MapInstance? Map { get; internal set; }
    public Vector3d Position { get; set; }
    public double Heading { get; set; }
    /// <summary>Invisible GMs: not seen by other players, not targeted by NPCs.</summary>
    public bool Hidden { get; set; }
    /// <summary>The CharAppear (0x3645) other players receive for this one.</summary>
    public required Func<byte[]> Appear { get; init; }
    /// <summary>Keys of the other players this player currently sees.</summary>
    public HashSet<int> Seeing { get; } = [];
    Vector3d? visibilityAnchor;
    long visibilityAtMs;
    /// <summary>Visibility pairs are O(n²); refresh after meaningful movement or once per second.</summary>
    public bool NeedsVisibilityRefresh(Vector3d at, long nowMs)
    {
        if (visibilityAnchor is not { } anchor || nowMs - visibilityAtMs >= 1000
            || (at.X - anchor.X) * (at.X - anchor.X) + (at.Y - anchor.Y) * (at.Y - anchor.Y) >= 2_000_000)
        {
            visibilityAnchor = at;
            visibilityAtMs = nowMs;
            return true;
        }
        return false;
    }
    public ConcurrentQueue<byte[]> Outbox { get; } = new();

    /// <summary>Queues messages for this player and wakes its connection, so they go out now rather than at its next 50 ms tick.</summary>
    public void Send(byte[] packet) { Outbox.Enqueue(packet); Session.WakeOutbox(); }
    public void Send(IEnumerable<byte[]> packets)
    {
        var any = false;
        foreach (var p in packets) { Outbox.Enqueue(p); any = true; }
        if (any) Session.WakeOutbox();
    }
}

/// <summary>One map's shared state. Every member is used under the world lock held exclusively, or shared with <see cref="Lock"/>.</summary>
public sealed partial class MapInstance(int mapId, Dictionary<int, Target> targets, NpcAi ai)
{
    /// <summary>Map work (taken after the world lock in shared mode).</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public int MapId { get; } = mapId;
    /// <summary>The owner's key for a private instance-layer copy; 0 for the shared map.</summary>
    public int Instance { get; init; }
    public Dictionary<int, Target> Targets { get; } = targets;
    public NpcAi Ai { get; } = ai;
    public Dictionary<int, long> RespawnAt { get; } = [];
    public Dictionary<int, WorldPlayer> Players { get; } = [];
    /// <summary>The next NPC AI tick (one per map, run by whichever session ticks first).</summary>
    public long AiTickAt { get; set; }
    public bool GeometryApplied { get; set; }
    /// <summary>A dungeon copy's shared run state (host, timer, clear, reward cube); null on the shared maps.</summary>
    public DungeonCopy? Dungeon { get; set; }

    static double Distance(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Whether <paramref name="viewer"/> should see <paramref name="other"/> now.</summary>
    static bool Visible(WorldPlayer viewer, WorldPlayer other) =>
        viewer != other && !other.Hidden && Distance(viewer.Position, other.Position) <= SharedWorld.VisibleRange;

    public void Join(WorldPlayer player)
    {
        player.Map?.Leave(player);
        player.Map = this;
        Players[player.Key] = player;
        UpdateVisibility(player);
    }

    /// <summary>Leaves the map: everyone who saw the player gets its CharDisappear.</summary>
    public void Leave(WorldPlayer player, byte reason = 1)
    {
        if (!Players.Remove(player.Key)) return;
        foreach (var other in Players.Values)
        {
            if (other.Seeing.Remove(player.Key)) other.Send(PlayerPackets.CharDisappear(player.Key, reason));
            player.Seeing.Remove(other.Key);
        }
        player.Seeing.Clear();
        player.Map = null;
    }

    /// <summary>
    /// Appear/disappear in both directions after the player moved, hid or changed looks
    /// (<paramref name="refresh"/>: resend its appearance to those who already see it).
    /// </summary>
    public void UpdateVisibility(WorldPlayer player, bool refresh = false)
    {
        foreach (var other in Players.Values)
        {
            if (other == player) continue;
            Pair(player, other, false);
            Pair(other, player, refresh);
        }
    }

    static void Pair(WorldPlayer viewer, WorldPlayer other, bool refresh)
    {
        var should = Visible(viewer, other);
        var sees = viewer.Seeing.Contains(other.Key);
        if (should && (!sees || refresh))
        {
            viewer.Seeing.Add(other.Key);
            viewer.Send(other.Appear());
            viewer.Send(other.Session.AppearExtras());   // e.g. the ride it sits on
        }
        else if (!should && sees)
        {
            viewer.Seeing.Remove(other.Key);
            viewer.Send(PlayerPackets.CharDisappear(other.Key));
        }
    }

    /// <summary>Messages about <paramref name="from"/> for every player who sees it.</summary>
    public void Broadcast(WorldPlayer from, IEnumerable<byte[]> packets)
    {
        var list = packets as IReadOnlyCollection<byte[]> ?? packets.ToList();
        if (list.Count == 0) return;
        foreach (var other in Players.Values)
            if (other != from && other.Seeing.Contains(from.Key)) other.Send(list);
    }

    /// <summary>Map-wide messages (NPCs) for every player except <paramref name="except"/>.</summary>
    public void BroadcastAll(IEnumerable<byte[]> packets, WorldPlayer? except = null)
    {
        var list = packets as IReadOnlyCollection<byte[]> ?? packets.ToList();
        if (list.Count == 0) return;
        // Each player gets the NPC messages about NPCs it sees (big maps stream NPCs by distance).
        foreach (var other in Players.Values)
            if (other != except) other.Send(other.Session.Visible(list));
    }

    /// <summary>Players the NPCs consider: in the map and not hidden.</summary>
    public List<(int Key, Vector3d Position)> NpcVictims() =>
        Players.Values.Where(p => !p.Hidden && !p.Session.Dead).Select(p => (p.Key, p.Position)).ToList();
}
