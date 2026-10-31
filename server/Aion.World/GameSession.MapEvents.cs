using System.Globalization;
using System.IO;
using Aion.Protocol.Native;

namespace Aion.World;

// Map events in this player's private instance copy (research/map-events.md): entering a dungeon or layer through an
// EnterInstanceLayer NPC (NpcConversation 0x4006) or a base-map trigger, the events of the map that can start (their
// quest step is active), the directives they run (spawns, NPC walks, captions, delays, the exit), the objectives the
// player completes in play, and the MapEvent packets (0x922e join, 0x922d progress, 0x922f leave). Spawn entries a
// StartSpawner directive names wait for it (dormant: HP 0, corpse removed, no respawn) instead of spawning at map load.
public sealed partial class GameSession : IMapEventHost
{
    long nextGroupReqKey = 1;

    long NextGroupReqKey() => nextGroupReqKey++;
    static MapEventTables MapEventData => MapEventTables.Embedded;
    /// <summary>Where unsupported directives and timed-out objectives are reported (the host logs them); also kept in MapEventNotes.</summary>
    public static Action<string>? MapEventLog { get; set; }
    /// <summary>Set by the Windows test host (AION_MAP_EVENT_DEBUG): a dedicated trace of spawns and aggro, separate from the host log.</summary>
    static readonly string? MapEventDebugPath = Environment.GetEnvironmentVariable("AION_MAP_EVENT_DEBUG");
    static readonly object MapEventDebugGate = new();

    readonly List<MapEventRun> mapEventRuns = [];

    /// <summary>Where the running events last moved the player inside this map (a same-map PCTeleport); the revival fallback.</summary>
    Vector3d? eventRevivalPoint;
    readonly List<byte[]> mapEventOut = [];
    readonly List<string> mapEventNotes = [];
    readonly HashSet<string> dormantSpawners = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> startedSpawners = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>EndSpawner / KillSpawner removed these. A later step must not revive them just because the npc id matches.</summary>
    readonly HashSet<string> endedSpawners = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When the current event state is sent again, so a client that dropped the tracker gets the quest title back.</summary>
    long mapEventShownAt;
    readonly HashSet<int> removedObjects = [];
    readonly List<(string Spawner, long DueMs, bool Kill, bool Spawn)> pendingSpawns = [];
    (string Point, string Mode, long DueMs)? pendingExit;
    /// <summary>The map and position the player entered the dungeon from (the way back for LastPosition exits).</summary>
    (int Map, Vector3d At)? instanceReturn;
    long mapEventNow;
    static int nextMapEventId = 0x149942;   // the official run's id; one per run on this host

    /// <summary>Walk-in layer volumes: edge-triggered entry on 0x3701 (throttled / cell change).</summary>
    readonly HashSet<int> walkInLayerInside = [];
    (int X, int Y)? walkInCell;
    long walkInCheckAt;
    /// <summary>After a teleport/load, ignore walk-in until the player has been outside quest layer volumes once.</summary>
    bool walkInNeedsOutsideApproach = true;
    /// <summary>After leaving a copy: the base map's EnterInstanceLayer triggers wait until the player has been outside them once.</summary>
    bool dungeonTriggerNeedsOutside;
    const long WalkInThrottleMs = 350;
    const double WalkInCellSize = 1200;

    void ResetWalkInLayerState()
    {
        walkInLayerInside.Clear();
        walkInCell = null;
        walkInCheckAt = 0;
        walkInNeedsOutsideApproach = true;
    }

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
    internal static bool IsInstanceMapId(int mapId) => IsInstanceMap(mapId);
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
        if (targets?.GetValueOrDefault(npcKey) is not { } npc)
        {
            LogNpcTalkRefused(npcKey, 0, null, "unknown_npc_key");
            Note($"NpcConversation: key {npcKey} is not an NPC here ({Convert.ToHexString(plain)})");
            return replies;
        }
        LogNpcTalk(npcKey, npc.NpcId, npc.Name, "NpcConversation");
        var pendingAsk = mapEventRuns.Any(r => !r.Finished && r.Open.Any(x => x.Objective.Type is "AskNpc" or "CloseNpc"
            && ((x.Objective.Npcs ?? []).Contains(npc.NpcId) || x.Objective.Names.Any(n => StepName(n, npc.Name)))));
        replies.AddRange(MapEventTalked(npcKey, null, close: false, now));
        if (pendingAsk) return replies;
        if (MapEventData.EntryNpc(npc.NpcId) is not { } entry)
        {
            LogNpcTalkRefused(npcKey, npc.NpcId, npc.Name, "no_entry_function");
            Note($"NpcConversation: {npc.Name} ({npc.NpcId}) has no entry function");
            return replies;
        }
        replies.AddRange(EnterInstanceLayerFromEntryNpc(npcKey, entry, now));
        return replies;
    }

    /// <summary>EnterInstanceLayer after 0x4006 or after the entry talk closes (0xe242).</summary>
    List<byte[]> EnterInstanceLayerFromEntryNpc(int npcKey, NpcEntry entry, long now)
    {
        if (targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        if (entry.Teleport == 0)
        {
            if (MapEventData.Dungeon(entry.Dungeon) is not { } room)
            {
                LogNpcTalkRefused(npcKey, npc.NpcId, npc.Name, "unknown_dungeon");
                return [];
            }
            if (!MeetsEntry(room, now, out var why))
            {
                LogNpcTalkRefused(npcKey, npc.NpcId, npc.Name, string.IsNullOrEmpty(why) ? "entry_refused" : why);
                Note($"NpcConversation: {npc.Name} -> dungeon {entry.Dungeon}/map {entry.Teleport}: entry refused ({why})");
                return [];
            }
        }
        var wasSeen = seenNpcs.Remove(npcKey);
        var moved = entry.Teleport != 0 ? TeleportToMap(entry.Teleport, now)
            : EnterPartyDungeon([entry.Dungeon], now);
        if (moved.Count == 0)
        {
            if (wasSeen) seenNpcs.Add(npcKey);
            LogNpcTalkRefused(npcKey, npc.NpcId, npc.Name, "entry_move_failed");
            Note($"NpcConversation: {npc.Name} -> dungeon {entry.Dungeon}/map {entry.Teleport}: no move (instance {instanceKey}, dead {dead}, reloading {reloading})");
            return [];
        }
        return [GamePackets.Death(npcKey, 1), .. moved];
    }

    /// <summary>0xe242 after a ClearMapEvent entry talk opened with 0xe240.</summary>
    List<byte[]> EnterInstanceLayerAfterEntryTalk(int npcKey, long now) =>
        targets?.GetValueOrDefault(npcKey) is { } npc && MapEventData.EntryNpc(npc.NpcId) is { } entry
            ? EnterInstanceLayerFromEntryNpc(npcKey, entry, now)
            : [];

    /// <summary>A base map's EnterInstanceLayer trigger actor (generated entries) under the player.</summary>
    List<byte[]> DungeonTrigger(Vector3d at, long now)
    {
        if (reloading || resources is null || dead || instanceKey != 0) return [];
        var entries = MapEventData.EntriesOn(worldMap);
        if (dungeonTriggerNeedsOutside)
        {
            if (!entries.Any(e => e.Contains(at))) dungeonTriggerNeedsOutside = false;
            return [];
        }
        foreach (var entry in entries)
        {
            if (!entry.Contains(at)) continue;
            if (WorldObjects.ObjectsOn(worldMap).Any(o => o.Portal is { } portal && portal.Contains(entry.Dungeon))) continue;
            if (EnterDungeon(entry.Dungeon, now, byVolume: true) is { Count: > 0 } moved) return moved;
        }
        return [];
    }

    bool LayerEventsCanStart(int layerMap, long now)
    {
        var events = MapEventData.EventsOn(layerMap);
        return events.Count == 0 || events.Any(e => MapEventRun.StartHolds(e, this, now));
    }

    /// <summary>Quest-gated instance layer volumes on the field map (0x3701 only; bbox, edge crossing).</summary>
    List<byte[]> WalkInLayerTrigger(int opcode, Vector3d at, long now)
    {
        if (opcode != 0x3701 || reloading || resources is null || dead || instanceKey != 0) return [];
        // SharedWorld harness maps (training dummy): skip walk-in so movement parity checks stay on the field map.
        if (options.TrainingDummy) return [];
        var cell = ((int)Math.Floor(at.X / WalkInCellSize), (int)Math.Floor(at.Y / WalkInCellSize));
        if (walkInCell == cell && now < walkInCheckAt + WalkInThrottleMs) return [];
        walkInCell = cell;
        walkInCheckAt = now;
        var layers = MapEventData.WalkInOn(worldMap);
        if (layers.Length == 0) return [];
        var anyInside = false;
        foreach (var layer in layers)
        {
            if (at.X < layer.XMin || at.X > layer.XMax || at.Y < layer.YMin || at.Y > layer.YMax)
            {
                walkInLayerInside.Remove(layer.LayerMap);
                continue;
            }
            var inside = layer.Volume.Contains(at);
            if (!inside)
            {
                walkInLayerInside.Remove(layer.LayerMap);
                continue;
            }
            anyInside = true;
            if (walkInNeedsOutsideApproach) continue;
            if (walkInLayerInside.Contains(layer.LayerMap)) continue;
            walkInLayerInside.Add(layer.LayerMap);
            if (MapEventData.Dungeon(layer.DungeonId) is not { } room || !MeetsEntry(room, now, out _))
                continue;
            if (!LayerEventsCanStart(layer.LayerMap, now)) continue;
            if (EnterDungeon(layer.DungeonId, now, byVolume: true) is { Count: > 0 } moved) return moved;
        }
        if (!anyInside) walkInNeedsOutsideApproach = false;
        return [];
    }

    /// <summary>
    /// The move into a dungeon's private copy: allowed when an event of its map can start now (its quest step is active),
    /// when the map has no events, or for staff. Arrival: the current position under an instance layer that overlays this
    /// map, else the dungeon's start point. The official move: 0x3647 self (reason 12, effect 0x41) then 0x3621.
    /// </summary>
    List<byte[]> EnterDungeon(int dungeonId, long now, int copy = 0, bool consumeTicket = false, bool throughDoor = false, bool byVolume = false)
    {
        // A trigger or walk-in volume enters on the quest step alone, staff included: a GM finishing HQ2101090 step 2 at the
        // Azrakar Brigade prison (2026-10-30) left World_D_Starter_HQ005, walked back over its EnterTrigger and was moved into
        // the finished layer again, behind its closed barrier. Staff keep the bypass for explicit entries (commands, dialogs).
        var staff = IsAdmin && !byVolume;
        if (MapEventData.Dungeon(dungeonId) is not { } room || resources is null || reloading || dead) return [];
        // Only served dungeon maps (their scripts, points and spawns are generated); staff may look at the rest.
        if (!staff && !MapEventData.Serves(room.Map)) { Note($"dungeon {room.Name}: map {room.Map} is not served"); return []; }
        var events = MapEventData.EventsOn(room.Map);
        if (copy == 0 && !staff && events.Count > 0 && !events.Any(e => MapEventRun.StartHolds(e, this, now)))
        {
            var open = questLog?.OpenClearMapEventNames().ToList() ?? [];
            Note($"dungeon {room.Name}: entry refused (map event start blocked; open ClearMapEvent=[{string.Join(", ", open)}])");
            return [];
        }
        if (!staff && !MeetsEntry(room, now, out var why)) { Note($"dungeon {room.Name}: entry refused ({why})"); return []; }
        var at = position ?? spawn?.Position ?? default;
        // A walk-in layer that overlays this map keeps the player's spot; a door (HQ003's Guard Storage Door, 2026-10-30: the
        // spot outside the closed door left the player unable to reach the storage's first trigger) puts them inside.
        var arrival = !throughDoor && MapEventData.Volume(room.Map) is { } volume && volume.Base == worldMap ? at : ArrivalPoint(room) ?? at;
        var targetCopy = copy != 0 ? copy : playerKey;
        var alreadyInCopy = instanceKey == targetCopy && worldMap == room.Map;
        if (worldMap == room.Map && instanceKey != targetCopy)
        {
            HandOverCopy();
            mapEventRuns.Clear();
            this.copy = null;
            pendingExit = null;
            if (worldPlayer?.Map is { } oldInst) oldInst.Leave(worldPlayer);
        }
        instanceReturn = (worldMap, at);
        instanceKey = targetCopy;
        instanceExitAt = null;
        // A door moves the player across the layer: the seamless layer move (12 / 0x41) left the client outside while the server
        // took the start point (2026-10-30, HQ003: TriggerActor_03 fired, the client stayed at the door), so a door is a PCTeleport
        // (reason 2) with the door's effect SeamlessDoorTeleport_heavy_in (TeleportEffect 40).
        var moved = InstanceMove(room.Map, arrival, now, teleportEffect: throughDoor ? DoorTeleportEffect : null);
        // Every member pays one weekly ticket on first entry (portal join into the leader's copy included); room UI passes consumeTicket too.
        if (moved.Count > 0 && !IsAdmin && Timed(room) && !alreadyInCopy)
            ConsumeExpeditionTicket(room, now);
        return moved;
    }

    /// <summary>
    /// Logging in where a solo dungeon was left (its copy is gone): its start point when one of its map events is started by a
    /// quest step still open (IncompleteQuestStep in the event's start, holding now) and the entry rules allow it, else null.
    /// The arrival cutscene plays as on a normal entry. OURS: the official relog inside a dungeon is not recorded.
    /// </summary>
    Vector3d? QuestDungeonResume(int map, long now)
    {
        if (MapEventData.DungeonOfMap(map) is not { MaxMember: <= 1 } room || !MapEventData.Serves(map) || MapEventData.Volume(map) is not null) return null;
        var wanted = MapEventData.EventsOn(map).Any(e => MapEventRun.StartHolds(e, this, now));
        if (!wanted || !MeetsEntry(room, now, out _) || ArrivalPoint(room) is not { } arrival) return null;
        pendingArrivalCutscene = room.ArrivalCutscene is { Length: > 0 } cutscene ? (cutscene, false) : null;
        return arrival;
    }

    /// <summary>A door's move into its layer: SeamlessDoorTeleport_heavy_in.</summary>
    const byte DoorTeleportEffect = 40;

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

    /// <param name="throughDoor">Entered by using a door object (EnvObjData Usage EnterInstanceLayer/EnterDungeon, UsageValue3
    /// SeamlessDoorTeleport_*_in): the player arrives at the layer's start point behind the door, not where they stood.</param>
    List<byte[]> EnterPartyDungeon(IReadOnlyList<int> dungeons, long now, bool throughDoor = false)
    {
        if (instanceKey != 0 || dungeons.Count == 0) return [];
        if (Social is { } s && worldPlayer is not null && s.PartyOf(worldPlayer) is { } party)
            foreach (var member in party.Members)
                if (member != worldPlayer && member.Map is { Instance: not 0 } inside
                    && dungeons.FirstOrDefault(id => MapEventData.Dungeon(id)?.Map == inside.MapId) is not 0 and var joined
                    && (IsAdmin || !CopyFull(inside, MapEventData.Dungeon(joined)!)))   // a full copy (sealed dungeons: one player) means a copy of one's own
                    return EnterDungeon(joined, now, inside.Instance, throughDoor: throughDoor);
        var wanted = questLog?.OpenMapEventTargets().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var chosen = dungeons.FirstOrDefault(id => MapEventData.Dungeon(id) is { } room && MapEventData.EventsOn(room.Map).Any(e => wanted.Contains(e.Name)));
        return EnterDungeon(chosen != 0 ? chosen : dungeons[0], now, throughDoor: throughDoor);
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

    /// <summary>Ends runs whose exported start conditions no longer hold (quest step moved back, relog inside an instance).</summary>
    List<byte[]> DropMapEventRunsUnlessStartHolds(long now)
    {
        var replies = new List<byte[]>();
        foreach (var run in mapEventRuns.ToList())
        {
            if (run.WindingDown || MapEventData.Event(run.Name) is not { } def || MapEventRun.StartHolds(def, this, now)) continue;
            mapEventRuns.Remove(run);
            replies.Add(GamePackets.MapEventLeave(run.Id));
            DebugMapEvent($"event drop {run.Name} run {run.Id}: start no longer holds");
        }
        return replies;
    }

    /// <summary>Right after the map's targets are set: the deferred spawn entries go dormant, before the NPC list is sent.</summary>
    void MapEventsPrepare()
    {
        DropMapEventRunsUnlessStartHolds(lastNow);
        if (mapEventsMap == worldMap && instanceKey != 0 && mapEventRuns.Count > 0) return;
        // A revival inside the copy after the script's MapExit fired (HQ004: the closing bombs can kill the player in its 2 s
        // delay) reloads the same map: the way out still holds, else the player waits in the cleared layer for the hold.
        var keepExit = mapEventsMap == worldMap && instanceKey != 0 ? pendingExit : null;

        mapEventsMap = 0;
        mapEventRuns.Clear();
        eventRevivalPoint = null;
        dormantSpawners.Clear();
        startedSpawners.Clear();
        endedSpawners.Clear();
        mapEventShownAt = 0;
        removedObjects.Clear();
        pendingSpawns.Clear();
        pendingExit = keepExit;
        npcBuffs.Clear();
        if (!RunsMapEvents || targets is null) return;
        dormantSpawners.UnionWith(MapEventData.Deferred(worldMap));
        foreach (var t in targets.Values)
        {
            if (t.Key == Combat.TrainingDummyKey) continue;
            if (dormantSpawners.Contains(t.Name))
            {
                t.Hp = 0;
                t.CorpseRemoved = true;
                respawnAt.Remove(t.Key);
            }
        }
    }

    /// <summary>After the NPC list: the map's startable events join (0x922e) and begin; a PCTeleport's arrival cutscene plays.</summary>
    List<byte[]> MapEventsStart(long now, bool resendExistingJoins = false)
    {
        var arrival = pendingArrivalCutscene is { } cutscene ? GamePackets.Cutscene(cutscene.Name, cutscene.Wait) : null;
        pendingArrivalCutscene = null;
        var started = MapEventsJoin(now, resendExistingJoins);
        if (arrival is not null) started.Insert(0, arrival);   // OBSERVED order: the cutscene, then the joins (2026-10-19)
        return started;
    }

    List<byte[]> MapEventsJoin(long now, bool resendExistingJoins = false)
    {
        // In a party member's copy: the owner's running events (their joins), none of this player's own.
        if (CopyOwner is { } owner)
            return owner.mapEventRuns.Select(run => GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.StartedMs, run.Variables)).ToList();
        if (!RunsMapEvents) return [];
        var leaves = DropMapEventRunsUnlessStartHolds(now);
        // A reload of the same map (a PCTeleport) keeps the runs and the client keeps its event state: the official boss-room
        // move in the 2026-10-19 recording sent no 0x922e after the load (the earlier re-join was a guess).
        if (mapEventsMap == worldMap && mapEventRuns.Count > 0)
        {
            if (!resendExistingJoins) return leaves;
            return [.. leaves, .. mapEventRuns.Select(run => GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.StartedMs, run.Variables))];
        }
        mapEventsMap = worldMap;
        mapEventNow = now;
        return StartEligibleEvents(now);
    }

    /// <summary>The map's events not running yet whose start conditions hold now join (0x922e) and begin (at entry, and when a field event's phase changes).</summary>
    List<byte[]> StartEligibleEvents(long now)
    {
        if (!RunsMapEvents) return [];
        mapEventNow = now;
        var replies = DropMapEventRunsUnlessStartHolds(now);
        foreach (var definition in MapEventData.EventsOn(worldMap))
        {
            if (mapEventRuns.Any(r => r.Name == definition.Name)) continue;
            var run = new MapEventRun(definition, Interlocked.Increment(ref nextMapEventId), this, now);
            if (!MapEventRun.StartHolds(definition, this, now)) continue;
            mapEventRuns.Add(run);
            replies.Add(GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.StartedMs, run.Variables));
            DebugMapEvent($"event start {definition.Name} run {run.Id} map {worldMap}");
            run.Start(now);
        }
        replies.AddRange(SyncMapEventEnvInteractFlags());
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
        foreach (var run in mapEventRuns.ToList())
        {
            var before = string.Join(',', run.Goals);
            var counted = run.UsedObject(spawnName, now);
            if (counted)
                DebugMapEvent($"UseEnvObj {spawnName}: counted {before} -> {string.Join(',', run.Goals)} step {run.StepOrder}");
            else if (run.Step is { } step)
            {
                var open = string.Join(", ", step.Objectives.Select((o, i) => (o, i))
                    .Where(x => x.o.Type == "UseEnvObj" && x.i < run.Goals.Length && run.Goals[x.i] < x.o.Count)
                    .Select(x => x.o.Value(0)));
                if (open.Length > 0) DebugMapEvent($"UseEnvObj {spawnName}: not counted; open {open}");
            }
        }
        return DrainMapEvents();
    }

    List<byte[]> MapEventPosition(Vector3d at, long now)
    {
        if (ToCopyOwner(o => o.MapEventPosition(at, now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        var triggers = WorldObjects.TriggersOn(worldMap);
        bool InsideTrigger(string name) => triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Contains(at));
        bool InsideSubzone(string name) => Subzones.InMapEventSubzone(worldMap, name, at);
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
        if (run is not null && !run.WindingDown && !MapEventRun.StartHolds(run.Definition, this, now))
        {
            mapEventRuns.Remove(run);
            run = null;
        }
        var done = run?.ClientObjective(index, now) == true;
        var replies = new List<byte[]> { GamePackets.ClientObjectiveReply(2, mapEventId, index, done ? 0 : 1) };
        if (run is null && mapEventRuns.All(r => r.Id != mapEventId))
            replies[0] = GamePackets.ClientObjectiveReply(2, mapEventId, index, 1);
        replies.AddRange(DrainMapEvents());
        return replies;
    }

    /// <summary>0xe334: a cutscene ended on the client; the CompleteCutscene objective naming it (own runs, or the copy owner's) is done.</summary>
    List<byte[]> MapEventCutsceneDone(string cutscene, long groupKey, long now)
    {
        var replies = new List<byte[]> { GamePackets.CutsceneCompleteReply(cutscene, groupKey) };
        if (groupKey != 0)
        {
            replies.Add(GamePackets.GroupRequestProgressed(groupKey, playerKey));
            replies.Add(GamePackets.GroupRequestComplete(groupKey));
        }
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
        MapEventAskNpcProximityFallback(now);
        if (mapEventRuns.Any(r => !r.Finished))
        {
            if (mapEventShownAt == 0) mapEventShownAt = now + 8000;
            else if (now >= mapEventShownAt)
            {
                mapEventShownAt = now + 8000;
                foreach (var run in mapEventRuns)
                {
                    if (run.Finished) continue;
                    // The corner title is this packet. Sending it again puts the name back when the client dropped it.
                    mapEventOut.Add(GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.StartedMs, run.Variables));
                    mapEventOut.Add(GamePackets.MapEventProgress(run.Id, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.Variables));
                }
            }
        }
        var replies = DrainMapEvents();
        if (pendingExit is { } exit && now >= exit.DueMs && !reloading)
        {
            pendingExit = null;
            replies.AddRange(LeaveInstance(now, exit.Mode, to: clearExit));
        }
        return replies;
    }

    // ---------- The host: what the runs do to the world ----------

    /// <summary>The appear goes out now. Far from the player it is not marked seen, so the next approach sends it again.</summary>
    void ShowSpawned(Target t)
    {
        mapEventOut.Add(GamePackets.NpcAppear(SpawnAppearance(t)));
        if (position is { } at && !InNpcView(t.Position, at)) seenNpcs.Remove(t.Key);
        else seenNpcs.Add(t.Key);
    }

    static bool InNpcView(Vector3d npc, Vector3d player)
    {
        var dx = npc.X - player.X;
        var dy = npc.Y - player.Y;
        return dx * dx + dy * dy <= NpcViewRange * NpcViewRange;
    }

    static bool StepName(string objective, string spawn) =>
        string.Equals(objective, spawn, StringComparison.OrdinalIgnoreCase)
        || spawn.StartsWith(objective + "_", StringComparison.OrdinalIgnoreCase)
        || objective.StartsWith(spawn + "_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kill and talk targets of the step that are already supposed to be here. Dead ones come back unless this step already counted them; a kill target becomes attackable.</summary>
    void IMapEventHost.EnsureStep(MapEventStep step, int[] goals)
    {
        if (targets is null) return;
        DebugMapEvent($"step {step.Id}: {string.Join("; ", step.Objectives.Select(o => $"{o.Type} {o.Value(0)}"))}");
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            var credited = i < goals.Length ? goals[i] : 0;
            if (o.Type is not ("KillNpc" or "AskNpc" or "CloseNpc" or "HpRemaining")) continue;
            if (credited >= o.Count) continue;
            var named = targets.Values.Where(t => o.Names.Any(n => StepName(n, t.Name))).ToList();
            var pool = named.Count > 0 ? named
                : o.Npcs is { Length: > 0 } ids ? targets.Values.Where(t => ids.Contains(t.NpcId)).ToList() : [];
            if (pool.Count == 0) continue;
            bool HeldBack(Target t) => t.Key == Combat.TrainingDummyKey || dormantSpawners.Contains(t.Name) || endedSpawners.Contains(t.Name);
            var ready = pool.Where(t => !HeldBack(t)).ToList();
            var living = ready.Count(t => !t.Dead && !t.CorpseRemoved);
            foreach (var t in ready.Where(t => t.Dead || t.CorpseRemoved).OrderBy(t => position is { } at ? t.Position.DistanceSquared(at) : 0).Take(Math.Max(0, o.Count - credited - living)))
            {
                if (o.Type == "KillNpc") t.Relationship = 11;
                BringBack(t);
            }
            if (o.Type == "KillNpc")
            {
                foreach (var t in ready.Where(t => !t.Dead && !t.CorpseRemoved)) MakeKillable(t);
                foreach (var t in pool.Where(t => dormantSpawners.Contains(t.Name) && !t.Hostile)) t.Relationship = 11;
            }
            else if (o.Type is "AskNpc" or "CloseNpc")
                foreach (var t in ready.Where(t => !t.Dead && !t.CorpseRemoved)) PrepareMapEventTalkNpc(t, o);
        }
        EnsureStepUseEnvObjects(step, goals);
    }

    /// <summary>UseEnvObj objectives: spawn and 0x363c as soon as the step opens (Valley of Hope layer props, CoastalCave barrierstone).</summary>
    void EnsureStepUseEnvObjects(MapEventStep step, int[] goals)
    {
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Type != "UseEnvObj" || i >= goals.Length || goals[i] >= o.Count) continue;
            foreach (var name in o.Names)
            {
                foreach (var (index, obj) in SpawnerObjects(name))
                {
                    removedObjects.Remove(index);
                    envObjCanInteract[index] = true;
                    var key = WorldObjects.FirstKey + index;
                    if (shownObjects.Contains(index))
                        mapEventOut.Add(GamePackets.InteractEnvObjCanInteract(key, true));
                    else if (InstanceMap || position is { } at && obj.At.DistanceSquared(at) <= WorldObjects.VisibleRange * WorldObjects.VisibleRange)
                    {
                        if (shownObjects.Add(index)) PushEnvObjSpawn(index, obj, mapEventNow);
                        else mapEventOut.Add(GamePackets.InteractEnvObjCanInteract(key, true));
                    }
                }
            }
        }
    }

    /// <summary>Quest-style relationship and a fresh appear so map-event AskNpc targets show the talk prompt (Elvida stays Companion in NpcData).</summary>
    void PrepareMapEventTalkNpc(Target t, MapEventObjective objective)
    {
        var questRelation = profileRace == 2 ? 4 : 3;   // NPC_Dark / NPC_Light
        mapEventOut.AddRange(ai.DisengageForTalk(t, mapEventNow));
        mapEventOut.AddRange(ai.Follow(t, playerKey, false, mapEventNow));
        t.AppearNonAggressive = true;
        if (t.Relationship != questRelation)
        {
            t.Relationship = questRelation;
            mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, questRelation));
        }
        var talk = objective.Value(1);
        var appear = SpawnAppearance(t) with { Relationship = questRelation, Aggressive = false };
        mapEventOut.Add(GamePackets.NpcAppear(appear));
        foreach (var run in mapEventRuns.Where(r => !r.Finished && r.Open.Any(x => x.Objective.Type is "AskNpc" or "CloseNpc")))
        {
            mapEventOut.Add(GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.StartedMs, run.Variables));
            mapEventOut.Add(GamePackets.MapEventProgress(run.Id, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.Variables));
            ((IMapEventHost)this).ScriptBeat(run, "talk-ready", $"{objective.Type} {objective.Value(0)} key={t.Key} talk={talk}", mapEventNow);
            Note($"wire AskNpc run={run.Id} step={run.StepOrder} goals=[{string.Join(',', run.Goals)}] sent 0x922e+0x922d 0x39cd rel={questRelation} 0x3641 key={t.Key} npc={t.NpcId} talk={talk}");
        }
        DebugMapEvent($"AskNpc ready {t.Name} ({t.NpcId}) talk={talk} rel={questRelation} appearAggressive=false");
        PlaytestDebug.Action("map_event_ask_npc_prepare",
            ("npcKey", t.Key), ("npcId", t.NpcId), ("spawn", t.Name), ("talk", talk), ("relationship", questRelation));
    }

    /// <summary>When the client never opens step talk on a prepared AskNpc (companion/combat NpcData, no 0xe240).</summary>
    void MapEventAskNpcProximityFallback(long now)
    {
        if (!options.MapEventAskNpcProximityFallback || position is not { } at || targets is null) return;
        const double range = 500;   // ~5 m in world units
        const long holdMs = 3000;
        foreach (var run in mapEventRuns.ToList())
        {
            foreach (var (index, objective) in run.Open.ToList())
            {
                if (objective.Type is not ("AskNpc" or "CloseNpc")) continue;
                var npc = targets.Values.FirstOrDefault(t => !t.Dead
                    && ((objective.Npcs ?? []).Contains(t.NpcId) || objective.Names.Any(n => StepName(n, t.Name))));
                if (npc is null || !data.MapEventAskNpcNeedsProximityFallback(npc.NpcId)) continue;
                if (npc.Position.DistanceSquared(at) > range * range) continue;
                var key = run.Id * 1000 + npc.Key;
                if (!askNpcNearSince.TryGetValue(key, out var since)) askNpcNearSince[key] = since = now;
                if (now - since < holdMs) continue;
                askNpcNearSince.Remove(key);
                DebugMapEvent($"AskNpc proximity fallback run={run.Id} {npc.Name} index={index}");
                AdvanceMapEventAfterTalk(run, npc.NpcId, npc.Name, index, objective.Type == "CloseNpc", now);
            }
        }
    }

    void AdvanceMapEventAfterTalk(MapEventRun run, int npcId, string spawnName, int index, bool close, long now)
    {
        run.Talked(npcId, spawnName, index, close, now);
        for (var adv = 1; adv <= 4; adv++)
            run.Tick(now + adv * 1500L);
    }

    readonly Dictionary<int, long> askNpcNearSince = [];

    List<byte[]> TryMapEventAskNpcInteractFallback(int npcKey, long now)
    {
        if (targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList())
        {
            foreach (var (index, objective) in run.Open.ToList())
            {
                if (objective.Type is not ("AskNpc" or "CloseNpc")) continue;
                if (!(objective.Npcs ?? []).Contains(npc.NpcId) && !objective.Names.Any(n => StepName(n, npc.Name))) continue;
                if (!data.MapEventAskNpcNeedsProximityFallback(npc.NpcId)) continue;
                DebugMapEvent($"AskNpc interact fallback (lock-on) run={run.Id} {npc.Name} index={index}");
                AdvanceMapEventAfterTalk(run, npc.NpcId, npc.Name, index, objective.Type == "CloseNpc", now);
                return DrainMapEvents();
            }
        }
        return [];
    }

    int IMapEventHost.CountDead(string[] names)
    {
        if (targets is null || names.Length == 0) return 0;
        return targets.Values.Count(t =>
            (t.Dead || t.CorpseRemoved)
            && t.Key != Combat.TrainingDummyKey
            && !dormantSpawners.Contains(t.Name)
            && !endedSpawners.Contains(t.Name)
            && names.Any(n => StepName(n, t.Name)));
    }

    void BringBack(Target t)
    {
        t.Hp = t.HpMax;
        t.CorpseRemoved = false;
        t.Held = false;
        t.Immune = false;
        respawnAt.Remove(t.Key);
        if (ai.Brains.TryGetValue(t.Key, out var brain))
        {
            t.Position = brain.Home;
            t.Direction = brain.HomeHeading;
            brain.Mode = NpcAi.Mode.Idle;
            brain.Pending = null;
            brain.VictimKey = 0;
            brain.VictimIsNpc = false;
            brain.Directed = false;
            brain.FollowKey = 0;
            brain.ResumeFollowKey = 0;
            brain.Hate.Clear();
            brain.Engaged = false;
        }
        ShowSpawned(t);
        DebugMapEvent($"revived {t.Name} npc {t.NpcId}");
    }

    void MakeKillable(Target t)
    {
        if (t.Hostile && !t.AppearNonAggressive) return;
        if (!t.Hostile)
        {
            t.Relationship = 11;
            mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, t.Relationship));
        }
        ReappearIfTurnedHostile(t);
    }

    /// <summary>A map-event NPC that was talked to (AskNpc prep sent a non-aggressive appear) and then turns Monster: the client keeps the
    /// appear-time peace state and will not select it (ranged "select a target"), so drop the talk flag and send a fresh appear.</summary>
    void ReappearIfTurnedHostile(Target t)
    {
        if (!t.Hostile || t.Dead) return;
        var wasTalk = t.AppearNonAggressive;
        t.AppearNonAggressive = false;
        mapEventOut.Add(GamePackets.NpcAppear(SpawnAppearance(t)));
        DebugMapEvent($"turned hostile {t.Name} ({t.NpcId}) key={t.Key} rel={t.Relationship} wasTalk={wasTalk}: re-appear");
    }

    IEnumerable<Target> SpawnerNpcs(string spawner) =>
        targets?.Values.Where(t => string.Equals(t.Name, spawner, StringComparison.OrdinalIgnoreCase)) ?? [];

    static bool EnvObjSpawnerMatches(string spawner, WorldObject o) =>
        MapEventRun.NameMatches(spawner, o.Name)
        || o.EnvName is { Length: > 0 } env && MapEventRun.NameMatches(spawner, env);

    IEnumerable<(int Index, WorldObject Object)> SpawnerObjects(string spawner) =>
        WorldObjects.ObjectsOn(worldMap).Select((o, i) => (i, o)).Where(x => EnvObjSpawnerMatches(spawner, x.o));

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
        var wasDormant = dormantSpawners.Remove(spawner);
        startedSpawners.Add(spawner);
        var matched = SpawnerNpcs(spawner).ToList();
        var revived = 0;
        var already = 0;
        foreach (var t in matched)
        {
            if (!t.Dead && !t.CorpseRemoved) { already++; continue; }
            revived++;
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            t.Held = false;
            t.Immune = false;
            respawnAt.Remove(t.Key);
            if (ai.Brains.TryGetValue(t.Key, out var brain)) t.Position = brain.Home;
            ShowSpawned(t);
        }
        endedSpawners.Remove(spawner);
        DebugMapEvent($"StartSpawner {spawner}: matched {matched.Count}, revived {revived}, already-living {already}, was-dormant {wasDormant}, map {worldMap}");
        if (matched.Count == 0) Note($"StartSpawner {spawner}: no NPC with that name on map {worldMap}");
        foreach (var (index, o) in SpawnerObjects(spawner))
        {
            removedObjects.Remove(index);
            if (position is { } at && o.At.DistanceSquared(at) <= WorldObjects.VisibleRange * WorldObjects.VisibleRange && shownObjects.Add(index))
                PushEnvObjSpawn(index, o, mapEventNow);
        }
    }

    /// <summary>EndSpawner: the NPCs vanish (0x3642 flag 7, OBSERVED for Robin); KillSpawner / a killed barrier: they die (HP 0, flag 3).</summary>
    void DoDespawn(string spawner, bool kill)
    {
        var all = string.Equals(spawner, "all", StringComparison.OrdinalIgnoreCase);
        var named = (all ? targets?.Values.Where(t => t.Key != Combat.TrainingDummyKey) ?? [] : SpawnerNpcs(spawner)).ToList();
        DebugMapEvent($"{(kill ? "KillSpawner" : "EndSpawner")} {spawner}: matched {named.Count}, living {named.Count(t => !t.Dead)}, map {worldMap}");
        if (!all && named.Count == 0) Note($"{(kill ? "KillSpawner" : "EndSpawner")} {spawner}: no NPC with that name on map {worldMap}");
        if (all) foreach (var t in named) endedSpawners.Add(t.Name);
        else endedSpawners.Add(spawner);
        foreach (var t in named)
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
        LogMapEventBeat(text);
        DebugMapEvent(text);
    }

    /// <summary>One line in the map-event debug file. The host log only gets Note, so a successful spawn does not flood it.</summary>
    void DebugMapEvent(string text)
    {
        if (string.IsNullOrEmpty(MapEventDebugPath)) return;
        try
        {
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} map={worldMap} {text}{Environment.NewLine}";
            lock (MapEventDebugGate) File.AppendAllText(MapEventDebugPath, line);
        }
        catch (IOException)
        {
            // A missed debug line must not stop the event.
        }
    }

    /// <summary>How many NPCs carry this spawner name, and whether any of them can be fought.</summary>
    string SpawnerState(string spawner)
    {
        var all = SpawnerNpcs(spawner).ToList();
        return $"matched {all.Count}, living {all.Count(t => !t.Dead)}, dead {all.Count(t => t.Dead)}, corpse-removed {all.Count(t => t.CorpseRemoved)}, dormant {dormantSpawners.Contains(spawner)}, started {startedSpawners.Contains(spawner)}";
    }

    void IMapEventHost.Spawn(string spawner, int delayMs)
    {
        CancelPendingSpawns(spawner);
        if (delayMs > 0) pendingSpawns.Add((spawner, mapEventNow + delayMs, false, true));
        else DoSpawn(spawner);
    }

    void IMapEventHost.Despawn(string spawner, int delayMs, bool kill)
    {
        CancelPendingSpawns(spawner);
        if (delayMs > 0) pendingSpawns.Add((spawner, mapEventNow + delayMs, kill, false));
        else DoDespawn(spawner, kill);
    }

    /// <summary>A later Start/End/KillSpawner overrides an earlier delayed one: HQ004's door (EndSpawner 2000 ms after step 6's use)
    /// is started again by Sven's step 7 talk; a talk inside those 2 s let the stale despawn remove the door step 10 opens.</summary>
    void CancelPendingSpawns(string spawner) =>
        pendingSpawns.RemoveAll(p => string.Equals(p.Spawner, spawner, StringComparison.OrdinalIgnoreCase));

    /// <summary>SummonAroundTarget: the spawner's NPCs placed around the target NPC (or the player) and spawned.</summary>
    void IMapEventHost.SummonAround(string target, string spawner, int count, double radius)
    {
        var centre = SpawnerNpcs(target).FirstOrDefault(t => !t.Dead)?.Position ?? position ?? spawn?.Position ?? default;
        var index = 0;
        // The common spawners (Basic_RecoveryOrb_01-04, E_FE_Common_RecoveryOrb_*) are not in any map's SpawnInfoList export.
        if (!SpawnerNpcs(spawner).Any()) Note($"SummonAroundTarget {spawner}: no spawn data for this spawner on map {worldMap}");
        foreach (var t in SpawnerNpcs(spawner).Where(t => t.Dead || t.CorpseRemoved).Take(Math.Max(1, count)))
        {
            var angle = index++ * 2 * Math.PI / Math.Max(1, count);
            t.Position = centre with { X = centre.X + Math.Cos(angle) * Math.Min(radius, 400), Y = centre.Y + Math.Sin(angle) * Math.Min(radius, 400) };
            t.Hp = t.HpMax;
            t.CorpseRemoved = false;
            ShowSpawned(t);
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
        EnsureLivingSpawner(spawner);
        var attackers = SpawnerNpcs(spawner).Where(t => !t.Dead && !t.CorpseRemoved).ToList();
        DebugMapEvent($"SetAggroPc {spawner}: living attackers {attackers.Count}; {SpawnerState(spawner)}");
        if (attackers.Count == 0) Note($"SetAggroPc {spawner}: no living attacker; {SpawnerState(spawner)}");
        foreach (var t in attackers) mapEventOut.AddRange(ai.ForceAggro(t, playerKey, mapEventNow));
    }

    /// <summary>SetAggroNpc [spawner, target spawner, aggro]: each NPC of the spawner attacks the nearest living NPC of the target.</summary>
    void IMapEventHost.SetAggroNpc(string spawner, string target)
    {
        EnsureLivingSpawner(target);
        EnsureLivingSpawner(spawner);
        var prey = SpawnerNpcs(target).Where(t => !t.Dead && !t.CorpseRemoved).ToList();
        var attackers = SpawnerNpcs(spawner).Where(t => !t.Dead && !t.CorpseRemoved).ToList();
        DebugMapEvent($"SetAggroNpc {spawner} -> {target}: attackers {attackers.Count}; target {SpawnerState(target)}; attacker {SpawnerState(spawner)}");
        if (prey.Count == 0) { Note($"SetAggroNpc {spawner} -> {target}: no living target; {SpawnerState(target)}"); return; }
        if (attackers.Count == 0) Note($"SetAggroNpc {spawner} -> {target}: no living attacker; {SpawnerState(spawner)}");
        foreach (var t in attackers)
            mapEventOut.AddRange(ai.ForceAggroNpc(t, prey.MinBy(p => p.Position.DistanceSquared(t.Position))!, mapEventNow));
    }

    /// <summary>Scripted aggro expects living spawners; early kills on SharedWorld leave corpses (HQ001 skurv before Mard's SetAggroNpc).</summary>
    void EnsureLivingSpawner(string spawner)
    {
        var all = SpawnerNpcs(spawner).ToList();
        if (all.Count == 0) return;
        if (all.Any(t => !t.Dead && !t.CorpseRemoved)) return;
        foreach (var t in all) BringBack(t);
        endedSpawners.Remove(spawner);
    }

    /// <summary>Where a player revives inside a dungeon: the running stage's RevivalPoint (a teleport point), else the dungeon's start; null elsewhere.</summary>
    Vector3d? DungeonRevivalPoint()
    {
        if (instanceKey == 0 || !MapEventData.Serves(worldMap)) return null;
        var points = MapEventData.Points(worldMap);
        foreach (var run in (CopyOwner ?? this).mapEventRuns)
            if (run.Stage?.RevivalPoint is { Length: > 0 } name && points.TryGetValue(name, out var p)) return new Vector3d(p[0], p[1], p[2]);
        // No stage revival point, but the event moved the player inside this map (the Tunapre lake cave's PCTeleport into the boss
        // room): revive there. The dungeon start is behind steps that are already done, so the player could never get back
        // (live 2026-10-30, Dungeon_D_0018 Stage_Boss; its PcDie fail resets the boss and restarts the stage).
        if (eventRevivalPoint is { } moved) return moved;
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
            ReappearIfTurnedHostile(t);
        }
    }

    // ---------- Scripted abnormals ----------

    /// <summary>A scripted abnormal on an NPC of this copy (0x382a on its key) and what the server makes of it.</summary>
    sealed class NpcBuff
    {
        public required int Uid, AbnormalId;
        public required long ExpireAt;
        public bool Held, Immune;
        /// <summary>Silence, slow (1/10000 of its speed) and damage over time on a monster (AbnormalSpecs.Npc).</summary>
        public bool Silence;
        public int SlowPermille;
        public List<NpcDot> Dots { get; } = [];
        public int SkillId;
        /// <summary>The relationship the NPC had before a ChangeRelationshipEntity effect, restored when the abnormal ends.</summary>
        public int? Restore;
    }

    /// <summary>A damage-over-time row on a monster: <see cref="PerTick"/> every <see cref="IntervalMs"/> until <see cref="Remaining"/> is spent.</summary>
    sealed class NpcDot
    {
        public required int EffectId, IntervalMs, PerTick;
        public required int Remaining;
        public required long NextAt;
    }

    readonly Dictionary<int, List<NpcBuff>> npcBuffs = [];
    int npcBuffUid = 2_000_000;

    /// <summary>
    /// What an abnormal's SkillAbnormalEffect rows mean to the server: held (stun, hold, stone, sleep, a blocked move), immune
    /// (an immunity or untargetable), a relationship. Other effects (stat changes, dots, casts) only show the icon.
    /// </summary>
    static (bool Held, bool Immune, int? Relationship) AbnormalModel(int abnormalId)
    {
        var (held, immune) = AbnormalCombat.NpcFlags(abnormalId);
        int? relationship = null;
        foreach (var (_, type, values) in AbnormalSpecs.Info(abnormalId)?.Effects ?? [])
            if (type == "ChangeRelationshipEntity" && values.Count >= 2 && MapEventTables.Relationship(values[1]) is { } relation)
                relationship = relation;
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
        t.Silenced = list?.Any(b => b.Silence) == true;
        t.SlowPermille = list is { Count: > 0 } ? list.Max(b => b.SlowPermille) : 0;
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
                if (t is not null) { t.Held = false; t.Immune = false; t.Silenced = false; t.SlowPermille = 0; }
                continue;
            }
            NpcDotTick(t, list, now);
            if (t.Dead) continue;
            if (list.Any(b => now >= b.ExpireAt)) RemoveNpcBuffs(t, b => now >= b.ExpireAt, ExpireTimeout);
        }
    }

    /// <summary>
    /// The damage-over-time rows of a monster's abnormals (a bleed, a burn): 0x3805 per tick (OBSERVED shape of the player's own dots)
    /// and its HP; the kill is this player's (loot, experience, quests).
    /// </summary>
    void NpcDotTick(Target t, List<NpcBuff> list, long now)
    {
        foreach (var b in list.ToList())
            foreach (var d in b.Dots)
                while (!t.Dead && now >= d.NextAt && d.NextAt <= b.ExpireAt && d.Remaining > 0)
                {
                    var amount = Math.Min(d.PerTick, d.Remaining);
                    d.Remaining -= amount;
                    d.NextAt += d.IntervalMs;
                    if (t.Immune) amount = 0;
                    t.Hp = Math.Max(0, t.Hp - amount);
                    mapEventOut.Add(RidePackets.AbnormalEffect(t.Key, playerKey, b.Uid, d.EffectId, d.Remaining, amount, b.SkillId));
                    mapEventOut.Add(GamePackets.NpcHp(t.Key, t.Hp));
                    if (t.Dead) mapEventOut.AddRange(PlayerKilledNpc(t, b.SkillId, playerKey, now));
                    else if (amount > 0 && targets is not null) mapEventOut.AddRange(ai.Engage(targets, t, now, playerKey, amount));
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
        else if (data.MapPoints(worldMap).TryGetValue(point, out var mp)) at = new Vector3d(mp[0], mp[1], mp[2]);
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
            if (map == worldMap) member.eventRevivalPoint = point3;
            member.pendingArrivalCutscene = arrival;
            if (!member.reloading) member.worldPlayer?.Send(member.MapEventMove(map, point3, mapEventNow, effectId, before, after));
        }
        pendingArrivalCutscene = arrival;
        if (map == worldMap) eventRevivalPoint = point3;
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
        run.MarkDungeonClear(mapEventNow);
        var room = MapEventData.DungeonOfMap(worldMap);
        var hold = room is { Hold: > 0 } ? room.Hold * 1000L : PartyDungeonHoldMs;
        var first = copy is { Cleared: false };
        // Everyone in the copy clears it (the quest's ClearMapEvent objective, paid at each one's next quest flush).
        foreach (var session in CopyMembers().Prepend(this))
        {
            if (!session.clearedEvents.Contains(run.Name)) session.clearedEvents.Add(run.Name);
            session.clearExit ??= session.ClearExitOf(run.Name);
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
            // OBSERVED (HQ001 clear #14161-14167): DungeonCompleteResult_NT, Complete (3), then End (4) until the hold's end — all copy members.
            BroadcastPartyDungeonClearEnd(room, mapEventNow - c.StartedMs, c.DungeonId, mapEventNow, hold);
        }
    }

    void IMapEventHost.DungeonFail(MapEventRun run) => Note($"map event {run.Name} failed");
    /// <summary>SetMapEnvState: 0x4001 MapEnvState_NT {map, env state u8, start time, enter false, no infos} (usmap fields; the entry sends state 0 with enter true). CANDIDATE.</summary>
    void IMapEventHost.MapEnvState(int state) => mapEventOut.Add(PacketWriter.Opcode(0x4001).I32(worldMap).U8((byte)state).I64(mapEventNow).U8(0).Varint(0).ToArray());
    /// <summary>SetMapEnvSetting: 0x4002 MapEnvSetting_NT {map, [{group i32, env state u8, start time}], enter false}. CANDIDATE.</summary>
    void IMapEventHost.MapEnvSetting(int group, int state) => mapEventOut.Add(PacketWriter.Opcode(0x4002).I32(worldMap).Varint(1).I32(group).U8((byte)state).I64(mapEventNow).U8(0).ToArray());
    /// <summary>Cutscene: EventFuncAction Cutscene (9) with the sequence name; the client plays it and reports CompleteCutscene (0x9230 kind 2). CANDIDATE.</summary>
    void IMapEventHost.Cutscene(string name) => mapEventOut.Add(GamePackets.Cutscene(name, groupReqKey: NextGroupReqKey()));
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

    void IMapEventHost.EnvObjChangePhase(string spawner, int phase)
    {
        var p = (byte)Math.Clamp(phase, 0, 255);
        foreach (var (index, o) in SpawnerObjects(spawner))
        {
            envObjPhase[index] = p;
            if (shownObjects.Contains(index)) PushEnvObjSpawn(index, o, mapEventNow);
        }
    }

    void IMapEventHost.SetEnvObjCanInteract(string spawner, bool canInteract)
    {
        foreach (var (index, _) in SpawnerObjects(spawner))
        {
            envObjCanInteract[index] = canInteract;
            var key = WorldObjects.FirstKey + index;
            mapEventOut.Add(GamePackets.InteractEnvObjCanInteract(key, canInteract));
        }
    }

    void IMapEventHost.DynamicTerrain(string spawner, bool block)
    {
        if (block) DoSpawn(spawner);
        else DoDespawn(spawner, false);
    }

    void IMapEventHost.Progress(MapEventRun run)
    {
        mapEventOut.Add(GamePackets.MapEventProgress(run.Id, (byte)run.Status, run.StageOrder, run.WireStep, run.Goals, run.Variables));
        mapEventOut.AddRange(SyncMapEventEnvInteractFlags());
    }

    bool IMapEventHost.EnvObjectUsed(string spawnName)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        for (var i = 0; i < objects.Count; i++)
        {
            if (!usedObjects.Contains(i)) continue;
            var o = objects[i];
            if (MapEventRun.NameMatches(spawnName, o.Name)) return true;
            if (o.EnvName is { Length: > 0 } env && MapEventRun.NameMatches(spawnName, env)) return true;
        }
        return false;
    }

    void IMapEventHost.ScriptBeat(MapEventRun run, string kind, string detail, long now)
    {
        if (!run.Name.Contains("Starter_HQ001", StringComparison.OrdinalIgnoreCase)) return;
        if (kind == "step-begin" && run.Step is { } step)
        {
            var types = string.Join(';', step.Objectives.Select(o => $"{o.Type}:{o.Value(0)}"));
            detail = $"{detail} objectiveTypes=[{types}]";
        }
        Note($"HQ001 beat [{kind}] run={run.Id} stage={run.StageOrder} step={run.StepOrder} goals=[{string.Join(',', run.Goals)}] {detail}");
    }

    void IMapEventHost.Unsupported(MapEventRun run, string what) => Note($"map event {run.Name}: {what}");

    /// <summary>The quest step is active (development logins without a quest log pass, so staff can walk every layer).</summary>
    bool IMapEventHost.QuestStepActive(int questId, int step) =>
        questLog is null ? IsAdmin : questLog.All.Any(q => q.QuestId == questId && q.Status == QuestLog.Incomplete && q.Step == step);

    /// <summary>Events with no start conditions in the export: quest ClearMapEvent, field-event phase, or non-story dungeons after entry.</summary>
    bool IMapEventHost.MapEventStartAllowed(MapEventDefinition definition)
    {
        if (questLog is null) return IsAdmin;
        if (questLog.OpenClearMapEventNames().Contains(definition.Name, StringComparer.OrdinalIgnoreCase)) return true;
        if (MapEventData.DungeonOfMap(definition.Map) is not { } room) return false;
        if (room.Type == "FieldEvent" && room.Event is { } fieldEvent)
        {
            if (copy?.Phase == "Prepare" && definition.Name.Contains("Prepare", StringComparison.OrdinalIgnoreCase)) return true;
            if (copy?.Phase != "Prepare" && string.Equals(definition.Name, fieldEvent, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (room.QuestGate is { Length: > 0 })
            return questLog.InstanceLayerGateOpen(room) || LayerUseEnvObjEntryOpen(room.Map);
        if (room.Type == "Quest") return QuestStepNeedsDungeonMap(definition.Map);
        return room.Type is not "InstanceLayer";
    }

    /// <summary>
    /// QuestTeleport_RQ 0x8d16: the journal's "Go to the quest area?" confirm. The destination comes from the quest's data
    /// (<see cref="QuestTeleportResolver"/>: the NPC the step asks for, wherever it stands, the ClearMapEvent's layer, the move
    /// point, the quest map). A Quest-type solo dungeon (Dream Crossroad: AQ2602000's Ashar) is entered, a field map is a
    /// teleport. QuestTeleport_RS 0x8d17 is {u16 result, i32 uid, i32 step objective index} (CANDIDATE: usmap names only
    /// _step_obj_index besides the usual result); it is sent on every outcome, result 1 when nothing could be resolved.
    /// Live 2026-10-30 HQ1102010 (Nania on map 1010, a field map): the dungeon-only lookup found nothing and the confirm did nothing.
    /// </summary>
    List<byte[]> QuestTeleport(int uid, long now)
    {
        List<byte[]> Reply(int result) => [PacketWriter.Opcode(0x8d17).U16(result).I32(uid).I32(0).ToArray()];
        if (questLog?.ByUid(uid) is not { } record || questLog.Book.Find(record.Code) is not { } quest) return Reply(1);
        var step = record.Status == QuestLog.Incomplete ? questLog.CurrentStep(record) : null;
        if (record.Status == QuestLog.Incomplete && step is null || record.Status is not (QuestLog.Incomplete or QuestLog.Complete))
        {
            Note($"QuestTeleport {record.Code} uid {uid}: quest is not in a state with an area (status {record.Status})");
            return Reply(1);
        }
        if (QuestTeleportResolver.Resolve(quest, step, data) is not { } dest)
        {
            Note($"QuestTeleport {record.Code} uid {uid}: no quest map to enter (status {record.Status}, step {record.Step})");
            return Reply(1);
        }
        if (resources is null || reloading || dead) return Reply(1);
        if (dest.Kind == QuestTeleportKind.QuestDungeon)
        {
            if (dest.Map == worldMap) return Reply(0);
            var moved = EnterDungeon(MapEventData.DungeonIdOfMap(dest.Map), now);
            Note($"QuestTeleport {record.Code} uid {uid}: dungeon map {dest.Map} {(moved.Count > 0 ? "entered" : "refused")} ({dest.Source})");
            return moved.Count > 0 ? [.. Reply(0), .. moved] : Reply(1);
        }
        Note($"QuestTeleport {record.Code} uid {uid}: map {dest.Map} ({dest.Source}{(dest.Npc is { } npc ? $" npc {npc}" : "")})");
        return [.. Reply(0), .. Teleport(dest.Map, dest.At, now)];
    }

    /// <summary>
    /// Where a cleared map event sends the player: the finish teleport (QuestData CompleteTeleportName) of the quest whose
    /// ClearMapEvent objective it pays (HQ1101070's DawnLegionBase_Indoor_Recovery_Start: the recovery room, not the way back in).
    /// Taken at the clear, before the quest moves on. Null: the layer's own exit.
    /// </summary>
    (int Map, Vector3d At)? ClearExitOf(string mapEvent)
    {
        if (questLog is null) return null;
        foreach (var quest in questLog.OpenClearMapEventQuests(mapEvent))
        {
            if (quest.FinishTeleport is not { } name) continue;
            var dest = QuestTeleportResolver.ForNamedPoint(name, quest.FinishMap ?? quest.Map, data, "clear-exit");
            if (dest is { Kind: QuestTeleportKind.Field }) return (dest.Map, dest.At);
        }
        return null;
    }

    /// <summary>The way out the cleared quest's finish teleport names (see <see cref="ClearExitOf"/>), set at the clear.</summary>
    (int Map, Vector3d At)? clearExit;

    /// <summary>Quest-type solo maps (Dream Crossroad, …): enter when the active step's AskNpc target spawns only there.</summary>
    bool QuestStepNeedsDungeonMap(int dungeonMap)
    {
        if (questLog is null) return false;
        // A finished quest waiting for its turn-in NPC there counts too (live 2026-10-30: AQ2602000 "The One Who Perceives"
        // complete, its finish NPC N_DreamCrossroad_Ashar_01 comes only from DreamCrossroad_MapEvent's StartSpawner, so the
        // event never started and Ashar never appeared).
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Complete))
            if (questLog.Book.Find(record.Code)?.FinishNpc is { } finish && GameData.Embedded.Spawns(dungeonMap).Any(s => s.NpcId == finish))
                return true;
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Incomplete))
        {
            if (questLog.CurrentStep(record) is not { } step) continue;
            foreach (var o in step.Objectives.Take(6))
            {
                if (o is { Type: "EnterSubZone", Target: not null }
                    && Subzones.OnMap(dungeonMap).Any(a => string.Equals(a.Name, o.Target, StringComparison.OrdinalIgnoreCase)))
                    return true;
                if (o is not { Type: "AskNpc" or "CloseNpc", Npc: var npc }) continue;
                if (GameData.Embedded.Spawns(dungeonMap).Any(s => s.NpcId == npc)) return true;
            }
        }
        return false;
    }

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
