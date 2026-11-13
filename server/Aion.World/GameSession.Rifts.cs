using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Hidden cubes and Spacetime Rifts (fix/rifts-cubes, user 2026-11-13). Data: WorldPools (tools/data/world-pools.json).
//
// HIDDEN CUBES (and the Altgard treasure boxes E_D1_TreasureBoxRare_002, the same EnvObjData category HiddenCubeDark): 206 MapData pool
// records keep two cubes each at random positions of the record (WorldPools, PoolState). OBSERVED (rec 20261106-105448 #26049-#32481,
// 115817 #145327, #212583): the cube's 0x3634 is the ordinary placed-object form with relationship 7 and state bytes 01 01 06 when it
// has just (re)spawned (its create time 0.2-0.35 s before the message), as every fresh spawn of the recordings (139 of 139 with 06);
// the use takes 3.9 s (InteractTime 4000); the end: 0x363a {key, interactor, count 1, the key item used: 630900003
// Key_Treasurebox_A_s_01a_b}, 0x8d6d, then +650 ms 0x8d05 (EnvObjDie) and +700 ms 0x3635 {key, 4, 3}; no 0x363b. Those two were
// opened by another player, so the opener's own reward is CANDIDATE: 0xe33a and 0x561b reason 49 as the other boxes (EnvBoxes).
// Rules from EnvObjData/RewardData: bIsKeyOnly cubes open only with one of RequireValues (any key, RequireCount 1, consumed); the
// others open without a key (bIsOptionalRequirement) and give RewardName, with a key also BasicKeyRewards (CANDIDATE: both). The
// L2/D2 cubes have no RewardData row and use their faction's tables (CANDIDATE). Respawn: server-rules hidden_cubes (CANDIDATE 15 min).
//
// SPACETIME RIFTS: 10 rift spawners (WorldPools.Rifts): PeriodSpawn every 180 min from 02:00 KST, open RiftData LifeTime 600 s at one of
// 5 positions, 400 uses, level 45-100, use time 2.5 s. Only the map's own faction may use one (L10N MSG_RIFT_USE_FAIL_NOT_PROPER_TRIBE
// "Cannot use portals of opposing faction."; refused here with EnvObj_Fail_Relationship, CANDIDATE result). The move: TeleportEffect 30
// (Teleport_Gate_Default_001) to an exit point of Teleport.dat RiftPortal_X_to_Y_Teleport_n on the other faction's map (the exit with the
// opening's position number, CANDIDATE pairing). The visit lasts rift_contentsticket_default_time (GlobalSetting 3600000 ms, OFFICIAL
// value; the ticket's recharges are not modelled), then the player goes back to the rift they came through, in their own world (the
// return path, OURS: no recording of an end). Announcement: Notice_NT "A Spacetime Rift has appeared." (MSG_RIFT_SPAWN's text, CANDIDATE
// form: the official ServerMsg_NT for it was never recorded). No rift was ever recorded: their spawn, use and move bytes are CANDIDATE.
//
// WORLDS (OURS): the other faction's world is RiftWorlds.Pick (busy: spread over both worlds, quiet: the populated one); the visitor
// joins that world's copy of the map (MapLayer of the visited world). Any move off the other faction's field maps ends the visit.
public sealed partial class GameSession
{
    /// <summary>A fresh (re)spawn shows with state byte 06 for this long after it came up (OBSERVED 0.2-0.35 s; OURS margin).</summary>
    const long FreshSpawnMs = 3000;
    /// <summary>OBSERVED offsets of the opened cube's 0x8d05 and 0x3635 {4, 3} after its completion (rec 105448 #32391 -> #32477, #32481).</summary>
    const long CubeDieMs = 650, CubeGoneMs = 700;
    /// <summary>How often a player on a field map rechecks the pools and rifts around them (rifts open and close, cubes come and go; OURS).</summary>
    const long RiftTickMs = 2000;

    /// <summary>The world (WorldCatalog id) whose copy of the other faction's map this player visits through a rift, else null.</summary>
    int? riftVisitWorld;
    /// <summary>Where the visit ends (the rift used, on the player's own map) and when.</summary>
    (int Map, Vector3d At, long Until)? riftVisit;
    /// <summary>Set while the rift itself moves the player (the move keeps the visit).</summary>
    bool riftMoving;
    long riftAnnounced, riftTickAt;
    PoolState? ownPools;
    static int riftTurn;

    /// <summary>Checks only: rules other than server-rules.xml.</summary>
    internal RiftRules? RiftRulesForChecks { get; set; }
    RiftRules RiftRules => RiftRulesForChecks ?? ServerRules.Embedded.Rifts;

    /// <summary>The visited world now (checks, staff).</summary>
    public int? RiftVisitWorld => riftVisitWorld;
    public (int Map, Vector3d At, long Until)? RiftVisit => riftVisit;

    /// <summary>The pools and rift uses of the player's copy of the map (the shared map's, else this session's own).</summary>
    PoolState Pools => worldPlayer?.Map is { } map && map.MapId == worldMap ? map.Pools
        : ownPools is { } own && own.MapId == worldMap ? own : ownPools = new PoolState(worldMap);

    /// <summary>Checks only: the pools of the player's map copy.</summary>
    internal PoolState PoolsForChecks => Pools;

    /// <summary>The world a player of each world counts as for the rift choice: the visited world while visiting, else their own.</summary>
    int RiftPopulationWorld => riftVisitWorld ?? ServerId;

    // ---------- Visibility ----------

    /// <summary>Whether a placed object stands now: a pool placement only while a slot of its pool stands there, a rift only while open
    /// at this position; every other object always.</summary>
    bool PoolOrRiftShown(int index, WorldObject o, long now) =>
        o.Pool is not null ? instanceKey == 0 && Pools.Standing(index, rng, now) is not null
        : o.Rift is null || instanceKey == 0 && WorldPools.RiftShown(worldMap, index, o, now, out _);

    /// <summary>0x3634 of a pool or rift placement: since when it stands, and state byte 06 while that is fresh (OBSERVED for every fresh spawn).</summary>
    byte[] PoolOrRiftSpawn(int index, WorldObject o, long now)
    {
        var since = o.Pool is not null ? Pools.Standing(index, rng, now)?.Since ?? 0
            : WorldPools.RiftShown(worldMap, index, o, now, out var opened) ? opened : 0;
        return PoolSpawn(WorldObjects.FirstKey + index, o, since > 0 ? since : now, since > 0 && now - since < FreshSpawnMs);
    }

    /// <summary>
    /// 0x3634 of a pool or rift placement: the placed-object form (GamePackets.EnvObjSpawn) with the third state byte 06 for a fresh spawn
    /// (OBSERVED: the hidden cube E_D1_TreasureBoxRare_002, rec 20261106-105448 #26049 `... 01 01 06 <create time> 0*8 <unique key> 07 00 00`).
    /// </summary>
    public static byte[] PoolSpawn(int key, WorldObject o, long createTime, bool fresh) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(4).U8(0).I32(o.Env).F32(o.Position[0]).F32(o.Position[1]).F32(o.Position[2])
            .I32(0).F32(o.Yaw).U8(1).U8(1).U8(fresh ? 6 : 1).I64(createTime).Zeros(8).I32(o.UniqueKey).U8((byte)o.Relationship).U8(0).U8(0).ToArray();

    // ---------- Use ----------

    /// <summary>A cube or rift that may not be used now: the refusal (before the use starts), else null.</summary>
    async Task<byte[]?> PoolOrRiftRefusalAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        if (o.Pool is not null)
        {
            if (instanceKey != 0 || Pools.Standing(index, rng, now) is null)
            {
                Note($"InteractEnvObj refused on map {worldMap}: cube {o.Name} (key {key}) is not there (opened)");
                return EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_NotFoundObjectState, o.Env);
            }
            if (WorldPools.CubeOf(o.Env) is { KeyOnly: true } cube && await CubeKeyAsync(cube, ct) is null)
            {
                Note($"InteractEnvObj refused on map {worldMap}: cube {o.Name} (key {key}) opens only with a key");
                return EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_NotEnoughItem, o.Env);
            }
            return null;
        }
        if (WorldPools.RiftOf(o.Rift) is not { } rift) return null;
        var refusal = RiftRefusal(rift, index, o, now);
        if (refusal is not 0)
        {
            Note($"InteractEnvObj refused on map {worldMap}: rift {rift.Spawner} (key {key}) result {refusal}");
            return EnvBoxPackets.InteractEnvObjRefused(key, refusal, o.Env);
        }
        return null;
    }

    /// <summary>Why a rift may not be used (EResult), or 0: closed or not at this position, the other faction's, the level, the use limit.</summary>
    int RiftRefusal(WorldPools.RiftInfo rift, int index, WorldObject o, long now)
    {
        if (instanceKey != 0 || !WorldPools.RiftShown(worldMap, index, o, now, out var opened)) return EResult.EnvObj_Fail_NotFoundRiftData;
        if (WorldPools.FieldRace(rift.Map) is var own and not 0 && own != profileRace) return EResult.EnvObj_Fail_Relationship;
        if (level < rift.MinLevel || level > rift.MaxLevel) return EResult.EnvObj_Fail_InvalidRiftLevel;
        if (rift.Exits.Count == 0) return EResult.EnvObj_Fail_NotFoundRiftTeleportData;
        if (rift.Limit > 0 && Pools.RiftUses(rift.Spawner, opened) >= rift.Limit) return EResult.EnvObj_Fail_CanInteractOnlyOnce;
        return 0;
    }

    /// <summary>A completed use of a cube or a rift (CompleteUseAsync hands them here).</summary>
    async Task<List<byte[]>> PoolOrRiftUsedAsync(int index, WorldObject o, int key, long now, CancellationToken ct) =>
        o.Pool is not null ? await OpenCubeAsync(index, o, key, now, ct) : await UseRiftAsync(index, o, key, now, ct);

    /// <summary>The key a cube takes from the bag: the first of its RequireValues the character holds, else null.</summary>
    async Task<ItemState?> CubeKeyAsync(WorldPools.CubeInfo cube, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || cube.Keys.Count == 0) return null;
        IReadOnlyList<ItemState> bag = [];
        await Stored(async () => { bag = await store.InventoryAsync(character, ct); return []; });
        foreach (var id in cube.Keys)
            if (bag.FirstOrDefault(i => i.TemplateId == id && i.Quantity >= cube.KeyCount && i.Location == "bag") is { } row) return row;
        return null;
    }

    /// <summary>
    /// A cube opened: it is taken for everyone on the map copy (back after the respawn time at a random free position of its pool), the
    /// OBSERVED end (0x363a with the key item, 0x8d6d; 0x8d05 +650 ms, 0x3635 {4, 3} +700 ms), the reply, the reward (0xe33a, the items
    /// stored and sent as 0x561b reason 49; the key consumed with it), quest and achievement progress (UseEnvObj names the cubes).
    /// </summary>
    async Task<List<byte[]>> OpenCubeAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        var cube = WorldPools.CubeOf(o.Env);
        var keyRow = cube is null ? null : await CubeKeyAsync(cube, ct);
        if (cube is { KeyOnly: true } && keyRow is null)
            return [EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_NotEnoughItem, o.Env)];
        if (!Pools.Take(index, rng, now, RiftRules.CubeRespawnMinutes * 60_000L))
        {
            Note($"cube {o.Name} (key {key}) was opened by someone else first");
            return [EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_NotFoundObjectState, o.Env)];
        }
        var replies = new List<byte[]>
        {
            new Protocol.Native.Generated.InteractEnvObjCompletedNT { Key = key, InteractorKey = playerKey, InteractionCount = 1, ItemId = keyRow?.TemplateId ?? 0 }.ToArray(),
            GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjReply(key),
        };
        // The cube leaves this player's view now (no visibility pass sends its own despawn) and goes with the official sequence.
        shownObjects.Remove(index);
        Schedule(now + CubeDieMs, _ => [EnvBoxPackets.EnvObjDie(key)]);
        Schedule(now + CubeGoneMs, _ => [GamePackets.EnvObjDespawn(key, 4, 3)]);
        if (cube is not null)
        {
            var tables = new List<string>();
            if (LootTables.Embedded.HasTable(cube.Normal)) tables.Add(cube.Normal);
            if (keyRow is not null && LootTables.Embedded.HasTable(cube.Key)) tables.Add(cube.Key);
            var items = LootTables.Embedded.RollTables(tables, profileRace, rng);
            if (Store is { } store && CharacterId is { } character && (items.Count > 0 || keyRow is not null))
            {
                var granted = await Stored(async () => (keyRow is not null
                    ? await store.ConsumeAndGrantAsync(character, keyRow.InstanceId, keyRow.Revision, cube.KeyCount, items, ct)
                    : await store.GrantLootAsync(character, items, ct)) ? [[]] : []);
                if (granted.Count > 0)
                {
                    if (items.Count > 0) replies.Add(EnvBoxPackets.EnvObjReward(key, o.At, items));
                    replies.AddRange(await InventoryChangesAsync(ct, EnvBoxPackets.IncreaseEnvObjReward, DecreaseItemUse));
                }
            }
            Note($"cube {o.Name} opened on map {worldMap}{(keyRow is null ? "" : $" with key {keyRow.TemplateId}")}: "
                 + string.Join(", ", items.Select(i => $"{i.ItemId} x{i.Count}")));
        }
        if (questLog is not null) replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        return replies;
    }

    /// <summary>
    /// Through a rift: counted against its limit, the ordinary end of a use (completed, finish, available, reply), quest and achievement
    /// progress, then the move into the chosen world's copy of the other faction's map at an exit point (RiftWorlds.Pick, OURS).
    /// </summary>
    async Task<List<byte[]>> UseRiftAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        if (WorldPools.RiftOf(o.Rift) is not { } rift) return [];
        var refusal = RiftRefusal(rift, index, o, now);
        if (refusal is 0 && WorldPools.RiftShown(worldMap, index, o, now, out var opened) && !Pools.UseRift(rift.Spawner, opened, rift.Limit))
            refusal = EResult.EnvObj_Fail_CanInteractOnlyOnce;
        if (refusal is not 0) return [EnvBoxPackets.InteractEnvObjRefused(key, refusal, o.Env)];
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key),
            GamePackets.InteractEnvObjAvailableState(key), GamePackets.InteractEnvObjReply(key),
        };
        if (questLog is not null) replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        // The exit: the one with the opening's position number (5 positions, 5 exits; CANDIDATE pairing), else the first.
        WorldPools.RiftShown(worldMap, index, o, now, out var openedAt);
        var points = WorldPools.RiftPoints(worldMap, rift.Spawner);
        var number = WorldPools.PointOf(rift, openedAt, points.Count);
        var exit = rift.Exits[number < rift.Exits.Count ? number : 0];
        var visited = options.Worlds is { } worlds ? RiftWorlds.Pick(worlds, ServerId, RiftPopulation, RiftRules, Interlocked.Increment(ref riftTurn)) : (int?)null;
        Note($"rift {rift.Spawner} ({rift.Rift}) on map {worldMap} -> map {rift.ArrivalMap} at {rift.Teleport}#{number}, world {visited?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        var home = position ?? o.At;
        riftMoving = true;
        try { replies.AddRange(Teleport(rift.ArrivalMap, exit, now, 0, (byte)Math.Clamp(rift.Effect, 0, 255))); }
        finally { riftMoving = false; }
        riftVisitWorld = visited;
        riftVisit = (rift.Map, home, now + RiftRules.VisitMinutes * 60_000L);
        return replies;
    }

    /// <summary>Players in a world now for the rift choice: its own players at home plus the rift visitors in it (OURS).</summary>
    int RiftPopulation(int world)
    {
        if (options.World is not { } shared) return 0;
        try { return shared.Online.Values.Count(p => p.Session.RiftPopulationWorld == world); }
        catch (InvalidOperationException) { return 0; }   // the list changed under a reader (outside the world lock): counted as empty
    }

    /// <summary>Teleport: a move off the other faction's field maps (home, a dungeon, the Abyss) ends a rift visit.</summary>
    void RiftTeleporting(int mapId)
    {
        if (riftMoving) return;
        if (riftVisit is null && riftVisitWorld is null) return;
        var race = WorldPools.FieldRace(mapId);
        if (race != 0 && race != profileRace) return;   // still in the other faction's land (a resurrection there)
        riftVisitWorld = null;
        riftVisit = null;
    }

    // ---------- Tick: announcement, the visit's end, the field maps' pools and rifts ----------

    async Task<List<byte[]>> RiftTickAsync(long now, CancellationToken ct)
    {
        if (resources is null || reloading || now < riftTickAt) return [];
        riftTickAt = now + Math.Min(RiftTickMs, 1000);
        var replies = new List<byte[]>();
        // The announcement: once per opening, to everyone in the world (server-wide).
        if (RiftRules.Announce && WorldPools.Rifts.Count > 0)
        {
            var latest = WorldPools.Rifts.Values.Select(r => WorldPools.OpenedAt(r, now) ?? 0).Max();
            if (latest > riftAnnounced)
            {
                riftAnnounced = latest;
                replies.Add(Notice(RiftSpawnText));
            }
        }
        // A visitor of the other faction's land without a visit (logged in there, a staff move): the visit starts now, back to the
        // rift land's own rift exits on the player's map (OURS). Staff stay.
        var here = WorldPools.FieldRace(worldMap);
        if (riftVisit is null && here != 0 && here != profileRace && instanceKey == 0 && !IsAdmin
            && WorldPools.Rifts.Values.FirstOrDefault(r => r.Map == worldMap && r.Exits.Count > 0) is { } back)
            riftVisit = (back.ArrivalMap, back.Exits[0], now + RiftRules.VisitMinutes * 60_000L);
        if (riftVisit is { } visit && now >= visit.Until && !dead)
        {
            Note($"rift visit over: back to map {visit.Map} in world {ServerId}");
            riftVisitWorld = null;
            riftVisit = null;
            replies.AddRange(Teleport(visit.Map, visit.At, now, 0, (byte)(WorldPools.Rifts.Values.FirstOrDefault()?.Effect ?? 0)));
            return replies;
        }
        if (here != 0 && position is { } at && now >= riftVisibilityAt)
        {
            riftVisibilityAt = now + RiftTickMs;
            replies.AddRange(ObjectVisibility(at, now));
        }
        await Task.CompletedTask;
        return replies;
    }

    long riftVisibilityAt;

    /// <summary>L10N Message_MSG_RIFT_SPAWN (en-US): the text of the official rift notice.</summary>
    public const string RiftSpawnText = "A Spacetime Rift has appeared.";
}
