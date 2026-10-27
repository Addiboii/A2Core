using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

// Map events in this player's private instance copy (research/map-events.md): entering a dungeon or layer through an
// EnterInstanceLayer NPC (NpcConversation 0x4006) or a base-map trigger, the events of the map that can start (their
// quest step is active), the directives they run (spawns, NPC walks, captions, delays, the exit), the objectives the
// player completes in play, and the MapEvent packets (0x922e join, 0x922d progress, 0x922f leave). Spawn entries a
// StartSpawner directive names wait for it (dormant: HP 0, corpse removed, no respawn) instead of spawning at map load.
public sealed partial class GameSession : IMapEventHost
{
    static MapEventTables MapEventData => MapEventTables.Embedded;
    /// <summary>Where unsupported directives and timed-out objectives are reported (the host logs them); also kept in MapEventNotes.</summary>
    public static Action<string>? MapEventLog { get; set; }

    readonly List<MapEventRun> mapEventRuns = [];
    readonly List<byte[]> mapEventOut = [];
    readonly List<string> mapEventNotes = [];
    readonly HashSet<string> dormantSpawners = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> startedSpawners = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> removedObjects = [];
    readonly List<(string Spawner, long DueMs, bool Kill, bool Spawn)> pendingSpawns = [];
    (string Point, string Mode, long DueMs)? pendingExit;
    /// <summary>The map and position the player entered the dungeon from (the way back for LastPosition exits).</summary>
    (int Map, Vector3d At)? instanceReturn;
    long mapEventNow;
    static int nextMapEventId = 0x149942;   // the official run's id; one per run on this host

    public IReadOnlyList<MapEventRun> MapEventRuns => mapEventRuns;
    public IReadOnlyList<string> MapEventNotes => mapEventNotes;
    /// <summary>This player hosts its copy (its scripts run here); a party member in a copy someone else hosts runs none.</summary>
    bool RunsMapEvents => instanceKey != 0 && (copy is null || copy.Host == this) && MapEventData.Serves(worldMap);

    /// <summary>The session whose scripts run this copy when it is another player's (a party member's expedition), else null.</summary>
    GameSession? CopyOwner => copy is { } c && c.Host != this ? c.Host : null;

    /// <summary>
    /// A play event in a party member's copy goes to the owner's scripts; what they answer reaches the owner through its
    /// outbox (the drain broadcasts to the rest of the copy). Both sessions hold the same map lock, so the call is direct.
    /// </summary>
    bool ToCopyOwner(Func<GameSession, List<byte[]>> act)
    {
        if (CopyOwner is not { } owner) return false;
        var replies = act(owner);
        if (replies.Count > 0) owner.worldPlayer?.Send(replies);
        return true;
    }
    /// <summary>A dungeon or instance-layer map (the generated dungeon table, or the hand-entered instances).</summary>
    static bool IsInstanceMap(int mapId) => Instances.Of(mapId) is not null || MapEventData.DungeonOfMap(mapId) is { } room && room.Type != "Abyss";   // the Abyss is a shared zone

    List<byte[]> DrainMapEvents()
    {
        if (mapEventOut.Count == 0) return [];
        var taken = mapEventOut.ToList();
        mapEventOut.Clear();
        // The rest of the copy (party members) hears the script too: captions, notes, cutscenes, progress, NPC moves and
        // deaths (filtered to the NPCs each one sees); not this player's own map reloads (PCTeleport).
        if (worldPlayer?.Map is { Instance: not 0, Players.Count: > 1 } copy)
            copy.BroadcastAll(taken.Where(b => (b[0] | b[1] << 8) != 0x3621).ToList(), worldPlayer);
        return taken;
    }

    // ---------- Entering ----------

    /// <summary>0x4006 NpcConversation_RQ: u8 (0 OBSERVED live 2026-10-18: `064000F2070000`), i32 NPC key.</summary>
    List<byte[]> NpcConversation(byte[] plain, long now)
    {
        var npcKey = plain.Length >= 7 ? BitConverter.ToInt32(plain, 3) : 0;
        var replies = new List<byte[]> { GamePackets.NpcConversationReply(npcKey) };
        if (targets?.GetValueOrDefault(npcKey) is not { } npc) { Note($"NpcConversation: key {npcKey} is not an NPC here ({Convert.ToHexString(plain)})"); return replies; }
        if (MapEventData.EntryNpc(npc.NpcId) is not { } entry) { Note($"NpcConversation: {npc.Name} ({npc.NpcId}) has no entry function"); return replies; }
        // OBSERVED (#9652-9656): 0x4007, then the conversation partner leaves the view (0x3642 flag 1) BEFORE the move
        // (0x3647 self, 0x3621); the client's FinishNpcInteraction_RQ that follows gets the failure 0x3e08. Live 2026-10-18:
        // with the partner removed only at the layer's load the client stayed in its talk mode (no controls, camera fixed).
        var wasSeen = seenNpcs.Remove(npcKey);   // not again among the load's removals
        var moved = entry.Teleport != 0 ? TeleportToMap(entry.Teleport, now)   // NpcTeleport: the Abyss portal, the teleport managers
            : EnterPartyDungeon([entry.Dungeon], now);   // a party shares the copy (field events, layers)
        if (moved.Count == 0)
        {
            if (wasSeen) seenNpcs.Add(npcKey);
            Note($"NpcConversation: {npc.Name} -> dungeon {entry.Dungeon}/map {entry.Teleport}: no move (instance {instanceKey}, dead {dead}, reloading {reloading})");
            return replies;
        }
        replies.Add(GamePackets.Death(npcKey, 1));
        replies.AddRange(moved);
        return replies;
    }

    /// <summary>A base map's EnterInstanceLayer trigger actor (generated entries) under the player.</summary>
    List<byte[]> DungeonTrigger(Vector3d at, long now)
    {
        if (reloading || resources is null || dead || instanceKey != 0) return [];
        foreach (var entry in MapEventData.EntriesOn(worldMap))
            if (entry.Contains(at) && EnterDungeon(entry.Dungeon, now) is { Count: > 0 } moved) return moved;
        return [];
    }

    /// <summary>
    /// The move into a dungeon's private copy: allowed when an event of its map can start now (its quest step is active),
    /// when the map has no events, or for staff. Arrival: the current position under an instance layer that overlays this
    /// map, else the dungeon's start point. The official move: 0x3647 self (reason 12, effect 0x41) then 0x3621.
    /// </summary>
    List<byte[]> EnterDungeon(int dungeonId, long now, int copy = 0)
    {
        if (MapEventData.Dungeon(dungeonId) is not { } room || resources is null || reloading || dead) return [];
        // Only served dungeon maps (their scripts, points and spawns are generated); staff may look at the rest.
        if (!IsAdmin && !MapEventData.Serves(room.Map)) { Note($"dungeon {room.Name}: map {room.Map} is not served"); return []; }
        var events = MapEventData.EventsOn(room.Map);
        if (copy == 0 && !IsAdmin && events.Count > 0 && !events.Any(e => new MapEventRun(e, 0, this, now).Holds(e.Start, now))) return [];
        if (!IsAdmin && !MeetsEntry(room, out var why)) { Note($"dungeon {room.Name}: entry refused ({why})"); return []; }
        var at = position ?? spawn?.Position ?? default;
        var arrival = MapEventData.Volume(room.Map) is { } volume && volume.Base == worldMap ? at : ArrivalPoint(room) ?? at;
        instanceReturn = (worldMap, at);
        instanceKey = copy != 0 ? copy : playerKey;
        instanceExitAt = null;
        return InstanceMove(room.Map, arrival, now);
    }

    /// <summary>
    /// Logging in where a solo dungeon was left (its copy is gone): its start point when one of its map events is started by a
    /// quest step still open (IncompleteQuestStep in the event's start, holding now) and the entry rules allow it, else null.
    /// The arrival cutscene plays as on a normal entry. OURS: the official relog inside a dungeon is not recorded.
    /// </summary>
    Vector3d? QuestDungeonResume(int map, long now)
    {
        if (MapEventData.DungeonOfMap(map) is not { MaxMember: <= 1 } room || !MapEventData.Serves(map) || MapEventData.Volume(map) is not null) return null;
        var wanted = MapEventData.EventsOn(map).Any(e => e.Start is { Length: > 0 } start
            && start.Any(g => g.Any(c => c.Type == "IncompleteQuestStep")) && new MapEventRun(e, 0, this, now).Holds(start, now));
        if (!wanted || !MeetsEntry(room, out _) || ArrivalPoint(room) is not { } arrival) return null;
        pendingArrivalCutscene = room.ArrivalCutscene is { Length: > 0 } cutscene ? (cutscene, false) : null;
        return arrival;
    }

    /// <summary>
    /// The observed instance-layer move: 0x3647 self (reason 12, effect 0x41), then 0x3621 with the same reason and effect; the
    /// load that follows is the short one with the map environment and the appearance's arrival bytes. Staff `LayerTest`
    /// leaves pieces out (<see cref="layerTestMask"/>) to find which of them locks the live client (2026-10-19).
    /// </summary>
    List<byte[]> InstanceMove(int map, Vector3d arrival, long now, string cutscene = "", byte? teleportEffect = null)
    {
        pendingInstanceMove = true;
        // A PCTeleport into the dungeon: reason 2 and the directive's effect in the self-disappear and the load (OBSERVED 2026-10-19,
        // the lake cave's exit: 0x3647 "02 0c", 0x3621 reason 2 effect 12); the seamless layer move keeps 12 / 0x41.
        var (reason, effect) = teleportEffect is { } given ? ((byte)2, given)
            : (TestPiece(LayerPieceMoveReason) ? InstanceReason : (byte)0, TestPiece(LayerPieceMoveReason) ? InstanceEffect : (byte)0);
        var move = Teleport(map, arrival, now, reason, effect, cutscene);
        return TestPiece(LayerPieceSelfDisappear) ? [PlayerPackets.CharDisappear(playerKey, reason, effect), .. move] : move;
    }

    /// <summary>Staff: Robin's layer (dungeon 96) entered with only the given pieces of the seamless move (see LayerPiece*).</summary>
    public List<byte[]> LayerTest(int mask, long now)
    {
        layerTestMask = mask;
        return EnterDungeonById(96, now);
    }

    public const int LayerPieceSelfDisappear = 1, LayerPieceMoveReason = 2, LayerPieceShortLoad = 4, LayerPieceEnvironment = 8, LayerPieceAppearance = 16;
    /// <summary>
    /// Pieces of the seamless move to send (a LayerPiece* mask; staff LayerTest overrides it until relog). Live 2026-10-19: the
    /// full observed set (31) locked the client's controls after the layer load every time; 0x3621 with reason 12 and effect
    /// 0x41 alone (2) gave a natural transition and a free player. One of the other four pieces locks the client (unknown
    /// which: the self 0x3647, the short load, the map environment, or the appearance's arrival bytes); until that is found
    /// the default is the move message alone with the full load.
    /// </summary>
    public const int LayerPiecesDefault = LayerPieceMoveReason;
    int layerTestMask = LayerPiecesDefault;
    bool TestPiece(int piece) => (layerTestMask & piece) != 0;
    /// <summary>The pending map load is an instance-layer move in or out (whatever reason byte 0x3621 carried).</summary>
    bool pendingInstanceMove;
    /// <summary>
    /// A cutscene to send as the Cutscene event function once the load completes: a PCTeleport's arrival cutscene (value 4, the
    /// server waits for its report) or the dungeon's own (DungeonInfo.ArrivalCutscene, no wait).
    /// </summary>
    (string Name, bool Wait)? pendingArrivalCutscene;

    /// <summary>The hold inside a party dungeon after its clear before the way out (OURS; the official CompleteStateHoldingTime is 600 s).</summary>
    public const long PartyDungeonHoldMs = 60_000;

    /// <summary>
    /// A PartyDungeon portal used (E_PD_*_EnterPortal: the Easy/Normal/Hard dungeons of its expedition). A party member
    /// already inside one of them takes the others into the same copy (its scripts, monsters and clear are shared);
    /// otherwise the difficulty an active quest step waits for (its ClearMapEvent target is one of the dungeon's events),
    /// else the first (Easy), in a fresh copy. The official client goes through its expedition window (DungeonRoom*,
    /// PartyDungeonEnter; their replies are unrecorded); here the portal moves the player at once.
    /// </summary>
    /// <summary>Builder EnterDungeon: any dungeon by id (a party shares the copy), for the ones the client enters through unrecorded windows (raids).</summary>
    public List<byte[]> EnterDungeonById(int dungeonId, long now) => EnterPartyDungeon([dungeonId], now);

    List<byte[]> EnterPartyDungeon(IReadOnlyList<int> dungeons, long now)
    {
        if (instanceKey != 0 || dungeons.Count == 0) return [];
        if (Social is { } s && worldPlayer is not null && s.PartyOf(worldPlayer) is { } party)
            foreach (var member in party.Members)
                if (member != worldPlayer && member.Map is { Instance: not 0 } inside
                    && dungeons.FirstOrDefault(id => MapEventData.Dungeon(id)?.Map == inside.MapId) is not 0 and var joined
                    && (IsAdmin || !CopyFull(inside, MapEventData.Dungeon(joined)!)))   // a full copy (sealed dungeons: one player) means a copy of one's own
                    return EnterDungeon(joined, now, inside.Instance);
        var wanted = questLog?.OpenMapEventTargets().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var chosen = dungeons.FirstOrDefault(id => MapEventData.Dungeon(id) is { } room && MapEventData.EventsOn(room.Map).Any(e => wanted.Contains(e.Name)));
        return EnterDungeon(chosen != 0 ? chosen : dungeons[0], now);
    }

    /// <summary>The dungeon's start: the hand-entered arrival, Dungeon.dat's race start point, else a PCStart / _Respawn / Start teleport point of the map.</summary>
    Vector3d? ArrivalPoint(DungeonInfo room)
    {
        if (Instances.Of(room.Map) is { } hand) return new Vector3d(hand.Arrival[0], hand.Arrival[1], hand.Arrival[2]);
        // The MapData teleport points, else the map points file (the expeditions keep theirs there: KraoCave_Respawn, ...).
        var points = MapEventData.Points(room.Map).Count > 0 ? MapEventData.Points(room.Map) : data.MapPoints(room.Map);
        var named = profileRace == 2 ? room.StartDark : room.StartLight;
        var candidates = new[] { named }.Concat(points.Keys.Where(k => k.Contains("PCStart", StringComparison.OrdinalIgnoreCase)))
            .Concat(points.Keys.Where(k => k.EndsWith("_Respawn", StringComparison.OrdinalIgnoreCase)))
            .Concat(points.Keys.Where(k => k.Contains("Start", StringComparison.OrdinalIgnoreCase)))
            .Concat(points.Keys);
        foreach (var candidate in candidates)
            if (candidate is not null && points.TryGetValue(candidate, out var p)) return new Vector3d(p[0], p[1], p[2]);
        return data.MapStart(room.Map)?.Position;
    }

    // ---------- After the map load ----------

    /// <summary>The map whose events are running (a reload of the same map, PCTeleport, keeps them).</summary>
    int mapEventsMap;

    /// <summary>Right after the map's targets are set: the deferred spawn entries go dormant, before the NPC list is sent.</summary>
    void MapEventsPrepare()
    {
        if (mapEventsMap == worldMap && instanceKey != 0 && mapEventRuns.Count > 0) return;
        mapEventsMap = 0;
        mapEventRuns.Clear();
        dormantSpawners.Clear();
        startedSpawners.Clear();
        removedObjects.Clear();
        pendingSpawns.Clear();
        pendingExit = null;
        npcBuffs.Clear();
        if (!RunsMapEvents || targets is null) return;
        dormantSpawners.UnionWith(MapEventData.Deferred(worldMap));
        foreach (var t in targets.Values)
            if (dormantSpawners.Contains(t.Name))
            {
                t.Hp = 0;
                t.CorpseRemoved = true;
                respawnAt.Remove(t.Key);
            }
    }

    /// <summary>After the NPC list: the map's startable events join (0x922e) and begin; a PCTeleport's arrival cutscene plays.</summary>
    List<byte[]> MapEventsStart(long now)
    {
        var arrival = pendingArrivalCutscene is { } cutscene ? GamePackets.Cutscene(cutscene.Name, cutscene.Wait) : null;
        pendingArrivalCutscene = null;
        var started = MapEventsJoin(now);
        if (arrival is not null) started.Insert(0, arrival);   // OBSERVED order: the cutscene, then the joins (2026-10-19)
        return started;
    }

    List<byte[]> MapEventsJoin(long now)
    {
        // In a party member's copy: the owner's running events (their joins), none of this player's own.
        if (CopyOwner is { } owner)
            return owner.mapEventRuns.Select(run => GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.StepOrder, run.Goals, run.StartedMs, run.Variables)).ToList();
        if (!RunsMapEvents) return [];
        // A reload of the same map (a PCTeleport) keeps the runs and the client keeps its event state: the official boss-room
        // move in the 2026-10-19 recording sent no 0x922e after the load (the earlier re-join was a guess).
        if (mapEventsMap == worldMap && mapEventRuns.Count > 0) return [];
        mapEventsMap = worldMap;
        mapEventNow = now;
        return StartEligibleEvents(now);
    }

    /// <summary>The map's events not running yet whose start conditions hold now join (0x922e) and begin (at entry, and when a field event's phase changes).</summary>
    List<byte[]> StartEligibleEvents(long now)
    {
        if (!RunsMapEvents) return [];
        mapEventNow = now;
        var replies = new List<byte[]>();
        foreach (var definition in MapEventData.EventsOn(worldMap))
        {
            if (mapEventRuns.Any(r => r.Name == definition.Name)) continue;
            var run = new MapEventRun(definition, Interlocked.Increment(ref nextMapEventId), this, now);
            if (!run.Holds(definition.Start, now)) continue;
            mapEventRuns.Add(run);
            replies.Add(GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.StepOrder, run.Goals, run.StartedMs, run.Variables));
            run.Start(now);
        }
        replies.AddRange(DrainMapEvents());
        return replies;
    }

    // ---------- Events from play ----------

    List<byte[]> MapEventKilled(Target target, long now)
    {
        if (ToCopyOwner(o => o.MapEventKilled(target, now))) return [];
        CubeKilled(target, now);
        if (mapEventRuns.Count == 0) return DrainMapEvents();
        mapEventNow = now;
        var alive = targets?.Values.Any(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase) && !t.Dead) == true;
        foreach (var run in mapEventRuns.ToList()) run.Killed(target.NpcId, target.Name, target.Hostile, alive, now);
        return DrainMapEvents();
    }

    List<byte[]> MapEventHp(Target target, long now)
    {
        if (ToCopyOwner(o => o.MapEventHp(target, now)) || mapEventRuns.Count == 0 || target.HpMax <= 0) return [];
        mapEventNow = now;
        var percent = (int)Math.Ceiling(target.Hp * 100.0 / target.HpMax);
        foreach (var run in mapEventRuns.ToList())
        {
            run.CombatStarted(now);   // the player's own hit is a fight too (PCCombatStart), whether or not the client bracketed it
            run.HpChanged(target.NpcId, target.Name, percent, now);
        }
        return DrainMapEvents();
    }

    /// <summary>A talk with an NPC: the step talk (0x8d2b kind 2) names the objective; closing the dialog (0xe242) counts CloseNpc only.</summary>
    List<byte[]> MapEventTalked(int npcKey, int? index, bool close, long now)
    {
        if (ToCopyOwner(o => o.MapEventTalked(npcKey, index, close, now)) || mapEventRuns.Count == 0 || targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.Talked(npc.NpcId, npc.Name, index, close, now);
        return DrainMapEvents();
    }

    List<byte[]> MapEventUsedObject(string spawnName, long now)
    {
        if (ToCopyOwner(o => o.MapEventUsedObject(spawnName, now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.UsedObject(spawnName, now);
        return DrainMapEvents();
    }

    List<byte[]> MapEventPosition(Vector3d at, long now)
    {
        if (ToCopyOwner(o => o.MapEventPosition(at, now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        var triggers = WorldObjects.TriggersOn(worldMap);
        bool InsideTrigger(string name) => triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Contains(at));
        bool InsideSubzone(string name) => Subzones.InSubzone(worldMap, name, at);
        foreach (var run in mapEventRuns.ToList()) run.PositionAt(InsideTrigger, InsideSubzone, now);
        return DrainMapEvents();
    }

    List<byte[]> MapEventCombatStarted(long now)
    {
        if (ToCopyOwner(o => o.MapEventCombatStarted(now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.CombatStarted(now);
        return DrainMapEvents();
    }

    /// <summary>ClientObjectiveComplete_RQ kind 2: a map event objective the client finished itself (cutscene, note, talk, guide).</summary>
    List<byte[]> MapEventClientObjective(int mapEventId, int index, long now)
    {
        // A member finished a client-side objective (a note, a cutscene) of the owner's run: that run takes it.
        if (CopyOwner is { } owner && owner.mapEventRuns.FirstOrDefault(r => r.Id == mapEventId) is { } theirs)
        {
            owner.mapEventNow = now;
            theirs.ClientObjective(index, now);
            var drained = owner.DrainMapEvents();
            if (drained.Count > 0) owner.worldPlayer?.Send(drained);
            return [GamePackets.ClientObjectiveReply(2, mapEventId, index, 0)];
        }
        mapEventNow = now;
        var run = mapEventRuns.FirstOrDefault(r => r.Id == mapEventId);
        var done = run?.ClientObjective(index, now) == true;
        var replies = new List<byte[]> { GamePackets.ClientObjectiveReply(2, mapEventId, index, done || run is not null ? 0 : 1) };
        replies.AddRange(DrainMapEvents());
        return replies;
    }

    /// <summary>0xe334: a cutscene ended on the client; the CompleteCutscene objective naming it (own runs, or the copy owner's) is done.</summary>
    List<byte[]> MapEventCutsceneDone(string cutscene, long groupKey, long now)
    {
        var replies = new List<byte[]> { GamePackets.CutsceneCompleteReply(cutscene, groupKey) };
        if (CopyOwner is { } owner)
        {
            owner.mapEventNow = now;
            var theirs = false;
            foreach (var run in owner.mapEventRuns) theirs |= run.CutsceneDone(cutscene, now);
            var drained = owner.DrainMapEvents();
            if (drained.Count > 0) owner.worldPlayer?.Send(drained);
            if (theirs) return replies;
        }
        mapEventNow = now;
        foreach (var run in mapEventRuns) run.CutsceneDone(cutscene, now);
        replies.AddRange(DrainMapEvents());
        return replies;
    }

    /// <summary>Each tick: due spawns, NPC arrivals, the runs' timers and the exit.</summary>
    List<byte[]> MapEventTick(long now)
    {
        if (mapEventRuns.Count == 0 && pendingSpawns.Count == 0 && pendingExit is null && npcBuffs.Count == 0 && copy is null) return [];
        mapEventNow = now;
        DungeonTimeLimit(now);
        var phase = DungeonPhaseTick(now);
        if (phase.Count > 0) mapEventOut.InsertRange(0, phase);
        NpcBuffTick(now);
        foreach (var pending in pendingSpawns.Where(p => now >= p.DueMs).ToList())
        {
            pendingSpawns.Remove(pending);
            if (pending.Spawn) DoSpawn(pending.Spawner);
            else DoDespawn(pending.Spawner, pending.Kill);
        }
        foreach (var (key, route) in ai.TakeArrivals())
            if (targets?.GetValueOrDefault(key) is { } npc)
                foreach (var run in mapEventRuns.ToList()) run.NpcArrived(npc.Name, route, now);
        foreach (var run in mapEventRuns.ToList()) run.Tick(now);
        var replies = DrainMapEvents();
        if (pendingExit is { } exit && now >= exit.DueMs && !reloading)
        {
            pendingExit = null;
            replies.AddRange(LeaveInstance(now, exit.Mode));
        }
        return replies;
    }

    // ---------- The host: what the runs do to the world ----------

    IEnumerable<Target> SpawnerNpcs(string spawner) =>
        targets?.Values.Where(t => string.Equals(t.Name, spawner, StringComparison.OrdinalIgnoreCase)) ?? [];

    IEnumerable<(int Index, WorldObject Object)> SpawnerObjects(string spawner) =>
        WorldObjects.ObjectsOn(worldMap).Select((o, i) => (i, o)).Where(x => string.Equals(x.o.Name, spawner, StringComparison.OrdinalIgnoreCase));

    /// <summary>Objects a map event keeps out of sight: dormant spawn entries and the ones it removed.</summary>
    bool MapEventHidden(int index, WorldObject o)
    {
        var host = CopyOwner ?? this;
        return host.removedObjects.Contains(index) || host.dormantSpawners.Count > 0 && host.dormantSpawners.Contains(o.Name);
    }

    /// <summary>
    /// spawn_act_id by NpcData id: the SpawnActName's action, an id per model that the extracted tables do not carry (the HQ002
    /// totem's "Spawn" is 1000597, the vessel's 1000600), so only the OBSERVED ones (capture 20261019-231752 #60769 and the
    /// Drakan after it). The vessel also carries abnormal 10000018 (nameplate hidden, untargetable) for its 2666 ms arrival.
    /// </summary>
    static readonly Dictionary<int, (int Act, int Abnormal, int AbnormalMs)> ObservedSpawnActs = new()
    {
        [2910018] = (1000600, 10000018, 2666),   // N_Common_DredgionB_01_001 "Spawn"
        [2290002] = (720135, 0, 0),              // M_L0_Starter_HQ002_Drakan_01 "Spawn_Npc_From_Dred_01"
        [2290011] = (720135, 0, 0),              // M_L0_Starter_HQ002_Drakan_02 "Spawn_Npc_From_Dred_01"
        [2290017] = (1000597, 0, 0),             // M_L0_HQ_DraTotemA_01_01 "Spawn"
    };

    /// <summary>A spawner's NPC as it appears when the map event starts it: reason NpcSpawn (OBSERVED), with its spawn act when known.</summary>
    NpcAppearance SpawnAppearance(Target t)
    {
        var appear = t.Appearance() with { Reason = NpcAppearance.NpcSpawnReason };
        if (!ObservedSpawnActs.TryGetValue(t.NpcId, out var act)) return appear;
        appear = appear with { SpawnAct = act.Act };
        return act.Abnormal == 0 ? appear : appear with
        {
            Abnormals = [new AbnormalWire(1, act.Abnormal, act.AbnormalMs, mapEventNow + act.AbnormalMs, CasterKey: t.Key, RangeBase: t.Position)],
        };
    }

    void DoSpawn(string spawner)
    {
        dormantSpawners.Remove(spawner);
        startedSpawners.Add(spawner);
        foreach (var t in SpawnerNpcs(spawner))
        {
            if (!t.Dead && !t.CorpseRemoved) continue;
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            t.Held = false;
            t.Immune = false;
            respawnAt.Remove(t.Key);
            if (ai.Brains.TryGetValue(t.Key, out var brain)) t.Position = brain.Home;
            seenNpcs.Add(t.Key);
            mapEventOut.Add(GamePackets.NpcAppear(SpawnAppearance(t)));
        }
        foreach (var (index, o) in SpawnerObjects(spawner))
        {
            removedObjects.Remove(index);
            if (position is { } at && o.At.DistanceSquared(at) <= WorldObjects.VisibleRange * WorldObjects.VisibleRange && shownObjects.Add(index))
                mapEventOut.Add(GamePackets.EnvObjSpawn(WorldObjects.FirstKey + index, o.Env, o.At, o.Yaw, mapEventNow, o.UniqueKey, (byte)o.Relationship));
        }
    }

    /// <summary>EndSpawner: the NPCs vanish (0x3642 flag 7, OBSERVED for Robin); KillSpawner / a killed barrier: they die (HP 0, flag 3).</summary>
    void DoDespawn(string spawner, bool kill)
    {
        var all = string.Equals(spawner, "all", StringComparison.OrdinalIgnoreCase);
        foreach (var t in (all ? targets?.Values.Where(t => t.Key != Combat.TrainingDummyKey) ?? [] : SpawnerNpcs(spawner)).ToList())
        {
            if (t.Dead && t.CorpseRemoved) continue;
            t.Hp = 0;
            t.CorpseRemoved = true;
            respawnAt.Remove(t.Key);
            mapEventOut.AddRange(ai.Killed(t));
            if (kill) mapEventOut.Add(GamePackets.NpcHp(t.Key, 0));
            mapEventOut.Add(GamePackets.Death(t.Key, kill ? 3 : 7));
            seenNpcs.Remove(t.Key);
        }
        foreach (var (index, _) in (all ? WorldObjects.ObjectsOn(worldMap).Select((o, i) => (i, o)) : SpawnerObjects(spawner)).ToList())
        {
            removedObjects.Add(index);
            if (shownObjects.Remove(index)) mapEventOut.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index));
        }
    }

    void Note(string text)
    {
        if (mapEventNotes.Count < 200) mapEventNotes.Add(text);
        MapEventLog?.Invoke(text);
    }

    void IMapEventHost.Spawn(string spawner, int delayMs)
    {
        if (delayMs > 0) pendingSpawns.Add((spawner, mapEventNow + delayMs, false, true));
        else DoSpawn(spawner);
    }

    void IMapEventHost.Despawn(string spawner, int delayMs, bool kill)
    {
        if (delayMs > 0) pendingSpawns.Add((spawner, mapEventNow + delayMs, kill, false));
        else DoDespawn(spawner, kill);
    }

    /// <summary>SummonAroundTarget: the spawner's NPCs placed around the target NPC (or the player) and spawned.</summary>
    void IMapEventHost.SummonAround(string target, string spawner, int count, double radius)
    {
        var centre = SpawnerNpcs(target).FirstOrDefault(t => !t.Dead)?.Position ?? position ?? spawn?.Position ?? default;
        var index = 0;
        foreach (var t in SpawnerNpcs(spawner).Where(t => t.Dead || t.CorpseRemoved).Take(Math.Max(1, count)))
        {
            var angle = index++ * 2 * Math.PI / Math.Max(1, count);
            t.Position = centre with { X = centre.X + Math.Cos(angle) * Math.Min(radius, 400), Y = centre.Y + Math.Sin(angle) * Math.Min(radius, 400) };
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            seenNpcs.Add(t.Key);
            mapEventOut.Add(GamePackets.NpcAppear(t.Appearance()));
        }
        startedSpawners.Add(spawner);
    }

    void IMapEventHost.MoveNpc(string spawner, string route)
    {
        if (data.Route(worldMap, route) is not { } path) { Note($"map event route {route} is not on map {worldMap}"); return; }
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead)) mapEventOut.AddRange(ai.Walk(t, path, mapEventNow));
    }

    void IMapEventHost.FollowPc(string spawner, bool follow)
    {
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead)) mapEventOut.AddRange(ai.Follow(t, playerKey, follow, mapEventNow));
    }

    /// <summary>NpcRotate: face the named NPC or object, else the player.</summary>
    void IMapEventHost.RotateNpc(string spawner, string target)
    {
        Vector3d? aim = target.Length == 0 ? position
            : SpawnerNpcs(target).FirstOrDefault(t => !t.Dead)?.Position ?? SpawnerObjects(target).Select(x => (Vector3d?)x.Object.At).FirstOrDefault();
        if (aim is not { } goal) return;
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead))
            mapEventOut.AddRange(ai.Face(t, NpcAi.HeadingTo(t.Position, (goal.X, goal.Y)), mapEventNow));
    }

    void IMapEventHost.Talk(string talk) => mapEventOut.Add(GamePackets.Caption(talk));
    void IMapEventHost.ChatBubble(string spawner, string talk) => mapEventOut.Add(GamePackets.Caption(talk));

    void IMapEventHost.SocialAction(string spawner, int action, bool stop)
    {
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead)) mapEventOut.Add(GamePackets.SocialAction(t.Key, action, stop));
    }

    void IMapEventHost.SetAggroPc(string spawner)
    {
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead)) mapEventOut.AddRange(ai.ForceAggro(t, playerKey, mapEventNow));
    }

    /// <summary>SetAggroNpc [spawner, target spawner, aggro]: each NPC of the spawner attacks the nearest living NPC of the target.</summary>
    void IMapEventHost.SetAggroNpc(string spawner, string target)
    {
        var prey = SpawnerNpcs(target).Where(t => !t.Dead).ToList();
        if (prey.Count == 0) { Note($"SetAggroNpc {spawner} -> {target}: no living target"); return; }
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead))
            mapEventOut.AddRange(ai.ForceAggroNpc(t, prey.MinBy(p => p.Position.DistanceSquared(t.Position))!, mapEventNow));
    }

    /// <summary>Where a player revives inside a dungeon: the running stage's RevivalPoint (a teleport point), else the dungeon's start; null elsewhere.</summary>
    Vector3d? DungeonRevivalPoint()
    {
        if (instanceKey == 0 || !MapEventData.Serves(worldMap)) return null;
        var points = MapEventData.Points(worldMap);
        foreach (var run in (CopyOwner ?? this).mapEventRuns)
            if (run.Stage?.RevivalPoint is { Length: > 0 } name && points.TryGetValue(name, out var p)) return new Vector3d(p[0], p[1], p[2]);
        return MapEventData.DungeonOfMap(worldMap) is { } room ? ArrivalPoint(room) : null;
    }

    /// <summary>
    /// ChangeRelationshipEntity: the spawner's NPCs take the relationship (Monster: attackable, and fighting when aggressive;
    /// NPC_Companion / NPC_Neutral: at peace), 0x39cd per visible NPC. Dormant ones spawn with it.
    /// </summary>
    void IMapEventHost.SetRelationship(string spawner, string relationship)
    {
        if (MapEventTables.Relationship(relationship) is not { } value) { Note($"ChangeRelationshipEntity {spawner} {relationship}: unknown relationship"); return; }
        foreach (var t in SpawnerNpcs(spawner))
        {
            if (t.Relationship == value) continue;
            t.Relationship = value;
            if (!t.Dead) mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, value));
        }
    }

    // ---------- Scripted abnormals ----------

    /// <summary>A scripted abnormal on an NPC of this copy (0x382a on its key) and what the server makes of it.</summary>
    sealed class NpcBuff
    {
        public required int Uid, AbnormalId;
        public required long ExpireAt;
        public bool Held, Immune;
        /// <summary>The relationship the NPC had before a ChangeRelationshipEntity effect, restored when the abnormal ends.</summary>
        public int? Restore;
    }

    readonly Dictionary<int, List<NpcBuff>> npcBuffs = [];
    int npcBuffUid = 2_000_000;

    /// <summary>
    /// What an abnormal's SkillAbnormalEffect rows mean to the server: held (stun, hold, stone, sleep, a blocked move), immune
    /// (an immunity or untargetable), a relationship. Other effects (stat changes, dots, casts) only show the icon.
    /// </summary>
    static (bool Held, bool Immune, int? Relationship) AbnormalModel(int abnormalId)
    {
        bool held = false, immune = false;
        int? relationship = null;
        foreach (var (_, type, values) in Boxes.Abnormal(abnormalId)?.Effects ?? [])
            switch (type)
            {
                case "Stun" or "Hold" or "Stone" or "Sleep" or "Aerial" or "Fear" or "Knockdown" or "Root" or "BlockMovePosition": held = true; break;
                case "ImmuneSkillEffectGroup" or "ImmuneDecreaseHP" or "BlockTargeting" or "Invincible": immune = true; break;
                case "ChangeRelationshipEntity" when values.Count >= 2 && MapEventTables.Relationship(values[1]) is { } relation: relationship = relation; break;
            }
        return (held, immune, relationship);
    }

    /// <summary>AddAbnormal / RemoveAbnormal: on the player (empty target, the buff system) or on a spawner's living NPCs.</summary>
    void IMapEventHost.Abnormal(int id, int durationMs, string target, bool remove)
    {
        if (target.Length == 0)
        {
            mapEventOut.AddRange(remove ? RemoveScriptedBuffs(id) : AddScriptedBuff(id, durationMs, mapEventNow));
            return;
        }
        foreach (var t in SpawnerNpcs(target).Where(t => !t.Dead))
        {
            if (remove) RemoveNpcBuffs(t, b => b.AbnormalId == id, ExpireEventFunction);
            else AddNpcBuff(t, id, durationMs);
        }
    }

    /// <summary>One abnormal per id on an NPC (a new one replaces it, CANDIDATE); caster the NPC itself, level 1, range base its position.</summary>
    void AddNpcBuff(Target t, int abnormalId, int durationMs)
    {
        RemoveNpcBuffs(t, b => b.AbnormalId == abnormalId, ExpireReplaced);
        var (held, immune, relationship) = AbnormalModel(abnormalId);
        var permanent = durationMs < 0;
        var buff = new NpcBuff { Uid = ++npcBuffUid, AbnormalId = abnormalId, ExpireAt = permanent ? long.MaxValue : mapEventNow + durationMs, Held = held, Immune = immune };
        if (relationship is { } shown && shown != t.Relationship)
        {
            buff.Restore = t.Relationship;
            t.Relationship = shown;
            mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, shown));
        }
        (npcBuffs.TryGetValue(t.Key, out var list) ? list : npcBuffs[t.Key] = []).Add(buff);
        RefreshNpcFlags(t);
        mapEventOut.Add(PlayerPackets.AbnormalAdd(t.Key, [new AbnormalWire(buff.Uid, abnormalId, permanent ? -1 : durationMs,
            permanent ? PassiveExpireTime : mapEventNow + durationMs, CasterKey: t.Key, Level: 1, RangeBase: t.Position)]));
    }

    void RemoveNpcBuffs(Target t, Func<NpcBuff, bool> which, byte reason)
    {
        if (!npcBuffs.TryGetValue(t.Key, out var list)) return;
        var gone = list.Where(which).ToList();
        if (gone.Count == 0) return;
        list.RemoveAll(gone.Contains);
        if (list.Count == 0) npcBuffs.Remove(t.Key);
        foreach (var b in gone)
            if (b.Restore is { } back)
            {
                t.Relationship = back;
                mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, back));
            }
        RefreshNpcFlags(t);
        mapEventOut.Add(PlayerPackets.AbnormalRemove(t.Key, gone.Select(b => (b.Uid, reason)).ToList()));
    }

    void RefreshNpcFlags(Target t)
    {
        var list = npcBuffs.GetValueOrDefault(t.Key);
        t.Held = list?.Any(b => b.Held) == true;
        t.Immune = list?.Any(b => b.Immune) == true;
    }

    /// <summary>Timed NPC abnormals run out (reason 1 Timeout); a dead NPC's go with its corpse.</summary>
    void NpcBuffTick(long now)
    {
        if (npcBuffs.Count == 0 || targets is null) return;
        foreach (var (key, list) in npcBuffs.ToList())
        {
            if (!targets.TryGetValue(key, out var t) || t.Dead)
            {
                npcBuffs.Remove(key);
                if (t is not null) { t.Held = false; t.Immune = false; }
                continue;
            }
            if (list.Any(b => now >= b.ExpireAt)) RemoveNpcBuffs(t, b => now >= b.ExpireAt, ExpireTimeout);
        }
    }

    /// <summary>
    /// PCTeleport: a teleport point of this map (a map reload in place; the official message for a same-map move is not known),
    /// else a Teleport.dat name (live 2026-10-19: the lake cave's Dungeon_L_0016_Teleport_BossRoom and its exit
    /// Dungeon_0005_StartPoint were "no such point", the cutscene step ran out by timeout and the player stayed in the cave).
    /// Another map's target is a move there: a dungeon map is a private copy (the instance move), a field map a teleport.
    /// The effect name is the TeleportEffect.dat row the 0x3621 effect byte names. OBSERVED in the official recording of
    /// 2026-10-19 (the lake cave): a cutscene before the move (value 3, the exit's 1291) is the Cutscene event function and then
    /// the move at once, nothing in 0x3621's cutscene field; the client reports it 17 s later, after the load. A cutscene on
    /// arrival (value 4, the boss room's 1290) rides in 0x3621's cutscene field and is sent again as the event function right
    /// after the load (the client reports that one 23 s later; the boss spawned on the report). The Ascension dungeon then
    /// plays its own 1293 (the kneel by Ashar) on arrival, from DungeonInfo.ArrivalCutscene.
    /// </summary>
    void IMapEventHost.TeleportPc(string point, string effect, string before, string after)
    {
        int map = worldMap;
        Vector3d? at = null;
        if (MapEventData.Points(worldMap).TryGetValue(point, out var p)) at = new Vector3d(p[0], p[1], p[2]);
        else if (MapEventData.Teleport(point) is { } target)
        {
            map = target.Map;
            at = target.At is [var x, var y, var z] ? new Vector3d(x, y, z)
                : MapEventData.DungeonOfMap(map) is { } room ? ArrivalPoint(room) : data.MapStart(map)?.Position;
        }
        if (at is not { } point3) { Note($"PCTeleport {point}: no such point on map {worldMap}"); return; }
        var effectId = checked((byte)MapEventData.TeleportEffect(effect));
        var arrival = after is { Length: > 0 } ? (after, true)
            : map != worldMap && MapEventData.DungeonOfMap(map)?.ArrivalCutscene is { Length: > 0 } own ? (own, false) : ((string, bool)?)null;
        foreach (var member in CopyMembers().ToList())
        {
            member.pendingArrivalCutscene = arrival;
            if (!member.reloading) member.worldPlayer?.Send(member.MapEventMove(map, point3, mapEventNow, effectId, before, after));
        }
        pendingArrivalCutscene = arrival;
        mapEventOut.AddRange(MapEventMove(map, point3, mapEventNow, effectId, before, after));
    }

    /// <summary>The move: a cutscene before it goes first as the event function; the arrival cutscene's name rides in 0x3621 (OBSERVED 2026-10-19).</summary>
    List<byte[]> MapEventMove(int map, Vector3d at, long now, byte effect, string before = "", string after = "")
    {
        List<byte[]> move;
        if (map == worldMap) move = Teleport(map, at, now, 2, effect, after);
        else if (!IsInstanceMap(map)) move = Teleport(map, at, now, 0, effect, after);
        else
        {
            instanceKey = playerKey;
            instanceExitAt = null;
            move = InstanceMove(map, at, now, after, effect);
        }
        return before.Length > 0 ? [GamePackets.Cutscene(before), .. move] : move;
    }

    void IMapEventHost.MapExit(string point, string mode)
    {
        pendingExit ??= (point, mode, mapEventNow);
        foreach (var member in CopyMembers()) member.pendingExit ??= pendingExit;
    }

    /// <summary>The other players in the copy this player hosts (party members in its expedition).</summary>
    IEnumerable<GameSession> CopyMembers() =>
        copy is { } c && c.Host == this && worldPlayer?.Map is { Instance: not 0 } map ? map.Players.Values.Where(p => p.Session != this).Select(p => p.Session) : [];

    /// <summary>DungeonClear: the quest objective (ClearMapEvent) is paid at the next quest flush.</summary>
    void IMapEventHost.DungeonClear(MapEventRun run)
    {
        var room = MapEventData.DungeonOfMap(worldMap);
        var hold = room is { Hold: > 0 } ? room.Hold * 1000L : PartyDungeonHoldMs;
        var first = copy is { Cleared: false };
        // Everyone in the copy clears it (the quest's ClearMapEvent objective, paid at each one's next quest flush).
        foreach (var session in CopyMembers().Prepend(this))
        {
            if (!session.clearedEvents.Contains(run.Name)) session.clearedEvents.Add(run.Name);
            // A dungeon whose event never says MapExit (hand-entered instances) leaves after the usual delay; a generated
            // dungeon holds Dungeon.dat's CompleteStateHoldingTime (expeditions 600 s for the reward cube, sealed dungeons
            // 10 s), then the way out, unless its script says MapExit first.
            if (Instances.Of(worldMap) is not null) session.instanceExitAt ??= mapEventNow + InstanceExitDelayMs;
            else if (room is not null) session.instanceExitAt ??= mapEventNow + hold;
            // The clear reward (RewardDungeon.dat RewardLight/RewardDark by race: sealed dungeons' kina, materials, a title), once per copy.
            if (first && room is not null && (session.profileRace == 2 ? room.RewardDark : room.RewardLight) is { Length: > 0 } reward)
                session.pendingRewards.Add(reward.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList());
            if (first && room is not null)
            {
                var earned = session.AchieveDungeonClear(room, run.Name, CopyMembers().Count() + 1);
                if (copy is { } cleared) earned.AddRange(session.AchieveDungeonClearLimits(room, mapEventNow - cleared.StartedMs, cleared.Deaths));
                if (session == this) mapEventOut.AddRange(earned); else session.worldPlayer?.Send(earned);
            }
        }
        if (copy is { } c && room is not null && !c.Cleared)
        {
            c.Cleared = true;
            // OBSERVED (HQ001 clear #14161-14167): DungeonCompleteResult_NT, the state Complete (3) at the clear time, then End (4) until the hold's end.
            mapEventOut.Add(DungeonCompleteResult(c.DungeonId, true));
            mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateComplete, mapEventNow, mapEventNow));
            mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, mapEventNow, mapEventNow + hold));
        }
    }

    void IMapEventHost.DungeonFail(MapEventRun run) => Note($"map event {run.Name} failed");
    /// <summary>SetMapEnvState: 0x4001 MapEnvState_NT {map, env state u8, start time, enter false, no infos} (usmap fields; the entry sends state 0 with enter true). CANDIDATE.</summary>
    void IMapEventHost.MapEnvState(int state) => mapEventOut.Add(PacketWriter.Opcode(0x4001).I32(worldMap).U8((byte)state).I64(mapEventNow).U8(0).Varint(0).ToArray());
    /// <summary>SetMapEnvSetting: 0x4002 MapEnvSetting_NT {map, [{group i32, env state u8, start time}], enter false}. CANDIDATE.</summary>
    void IMapEventHost.MapEnvSetting(int group, int state) => mapEventOut.Add(PacketWriter.Opcode(0x4002).I32(worldMap).Varint(1).I32(group).U8((byte)state).I64(mapEventNow).U8(0).ToArray());
    /// <summary>Cutscene: EventFuncAction Cutscene (9) with the sequence name; the client plays it and reports CompleteCutscene (0x9230 kind 2). CANDIDATE.</summary>
    void IMapEventHost.Cutscene(string name) => mapEventOut.Add(GamePackets.Cutscene(name));
    void IMapEventHost.ShowGuide(string guide) => mapEventOut.Add(GamePackets.Guide(guide));
    void IMapEventHost.FocusCamera(string camera, string talk) => mapEventOut.Add(GamePackets.FocusCamera(camera, talk));
    /// <summary>ShowNote: the note by its NoteData name (the client reads it and reports CloseNote with 0x9230 kind 2).</summary>
    void IMapEventHost.ShowNote(string note) => mapEventOut.Add(GamePackets.Note(note));
    /// <summary>ShowMessage / BroadCastingMessage: the message by string key (the argument, an NPC name, is not carried; CANDIDATE).</summary>
    void IMapEventHost.Message(string key, string argument) => mapEventOut.Add(GamePackets.Message(key));

    /// <summary>ChangeEnvObjState: Finish / Dead removes the object (a door opens, a wall falls); other states are left as they are.</summary>
    void IMapEventHost.EnvObjState(string spawner, string state)
    {
        if (state is "Finish" or "Dead" or "Despawn") DoDespawn(spawner, false);
    }

    void IMapEventHost.DynamicTerrain(string spawner, bool block)
    {
        if (block) DoSpawn(spawner);
        else DoDespawn(spawner, false);
    }

    void IMapEventHost.Progress(MapEventRun run) =>
        mapEventOut.Add(GamePackets.MapEventProgress(run.Id, (byte)run.Status, run.StageOrder, run.StepOrder, run.Goals, run.Variables));

    void IMapEventHost.Unsupported(MapEventRun run, string what) => Note($"map event {run.Name}: {what}");

    /// <summary>The quest step is active (development logins without a quest log pass, so staff can walk every layer).</summary>
    bool IMapEventHost.QuestStepActive(int questId, int step) =>
        questLog is null ? IsAdmin : questLog.All.Any(q => q.QuestId == questId && q.Status == QuestLog.Incomplete && q.Step == step);

    bool IMapEventHost.NpcInCombat(string spawner) =>
        SpawnerNpcs(spawner).Any(t => !t.Dead && ai.Brains.TryGetValue(t.Key, out var brain) && brain.Engaged);

    bool IMapEventHost.SpawnerAlive(string spawner) => SpawnerNpcs(spawner).Any(t => !t.Dead);

    bool IMapEventHost.SpawnerExists(string spawner) =>
        startedSpawners.Contains(spawner) || !dormantSpawners.Contains(spawner) && (SpawnerNpcs(spawner).Any() || SpawnerObjects(spawner).Any());

    bool IMapEventHost.PlayerDead => dead;

    double? IMapEventHost.DistanceTo(string spawner)
    {
        if (position is not { } at) return null;
        var spots = SpawnerNpcs(spawner).Where(t => !t.Dead).Select(t => t.Position).Concat(SpawnerObjects(spawner).Select(x => x.Object.At)).ToList();
        return spots.Count == 0 ? null : spots.Min(p => Math.Sqrt(p.DistanceSquared(at)));
    }
}
