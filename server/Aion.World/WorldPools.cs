using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The random pools (hidden cubes, the Altgard treasure boxes) and the Spacetime Rifts of the four field maps, from the client tables
/// (tools/world_objects.py pools -> tools/data/world-pools.json; see that command for the sources). Their placements are appended to
/// the maps' objects after world-objects.json (WorldObjects.ObjectsOn), so they use the ordinary object keys, visibility and use.
/// <para>
/// What the data says (client tables 2026-11-09):
/// - 206 pool records on 1010 / 1011 / 1110 / 1111 (95 / 26 / 59 / 26), 3722 positions, 2 EnvObjData ids each: two cubes up per record
///   at random positions of the record (CANDIDATE reading of "fewer ids than positions", the form of every one-of-several spawner).
/// - 10 rifts (1110: 3, 1010: 3, 1111: 2, 1011: 2), each a PeriodSpawn Cycle row: every 180 min from 02:00 KST, all week, open for
///   RiftData LifeTime 600 s, at one of its record's 5 positions; usable 400 times (InteractCount), use time 2.5 s, level 45-100, no cost;
///   the move: TeleportEffect 30 (Teleport_Gate_Default_001) to one of the 5 exit points of Teleport.dat RiftPortal_X_to_Y_Teleport_n.
/// </para>
/// </summary>
public static class WorldPools
{
    /// <summary>A rift spawner: where it opens, its RiftData row, its schedule and its exit points on the other faction's map.</summary>
    public sealed record RiftInfo(string Spawner, int Map, int Env, string Rift, int RiftId, int ArrivalMap, int MinLevel, int MaxLevel,
        int LifeSeconds, int Limit, int InteractMs, int Effect, FieldBosses.Schedule Schedule, string? Teleport, IReadOnlyList<Vector3d> Exits,
        IReadOnlyList<(int Item, int Count)> Costs);

    /// <summary>A hidden cube kind: its faction (EnvObjData Category HiddenCubeLight 1 / Dark 2), whether it opens only with a key,
    /// the RewardData tables without (RewardName) and with a key (BasicKeyRewards), and the key items (RequireValues, any one of them).</summary>
    public sealed record CubeInfo(int Env, string Name, int Faction, bool KeyOnly, bool OptionalKey, string Normal, string Key,
        IReadOnlyList<int> Keys, int KeyCount, bool RewardData);

    sealed record RiftRow(
        [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("env")] int Env, [property: JsonPropertyName("rift")] string Rift,
        [property: JsonPropertyName("rift_id")] int RiftId, [property: JsonPropertyName("arrival_map")] int ArrivalMap,
        [property: JsonPropertyName("min_level")] int MinLevel, [property: JsonPropertyName("max_level")] int MaxLevel,
        [property: JsonPropertyName("life_s")] int LifeSeconds, [property: JsonPropertyName("costs")] int[][] Costs,
        [property: JsonPropertyName("limit")] int Limit, [property: JsonPropertyName("interact_ms")] int InteractMs,
        [property: JsonPropertyName("effect")] int Effect, [property: JsonPropertyName("period")] JsonElement[] Period,
        [property: JsonPropertyName("teleport")] string? Teleport, [property: JsonPropertyName("exits")] double[][] Exits);

    sealed record CubeRow(
        [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("faction")] int Faction,
        [property: JsonPropertyName("key_only")] bool KeyOnly, [property: JsonPropertyName("optional_key")] bool OptionalKey,
        [property: JsonPropertyName("normal")] string Normal, [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("keys")] int[] Keys, [property: JsonPropertyName("key_count")] int KeyCount,
        [property: JsonPropertyName("reward_data")] bool RewardData);

    sealed record FileData(
        [property: JsonPropertyName("maps")] Dictionary<string, WorldObject[]> Maps,
        [property: JsonPropertyName("rifts")] Dictionary<string, RiftRow> Rifts,
        [property: JsonPropertyName("cubes")] Dictionary<string, CubeRow> Cubes);

    sealed record Tables(Dictionary<int, WorldObject[]> Maps, Dictionary<string, RiftInfo> Rifts, Dictionary<int, CubeInfo> Cubes);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(WorldPools).Assembly.GetManifestResourceStream("world-pools.json")
            ?? throw new InvalidOperationException("world-pools.json is not embedded");
        var file = JsonSerializer.Deserialize<FileData>(stream) ?? throw new InvalidDataException("world-pools.json");
        static int Int(string s) => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var rifts = file.Rifts.ToDictionary(kv => kv.Key, kv =>
        {
            var r = kv.Value;
            var p = r.Period;
            if (p.Length < 5 || p[4].GetString() is not { Length: 7 } days) throw new InvalidDataException($"world-pools.json: rift {kv.Key} period");
            var schedule = new FieldBosses.Schedule(p[0].GetInt32(), kv.Key, p[1].GetString()!, r.Map, kv.Key, p[2].GetInt32(), p[3].GetInt32(),
                days.Select(c => c == '1').ToArray());
            return new RiftInfo(kv.Key, r.Map, r.Env, r.Rift, r.RiftId, r.ArrivalMap, r.MinLevel, r.MaxLevel, r.LifeSeconds, r.Limit, r.InteractMs,
                r.Effect, schedule, r.Teleport, r.Exits.Where(e => e.Length >= 3).Select(e => new Vector3d(e[0], e[1], e[2])).ToList(),
                r.Costs.Where(c => c.Length >= 2).Select(c => (c[0], c[1])).ToList());
        }, StringComparer.OrdinalIgnoreCase);
        var cubes = file.Cubes.ToDictionary(kv => Int(kv.Key), kv => new CubeInfo(Int(kv.Key), kv.Value.Name, kv.Value.Faction, kv.Value.KeyOnly,
            kv.Value.OptionalKey, kv.Value.Normal, kv.Value.Key, kv.Value.Keys, Math.Max(1, kv.Value.KeyCount), kv.Value.RewardData));
        foreach (var (map, objects) in file.Maps)
            foreach (var o in objects)
                if (o.Rift is { } spawner && !rifts.ContainsKey(spawner)) throw new InvalidDataException($"world-pools.json: map {map} names unknown rift {spawner}");
        return new Tables(file.Maps.ToDictionary(kv => Int(kv.Key), kv => kv.Value), rifts, cubes);
    });

    /// <summary>The pool and rift placements of a map (appended to its world-objects.json list).</summary>
    public static IReadOnlyList<WorldObject> ObjectsOn(int mapId) => tables.Value.Maps.GetValueOrDefault(mapId) ?? [];
    public static IReadOnlyDictionary<string, RiftInfo> Rifts => tables.Value.Rifts;
    public static RiftInfo? RiftOf(string? spawner) => spawner is not null ? tables.Value.Rifts.GetValueOrDefault(spawner) : null;
    public static CubeInfo? CubeOf(int env) => tables.Value.Cubes.GetValueOrDefault(env);
    public static IReadOnlyCollection<CubeInfo> Cubes => tables.Value.Cubes.Values;

    /// <summary>The faction of a field map's own players (MapData World_L_A/L_B = Light 1, World_D_A/D_B = Dark 2), 0 for any other map.</summary>
    public static int FieldRace(int mapId) => mapId switch { 1010 or 1011 => 1, 1110 or 1111 => 2, _ => 0 };

    // ---------- Rift schedule ----------

    /// <summary>When the rift's current opening began (Unix ms), or null while it is closed: the last PeriodSpawn time (KST) within LifeTime.</summary>
    public static long? OpenedAt(RiftInfo rift, long now) =>
        FieldBosses.LastSpawn(rift.Schedule, now) is { } last && now < last + rift.LifeSeconds * 1000L ? last : null;

    /// <summary>The next opening after <paramref name="now"/> (Unix ms).</summary>
    public static long? NextOpening(RiftInfo rift, long now) => FieldBosses.NextSpawn(rift.Schedule, now);

    /// <summary>
    /// Which of the spawner's positions an opening uses: one picked at random per opening (CANDIDATE: no rift was recorded), the same
    /// for every map copy and session (a stable hash of the spawner and the opening time, not a stored roll).
    /// </summary>
    public static int PointOf(RiftInfo rift, long openedAt, int count)
    {
        if (count <= 1) return 0;
        var h = 2166136261u;
        foreach (var c in rift.Spawner.ToLowerInvariant()) h = (h ^ c) * 16777619u;
        var t = (ulong)(openedAt / 60_000);
        for (var i = 0; i < 8; i++) h = (h ^ (byte)(t >> (8 * i))) * 16777619u;
        return (int)(h % (uint)count);
    }

    /// <summary>The placements (object indexes on the map) of each rift spawner, in their MapData order.</summary>
    public static IReadOnlyList<int> RiftPoints(int mapId, string spawner) => riftPoints.GetOrAdd((mapId, spawner.ToLowerInvariant()), key =>
        WorldObjects.ObjectsOn(key.Map).Select((o, i) => (o, i)).Where(x => string.Equals(x.o.Rift, spawner, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i).ToArray());

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Map, string Spawner), int[]> riftPoints = new();

    /// <summary>Whether a rift placement stands now: its spawner is open and this opening uses this position.</summary>
    public static bool RiftShown(int mapId, int index, WorldObject o, long now, out long openedAt)
    {
        openedAt = 0;
        if (RiftOf(o.Rift) is not { } rift || OpenedAt(rift, now) is not { } opened) return false;
        var points = RiftPoints(mapId, rift.Spawner);
        openedAt = opened;
        return points.Count > 0 && points[PointOf(rift, opened, points.Count)] == index;
    }

    // ---------- Pools ----------

    /// <summary>A pool record: the kinds of its slots and, per position, the object index of each kind there.</summary>
    public sealed record Pool(int Key, IReadOnlyList<int> Slots, IReadOnlyList<Dictionary<int, int>> Positions);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (Dictionary<int, Pool> Pools, Dictionary<int, (int Pool, int Position)> ByIndex)> pools = new();

    static (Dictionary<int, Pool> Pools, Dictionary<int, (int Pool, int Position)> ByIndex) PoolsOn(int mapId) => pools.GetOrAdd(mapId, id =>
    {
        var objects = WorldObjects.ObjectsOn(id);
        var byPool = new Dictionary<int, (int[] Slots, List<(double X, double Y, double Z)> Order, Dictionary<(double, double, double), Dictionary<int, int>> At)>();
        for (var i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not { Pool: { } key } o) continue;
            if (!byPool.TryGetValue(key, out var p)) byPool[key] = p = (o.PoolSlots is { Length: > 0 } s ? s : [o.Env], [], []);
            var at = (o.Position[0], o.Position[1], o.Position[2]);
            if (!p.At.TryGetValue(at, out var kinds)) { p.At[at] = kinds = []; p.Order.Add(at); }
            kinds[o.Env] = i;
        }
        var result = new Dictionary<int, Pool>();
        var byIndex = new Dictionary<int, (int, int)>();
        foreach (var (key, p) in byPool)
        {
            var positions = p.Order.Select(at => p.At[at]).ToList();
            result[key] = new Pool(key, p.Slots, positions);
            for (var n = 0; n < positions.Count; n++)
                foreach (var index in positions[n].Values) byIndex[index] = (key, n);
        }
        return (result, byIndex);
    });

    public static IReadOnlyDictionary<int, Pool> PoolsOnMap(int mapId) => PoolsOn(mapId).Pools;
    public static (int Pool, int Position)? PoolPlace(int mapId, int index) => PoolsOn(mapId).ByIndex.TryGetValue(index, out var p) ? p : null;
}

/// <summary>
/// The random pools and rift uses of one copy of a field map (MapInstance.Pools; a session without a shared world keeps its own):
/// which position each pool slot stands on, since when, and when an opened slot comes back; how often each rift opening was used.
/// Thread-safe (the map's sessions may ask from their own loops).
/// </summary>
public sealed class PoolState(int mapId)
{
    readonly object gate = new();
    /// <summary>Per pool: per slot the position it stands on (-1 while waiting), since when, and when a waiting slot comes back.</summary>
    readonly Dictionary<int, (int Position, long Since, long Due)[]> slots = [];
    readonly Dictionary<(string Spawner, long Opened), int> riftUses = [];

    public int MapId { get; } = mapId;

    /// <summary>A placement shown on the map: the pool slot standing there (since when), or null. Settles due slots first.</summary>
    public (int Slot, long Since)? Standing(int index, Random rng, long now)
    {
        if (WorldPools.PoolPlace(MapId, index) is not { } place || !WorldPools.PoolsOnMap(MapId).TryGetValue(place.Pool, out var pool)) return null;
        lock (gate)
        {
            var state = Settle(pool, rng, now);
            for (var s = 0; s < state.Length; s++)
                if (state[s].Position == place.Position && pool.Positions[place.Position].GetValueOrDefault(pool.Slots[s], -1) == index) return (s, state[s].Since);
            return null;
        }
    }

    /// <summary>Opens (takes) the slot standing on this placement: it waits <paramref name="respawnMs"/>, then comes back at a random free
    /// position of its pool. False when nothing stands there (already opened by someone else).</summary>
    public bool Take(int index, Random rng, long now, long respawnMs)
    {
        if (WorldPools.PoolPlace(MapId, index) is not { } place || !WorldPools.PoolsOnMap(MapId).TryGetValue(place.Pool, out var pool)) return false;
        lock (gate)
        {
            var state = Settle(pool, rng, now);
            for (var s = 0; s < state.Length; s++)
                if (state[s].Position == place.Position && pool.Positions[place.Position].GetValueOrDefault(pool.Slots[s], -1) == index)
                {
                    state[s] = (-1, 0, now + Math.Max(0, respawnMs));
                    return true;
                }
            return false;
        }
    }

    /// <summary>The positions the pool's slots stand on now (checks and the staff view).</summary>
    public IReadOnlyList<int> Positions(int poolKey, Random rng, long now)
    {
        if (!WorldPools.PoolsOnMap(MapId).TryGetValue(poolKey, out var pool)) return [];
        lock (gate) return Settle(pool, rng, now).Select(s => s.Position).ToList();
    }

    /// <summary>
    /// The pool's slots, created on first use (each slot at a random free position, standing since long ago: the first sight is not a
    /// fresh spawn) and with every waiting slot whose time came put on a random position no other slot of the pool holds. Under the gate.
    /// </summary>
    (int Position, long Since, long Due)[] Settle(WorldPools.Pool pool, Random rng, long now)
    {
        if (!slots.TryGetValue(pool.Key, out var state))
        {
            slots[pool.Key] = state = new (int, long, long)[pool.Slots.Count];
            for (var s = 0; s < state.Length; s++) state[s] = (-1, 0, long.MinValue);
        }
        for (var s = 0; s < state.Length; s++)
        {
            if (state[s].Position >= 0 || state[s].Due > now) continue;
            var free = Enumerable.Range(0, pool.Positions.Count)
                .Where(p => pool.Positions[p].ContainsKey(pool.Slots[s]) && state.All(x => x.Position != p)).ToList();
            if (free.Count == 0) continue;
            var since = state[s].Due == long.MinValue ? 0 : state[s].Due;
            state[s] = (free[rng.Next(free.Count)], since, 0);
        }
        return state;
    }

    /// <summary>Counts one use of a rift opening; false when its InteractCount limit is reached.</summary>
    public bool UseRift(string spawner, long openedAt, int limit)
    {
        lock (gate)
        {
            var used = riftUses.GetValueOrDefault((spawner, openedAt));
            if (limit > 0 && used >= limit) return false;
            riftUses[(spawner, openedAt)] = used + 1;
            foreach (var old in riftUses.Keys.Where(k => k.Opened < openedAt - 86_400_000L).ToList()) riftUses.Remove(old);
            return true;
        }
    }

    public int RiftUses(string spawner, long openedAt)
    {
        lock (gate) return riftUses.GetValueOrDefault((spawner, openedAt));
    }
}

public sealed partial class MapInstance
{
    PoolState? pools;
    /// <summary>The random pools and rift uses of this copy of the map (WorldPools).</summary>
    public PoolState Pools => LazyInitializer.EnsureInitialized(ref pools, () => new PoolState(MapId));
}
