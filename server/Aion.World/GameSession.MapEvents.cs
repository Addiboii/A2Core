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
    /// <summary>NPCs that reached the end of a route the client marks as a despawn (RouteActions): key, when they vanish.</summary>
    readonly List<(int Key, long DueMs)> routeDespawns = [];
    /// <summary>NPCs getting up from a held pose (a stopped Persistant social action): key, when they can walk (RouteActions.GetUpMs).</summary>
    readonly Dictionary<int, long> gettingUp = [];
    /// <summary>Walks a script started while the NPC was still getting up: key, route, when they start.</summary>
    readonly List<(int Key, NpcRoute Route, long DueMs)> pendingWalks = [];
    (string Point, string Mode, long DueMs)? pendingExit;
    /// <summary>Where the event last moved the player inside this map (PCTeleport): the revival point when the stage names none.</summary>
    Vector3d? eventRevivalPoint;
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

    /// <summary>Checks only: a run of this session (a synthetic script).</summary>
    internal void AddMapEventRunForChecks(MapEventRun run) => mapEventRuns.Add(run);

    /// <summary>Checks only: when a walk waiting for its NPC to get up starts (0: none).</summary>
    internal long PendingWalkForChecks(int key) => pendingWalks.FirstOrDefault(w => w.Key == key).DueMs;

    /// <summary>Checks only: the map event packets waiting to go out.</summary>
    internal List<byte[]> DrainMapEventsForChecks() => DrainMapEvents();

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
        // A scheduled festival event (GameSession.Systems2.cs): its NPC lets in only during the entry window.
        if (entry.Teleport == 0 && MapEventData.Dungeon(entry.Dungeon) is { Type: "FieldEvent" } eventRoom && FieldEventEntryClosed(eventRoom.Map, now))
        {
            Note($"NpcConversation: {npc.Name} -> field event dungeon {entry.Dungeon}: not in its entry window");
            return replies;
        }
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
        {
            var inside = entry.Contains(at);
            // The client left this layer by itself (it found the player outside its volume): its trigger waits until the player has
            // stepped out of it (live 2026-11-09, Axel3 by the Ugaros camp: in and out of GarrisonTerritory_L1_H1 every few 100 ms).
            if (entry.Dungeon == leftByClientDungeon)
            {
                if (!inside) leftByClientDungeon = 0;
                continue;
            }
            if (inside && EnterDungeon(entry.Dungeon, now, byTrigger: true) is { Count: > 0 } moved) return moved;
        }
        return [];
    }

    /// <summary>
    /// The move into a dungeon's private copy: allowed when an event of its map can start now (its quest step is active),
    /// when the map has no events, or for staff. Arrival: the current position under an instance layer that overlays this
    /// map, else the dungeon's start point. The official move: 0x3647 self (reason 12, effect 0x41) then 0x3621.
    /// </summary>
    internal List<byte[]> EnterDungeonForChecks(int dungeonId, long now, bool byTrigger) => EnterDungeon(dungeonId, now, byTrigger: byTrigger);

    /// <summary>OURS: door/portal-entered overlay layers whose data has no walkable player start, so the generic entry wedges the
    /// player at the threshold between the golden wall and the inner door; a known-walkable interior point (an NPC spawn) is used
    /// instead. HQ003 guard storage (110004) -> Anders's spawn N_D_Starter_HQ003_Anders_01 (live 2026-11-07, Prison Compound quest).</summary>
    static readonly Dictionary<int, Vector3d> DoorEntryArrival = new() { [110004] = new Vector3d(32788, 17673, 9202), [200005] = new Vector3d(-44, -403900, 71316) };

    /// <summary>Set while a door object's portal is being used (EnterDungeon then moves the player inside a layer instead of a seamless switch).</summary>
    bool enteringByDoor;

    /// <summary>The dungeon whose layer the client asked to leave (0x4004 / 0x930d / 0xe21d): its entry trigger is ignored until the
    /// player has stepped out of it.</summary>
    int leftByClientDungeon;

    List<byte[]> EnterDungeon(int dungeonId, long now, int copy = 0, bool byTrigger = false)
    {
        if (MapEventData.Dungeon(dungeonId) is not { } room || resources is null || reloading || dead) return [];
        // Only served dungeon maps (their scripts, points and spawns are generated); staff may look at the rest.
        if (!IsAdmin && !MapEventData.Serves(room.Map)) { Note($"dungeon {room.Name}: map {room.Map} is not served"); return []; }
        var events = MapEventData.EventsOn(room.Map);
        // Walking into the trigger box follows the quest for staff too (live 2026-11-07: a staff character finished the sky-island
        // script, MapExit put it on map 1000 inside the box, and it was pulled back into the layer at once); staff still enter by
        // NPC and command.
        if (copy == 0 && (!IsAdmin || byTrigger) && events.Count > 0 && !events.Any(e => new MapEventRun(e, 0, this, now).Holds(e.Start, now))) return [];
        // Walking into an entry box pulls the player in only for a quest-gated layer (an event with a start condition, the story
        // layers). A layer whose events start unconditionally (strongholds, Seal dungeons) is entered by its door object, as official
        // (rec 20261106-105448 #218.163: the Seal 320013 entry followed the door's 0x363a). Live 2026-11-11: walking by - or logging
        // in at - the Ugaros Thieves Campsite pulled the player into the layer, and the client stayed in its dungeon state.
        if (byTrigger && events.Count > 0 && events.All(e => !e.Start.Any())) return [];
        if (!IsAdmin && !MeetsEntry(room, out var why)) { Note($"dungeon {room.Name}: entry refused ({why})"); return []; }
        var at = position ?? spawn?.Position ?? default;
        // OURS (user 2026-11-07): a door/portal entry into an overlay layer can drop the player at the building threshold, wedged
        // between the golden boundary wall and the inner door (HQ003 guard storage 110004: could not move, position spread ~1 m).
        // A known-walkable interior arrival (an NPC's spawn inside) is used for those maps.
        // A door into a layer over this map: the layer's own MapData point (D_Starter_Layer_2_Respawn sits 2.6 m behind the Prison
        // Compound back door, straight in) when it lies inside the layer. The generic inward step went diagonally towards the
        // polygon's middle and left the player in the door wall (live 2026-11-09, sorcy, 110005).
        var layerVolume = MapEventData.Volume(room.Map) is { } v && v.Base == worldMap ? v : null;
        // A trigger box outside the layer's volume (GarrisonTerritory_L1_H1: the box is 60 m from it) arrives at the layer's own point
        // too; the generic inward step stopped 8 m inside the edge and the client left the layer again at once.
        var doorPoint = layerVolume is not null && (enteringByDoor || byTrigger && !layerVolume.Contains(at)) ? DoorArrival(room, layerVolume, at) : null;
        var arrival = DoorEntryArrival.TryGetValue(room.Map, out var fixedArrival) ? fixedArrival
            : doorPoint ?? (layerVolume is not null ? InsideLayer(layerVolume, at) : ArrivalPoint(room) ?? at);
        instanceReturn = (worldMap, at);
        instanceKey = copy != 0 ? copy : playerKey;
        instanceExitAt = null;
        // Through a door into a layer over this map: a full move to the arrival inside. The seamless layer move left the client where it
        // stood - at the door, outside the layer's boundary - and it left the layer by itself 5 s later (live 2026-11-09, sorcy: the
        // Prison Compound back door into 110005; HQ003's storage door left the player outside the storage the same way).
        if (enteringByDoor && MapEventData.Volume(room.Map) is { } doorLayer && doorLayer.Base == worldMap)
        {
            // The door's seamless teleport effect in the move (reason 2, e.g. 40 SeamlessDoorTeleport_heavy_in): the client plays the
            // push-open animation and walks in. Without one, a plain move.
            Note($"dungeon {room.Name}: entered by its door (effect {doorEffect}), moved inside to {arrival}");
            return doorEffect > 0 ? InstanceMove(room.Map, arrival, now, teleportEffect: (byte)doorEffect) : Teleport(room.Map, arrival, now);
        }
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
    /// <summary>OURS: an overlay layer is entered at least this far (cm) inside its volume's edge.</summary>
    public const double LayerEntryInset = 600;

    /// <summary>
    /// Where a layer over this map is entered from a spot: that spot when it lies well inside the layer's volume, else the spot
    /// moved toward the volume's middle. Live 2026-11-06: entered 43 cm inside HQ004's edge (the saved way out), the client
    /// asked to leave the layer at once (0xe21d) and the player bounced 44 times at its golden wall.
    /// </summary>
    internal static Vector3d InsideLayer(LayerVolume volume, Vector3d at)
    {
        var corners = volume.Points.Length > 1 && volume.Points[0].SequenceEqual(volume.Points[^1]) ? volume.Points[..^1] : volume.Points;
        if (corners.Length < 3) return at;
        double EdgeDistance(double x, double y)
        {
            var best = double.MaxValue;
            for (var i = 0; i < corners.Length; i++)
            {
                double ax = corners[i][0], ay = corners[i][1], bx = corners[(i + 1) % corners.Length][0], by = corners[(i + 1) % corners.Length][1];
                double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
                var t = len == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len, 0, 1);
                best = Math.Min(best, Math.Sqrt(Math.Pow(x - (ax + t * dx), 2) + Math.Pow(y - (ay + t * dy), 2)));
            }
            return best;
        }
        if (volume.Contains(at) && EdgeDistance(at.X, at.Y) >= LayerEntryInset) return at;
        double cx = corners.Average(c => c[0]), cy = corners.Average(c => c[1]);
        double vx = cx - at.X, vy = cy - at.Y, length = Math.Sqrt(vx * vx + vy * vy);
        if (length < 1) return at;
        // Step inward until well inside (or the middle is reached).
        for (var step = LayerEntryInset; step < length; step += LayerEntryInset)
        {
            var moved = at with { X = at.X + vx / length * step, Y = at.Y + vy / length * step };
            if (volume.Contains(moved) && EdgeDistance(moved.X, moved.Y) >= LayerEntryInset) return moved;
        }
        return at with { X = cx, Y = cy };
    }

    /// <summary>
    /// A door into a layer over this map: the layer's own MapData point inside its volume nearest the door. The Longest Night's cabin door
    /// (110006) stands 550 cm from D_Starter_Layer_3_Respawn_Village; the first point (Respawn_Valley) is 8000 cm away past the village fight
    /// the first step happens in (content audit 2026-11-09). One point (the Prison Compound's D_Starter_Layer_2_Respawn) is that point.
    /// </summary>
    Vector3d? DoorArrival(DungeonInfo room, LayerVolume volume, Vector3d door)
    {
        if (Instances.Of(room.Map) is not null) return ArrivalPoint(room) is { } hand && volume.Contains(hand) ? hand : null;
        var inside = MapEventData.Points(room.Map).Values.Select(p => new Vector3d(p[0], p[1], p[2])).Where(volume.Contains).ToList();
        return inside.Count == 0 ? null : inside.MinBy(p => p.DistanceSquared(door));
    }

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
        // A revival inside the copy after the script's MapExit fired (HQ004: the closing bombs can kill the player in its 2 s
        // delay) reloads the same map: the way out still holds, else the player waits in the cleared layer. (A2Core PR #4.)
        var keepExit = mapEventsMap == worldMap && instanceKey != 0 ? pendingExit : null;
        mapEventsMap = 0;
        mapEventRuns.Clear();
        eventRevivalPoint = null;
        dormantSpawners.Clear();
        startedSpawners.Clear();
        removedObjects.Clear();
        ClearEnvObjDirectives();
        finishedObjects.Clear();
        scriptKills.Clear();
        pendingSpawns.Clear();
        pendingAggro.Clear();
        routeDespawns.Clear();
        gettingUp.Clear();
        pendingWalks.Clear();
        pendingExit = keepExit;
        npcBuffs.Clear();
        if (!RunsMapEvents || targets is null) return;
        dormantSpawners.UnionWith(MapEventData.Deferred(worldMap));
        // A festival game's own spawners (trap boards, round bonus orbs, its EventFunc phases) wait for the game.
        dormantSpawners.UnionWith(FestivalDormant(worldMap));
        // A daily dungeon's waves come order by order (GameSession.DailyDungeons.cs).
        dormantSpawners.UnionWith(DailyDormantSpawners(worldMap, targets.Values.Select(t => t.Name).Concat(WorldObjects.ObjectsOn(worldMap).Select(o => o.Name))));
        // A permanent placement of the same friendly NPC a script spawns stays hidden in the copy: Caelid's office (101001) has
        // N_Kalid_L1_02 (the script's) and N_Kalid_L1_3, both 2930005; official shows one Kalid (live 2026-11-07: two).
        // A placement the script itself names is never hidden: the HQ002 layer (100011) moves and ends N_L0_Starter_HQ002_Nania_01_1,
        // a permanent placement with the template of the spawned Nania_01 (live 2026-11-07: Nania missing in HQ1101030 step 3).
        var scripted = targets.Values.Where(t => dormantSpawners.Contains(t.Name) && !t.Hostile).Select(t => t.NpcId).ToHashSet();
        if (scripted.Count > 0 && MapEventData.Volume(worldMap) is not null)
        {
            var named = ScriptNames(worldMap);
            foreach (var t in targets.Values)
                if (!t.Hostile && scripted.Contains(t.NpcId) && !dormantSpawners.Contains(t.Name) && t.Name.StartsWith("N_", StringComparison.Ordinal)
                    && !named.Contains(t.Name))
                    dormantSpawners.Add(t.Name);
        }
        foreach (var t in targets.Values)
            if (dormantSpawners.Contains(t.Name))
            {
                t.Hp = 0;
                t.CorpseRemoved = true;
                respawnAt.Remove(t.Key);
            }
    }

    /// <summary>Every name the map's scripts mention (directive and objective values, comma lists split).</summary>
    internal static HashSet<string> ScriptNames(int map)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in MapEventData.EventsOn(map))
        {
            var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(e));
            void Walk(System.Text.Json.JsonElement x)
            {
                switch (x.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object: foreach (var p in x.EnumerateObject()) Walk(p.Value); break;
                    case System.Text.Json.JsonValueKind.Array: foreach (var v in x.EnumerateArray()) Walk(v); break;
                    case System.Text.Json.JsonValueKind.String:
                        foreach (var n in x.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) names.Add(n);
                        break;
                }
            }
            Walk(json.RootElement);
        }
        return names;
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
            return owner.mapEventRuns.Select(run => GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.StepOrder, run.JoinGoals, run.StartedMs, run.Variables)).ToList();
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
            replies.Add(GamePackets.MapEventJoin(run.Id, run.Name, (byte)run.Status, run.StageOrder, run.StepOrder, run.JoinGoals, run.StartedMs, run.Variables));
            run.Start(now);
        }
        replies.AddRange(DrainMapEvents());
        return replies;
    }

    // ---------- Events from play ----------

    List<byte[]> MapEventKilled(Target target, long now)
    {
        FestivalKilled(target, now);   // the killer's points (GameSession.FestivalGames.cs)
        if (ToCopyOwner(o => o.MapEventKilled(target, now))) return [];
        CubeKilled(target, now);
        DailyKilled(target, now);
        if (mapEventRuns.Count == 0) return DrainMapEvents();
        mapEventNow = now;
        var alive = targets?.Values.Any(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase) && !t.Dead) == true;
        foreach (var run in mapEventRuns.ToList()) run.Killed(target.NpcId, target.Name, target.Hostile, alive, now, target.SubType);
        return DrainMapEvents();
    }

    /// <summary>An NPC the script will have the player destroy only at a later step (its KillNpc objective is still ahead): immune
    /// until then, so a watchstone/totem is not broken early (user 2026-11-07, Asmodian HQ001 Balaur Watchstone).</summary>
    bool KillPending(Target t) => (CopyOwner ?? this).mapEventRuns.Any(r => r.KillPending(t.NpcId, t.Name));

    /// <summary>The lowest HP a target may drop to while a map event of this copy needs it (HpRemaining threshold, AskNpc NPCs 1 HP).</summary>
    int HpFloor(Target t)
    {
        var runs = (CopyOwner ?? this).mapEventRuns;
        if (runs.Count == 0 || t.HpMax <= 0) return 0;
        double? floor = null;
        foreach (var run in runs) if (run.HpFloor(t.NpcId, t.Name) is { } f) floor = Math.Min(floor ?? 100, f);
        return floor is { } percent ? (int)Math.Max(1, Math.Floor(t.HpMax * percent / 100.0)) : 0;
    }

    List<byte[]> MapEventHp(Target target, long now, bool byPlayer = true)
    {
        if (ToCopyOwner(o => o.MapEventHp(target, now, byPlayer)) || mapEventRuns.Count == 0 || target.HpMax <= 0) return [];
        mapEventNow = now;
        var percent = (int)Math.Ceiling(target.Hp * 100.0 / target.HpMax);
        foreach (var run in mapEventRuns.ToList())
        {
            // The player's own hit is a fight too (PCCombatStart), whether or not the client bracketed it; an NPC's hit is not.
            if (byPlayer) run.CombatStarted(now);
            run.HpChanged(target.NpcId, target.Name, percent, now);
        }
        return DrainMapEvents();
    }

    /// <summary>A talk with an NPC: the step talk (0x8d2b kind 2: AskNpc, CloseNpc) names the objective; closing the dialog (0xe242) counts CloseNpc only.</summary>
    List<byte[]> MapEventTalked(int npcKey, int? index, bool close, long now)
    {
        if (ToCopyOwner(o => o.MapEventTalked(npcKey, index, close, now)) || mapEventRuns.Count == 0 || targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.Talked(npc.NpcId, npc.Name, index, close, now);
        return DrainMapEvents();
    }

    List<byte[]> MapEventUsedObject(string spawnName, long now, string? envName = null)
    {
        if (ToCopyOwner(o => o.MapEventUsedObject(spawnName, now, envName)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.UsedObject(spawnName, now, envName);
        return DrainMapEvents();
    }

    /// <summary>An object the player destroyed (EnvDestroy, bombs): its uses and the UpdateEnvObjState "Dead" objectives.</summary>
    List<byte[]> MapEventDestroyed(string spawnName, long now)
    {
        if (ToCopyOwner(o => o.MapEventDestroyed(spawnName, now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.Destroyed(spawnName, now);
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

    /// <summary>
    /// The client reported entering a trigger actor's volume (0x3636 with its key): that volume's EnterVolumePC objectives are done,
    /// as official (capture 20261107-014953: each Brigade escape trigger's interaction advanced the step, and Seq_1/2/3 set the map
    /// environment states 2/3/4 that open the way). The position check stays for clients that never report it.
    /// </summary>
    List<byte[]> MapEventEnteredTrigger(string name, long now)
    {
        if (ToCopyOwner(o => o.MapEventEnteredTrigger(name, now)) || mapEventRuns.Count == 0) return [];
        mapEventNow = now;
        foreach (var run in mapEventRuns.ToList()) run.PositionAt(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase), _ => false, now);
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
        // A keyed cutscene of this session's runs (or the copy owner's): its member is done and the request complete, once
        // (OBSERVED solo; in a party the first member completes it, CANDIDATE).
        if (groupKey != 0 && (openGroupRequests.Remove(groupKey) || CopyOwner?.openGroupRequests.Remove(groupKey) == true))
            replies.AddRange([GamePackets.GroupRequestProgressed(groupKey, playerKey), GamePackets.GroupRequestComplete(groupKey)]);
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
        if (mapEventRuns.Count == 0 && pendingSpawns.Count == 0 && pendingAggro.Count == 0 && routeDespawns.Count == 0 && pendingWalks.Count == 0 && pendingExit is null && npcBuffs.Count == 0 && copy is null) return [];
        mapEventNow = now;
        DungeonTimeLimit(now);
        DungeonEndTick(now);   // GameSession.PartyDungeons.cs
        DailyTick(now);
        var phase = DungeonPhaseTick(now);
        if (phase.Count > 0) mapEventOut.InsertRange(0, phase);
        NpcBuffTick(now);
        foreach (var order in pendingAggro.Where(p => now >= p.DueMs).ToList())
        {
            pendingAggro.Remove(order);
            AggroNpc(order.Spawner, order.Target);
        }
        foreach (var pending in pendingSpawns.Where(p => now >= p.DueMs).ToList())
        {
            pendingSpawns.Remove(pending);
            if (pending.Spawn) DoSpawn(pending.Spawner);
            else DoDespawn(pending.Spawner, pending.Kill);
        }
        foreach (var (key, route) in ai.TakeArrivals())
            if (targets?.GetValueOrDefault(key) is { } npc)
            {
                foreach (var run in mapEventRuns.ToList()) run.NpcArrived(npc.Name, route, now);
                if (RouteActions.Embedded.DespawnsAtEnd(worldMap, route)) routeDespawns.Add((key, now + RouteActions.DespawnDelayMs));
            }
        foreach (var walk in pendingWalks.Where(w => now >= w.DueMs).ToList())
        {
            pendingWalks.Remove(walk);
            if (targets?.GetValueOrDefault(walk.Key) is { Dead: false } walker) mapEventOut.AddRange(ai.Walk(walker, walk.Route, now));
        }
        foreach (var due in routeDespawns.Where(d => now >= d.DueMs).ToList())
        {
            routeDespawns.Remove(due);
            if (targets?.GetValueOrDefault(due.Key) is { } walker) DespawnTarget(walker, kill: false);
        }
        foreach (var run in mapEventRuns.ToList()) run.Tick(now);
        ScriptKillsCount(now);
        var festival = FestivalTick(now);   // GameSession.FestivalGames.cs
        var replies = DrainMapEvents();
        replies.AddRange(festival);
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
        return host.removedObjects.Contains(index) || host.dormantSpawners.Count > 0 && host.dormantSpawners.Contains(o.Name)
            || host.copy?.Festival?.Rejected.Contains(index) == true;   // one object per position of a festival spawn entry
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
            copy?.Festival?.Taken.Remove(index);   // a festival spawner started again gives its points again
            if (position is { } at && o.At.DistanceSquared(at) <= WorldObjects.VisibleRange * WorldObjects.VisibleRange && !(InstanceMap && RemovedBarrier(o))
                && copy?.Festival?.Rejected.Contains(index) != true && shownObjects.Add(index))
                mapEventOut.Add(PlacedSpawn(WorldObjects.FirstKey + index, o, mapEventNow));   // a bGetRewardOnce box with its spawn achievement
        }
    }

    /// <summary>EndSpawner: the NPCs vanish (0x3642 flag 7, OBSERVED for Robin); KillSpawner / a killed barrier: they die (HP 0, flag 3).</summary>
    void DoDespawn(string spawner, bool kill)
    {
        var all = string.Equals(spawner, "all", StringComparison.OrdinalIgnoreCase);
        foreach (var t in (all ? targets?.Values.Where(t => t.Key != Combat.TrainingDummyKey) ?? [] : SpawnerNpcs(spawner)).ToList())
            DespawnTarget(t, kill);
        foreach (var (index, _) in (all ? WorldObjects.ObjectsOn(worldMap).Select((o, i) => (i, o)) : SpawnerObjects(spawner)).ToList())
        {
            removedObjects.Add(index);
            if (shownObjects.Remove(index)) mapEventOut.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index));
        }
    }

    /// <summary>One NPC leaves the copy: despawned (0x3642 act 7) or killed (HP 0, act 3); it stays out until a spawn starts it again.</summary>
    void DespawnTarget(Target t, bool kill)
    {
        if (t.Dead && t.CorpseRemoved) return;
        t.Hp = 0;
        t.CorpseRemoved = true;
        respawnAt.Remove(t.Key);
        mapEventOut.AddRange(ai.Killed(t));
        if (kill) mapEventOut.Add(GamePackets.NpcHp(t.Key, 0));
        if (kill) scriptKills.Add(t);
        mapEventOut.Add(GamePackets.Death(t.Key, kill ? 3 : 7));
        seenNpcs.Remove(t.Key);
    }

    void Note(string text)
    {
        if (mapEventNotes.Count < 200) mapEventNotes.Add(text);
        if (!QuietNotes) MapEventLog?.Invoke($"[{CharacterName ?? "?"} map {worldMap}] {text}");
    }

    /// <summary>Bots keep their notes to themselves (100 of them would bury the players' notes in logs/session-notes.log).</summary>
    public bool QuietNotes { get; set; }

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
    /// is started again by Sven's step 7 talk; a talk inside those 2 s let the stale despawn remove the door step 10 opens. (A2Core PR #4.)</summary>
    void CancelPendingSpawns(string spawner) =>
        pendingSpawns.RemoveAll(p => string.Equals(p.Spawner, spawner, StringComparison.OrdinalIgnoreCase));

    /// <summary>SummonAroundTarget: the spawner's NPCs placed around the target NPC (or the player) and spawned.</summary>
    void IMapEventHost.SummonAround(string target, string spawner, int count, double radius)
    {
        var centre = SpawnerNpcs(target).FirstOrDefault(t => !t.Dead)?.Position ?? position ?? spawn?.Position ?? default;
        // An EnvObjData template instead of a spawner (CoastalCave, DaevaHunter: Basic_RecoveryOrb_01 around a monster): the objects
        // are summoned (GameSession.EnvBoxes.cs); their origin key the target NPC's (CANDIDATE). They go out with the event's messages.
        if (EnvUsages.IdOf(spawner) is var summonedEnv and > 0 && !SpawnerNpcs(spawner).Any())
        {
            var origin = SpawnerNpcs(target).FirstOrDefault()?.Key ?? playerKey;
            mapEventOut.AddRange(SummonEnvObjects(Enumerable.Repeat(summonedEnv, Math.Max(1, count)).ToList(), centre, radius, origin, mapEventNow, broadcast: false));
            return;
        }
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
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead))
        {
            // An NPC still getting up from a held pose walks once it stands (OBSERVED: Elvan's step-3 walk 3.8 s after his 0x9508).
            pendingWalks.RemoveAll(w => w.Key == t.Key);
            if (gettingUp.TryGetValue(t.Key, out var stands) && stands > mapEventNow) pendingWalks.Add((t.Key, path, stands));
            else mapEventOut.AddRange(ai.Walk(t, path, mapEventNow));
        }
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

    void IMapEventHost.Talk(string talk)
    {
        lastTalkMs = mapEventNow;
        mapEventOut.Add(GamePackets.Caption(talk));
    }
    void IMapEventHost.ChatBubble(string spawner, string talk) => mapEventOut.Add(GamePackets.Caption(talk));

    void IMapEventHost.SocialAction(string spawner, int action, bool stop)
    {
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead))
        {
            mapEventOut.Add(GamePackets.SocialAction(t.Key, action, stop));
            if (stop && RouteActions.Embedded.GetUpMs(action) is > 0 and var getUp) gettingUp[t.Key] = mapEventNow + getUp;
            else if (!stop) gettingUp.Remove(t.Key);
        }
    }

    void IMapEventHost.SetAggroPc(string spawner)
    {
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead)) mapEventOut.AddRange(ai.ForceAggro(t, playerKey, mapEventNow));
    }

    /// <summary>SetAggroNpc [spawner, target spawner, aggro]: each NPC of the spawner attacks the nearest living NPC of the target.</summary>
    /// <summary>
    /// SetAggroNpc: the spawner's NPCs fight the target spawner's, the nearest first, then the rest of them. An order given with
    /// an NPC talk waits for it (OURS, <see cref="TalkHoldMs"/>): Munihelle's "attack the watchstone" and Elvida's attack came
    /// in one moment, so she attacked before the line was read.
    /// </summary>
    void IMapEventHost.SetAggroNpc(string spawner, string target)
    {
        if (lastTalkMs == mapEventNow) pendingAggro.Add((spawner, target, mapEventNow + TalkHoldMs));
        else AggroNpc(spawner, target);
    }

    void AggroNpc(string spawner, string target)
    {
        var prey = SpawnerNpcs(target).Where(t => !t.Dead).ToList();
        if (prey.Count == 0) { Note($"SetAggroNpc {spawner} -> {target}: no living target"); return; }
        foreach (var t in SpawnerNpcs(spawner).Where(t => !t.Dead))
            mapEventOut.AddRange(ai.ForceAggroNpc(t, prey.MinBy(p => p.Position.DistanceSquared(t.Position))!, mapEventNow, prey));
    }

    /// <summary>How long an NPC talk holds an attack order given with it (OURS: a caption's reading time).</summary>
    public const int TalkHoldMs = 3000;
    long lastTalkMs = -1;
    readonly List<(string Spawner, string Target, long DueMs)> pendingAggro = [];

    /// <summary>Where a player revives inside a dungeon: the running stage's RevivalPoint (a teleport point), else the dungeon's start; null elsewhere.</summary>
    Vector3d? DungeonRevivalPoint()
    {
        if (instanceKey == 0 || !MapEventData.Serves(worldMap)) return null;
        var points = MapEventData.Points(worldMap);
        foreach (var run in (CopyOwner ?? this).mapEventRuns)
            if (run.Stage?.RevivalPoint is { Length: > 0 } name && points.TryGetValue(name, out var p)) return new Vector3d(p[0], p[1], p[2]);
        // No stage revival point, but the event moved the player inside this map (the Tunapre lake cave's PCTeleport into the boss
        // room): revive there, since the dungeon start is behind steps that are already done (A2Core PR #4, live 2026-10-30 Dungeon_D_0018).
        if (eventRevivalPoint is { } moved) return moved;
        return MapEventData.DungeonOfMap(worldMap) is { } room ? ArrivalPoint(room) : null;
    }

    /// <summary>
    /// ChangeRelationshipEntity: the spawner's NPCs take the relationship (Monster: attackable, and fighting when aggressive;
    /// NPC_Companion / NPC_Neutral: at peace), 0x8d92 per visible NPC. Dormant ones spawn with it.
    /// </summary>
    void IMapEventHost.SetRelationship(string spawner, string relationship)
    {
        if (MapEventTables.Relationship(relationship) is not { } value) { Note($"ChangeRelationshipEntity {spawner} {relationship}: unknown relationship"); return; }
        foreach (var t in SpawnerNpcs(spawner))
        {
            // The script makes it a monster: attackable even when NpcData says it cannot be attacked or targeted (Target.Untouchable).
            if (t.Untouchable && Target.IsHostile(value)) t.Untouchable = false;
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

    /// <summary>One abnormal per id on an NPC (applied again it keeps its uid: 0x382b, OFFICIAL); caster the NPC itself, level 1, range base its position.</summary>
    void AddNpcBuff(Target t, int abnormalId, int durationMs, bool hold = false, int? casterKey = null, int? skillId = null, byte level = 1)
    {
        var (held, immune, relationship) = AbnormalModel(abnormalId);
        held |= hold;
        // An NPC whose standing abnormal is an immunity (10000001 of the hit-gauge totems) resists what it lists: the official
        // 0x3805 (mask 0, caster the NPC, the immunity's uid 1 and effect) and 0x382d ImmuneAbnormal_NT, no abnormal.
        var standing = t.CcImmune ? Target.AirPassiveAbnormal : t.SpawnAbnormal;
        if (casterKey is int by && by != t.Key && standing != 0 && AbnormalTable.Embedded.Blocking(standing, abnormalId, held) is { } shield)
        {
            mapEventOut.Add(RidePackets.AbnormalTriggered(t.Key, t.Key, 1, shield.Id));
            mapEventOut.Add(RidePackets.ImmuneAbnormal(t.Key, [shield.Id]));
            return;
        }
        // A player's stun on a boss: diminishing, immunity, the stagger gauge (BossControl; scripted map-event holds are untouched).
        if (held && t.Rank == NpcRank.Hero && casterKey is int caster && caster != t.Key && durationMs > 0)
        {
            (durationMs, _) = BossControl.OnCrowdControl(t, durationMs, mapEventNow);
            if (durationMs <= 0)
            {
                // Shrugged off: shown as the official CC immunity (effect 100000010 of 10000001, the bosses' immunity in the
                // recordings: 2701034, 2500016). CANDIDATE: our bosses do not carry 10000001, BossControl decides (OURS).
                if (AbnormalTable.Embedded.Of(AbnormalTable.CrowdControlImmunity)?.Effects.FirstOrDefault(e => e.Type == "ImmuneAbnormal") is { } cc)
                {
                    mapEventOut.Add(RidePackets.AbnormalTriggered(t.Key, t.Key, 1, cc.Id));
                    mapEventOut.Add(RidePackets.ImmuneAbnormal(t.Key, [cc.Id]));
                }
                return;
            }
        }
        // Applied again: same uid, 0x382b AbnormalChange_NT with the new expire time / caster / skill (OFFICIAL, see PlayerPackets.AbnormalChange).
        var previous = npcBuffs.GetValueOrDefault(t.Key)?.FirstOrDefault(b => b.AbnormalId == abnormalId);
        if (previous is not null) npcBuffs[t.Key].Remove(previous);
        var permanent = durationMs < 0;
        var buff = new NpcBuff { Uid = previous?.Uid ?? ++npcBuffUid, AbnormalId = abnormalId, ExpireAt = permanent ? long.MaxValue : mapEventNow + durationMs, Held = held, Immune = immune };
        if (previous?.Restore is { } restore) buff.Restore = restore;
        else if (relationship is { } shown && shown != t.Relationship)
        {
            buff.Restore = t.Relationship;
            t.Relationship = shown;
            mapEventOut.Add(GamePackets.RelationshipUpdate(t.Key, shown));
        }
        (npcBuffs.TryGetValue(t.Key, out var list) ? list : npcBuffs[t.Key] = []).Add(buff);
        RefreshNpcFlags(t);
        var wire = new AbnormalWire(buff.Uid, abnormalId, permanent ? -1 : durationMs,
            permanent ? PassiveExpireTime : mapEventNow + durationMs, CasterKey: casterKey ?? t.Key, Level: level, SkillId: skillId, RangeBase: t.Position);
        mapEventOut.Add(previous is null ? PlayerPackets.AbnormalAdd(t.Key, [wire]) : PlayerPackets.AbnormalChange(t.Key, wire, AbnormalTable.Embedded.ShowChange(abnormalId)));
        if (casterKey == playerKey && position is { } from) mapEventOut.AddRange(AbnormalMove(t, abnormalId, from, mapEventNow));
    }

    /// <summary>
    /// The forced move of a knockback / pull / knockdown abnormal on a monster (client SkillAbnormalEffect rows, OFFICIAL wire):
    /// Move_CannotUseSkill [knockback|Push, cm] pushes it away from the caster (0x3814 Abnormal_Move_Start_NT move type 1, then
    /// 0x3815 Abnormal_Move_Stop_NT at the goal when the move time is over), [Pull, cm] draws it to that distance from the caster
    /// (type 2), KnockdownInstant [cm] puts it there at once (0x3816 Abnormal_Move_Instant_NT type 4, the effect id). Terrain 1
    /// (ground) both ends and effect id 0 in the start as in all official samples (22 starts, 22 stops, 50 instants). The move
    /// time is 100 ms (the most common official value: 50-300 observed, CANDIDATE).
    /// </summary>
    List<byte[]> AbnormalMove(Target t, int abnormalId, Vector3d caster, long now)
    {
        if (AbnormalTable.Embedded.Of(abnormalId) is not { } row || t.Dead) return [];
        const int MoveMs = 100;
        var dx = t.Position.X - caster.X; var dy = t.Position.Y - caster.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var (ux, uy) = length < 1 ? (1.0, 0.0) : (dx / length, dy / length);
        Vector3d Floor(Vector3d p) => ai.Nav?.FloorAt(p.X, p.Y, p.Z, 500) is { } z ? p with { Z = z } : p;
        foreach (var e in row.Effects)
        {
            var kind = e.Values.Count > 0 ? e.Values[0] : "";
            var cm = e.Int(1);
            if (e.Type == "Move_CannotUseSkill" && kind is "knockback" or "Push" or "Pull")
            {
                var pull = kind == "Pull";
                var goal = Floor(pull ? new Vector3d(caster.X + ux * cm, caster.Y + uy * cm, t.Position.Z)
                    : new Vector3d(t.Position.X + ux * cm, t.Position.Y + uy * cm, t.Position.Z));
                var key = t.Key;
                t.Position = goal;
                Schedule(now + MoveMs, _ => targets is not null && targets.TryGetValue(key, out var still) && !still.Dead
                    ? [new Aion.Protocol.Native.Generated.AbnormalMoveStopNT { Key = key, StopPos = Aion.Protocol.Native.Generated.CommonVec3.From(goal), MoveType = (byte)(pull ? 2 : 1), EndTerrainType = 1 }.ToArray()]
                    : []);
                return [new Aion.Protocol.Native.Generated.AbnormalMoveStartNT { Key = t.Key, CasterKey = playerKey, EffectId = 0, GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(goal),
                    MoveTimeMs = MoveMs, MoveType = (byte)(pull ? 2 : 1), StartTerrainType = 1, EndTerrainType = 1 }.ToArray()];
            }
            if (e.Type == "KnockdownInstant")
            {
                var goal = Floor(new Vector3d(t.Position.X + ux * e.Int(0), t.Position.Y + uy * e.Int(0), t.Position.Z));
                t.Position = goal;
                return [new Aion.Protocol.Native.Generated.AbnormalMoveInstantNT { Key = t.Key, CasterKey = playerKey, EffectId = e.Id,
                    GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(goal), MoveType = 4, StartTerrainType = 1, EndTerrainType = 1 }.ToArray()];
            }
        }
        return [];
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
        if (at is not { } point3)
        {
            // Teleport.dat names it but no MapData we hold places its actor (D_Starter_Layer_3_StartPoint: the Longest Night's first
            // step, its cutscene Cutscene_D_Starter_Seq_2300 before the move). The player stays where the entry put him; the
            // directive's cutscenes still play, so the step's CompleteCutscene is reported by the client instead of timing out.
            Note($"PCTeleport {point}: no such point on map {worldMap}; its cutscenes play in place");
            foreach (var cutscene in new[] { before, after }.Where(c => c.Length > 0)) mapEventOut.Add(GamePackets.Cutscene(cutscene));
            return;
        }
        // The instance's recorded final move (instances.json final): its cutscene before the move and the official landing spot.
        if (Instances.Of(worldMap)?.Final is { } final && string.Equals(final.Teleport, point, StringComparison.OrdinalIgnoreCase))
        {
            if (before.Length == 0) before = final.Cutscene;
            if (final.Position is [var fx, var fy, var fz]) point3 = new Vector3d(fx, fy, fz);
        }
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
        // A move to another world map is a region move: reason 2 with its effect, the flight effect 12 when the script names
        // none (OBSERVED in all 16 recordings: quest teleports and region moves are reason 2, effect 12).
        else if (!IsInstanceMap(map)) move = Teleport(map, at, now, 2, effect != 0 ? effect : RegionMoveEffect, after);
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
        // A festival game's clear (Racing 10 s after the finish, Defence after the waves) ends the game with its rewards.
        if (copy is { Festival: { } festival } fc && fc.Host == this)
        {
            if (!clearedEvents.Contains(run.Name)) clearedEvents.Add(run.Name);
            FestivalFinish(festival, mapEventNow);
            return;
        }
        // A daily dungeon's clear ends its run with the points (GameSession.DailyDungeons.cs).
        if (copy is { Cleared: false } daily && daily.Host == this && Dailies.Of(daily.DungeonId) is { } dailyRoom)
        {
            DailyEnd(dailyRoom, daily, mapEventNow, timeUp: false, run.Name);
            return;
        }
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
            // A story layer (an instance layer over a base map: Caelid's office) is left through its exit door or the script's
            // MapExit, never by a timer (live 2026-11-07: the office's short hold moved the player out right after the clear, before
            // the last talk, and the door looked dead).
            if (MapEventData.Volume(worldMap) is not null) { }
            else if (Instances.Of(worldMap) is not null) session.instanceExitAt ??= mapEventNow + InstanceExitDelayMs;
            else if (room is not null) session.instanceExitAt ??= mapEventNow + hold;
            // The clear reward (RewardDungeon.dat RewardLight/RewardDark by race: sealed dungeons' kina, materials, a title), once per copy.
            if (first && room is not null && (session.profileRace == 2 ? room.RewardDark : room.RewardLight) is { Length: > 0 } reward)
                session.pendingRewards.Add(reward.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList());
            if (first && room is not null)
            {
                var earned = session.AchieveDungeonClear(room, run.Name, CopyMembers().Count() + 1);
                if (copy is { } cleared) earned.AddRange(session.AchieveDungeonClearLimits(room, mapEventNow - cleared.StartedMs, cleared.Deaths));
                if (copy is { } scored) session.RecordSeasonClear(room, scored.DungeonId, mapEventNow - scored.StartedMs, scored.Deaths);   // Rank (GameSession.Ranking.cs)
                if (session == this) mapEventOut.AddRange(earned); else session.worldPlayer?.Send(earned);
            }
        }
        if (copy is { } c && room is not null && !c.Cleared)
        {
            c.Cleared = true;
            // OBSERVED (community recordings 2026-11-04 to 06, reports/dungeons.md 2 and 7): an expedition sends 0x6108 `00` first;
            // DungeonCompleteResult_NT with the clear reward (sealed 310027 #44952); Complete (3) from the clear for Dungeon.dat's
            // hold (sealed [t, t + 10 s] #44953; the layer's hold 0 [t, t] #16201); End (4) when it ends, for 600 s (#45201,
            // the layer's 50 ms later #16209). The way out keeps its own timing (instanceExitAt: the hold, else 60 s, OURS).
            var tableHold = room.Hold > 0 ? room.Hold * 1000L : 0;
            c.ClearedMs = mapEventNow;
            c.CompleteEndsMs = mapEventNow + tableHold;
            if (room.Type == "Party") mapEventOut.Add(PacketWriter.Opcode(DungeonClearKillNpcNt).U8(0).ToArray());
            var shown = (profileRace == 2 ? room.RewardDark : room.RewardLight) is { Length: > 0 } won
                ? won.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList() : null;
            mapEventOut.Add(DungeonCompleteResult(c.DungeonId, true, shown));
            mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateComplete, c.ClearedMs, c.CompleteEndsMs));
            if (tableHold == 0)
            {
                c.EndSent = true;
                mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, c.ClearedMs, c.ClearedMs + DungeonEndStateMs));
            }
        }
    }

    void IMapEventHost.DungeonFail(MapEventRun run) => Note($"map event {run.Name} failed");
    /// <summary>SetMapEnvState: 0x4001 MapEnvState_NT {map, env state u8, start time, enter false, no infos} (usmap fields; the entry sends state 0 with enter true). CANDIDATE.</summary>
    void IMapEventHost.MapEnvState(int state)
    {
        // Kept for the copy: a reload into the same map (PCTeleport with a cutscene) shows the changed map, as official
        // (capture 20261107-014953: the re-entry's 0x4001 carries state 1 and its start time, the entry form "0100").
        mapEnvState = (worldMap, state, mapEventNow);
        mapEventOut.Add(PacketWriter.Opcode(0x4001).I32(worldMap).U8((byte)state).I64(mapEventNow).U8(0).Varint(0).ToArray());
    }

    /// <summary>The map environment state a map event set in this copy (map, state, since), or null.</summary>
    (int Map, int State, long Since)? mapEnvState;
    /// <summary>SetMapEnvSetting: 0x4002 MapEnvSetting_NT {map, [{group i32, env state u8, start time}], enter false}. CANDIDATE.</summary>
    void IMapEventHost.MapEnvSetting(int group, int state) => mapEventOut.Add(PacketWriter.Opcode(0x4002).I32(worldMap).Varint(1).I32(group).U8((byte)state).I64(mapEventNow).U8(0).ToArray());
    /// <summary>Cutscene: EventFuncAction Cutscene (9) with the sequence name; the client plays it and reports CompleteCutscene (0x9230 kind 2). CANDIDATE.</summary>
    void IMapEventHost.Cutscene(string name)
    {
        var key = Interlocked.Increment(ref lastGroupRequestKey);
        openGroupRequests.Add(key);
        mapEventOut.Add(GamePackets.Cutscene(name, groupKey: key));
    }

    /// <summary>Group request keys, rising across the server (OBSERVED rising; OURS: from 1 at each start).</summary>
    static long lastGroupRequestKey;
    readonly HashSet<long> openGroupRequests = [];
    void IMapEventHost.ShowGuide(string guide) => mapEventOut.Add(GamePackets.Guide(guide));
    void IMapEventHost.FocusCamera(string camera, string talk) => mapEventOut.Add(GamePackets.FocusCamera(camera, talk));
    /// <summary>ShowNote: the note by its NoteData name (the client reads it and reports CloseNote with 0x9230 kind 2).</summary>
    void IMapEventHost.ShowNote(string note) => mapEventOut.Add(GamePackets.Note(note));
    /// <summary>ShowMessage / BroadCastingMessage: the message by string key (the argument, an NPC name, is not carried; CANDIDATE).</summary>
    void IMapEventHost.Message(string key, string argument) => mapEventOut.Add(GamePackets.Message(key));

    /// <summary>
    /// ChangeEnvObjState naming an EnvObjData template (478 of the 480 in the client scripts: E_L0_Layer_1_DoorBoss_001): every object
    /// of the map placed from that template takes the state. OBSERVED (capture 20261107-010735, Elyos Layer_1_2): "DoorBoss_001 Finish"
    /// sent 0x8d6d InteractionEnvObjFinishState_NT to the three BossDoor objects (#12609-12611) and "Available" 0x363b to the same three
    /// (#13310-13312); "Door_001 Finish" of Layer_1_1 one 0x8d6d (#2209); no despawn. Before, the template name matched no spawn name,
    /// so these directives did nothing (doors scripted open stayed shut on the client). A spawn name (Fire Temple's two) keeps the old
    /// removal; Dead / Despawn remove the objects.
    /// </summary>
    void IMapEventHost.EnvObjState(string spawner, string state)
    {
        if (!spawner.StartsWith("E_", StringComparison.OrdinalIgnoreCase) || state is "Dead" or "Despawn")
        {
            if (state is "Finish" or "Dead" or "Despawn")
            {
                DoDespawn(spawner, false);
                foreach (var (index, _) in TemplateObjects(spawner)) DespawnObject(index);
            }
            return;
        }
        if (state is not ("Finish" or "Available")) return;
        foreach (var (index, _) in TemplateObjects(spawner))
        {
            var key = WorldObjects.FirstKey + index;
            if (state == "Finish") finishedObjects.Add(index); else finishedObjects.Remove(index);
            if (shownObjects.Contains(index)) mapEventOut.Add(state == "Finish" ? GamePackets.InteractEnvObjFinishState(key) : GamePackets.InteractEnvObjAvailableState(key));
        }
    }

    /// <summary>Objects of this map placed from the EnvObjData template (exact name, as the scripts write it).</summary>
    IEnumerable<(int Index, WorldObject Object)> TemplateObjects(string template) =>
        WorldObjects.ObjectsOn(worldMap).Select((o, i) => (i, o)).Where(x => string.Equals(EnvUsages.Of(x.o.Env)?.Name, template, StringComparison.OrdinalIgnoreCase));

    void DespawnObject(int index)
    {
        removedObjects.Add(index);
        if (shownObjects.Remove(index)) mapEventOut.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index));
    }

    /// <summary>
    /// NPCs a KillSpawner directive killed: their deaths count for the kill objectives like a player's kill, after the tick that ran the
    /// directive (not inside it). HQ2103220's layer (World_D_B_Layer_HQ010, step 3): each injured soldier's HpRemaining post kills an
    /// invisible NPC (KillSpawner N_D2_HQ010_Invisible_01_n) and the step waits for "KillNpc N_D2_HQ010_Invisible_01 x4"; the player can
    /// never hit them, so without this the step never ended (content walk 2026-11-09).
    /// </summary>
    readonly List<Target> scriptKills = [];

    void ScriptKillsCount(long now)
    {
        for (var guard = 0; scriptKills.Count > 0 && guard < 4; guard++)
        {
            var killed = scriptKills.ToList();
            scriptKills.Clear();
            foreach (var t in killed)
            {
                var alive = targets?.Values.Any(o => string.Equals(o.Name, t.Name, StringComparison.OrdinalIgnoreCase) && !o.Dead) == true;
                foreach (var run in mapEventRuns.ToList()) run.Killed(t.NpcId, t.Name, t.Hostile, alive, now, t.SubType);
            }
        }
        scriptKills.Clear();
    }

    /// <summary>Objects a map event set to their finished state (ChangeEnvObjState Finish): shown finished when they come into view later.</summary>
    readonly HashSet<int> finishedObjects = [];

    /// <summary>
    /// The name a used or destroyed object reports to the map events: its EnvObjData template when an open UseEnvObj objective names it
    /// (the scripts write templates: E_D0_Layer_2_PrisonDoorSmall_001), else its spawn name. Interacted, destroyed (EnvDestroy) and
    /// bombed objects all go through this; destroyed ones used only the spawn name and never counted (World_L_Starter_Layer_1_1 tanks
    /// E_L0_Layer_1_TankOn_001, World_D_Starter_Layer_1_1 E_D0_Layer_1_CrackDoor_001: content audit 2026-11-09).
    /// </summary>
    string MapEventObjectName(WorldObject o) => MapEventObjectName(o, (CopyOwner ?? this).mapEventRuns);

    internal static string MapEventObjectName(WorldObject o, IEnumerable<MapEventRun> runs) =>
        EnvUsages.Of(o.Env) is { Name.Length: > 0 } envData && runs.Any(r => r.WantsObject(envData.Name)) ? envData.Name : o.Name;

    void IMapEventHost.DynamicTerrain(string spawner, bool block)
    {
        if (block) DoSpawn(spawner);
        else DoDespawn(spawner, false);
    }

    /// <summary>
    /// 0x922d for a run's change. A run that ends through its script's MapExit sends no Complete (3) progress: the move out and
    /// 0x922f MapEventLeave close it. OBSERVED: none of the 7 official events ending in MapExit (Light HQ001-HQ004, FQ_D1 EP2_2/3/4,
    /// 2026-11-06/07) sent status 3, the 6 ending otherwise did (Layer_1_1, Layer_1_2, Seal_Altgard_013, BossChallenge,
    /// Dungeon_0005, DreamCrossroad). Our 3 went out in the same instant as the exit move (trace 20261110-122001 #215.250).
    /// </summary>
    void IMapEventHost.Progress(MapEventRun run)
    {
        if (run.Status == MapEventRun.Complete && pendingExit is not null) return;
        mapEventOut.Add(GamePackets.MapEventProgress(run.Id, (byte)run.Status, run.StageOrder, run.StepOrder, run.Goals, run.Variables));
    }

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

    // ---------- Held objectives, aggro lists, behaviours (audit 2026-11-13 G10, G11, G14, G15) ----------

    /// <summary>Item.dat internal names to ids (map-event CollectItem objectives name their item: Quest_UseItem_NoEffect_Key_A_c_01a_b).</summary>
    static readonly Lazy<Dictionary<string, int>> ItemIdsByName = new(() =>
        ItemDB.Embedded.All.Where(i => i.InternalName.Length > 0).GroupBy(i => i.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(i => i.Id), StringComparer.OrdinalIgnoreCase));

    /// <summary>The bag's count of the item (stored characters; development logins without a store cannot tell: -1).</summary>
    long IMapEventHost.HeldItems(string itemName) =>
        Store is null || !ItemIdsByName.Value.TryGetValue(itemName, out var item) ? -1
            : storedItems.Values.Where(i => i.TemplateId == item && i.Location == "bag").Sum(i => i.Quantity);

    /// <summary>The abnormal is on the player, or landed since the step began (a scripted or a monster's abnormal: GameSession.Buffs.cs).</summary>
    bool? IMapEventHost.GainedAbnormal(int abnormalId, long sinceMs) =>
        buffs.Any(b => b.AbnormalId == abnormalId) || abnormalsGainedAt.TryGetValue(abnormalId, out var at) && at >= sinceMs;

    /// <summary>NPCs of this copy seen fighting while a RemoveFromAggroList condition watched them.</summary>
    readonly HashSet<int> aggroWatched = [];

    /// <summary>
    /// RemoveFromAggroList: an NPC of the spawner fought (engaged) and now, alive, has left the fight: our AI drops its aggro list when it
    /// resets (leash, its players dead or gone). Reported once per fight. CANDIDATE: the official list holds each attacker separately;
    /// ours reads the boss's reset as "the players left the list" (one player at a time is not told apart).
    /// </summary>
    bool IMapEventHost.RemovedFromAggro(string spawner)
    {
        var removed = false;
        foreach (var t in SpawnerNpcs(spawner))
        {
            if (t.Dead) { aggroWatched.Remove(t.Key); continue; }
            var fighting = ai.Brains.TryGetValue(t.Key, out var brain) && brain.Engaged;
            if (fighting) aggroWatched.Add(t.Key);
            else if (aggroWatched.Remove(t.Key)) removed = true;
        }
        return removed;
    }

    /// <summary>ChangeBT: the NPCs take the behaviour (NpcAi.FightingBehaviour: who looks for monsters to fight).</summary>
    void IMapEventHost.ChangeBehaviour(string spawner, string behaviour)
    {
        foreach (var t in SpawnerNpcs(spawner)) t.Behaviour = behaviour.Length == 0 ? null : behaviour;
    }

    /// <summary>
    /// NpcChangePhase: kept on the NPC (Target.Phase) and noted. CANDIDATE: no official NpcChangePhase is recorded (the Caras bodies of
    /// Dungeon_D_0001/0016 and Daeva_Garrison_D2_R1, the Dratona eggs, Beritra's 8 uses); its message, if the client gets one, is unknown,
    /// so nothing is sent.
    /// </summary>
    void IMapEventHost.NpcPhase(string spawner, int phase)
    {
        foreach (var t in SpawnerNpcs(spawner)) t.Phase = phase;
        Note($"NpcChangePhase {spawner} -> {phase} (kept on the NPC, no message: CANDIDATE)");
    }

    double? IMapEventHost.DistanceTo(string spawner)
    {
        if (position is not { } at) return null;
        var spots = SpawnerNpcs(spawner).Where(t => !t.Dead).Select(t => t.Position).Concat(SpawnerObjects(spawner).Select(x => x.Object.At)).ToList();
        return spots.Count == 0 ? null : spots.Min(p => Math.Sqrt(p.DistanceSquared(at)));
    }
}
