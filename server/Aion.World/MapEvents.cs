using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

// Map events: the scripted stages of instance layers and dungeons (tools/map_events.py -> tools/data/map-events.json,
// from the client's Data/MapEvent/*.dat). An event belongs to a map and starts when its start conditions hold (a quest
// step being active); its stages hold steps, each with directives run before it (spawns, NPC walks, captions, delays),
// objectives (kills, talks, objects, trigger areas, NPC arrivals) with directives after each, and directives after the
// step. The last step's directives end with DungeonClear (the quest's ClearMapEvent objective) and MapExit.
// OBSERVED in the official HQ001 run (capture 20261013-144836 #9605-14304): MapEventJoin_NT 0x922e at entry, one
// MapEventUpdateProgress_NT 0x922d per objective change and per step, MapEventLeave_NT 0x922f after the exit move;
// the step's objectives count from the step start while its pre-directives still play; looped directives (a hurry
// caption every 15 s) do not block the ones after them; Delay does.

public sealed record MapEventCondition([property: JsonPropertyName("t")] string Type, [property: JsonPropertyName("v")] string[] Values)
{
    public string Value(int i) => i < Values.Length ? Values[i] ?? "" : "";
}

public sealed record MapEventDirective(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("v")] string?[] Values,
    [property: JsonPropertyName("n")] int Loop = 1,
    [property: JsonPropertyName("i")] int Interval = 0,
    [property: JsonPropertyName("pre")] bool PreDelay = false,
    [property: JsonPropertyName("c")] MapEventCondition[][]? Conditions = null)
{
    public string Value(int i) => i < Values.Length ? Values[i] ?? "" : "";
    public int Int(int i, int fallback = 0) => int.TryParse(Value(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public bool Flag(int i) => string.Equals(Value(i), "True", StringComparison.OrdinalIgnoreCase);
    /// <summary>A looped directive repeats every Interval (Loop 0: for ever) and does not hold up the ones after it.</summary>
    public bool Repeats => Loop != 1;
}

public sealed record MapEventObjective(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("v")] string?[] Values,
    [property: JsonPropertyName("opt")] bool Optional = false,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("npcs")] int[]? Npcs = null,
    [property: JsonPropertyName("post")] MapEventDirective[]? Post = null,
    [property: JsonPropertyName("pre")] MapEventDirective[]? Pre = null)
{
    public string Value(int i) => i < Values.Length ? Values[i] ?? "" : "";
    /// <summary>The comma-separated names of the first value (NPCs, spawners, objects).</summary>
    public string[] Names => Value(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public int Count => Type switch
    {
        "KillNpc" or "UseEnvObj" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" or "CollectItem" or "UseUsableItemType"
            => int.TryParse(Value(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1,
        // The points the step's UnifiedObjectivesList kills must add up to (the client's UnifiedObjectiveScore, the same number).
        "AchieveUnifiedObjectivesScore" => int.TryParse(Value(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) && score > 0 ? score : 1,
        _ => 1,
    };
    /// <summary>Objectives only the client can finish (a cutscene, a note, a talk, a guide); it reports them with 0x9230.</summary>
    public bool ClientSide => Type is "CompleteCutscene" or "CloseNote" or "CompleteNpcTalk" or "CloseGuide";
    /// <summary>
    /// Objectives the server does not track yet (puzzles, item uses): finished after a grace period so the event goes on. A score
    /// without its UnifiedObjectivesList, a wave not in WaveStepData and an object state no play reaches (None) also wait for it
    /// (MapEventRun.Tracked); CollectItem and GetAbnormal too when the host cannot tell (MapEventRun.HeldKnown).
    /// </summary>
    public bool Unsupported => Type is "ClearBoardPuzzle" or "PCSocial" or "UseUsableItemType" or "KillNpcCreatureType";

    /// <summary>Objectives counted from the player's state (the bag's items, the abnormals gained), checked every tick.</summary>
    public bool Held => Type is "CollectItem" or "GetAbnormal";

    /// <summary>UpdateEnvObjState states play reaches: destroyed (Dead), used (Occupied while used, then Finish).</summary>
    public static bool TrackedObjectState(string state) => state is "Dead" or "Finish" or "Occupied";
}

public sealed record MapEventStep(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("pre")] MapEventDirective[] Pre,
    [property: JsonPropertyName("obj")] MapEventObjective[] Objectives,
    [property: JsonPropertyName("post")] MapEventDirective[] Post,
    [property: JsonPropertyName("fail")] MapEventCondition[][]? Fail = null,
    [property: JsonPropertyName("faildir")] MapEventDirective[]? FailDirectives = null,
    [property: JsonPropertyName("uobj")] MapEventObjective[]? Unified = null,
    [property: JsonPropertyName("score")] int Score = 0);

public sealed record MapEventStage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("steps")] MapEventStep[] Steps,
    [property: JsonPropertyName("fail")] MapEventCondition[][]? Fail = null,
    [property: JsonPropertyName("goto")] string? FailGoTo = null,
    [property: JsonPropertyName("faildir")] MapEventDirective[]? FailDirectives = null,
    [property: JsonPropertyName("revive")] string? RevivalPoint = null);

public sealed record MapEventRule([property: JsonPropertyName("c")] MapEventCondition[][] Conditions, [property: JsonPropertyName("d")] MapEventDirective[] Directives);

public sealed record MapEventDefinition(
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("start")] MapEventCondition[][] Start,
    [property: JsonPropertyName("fail")] MapEventCondition[][] Fail,
    [property: JsonPropertyName("stages")] MapEventStage[] Stages,
    [property: JsonPropertyName("global")] MapEventRule[]? Rules = null,
    [property: JsonPropertyName("complete")] MapEventDirective[]? OnComplete = null,
    [property: JsonPropertyName("failure")] MapEventDirective[]? OnFailure = null,
    [property: JsonPropertyName("values")] string[]? Values = null)
{
    public string Name { get; set; } = "";
}

/// <summary>Dungeon.dat: a dungeon or instance layer by id (EnterInstanceLayer NPCs and triggers name it).</summary>
public sealed record DungeonInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("exit")] string Exit,
    [property: JsonPropertyName("start_light")] string StartLight = "None",
    [property: JsonPropertyName("start_dark")] string StartDark = "None",
    [property: JsonPropertyName("hold")] int Hold = 0,
    [property: JsonPropertyName("limit")] int Limit = 0,
    [property: JsonPropertyName("group")] string Group = "None",
    [property: JsonPropertyName("event")] string? Event = null,
    [property: JsonPropertyName("ready")] int Ready = 0,
    [property: JsonPropertyName("progress")] int Progress = 0,
    [property: JsonPropertyName("min_level")] int MinLevel = 0,
    [property: JsonPropertyName("max_level")] int MaxLevel = 0,
    [property: JsonPropertyName("item_level")] int ItemLevel = 0,
    [property: JsonPropertyName("max_member")] int MaxMember = 0,
    [property: JsonPropertyName("cube_boss")] int CubeBoss = 0,
    [property: JsonPropertyName("cube")] Dictionary<string, long[][]>? Cube = null,
    // RewardOdEnergyCube.dat TicketName and ConsumeTicketCount: what opening the cube costs (build 26262663).
    [property: JsonPropertyName("cube_ticket")] System.Text.Json.JsonElement[]? CubeTicket = null,
    [property: JsonPropertyName("quest_gate")] System.Text.Json.JsonElement[][]? QuestGate = null,
    [property: JsonPropertyName("reward_light")] long[][]? RewardLight = null,
    [property: JsonPropertyName("reward_dark")] long[][]? RewardDark = null,
    [property: JsonPropertyName("arrival_cutscene")] string? ArrivalCutscene = null,
    // Revives allowed per run (Dungeon.dat bUseRebirthLimit + RebirthLimitCount, OBSERVED in the table, build 26262663); 0: no limit.
    [property: JsonPropertyName("rebirth_limit")] int RebirthLimit = 0);

/// <summary>A base map's EnterInstanceLayer trigger actor: walking into it enters the dungeon.</summary>
public sealed record DungeonEntry(
    [property: JsonPropertyName("dungeon")] int Dungeon,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("center")] double[] Center,
    [property: JsonPropertyName("direction")] double Direction,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("box")] double[] Box,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("height")] double Height)
{
    public bool Contains(Vector3d at) => new TriggerArea(Trigger, Center, Direction, Shape == "Cylinder" ? "Sphere" : Shape, Box, Radius, Height).Contains(at);
}

public sealed record NpcEntry([property: JsonPropertyName("dungeon")] int Dungeon = 0, [property: JsonPropertyName("talk")] string Talk = "",
    [property: JsonPropertyName("teleport")] int Teleport = 0);

/// <summary>An instance layer's volume over its base map (the layer shows the same ground; entering and leaving keep the position).</summary>
public sealed record LayerVolume(
    [property: JsonPropertyName("base")] int Base,
    [property: JsonPropertyName("points")] double[][] Points,
    [property: JsonPropertyName("zmin")] double ZMin,
    [property: JsonPropertyName("zmax")] double ZMax)
{
    /// <summary>Point in the polygon (even-odd), ignoring height.</summary>
    public bool Contains(Vector3d at)
    {
        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var (xi, yi, xj, yj) = (Points[i][0], Points[i][1], Points[j][0], Points[j][1]);
            if (yi > at.Y != yj > at.Y && at.X < (xj - xi) * (at.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>A Teleport.dat row: its map and the MapData teleport actor's location (null for PlayerStart: the map's arrival point).</summary>
public sealed record TeleportTarget(
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("at")] double[]? At);

/// <summary>The embedded map-events.json: events by name and map, dungeons, their entries, teleport points and layer volumes.</summary>
public sealed class MapEventTables
{
    sealed record File(
        [property: JsonPropertyName("served_maps")] int[] ServedMaps,
        [property: JsonPropertyName("events")] Dictionary<string, MapEventDefinition> Events,
        [property: JsonPropertyName("maps")] Dictionary<string, string[]> Maps,
        [property: JsonPropertyName("dungeons")] Dictionary<string, DungeonInfo> Dungeons,
        [property: JsonPropertyName("npc_entries")] Dictionary<string, NpcEntry> NpcEntries,
        [property: JsonPropertyName("entries")] Dictionary<string, DungeonEntry[]> Entries,
        [property: JsonPropertyName("points")] Dictionary<string, Dictionary<string, double[]>> Points,
        [property: JsonPropertyName("volumes")] Dictionary<string, LayerVolume> Volumes,
        [property: JsonPropertyName("deferred")] Dictionary<string, string[]> Deferred,
        [property: JsonPropertyName("teleports")] Dictionary<string, TeleportTarget>? Teleports = null,
        [property: JsonPropertyName("teleport_effects")] Dictionary<string, int>? TeleportEffects = null,
        [property: JsonPropertyName("waves")] Dictionary<string, string[][]>? Waves = null);

    readonly File file;
    readonly Dictionary<int, int> dungeonByMap = [];
    readonly HashSet<int> served;

    MapEventTables(File file)
    {
        this.file = file;
        foreach (var name in file.Events.Keys) file.Events[name].Name = name;
        foreach (var (id, room) in file.Dungeons) dungeonByMap.TryAdd(room.Map, int.Parse(id, CultureInfo.InvariantCulture));
        served = [.. file.ServedMaps];
    }

    static readonly Lazy<MapEventTables> embedded = new(() =>
    {
        using var stream = typeof(MapEventTables).Assembly.GetManifestResourceStream("map-events.json") ?? throw new InvalidOperationException("map-events.json is not embedded");
        return new MapEventTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("map-events.json"));
    });

    public static MapEventTables Embedded => embedded.Value;
    static string K(int id) => id.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<string, MapEventDefinition> Events => file.Events;
    public MapEventDefinition? Event(string name) => file.Events.GetValueOrDefault(name);
    /// <summary>The events of a map the server runs (served instance layers and dungeons).</summary>
    public IReadOnlyList<MapEventDefinition> EventsOn(int mapId) =>
        served.Contains(mapId) ? (file.Maps.GetValueOrDefault(K(mapId)) ?? []).Select(n => file.Events[n]).ToList() : [];
    public bool Serves(int mapId) => served.Contains(mapId);

    /// <summary>ERelationshipEntity (usmap): the names ChangeRelationshipEntity directives and abnormal effects use, to the wire byte.</summary>
    static readonly Dictionary<string, int> relationships = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NoRelation"] = 0, ["PC_Light_Common"] = 1, ["PC_Darkness_Common"] = 2, ["NPC_Light"] = 3, ["NPC_Dark"] = 4, ["NPC_Neutral"] = 5,
        ["Monster"] = 6, ["EnvObj_Neutral"] = 7, ["EnvObj_Light"] = 8, ["EnvObj_Dark"] = 9, ["Rift"] = 10, ["Monster_AntiCompanion"] = 11,
        ["Monster_PCFriendly"] = 12, ["NPC_Companion"] = 13, ["NPC_NoInteraction"] = 14, ["EnvObj_Destroy"] = 15, ["Monster_TeamA"] = 16,
        ["Monster_TeamB"] = 17, ["Monster_TeamC"] = 18, ["Monster_TeamD"] = 19, ["Monster_TeamE"] = 20, ["Monster_Invisible"] = 21,
        ["PC_Summon"] = 22, ["PC_FriendlyToAll"] = 23, ["PC_RoleA_Common"] = 24, ["PC_RoleB_Common"] = 25, ["EnvObj_RoleA"] = 26,
        ["EnvObj_RoleB"] = 27, ["EnvObj_MonsterEnemy"] = 28, ["Pc_FreeForAll_Common"] = 29, ["PC_EnemyToAll"] = 30, ["PC_Light_Friendly"] = 31,
        ["PC_Darkness_Friendly"] = 32, ["NPC_Light_Guard"] = 33, ["NPC_Dark_Guard"] = 34,
    };
    public static int? Relationship(string name) => relationships.TryGetValue(name.Trim(), out var value) ? value : null;
    public const int FriendlyToAll = 23;
    public DungeonInfo? Dungeon(int id) => file.Dungeons.GetValueOrDefault(K(id));
    /// <summary>The dungeon whose map this is (served or not), or null for a field map.</summary>
    public DungeonInfo? DungeonOfMap(int mapId) => dungeonByMap.TryGetValue(mapId, out var id) ? Dungeon(id) : null;
    public int DungeonIdOfMap(int mapId) => dungeonByMap.GetValueOrDefault(mapId);
    public IReadOnlyList<DungeonEntry> EntriesOn(int baseMap) => file.Entries.GetValueOrDefault(K(baseMap)) ?? [];
    public NpcEntry? EntryNpc(int npcId) => file.NpcEntries.GetValueOrDefault(K(npcId));
    /// <summary>A base map's EnterInstanceLayer trigger that enters the dungeon (its centre), or null.</summary>
    public (int BaseMap, Vector3d At)? EntryTriggerOf(int dungeonId)
    {
        foreach (var (map, entries) in file.Entries)
            foreach (var e in entries)
                if (e.Dungeon == dungeonId && e.Center is [var x, var y, var z])
                    return (int.Parse(map, CultureInfo.InvariantCulture), new Vector3d(x, y, z));
        return null;
    }
    /// <summary>The field NPCs whose dialog enters the dungeon (npc_entries).</summary>
    public IEnumerable<int> EntryNpcsOf(int dungeonId) =>
        file.NpcEntries.Where(p => p.Value.Dungeon == dungeonId).Select(p => int.Parse(p.Key, CultureInfo.InvariantCulture)).Order();
    public IReadOnlyDictionary<string, double[]> Points(int mapId) => file.Points.GetValueOrDefault(K(mapId)) ?? new Dictionary<string, double[]>();
    public LayerVolume? Volume(int mapId) => file.Volumes.GetValueOrDefault(K(mapId));
    /// <summary>A Teleport.dat name a PCTeleport uses that is not a point of its own map: the target map and location.</summary>
    public TeleportTarget? Teleport(string name) => file.Teleports?.GetValueOrDefault(name);
    /// <summary>A PCTeleport effect name's TeleportEffect.dat id (the 0x3621 effect byte), 0 when unknown or empty.</summary>
    public int TeleportEffect(string name) => file.TeleportEffects?.GetValueOrDefault(name) ?? 0;
    /// <summary>WaveStepData.dat: a wave's spawners by order (StartWave spawns order 1; each next order when the one before is dead).</summary>
    public string[][]? Wave(string name) => file.Waves?.GetValueOrDefault(name);
    /// <summary>Spawn entries a StartSpawner directive of the map's events starts: not spawned at map load.</summary>
    public IReadOnlySet<string> Deferred(int mapId) => (file.Deferred.GetValueOrDefault(K(mapId)) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a running event asks of the world (the session implements it for its private instance copy).</summary>
public interface IMapEventHost
{
    /// <summary>The dungeon phase the DungeonState condition compares with (Prepare before a field event starts, else Process).</summary>
    string DungeonStateName => "Process";
    void Spawn(string spawner, int delayMs);
    /// <summary>EndSpawner (despawn, flag 7) or KillSpawner (dies) after a delay; DespawnAll with "all".</summary>
    void Despawn(string spawner, int delayMs, bool kill);
    void SummonAround(string target, string spawner, int count, double radius);
    void MoveNpc(string spawner, string route);
    void FollowPc(string spawner, bool follow);
    void RotateNpc(string spawner, string target);
    void Talk(string talk);
    void ChatBubble(string spawner, string talk);
    void SocialAction(string spawner, int action, bool stop);
    void SetAggroPc(string spawner);
    void SetAggroNpc(string spawner, string target);
    void SetRelationship(string spawner, string relationship);
    /// <summary>PCTeleport: the point, the TeleportEffect.dat name (value 1), the cutscene before the move (value 3) and the one after it (value 4).</summary>
    void TeleportPc(string point, string effect, string before, string after);
    void MapExit(string point, string mode);
    void DungeonClear(MapEventRun run);
    void DungeonFail(MapEventRun run);
    void MapEnvState(int state);
    void MapEnvSetting(int group, int state);
    void Cutscene(string name);
    void ShowGuide(string guide);
    /// <summary>StartFocusCamera: the camera actor and the talk shown with it (EventFuncAction 45).</summary>
    void FocusCamera(string camera, string talk);
    void ShowNote(string note);
    void Message(string key, string argument);
    /// <summary>AddAbnormal / RemoveAbnormal on the player (empty target) or a spawner's NPCs; duration in ms, -1 for the run.</summary>
    void Abnormal(int id, int durationMs, string target, bool remove);
    void EnvObjState(string spawner, string state);
    void DynamicTerrain(string spawner, bool block);
    /// <summary>EnvObjChangePhase {spawner, phase}: the objects' phase (A2Core PR #4; the session re-sends their 0x3634).</summary>
    void EnvObjChangePhase(string spawner, int phase) { }
    /// <summary>SetEnvObjCanInteract {spawner, True/False}: whether the objects can be used (A2Core PR #4).</summary>
    void SetEnvObjCanInteract(string spawner, bool canInteract) { }
    void Progress(MapEventRun run);
    void Unsupported(MapEventRun run, string what);

    bool QuestStepActive(int questId, int step);
    bool NpcInCombat(string spawner);
    /// <summary>Any NPC of the spawner alive now.</summary>
    bool SpawnerAlive(string spawner);
    /// <summary>The spawner was ever spawned in this run (NotExistSpawner, KillSpawner conditions).</summary>
    bool SpawnerExists(string spawner);
    bool PlayerDead { get; }
    double? DistanceTo(string spawner);

    /// <summary>How many of an item (Item.dat internal name) the player holds in the bag; -1 when the host cannot tell (then the
    /// CollectItem objective waits for the fallback).</summary>
    long HeldItems(string itemName) => -1;
    /// <summary>Whether the player has gained the abnormal since <paramref name="sinceMs"/> or carries it now; null when the host cannot tell.</summary>
    bool? GainedAbnormal(int abnormalId, long sinceMs) => null;
    /// <summary>
    /// RemoveFromAggroList: an NPC of the spawner that fought the players has dropped them from its aggro list (it reset, its players
    /// left or died) while it lives; reported once per drop.
    /// </summary>
    bool RemovedFromAggro(string spawner) => false;
    /// <summary>ChangeBT: the spawner's NPCs switch to a behaviour tree (Def_Companion_NPC_01, Def_Companion_NPC_NoCombat, Def_Citizen_01 ...).</summary>
    void ChangeBehaviour(string spawner, string behaviour) { }
    /// <summary>NpcChangePhase: the spawner's NPCs enter a phase (1, 2: the Caras bodies, the Dratona eggs, Beritra).</summary>
    void NpcPhase(string spawner, int phase) { }
}

/// <summary>One player's run of a map event: stage, step, objective counters, directive sequences and repeaters.</summary>
public sealed class MapEventRun
{
    public const int Ready = 1, Incomplete = 2, Complete = 3, Expired = 4;
    /// <summary>Client-side and untracked objectives finish by themselves after this long, so the story cannot stall.</summary>
    public const int FallbackMs = 30_000;
    public const int VariableCount = 8;

    sealed class Sequence(IReadOnlyList<MapEventDirective> list, int step)
    {
        public IReadOnlyList<MapEventDirective> List { get; } = list;
        public int Step { get; } = step;
        public int Index { get; set; }
        public long WaitUntil { get; set; }
        public bool Done => Index >= List.Count;
    }

    sealed class Repeater(MapEventDirective directive, long nextMs)
    {
        public MapEventDirective Directive { get; } = directive;
        public long NextMs { get; set; } = nextMs;
        public int Fired { get; set; }
    }

    /// <summary>The session running this run (a party copy's host; hands over when that player leaves the copy).</summary>
    public IMapEventHost Host { get; internal set; }
    readonly List<Sequence> sequences = [];
    readonly List<Repeater> repeaters = [];
    readonly HashSet<string> doneKeys = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> clearedStages = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> firedRules = [];
    readonly long[] fallbackAt = new long[16];
    int stepSerial;
    bool stepObjectivesDone;
    bool stageFailing;
    /// <summary>
    /// Kills of this run that no objective counted when they happened (a KillSpawner / KillNpc target killed before its step, by a
    /// companion, a SetAggroNpc NPC or a KillSpawner directive): a later step's named kill objectives count them when it begins.
    /// </summary>
    readonly List<(int NpcId, string SpawnName)> uncountedKills = [];
    bool replaying;
    /// <summary>Held objectives (CollectItem, GetAbnormal) whose host can tell: no 30 s fallback, only this long safety net (OURS).</summary>
    public const int HeldFallbackMs = 300_000;

    public MapEventRun(MapEventDefinition definition, int id, IMapEventHost host, long now)
    {
        Definition = definition;
        Id = id;
        Host = host;
        StartedMs = now;
        Variables = new int[Math.Max(VariableCount, definition.Values?.Length ?? 0)];
        Goals = [];
    }

    public MapEventDefinition Definition { get; }
    public string Name => Definition.Name;
    public int Id { get; }
    public int Status { get; private set; } = Incomplete;
    public int StageIndex { get; private set; }
    public int StepIndex { get; private set; }
    public long StartedMs { get; }
    public int[] Goals { get; private set; }
    /// <summary>
    /// The objective counts MapEventJoin_NT carries: the current step's, one per objective, also before the run has begun its first
    /// step (zeros). OBSERVED: all 18 joins in the recordings at an event's start list the first step's objectives as zeros (HQ001 4,
    /// HQ003 2, InstanceLayer_2 2, Dungeon_0005 6, the rest 1); an empty list (live 2026-11-11, GarrisonTerritory_H1 with 9 objectives)
    /// left the client without the step's objectives until the first progress update (CANDIDATE: no objective markers meanwhile).
    /// </summary>
    public int[] JoinGoals => Goals.Length > 0 || Finished || Step is not { } step ? Goals : new int[step.Objectives.Length];
    public int[] Variables { get; }
    public bool Finished => Status is Complete or Expired;
    public MapEventStage? Stage => StageIndex < Definition.Stages.Length ? Definition.Stages[StageIndex] : null;
    public MapEventStep? Step => Stage is { } stage && StepIndex < stage.Steps.Length ? stage.Steps[StepIndex] : null;
    public int StageOrder => Stage?.Id ?? 0;
    /// <summary>
    /// The step as the client counts it: its place within the stage (1..), not the script's step id. OBSERVED (capture
    /// 20261107-014953, the Brigade event): stage 3's steps (ids 3, 4) went out as 1 and 2, stage 4's (id 5) and stage 5's (id 6) as 1.
    /// With the ids the client tracked the wrong step from stage 2 on: no talk with the injured commander, no escape triggers.
    /// </summary>
    public int StepOrder => Step is null ? 0 : StepIndex + 1;
    /// <summary>The step as 0x922e / 0x922d carry it (the GitHub name of <see cref="StepOrder"/>).</summary>
    public int WireStep => StepOrder;
    /// <summary>The script's own id of the current step.</summary>
    public int StepId => Step?.Id ?? 0;
    /// <summary>Objectives of the current step still open (for markers and area checks); none once the run is finished (its Goals
    /// are emptied while the last step stays current: indexing them threw, e.g. HpFloor on a hit after the clear).</summary>
    public IEnumerable<(int Index, MapEventObjective Objective)> Open =>
        !Finished && Step is { } step ? step.Objectives.Select((o, i) => (i, o)).Where(x => x.i < Goals.Length && Goals[x.i] < x.o.Count) : [];

    public void Start(long now)
    {
        if (Definition.Stages.Length == 0 || Definition.Stages[0].Steps.Length == 0) { Status = Complete; return; }
        BeginStep(now);
    }

    void BeginStep(long now)
    {
        var step = Step!;
        Goals = new int[step.Objectives.Length];
        stepStartedMs = now;
        repeaters.Clear();
        stepObjectivesDone = false;
        stepSerial++;
        Array.Fill(fallbackAt, 0);
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if ((o.ClientSide || o.Unsupported || !Tracked(o, step)) && i < fallbackAt.Length) fallbackAt[i] = now + FallbackMs;
            // The bag's items and the gained abnormals count by state (audit 2026-11-13 G10/G15; before: always the 30 s fallback).
            else if (o.Held && i < fallbackAt.Length) fallbackAt[i] = now + (HeldKnown(o) ? HeldFallbackMs : FallbackMs);
            // A ClearWave whose wave no StartWave of the event starts (Dungeon_Daily_0001/0002: the wave's first order is the map's own
            // OrcusGem) runs from the step that waits for it (CANDIDATE: no script starts it, the client data says nothing else).
            if (o.Type == "ClearWave" && Tracked(o, step) && !waves.ContainsKey(o.Value(0)) && !clearedWaves.Contains(o.Value(0)) && !StartsWave(o.Value(0)))
                StartWave(o.Value(0));
            // A CloseGuide objective shows its guide (the forced popup) when the step begins: OBSERVED 2026-10-19, ShowGuide (46) for
            // Tutorial_ForcedPopup_Fly_01 at the Subspace's first step and Tutorial_ForcedPopup_L1_ExitDungeon_01 with the Dream
            // Crossroad's join; the client reports CloseGuide (0x9230 kind 2) when the player closes it. Without it the player waited
            // for the 30 s fallback (live 2026-10-20).
            if (o.Type == "CloseGuide" && o.Value(0) is { Length: > 0 } popup) Host.ShowGuide(popup);
            if (o.Pre is { Length: > 0 } pre) sequences.Add(new Sequence(pre, stepSerial));
        }
        sequences.Add(new Sequence(step.Pre, stepSerial));
        Tick(now);
        ReplayKills(now);
        CheckStep(now);
    }

    /// <summary>
    /// The named kills this run made before the current step (audit 2026-11-13 G2): KillNpc counts each uncounted kill of its NPCs and
    /// KillSpawner the uncounted kill of its spawner, when that spawn entry has nobody alive now. Before, a target killed early (Dungeon_L_0016's Boss_1 during
    /// the step-1.2 cutscene, 12 bot reports) left its step waiting for ever. CANDIDATE: the official server may make such targets
    /// immune until their step instead (no recording of an early kill); KillPending already does that for KillNpc targets of later steps.
    /// </summary>
    void ReplayKills(long now)
    {
        if (replaying || uncountedKills.Count == 0) return;
        replaying = true;
        try
        {
            for (var guard = 0; guard < 8 && !Finished && uncountedKills.Count > 0; guard++)
            {
                var serial = stepSerial;
                foreach (var kill in uncountedKills.ToList())
                {
                    if (Finished || stepSerial != serial) break;
                    if (Apply(o => NamedKill(o, kill.NpcId, kill.SpawnName, !Host.SpawnerAlive(kill.SpawnName)), now)) uncountedKills.Remove(kill);
                }
                if (stepSerial == serial) break;   // the step stayed: every kill was tried against it
            }
        }
        finally { replaying = false; }
    }

    /// <summary>
    /// The kill objectives that name their target (NPC ids, spawn names): the ones an earlier kill may still count for, only while its
    /// spawn entry has nobody alive (a target that came back, a respawned field monster, is fought again as before).
    /// </summary>
    static bool NamedKill(MapEventObjective o, int npcId, string spawnName, bool spawnerDead) => spawnerDead && o.Type switch
    {
        "KillNpc" => (o.Npcs ?? []).Contains(npcId) || Names(o, spawnName),
        "KillSpawner" => Names(o, spawnName),
        _ => false,
    };

    /// <summary>The kills kept for later steps (checks).</summary>
    public int UncountedKills => uncountedKills.Count;

    // ---------- Held objectives (CollectItem, GetAbnormal) ----------

    /// <summary>When the current step began: abnormals gained from then on count for its GetAbnormal objectives.</summary>
    long stepStartedMs;

    bool HeldKnown(MapEventObjective o) => o.Type switch
    {
        "CollectItem" => Host.HeldItems(o.Value(0)) >= 0,
        "GetAbnormal" => int.TryParse(o.Value(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Host.GainedAbnormal(id, stepStartedMs) is not null,
        _ => false,
    };

    /// <summary>
    /// CollectItem follows the count of the named item in the bag (as the quests' collect steps, GameSession.QuestItems.cs; a count once
    /// reached stays: a Seal key is used on its door right after); GetAbnormal is done once the player gained the abnormal. The Seal
    /// events' "Quest_UseItem_NoEffect_Key_A_c_01a_b" (Worn Key 534960001, from the Seal_Dungeon_Usable_Env reward objects) waited for
    /// the 30 s fallback in 12 Seal events (EQ1810011/021/025/057/059, EQ2803014, EQ2810010/011/022/048/052/057).
    /// </summary>
    void HeldTick(long now)
    {
        if (Finished || Step is not { } step) return;
        for (var i = 0; i < step.Objectives.Length && i < Goals.Length && ReferenceEquals(Step, step) && !Finished; i++)
        {
            var o = step.Objectives[i];
            if (!o.Held || Goals[i] >= o.Count) continue;
            var reached = o.Type switch
            {
                "CollectItem" => Host.HeldItems(o.Value(0)) is var held and >= 0 ? (int)Math.Min(o.Count, held) : 0,
                "GetAbnormal" => int.TryParse(o.Value(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Host.GainedAbnormal(id, stepStartedMs) == true ? o.Count : 0,
                _ => 0,
            };
            if (reached > Goals[i]) Apply(x => ReferenceEquals(x, o), now, reached - Goals[i]);
        }
    }

    /// <summary>Objectives of the types the server tracks only with their data: a score with its UnifiedObjectivesList, a wave of
    /// WaveStepData, an object state that play reaches. The others finish by the fallback.</summary>
    static bool Tracked(MapEventObjective o, MapEventStep step) => o.Type switch
    {
        "AchieveUnifiedObjectivesScore" => step.Unified is { Length: > 0 },
        "ClearWave" => MapEventTables.Embedded.Wave(o.Value(0)) is { Length: > 0 },
        "UpdateEnvObjState" => MapEventObjective.TrackedObjectState(o.Value(1)),
        _ => true,
    };

    // ---------- Waves ----------
    // WaveStepData.dat: a wave is spawner groups in order. StartWave spawns order 1; once no NPC of the current order lives the next
    // order spawns (CANDIDATE: the client data has the orders, not the rule between them; an order of objects only goes on at once);
    // after the last order the wave is cleared (ClearWave). EndWave stops it (its NPCs stay; the scripts despawn them themselves).

    readonly Dictionary<string, int> waves = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> clearedWaves = new(StringComparer.OrdinalIgnoreCase);

    bool StartsWave(string wave) =>
        Definition.Stages.SelectMany(st => st.Steps).SelectMany(st => st.Pre.Concat(st.Post).Concat(st.Objectives.SelectMany(o => (o.Pre ?? []).Concat(o.Post ?? []))))
            .Concat(Definition.Rules?.SelectMany(r => r.Directives) ?? [])
            .Any(d => d.Type == "StartWave" && string.Equals(d.Value(0), wave, StringComparison.OrdinalIgnoreCase));

    void StartWave(string wave)
    {
        if (MapEventTables.Embedded.Wave(wave) is not { Length: > 0 } orders) { Host.Unsupported(this, $"directive StartWave {wave}: not in WaveStepData"); return; }
        clearedWaves.Remove(wave);
        waves[wave] = 0;
        foreach (var spawner in orders[0]) Host.Spawn(spawner, 0);
    }

    /// <summary>The run's waves stop where they are (a daily dungeon's time is up): no next order spawns.</summary>
    public void StopWaves() => waves.Clear();

    /// <summary>A spawner of a wave this run started (running or cleared), any order.</summary>
    public bool WaveNpc(string spawner) =>
        waves.Keys.Concat(clearedWaves).Any(w => MapEventTables.Embedded.Wave(w)?.Any(order => order.Contains(spawner, StringComparer.OrdinalIgnoreCase)) == true);

    /// <summary>The spawners of every running wave's current order (what the player fights now).</summary>
    public IEnumerable<string> WaveSpawners() =>
        waves.SelectMany(w => MapEventTables.Embedded.Wave(w.Key) is { } orders && w.Value < orders.Length ? orders[w.Value] : []);

    /// <summary>The spawners of one wave's current order, if this run is running it.</summary>
    public IEnumerable<string> WaveSpawners(string wave) =>
        waves.TryGetValue(wave, out var at) && MapEventTables.Embedded.Wave(wave) is { } orders && at < orders.Length ? orders[at] : [];

    void WaveTick(long now)
    {
        foreach (var (wave, order) in waves.ToList())
        {
            if (MapEventTables.Embedded.Wave(wave) is not { } orders) { waves.Remove(wave); continue; }
            var at = order;
            while (at < orders.Length && !orders[at].Any(Host.SpawnerAlive))
            {
                if (++at < orders.Length) foreach (var spawner in orders[at]) Host.Spawn(spawner, 0);
            }
            if (at < orders.Length) { waves[wave] = at; continue; }
            waves.Remove(wave);
            clearedWaves.Add(wave);
            Apply(o => o.Type == "ClearWave" && string.Equals(o.Value(0), wave, StringComparison.OrdinalIgnoreCase), now);
        }
    }

    // ---------- Events from the world ----------

    /// <summary>Counts an event against the current step's open objectives; true when something changed.</summary>
    bool Apply(Func<MapEventObjective, bool> matches, long now, int amount = 1, bool whole = false)
    {
        if (Finished || Step is not { } step) return false;
        var changed = false;
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (Goals[i] >= o.Count || !matches(o)) continue;
            Goals[i] = whole ? o.Count : Math.Min(o.Count, Goals[i] + amount);
            changed = true;
            if (Goals[i] >= o.Count) ObjectiveDone(o, now);
        }
        if (changed)
        {
            Host.Progress(this);
            Tick(now);   // the finished objectives' directives start at once (Elvan walked right after the third kill)
            CheckStep(now);
        }
        return changed;
    }

    void ObjectiveDone(MapEventObjective objective, long now)
    {
        if (objective.Key is { Length: > 0 } key) doneKeys.Add(key);
        if (objective.Post is { Length: > 0 } post) sequences.Add(new Sequence(post, stepSerial));
    }

    /// <summary>Whether an NPC is the one an objective names (its NpcData ids, or its spawner name): bots look for their targets with it.</summary>
    public static bool Targets(MapEventObjective o, int npcId, string spawnName) => (o.Npcs ?? []).Contains(npcId) || Names(o, spawnName);

    static bool NameMatches(string name, string spawnName) =>
        string.Equals(name, spawnName, StringComparison.OrdinalIgnoreCase) || spawnName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase);

    // The fuzzy matches (kind word, synonyms) are for map spawn names only: an EnvObjData template name is the object itself, so a
    // different template never counts. Live 2026-11-11: in the Ugaros Thieves Campsite (World_L1_GarrisonTerritory_H1, "UseEnvObj
    // E_L1_Garrison_H1_Box_01 x5" = Supply Chests) the Stronghold Chests E_L1_Garrison_BalaurBox_01 counted as supplies through the
    // shared word "Garrison" ("Box" is too short to be a kind).
    static bool Names(MapEventObjective o, string spawnName) => o.Names.Any(n => NameMatches(n, spawnName))
        || o.Type == "UseEnvObj" && EnvUsages.IdOf(spawnName) == 0 && o.Names.Any(n => n.StartsWith("E_", StringComparison.OrdinalIgnoreCase) && BaseWord(n) is { Length: > 2 } word
            && string.Equals(word, BaseWord(spawnName), StringComparison.OrdinalIgnoreCase))
        || o.Type == "UseEnvObj" && EnvUsages.IdOf(spawnName) == 0 && o.Names.Any(n => KindMatches(n, spawnName));

    /// <summary>Words that are not an object's kind (map/dungeon/layer parts of EnvObj and spawn names).</summary>
    static readonly HashSet<string> NotKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Layer", "Dungeon", "Env", "EnvObj", "Spawn", "SpawnMulti", "Single", "Multi", "Hit", "Starter", "InstanceLayer", "Common", "All" };

    /// <summary>Same object, other words (EnvObj template vs map spawn): the Coastal Cave's E_CoastalCave_Corpse_001 is S1_Env_Deadbody_CoastalCave.</summary>
    static readonly Dictionary<string, string[]> KindSynonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Corpse"] = ["Deadbody", "Body"], ["Deadbody"] = ["Corpse"],
            // Leda's (the illusion god's) barrier: Valley of Hope step 19 wants E_CoastalCave_Barrier_002, the map has
            // S6_Env_LedaPortal_CoastalCave in front of the boss room (live 2026-11-08: used 10 times, never counted). Not "Portal"
            // in general: the cave's exit portal must not count as the barrier.
            ["Barrier"] = ["LedaPortal"],
        };

    /// <summary>
    /// A UseEnvObj template name (E_&lt;map&gt;_&lt;kind...&gt;_NNN) and a spawn share the object's kind word (prefix, either way: StoneWall /
    /// StoneWall1), or a synonym. Live 2026-11-07: Valley of Hope's corpse (E_CoastalCave_Corpse_001 vs S1_Env_Deadbody_CoastalCave)
    /// never counted, the player was stuck pressing F on it (27 interactions answered, the map event did not move).
    /// </summary>
    internal static bool KindMatches(string template, string spawnName)
    {
        static IEnumerable<string> Words(string s) => s.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.All(char.IsDigit));
        var kinds = Words(template).Skip(2).Where(w => w.Length > 3 && !NotKinds.Contains(w)).TakeLast(1).ToList();
        if (kinds.Count == 0) return false;
        var all = kinds.Concat(kinds.SelectMany(k => KindSynonyms.GetValueOrDefault(k) ?? [])).ToList();
        var spawn = Words(spawnName).Where(w => w.Length > 3 && !NotKinds.Contains(w)).ToList();
        // A longer spawn word may carry the kind inside it (LostOdium for E_CoastalCave_Odium_001: live 2026-11-08 the Odylium in
        // Valley of Hope played its gathering animation and nothing counted), for kinds of 5+ letters only.
        return all.Any(k => spawn.Any(w => w.StartsWith(k, StringComparison.OrdinalIgnoreCase) || k.StartsWith(w, StringComparison.OrdinalIgnoreCase) && w.Length >= 5
            || k.Length >= 5 && w.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The object kind in a name: the last word before the trailing number ("E_L0_Layer_2_Tower_01" and the spawn
    /// "L_Starter_InstanceLayer_2_EnvObj_Tower_1" are both Tower). A UseEnvObj objective names the EnvObj template (E_...), the
    /// map knows the spawn name: without this the Brigade towers never counted (live 2026-11-06, HQ1101070 stuck).
    /// </summary>
    static string BaseWord(string name) =>
        name.Split('_', StringSplitOptions.RemoveEmptyEntries).Reverse().FirstOrDefault(p => !p.All(char.IsDigit)) ?? "";

    /// <summary>An NPC of this instance died (its NpcData id, spawn entry name); <paramref name="spawnerAlive"/>: others of its spawner still live.</summary>
    public bool Killed(int npcId, string spawnName, bool hostile, bool spawnerAlive, long now, string subType = "")
    {
        bool Counts(MapEventObjective o) => o.Type switch
        {
            "KillNpc" => (o.Npcs ?? []).Contains(npcId) || Names(o, spawnName),
            "KillSpawner" => Names(o, spawnName) && !spawnerAlive,
            // KillNpcSubtype names the NpcData NpcSubType (DungeonWave_0001: NormalMonster kills fill the gauge, EliteMonster ones the
            // optional 100); a kill whose subtype is unknown counts as before.
            "KillNpcSubtype" => hostile && (subType.Length == 0 || o.Value(0).Length == 0 || string.Equals(o.Value(0), subType, StringComparison.OrdinalIgnoreCase)),
            "KillNpcRelationshipEntity" or "KillNpcCreatureType" => hostile,
            _ => false,
        };
        // "Collect points" (UnifiedObjectives steps): each matching kill of the UnifiedObjectivesList gives its value 1 in points
        // (Arkanis' Grade1 elites 2, NormalMonster 1); AchieveUnifiedObjectivesScore counts them up to the step's score.
        var points = !Finished && Step is { Unified: { Length: > 0 } unified } ? unified.Where(Counts).Sum(o => o.Count) : 0;
        // A kill that counts now also takes the kept kills its objective names with it (the first NPCs of a KillSpawner spawner, which
        // never counted alone), before the next step can begin and count them again (G2).
        var running = !Finished && Step is not null;
        if (running)
        {
            var counting = Step!.Objectives.Where((o, i) => i < Goals.Length && Goals[i] < o.Count && Counts(o)).ToList();
            if (counting.Count > 0) uncountedKills.RemoveAll(k => counting.Any(o => NamedKill(o, k.NpcId, k.SpawnName, true)));
        }
        var changed = Apply(Counts, now);
        if (points > 0) changed |= Apply(o => o.Type == "AchieveUnifiedObjectivesScore", now, points);
        // Kept for a later step unless it counted (G2).
        if (running && !changed && uncountedKills.Count < 512) uncountedKills.Add((npcId, spawnName));
        return changed;
    }

    /// <summary>
    /// The player talked to an NPC (the step talk, 0x8d2b kind 2; <paramref name="index"/> names the objective when the client said
    /// which) or closed its dialog (<paramref name="close"/>: CloseNpc objectives only).
    /// </summary>
    /// <summary>
    /// An NPC that a KillNpc objective in a step NOT yet reached will target: immune until then (OURS, user 2026-11-07). The
    /// Asmodian HQ001 Balaur Watchstone (DraTotemA, 2590052) spawns in stage 1 alongside Elvida but must not be destroyable until
    /// her cue to destroy it (stage 2 step 9); as a plain monster the player could break it during the earlier brigade fight.
    /// </summary>
    public bool KillPending(int npcId, string spawnName)
    {
        bool future = false, nowOrDone = false;
        for (var si = 0; si < Definition.Stages.Length; si++)
        {
            var steps = Definition.Stages[si].Steps;
            for (var ti = 0; ti < steps.Length; ti++)
                if (steps[ti].Objectives.Any(o => o.Type == "KillNpc" && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))))
                {
                    if (si > StageIndex || si == StageIndex && ti > StepIndex) future = true;
                    else nowOrDone = true;   // its kill is the current step (killable now) or already past
                }
        }
        return future && !nowOrDone;
    }

    /// <remarks>
    /// CloseNpc is "come close to the NPC" (NpcData CloseEventRange): the client reports it with the same step talk, no dialog
    /// open. OBSERVED: official Layer_1_2 step 6 (CloseNpc Hitanie) counted with the 0x8d2c kind 2 reply and no 0xe241 (capture
    /// 20261107-010735 #237.895); Dungeon_0005's five approach objectives likewise (20261107-012837 #138-144). We counted CloseNpc
    /// only when a dialog closed: 12 step talks for objectives 3-7 changed nothing in our traces 2026-11-07..10 (they waited for
    /// the 30 s fallback). A dialog closing next to the NPC still counts it.
    /// </remarks>
    public bool Talked(int npcId, string spawnName, int? index, bool close, long now) =>
        Apply(o => (close ? o.Type == "CloseNpc" : o.Type is "AskNpc" or "CloseNpc") && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))
                   && (index is not { } i || Step!.Objectives.Length <= i || ReferenceEquals(Step.Objectives[i], o)), now);

    /// <summary>
    /// An object a UseEnvObj objective names: exactly its spawn entry or exactly its EnvObjData name (CANDIDATE, from the client's
    /// tables: of the 2232 UseEnvObj names of map-events.json, the spawn-name prefix found placed objects for 1057, this rule for
    /// 2000; the prefix also took other EnvObjData rows' objects, such as AbyssStone_01's V01/V02 variants). Munihelle's second
    /// firewood (E_D0_FireWood_002 spawned as E_D0_FireWood_001_2) answers to its own name, not the first batch's (live 2026-10-28).
    /// Without an EnvObjData name, the spawn-name rule of NPCs.
    /// </summary>
    internal static bool NamesObject(MapEventObjective o, string spawnName, string? envName) => envName is null ? Names(o, spawnName)
        : o.Names.Any(n => string.Equals(n, envName, StringComparison.OrdinalIgnoreCase) || string.Equals(n, spawnName, StringComparison.OrdinalIgnoreCase));

    public bool UsedObject(string spawnName, long now, string? envName = null)
    {
        // A used object went through Occupied (while used) to Finish: UpdateEnvObjState objectives on those states count it too.
        var state = Apply(o => o.Type == "UpdateEnvObjState" && o.Value(1) is "Finish" or "Occupied" && NamesObject(o, spawnName, envName), now);
        if (!Apply(o => o.Type == "UseEnvObj" && NamesObject(o, spawnName, envName), now)) return state;
        // The used object opens the note the step waits for (CloseNote): Valley of Hope's corpse (UseEnvObj E_CoastalCave_Corpse_001 +
        // CloseNote Note_CoastalCave_001, no ShowNote directive in the script; the object's own note is client data we do not have).
        // Live 2026-11-08: no note opened, the step waited for its timeout (10-20 s) before Luteros moved on.
        if (!Finished && Step is { } step)
            for (var i = 0; i < step.Objectives.Length && i < Goals.Length; i++)
                if (step.Objectives[i] is { Type: "CloseNote" } note && Goals[i] < note.Count && notesShown.Add(note.Value(0)))
                    Host.ShowNote(note.Value(0));
        return true;
    }

    /// <summary>An object the player destroyed (EnvDestroy, bombs): its uses, and UpdateEnvObjState "Dead" (Dungeon_D_0001's stone wall,
    /// E_Dungeon_D_0001_StoneWall_001 Dead, waited for the 30 s fallback before).</summary>
    public bool Destroyed(string spawnName, long now)
    {
        var dead = Apply(o => o.Type == "UpdateEnvObjState" && string.Equals(o.Value(1), "Dead", StringComparison.OrdinalIgnoreCase) && Names(o, spawnName), now);
        return UsedObject(spawnName, now) | dead;
    }

    /// <summary>Notes this run opened for a used object (each once).</summary>
    readonly HashSet<string> notesShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a UseEnvObj objective of the current step still counts this spawner (the object despawns once used: OBSERVED for the phoenix eggs).</summary>
    public bool WantsObject(string spawnName, string? envName = null) =>
        !Finished && Step is { } step && step.Objectives.Where((o, i) => i < Goals.Length && Goals[i] < o.Count
            && (o.Type == "UseEnvObj" || o.Type == "UpdateEnvObjState" && MapEventObjective.TrackedObjectState(o.Value(1))) && NamesObject(o, spawnName, envName)).Any();

    /// <summary>Where the player stands: EnterVolumePC (trigger names) and EnterSubZone objectives.</summary>
    public bool PositionAt(Func<string, bool> insideTrigger, Func<string, bool> insideSubzone, long now) =>
        Apply(o => o.Type == "EnterVolumePC" ? insideTrigger(o.Value(0)) : o.Type == "EnterSubZone" && insideSubzone(o.Value(0)), now);

    public bool CombatStarted(long now) => Apply(o => o.Type == "PCCombatStart", now);

    public bool NpcArrived(string spawnName, string route, long now) =>
        Apply(o => o.Type == "NpcArriveWaypoint" && NameMatches(o.Value(0), spawnName)
                   && (o.Value(1).Length == 0 || string.Equals(o.Value(1), route, StringComparison.OrdinalIgnoreCase)), now);

    /// <summary>An NPC's HP changed: HpRemaining objectives (percent) naming it.</summary>
    public bool HpChanged(int npcId, string spawnName, int percent, long now) =>
        Apply(o => o.Type == "HpRemaining" && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))
                   && int.TryParse(o.Value(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && percent <= limit, now);

    /// <summary>
    /// The lowest HP an NPC may drop to while this run needs it (percent of its maximum), or null: an open HpRemaining objective
    /// on it stops at its threshold (the Asmodian HQ001 "stop Elvida" fight: 70 then 40, she must not die; live 2026-11-07 she
    /// was killed before the step counted), and an NPC a later step still talks to (AskNpc) never dies (1 HP).
    /// </summary>
    public double? HpFloor(int npcId, string spawnName)
    {
        double? floor = null;
        // Only for an NPC the script turns friendly later (ChangeRelationshipEntity to a non-monster: Elvida becomes NPC_Companion
        // at 40 %); a boss's HP phases (also HpRemaining) still let it die from a big hit.
        if (TurnsFriendly(spawnName))
            foreach (var (_, o) in Open)
                if (o.Type == "HpRemaining" && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))
                    && int.TryParse(o.Value(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
                    floor = Math.Max(floor ?? 0, limit);   // the next threshold down (70 before 40)
        if (floor is null && Stage is not null
            && Definition.Stages.Skip(StageIndex).SelectMany(st => st.Steps).SelectMany(st => st.Objectives)
                .Any(o => o.Type == "AskNpc" && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))))
            floor = 0;
        return floor;
    }

    bool TurnsFriendly(string spawnName)
    {
        IEnumerable<MapEventDirective> All(MapEventStep st) =>
            st.Pre.Concat(st.Post).Concat(st.Objectives.SelectMany(o => (o.Pre ?? []).Concat(o.Post ?? [])));
        return Definition.Stages.SelectMany(st => st.Steps).SelectMany(All).Any(d => d.Type == "ChangeRelationshipEntity"
            && string.Equals(d.Value(0), spawnName, StringComparison.OrdinalIgnoreCase)
            && d.Value(1) is { } to && !to.StartsWith("Monster", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The client finished objective <paramref name="index"/> itself (0x9230 kind 2: cutscene, note, talk, guide).</summary>
    public bool ClientObjective(int index, long now) =>
        Step is { } step && index >= 0 && index < step.Objectives.Length && Apply(o => ReferenceEquals(o, step.Objectives[index]), now, whole: true);

    public bool CutsceneDone(string name, long now) => Apply(o => o.Type == "CompleteCutscene" && string.Equals(o.Value(0), name, StringComparison.OrdinalIgnoreCase), now);

    public bool EnvObjStateChanged(string spawnName, string state, long now) =>
        Apply(o => o.Type == "UpdateEnvObjState" && Names(o, spawnName) && string.Equals(o.Value(1), state, StringComparison.OrdinalIgnoreCase), now);

    // ---------- Steps ----------

    /// <summary>The current step's required objectives (all of them when none is required) reached their counts.</summary>
    bool StepGoalsMet()
    {
        if (Step is not { } step) return false;
        var required = step.Objectives.Select((o, i) => (o, i)).Where(x => !x.o.Optional).ToList();
        if (required.Count == 0) required = step.Objectives.Select((o, i) => (o, i)).ToList();
        return required.All(x => x.i < Goals.Length && Goals[x.i] >= x.o.Count);
    }

    void CheckStep(long now)
    {
        if (Finished || stepObjectivesDone || Step is not { } step || !StepGoalsMet()) return;
        stepObjectivesDone = true;
        if (step.Post.Length > 0) sequences.Add(new Sequence(step.Post, stepSerial));
        Tick(now);
    }

    void NextStep(long now)
    {
        if (Finished) return;
        repeaters.Clear();
        var stage = Stage!;
        if (StepIndex + 1 < stage.Steps.Length) StepIndex++;
        else
        {
            if (stage.Key.Length > 0) clearedStages.Add(stage.Key);
            if (StageIndex + 1 < Definition.Stages.Length) { StageIndex++; StepIndex = 0; }
            else
            {
                Status = Complete;
                Goals = [];
                if (Definition.OnComplete is { Length: > 0 } done) sequences.Add(new Sequence(done, ++stepSerial));
                Host.Progress(this);
                Tick(now);
                return;
            }
        }
        BeginStep(now);
        Host.Progress(this);
    }

    /// <summary>A stage failed (its fail conditions held): run its fail directives and go to the named stage, else restart it.</summary>
    void FailStage(MapEventStage stage, long now)
    {
        sequences.Clear();
        repeaters.Clear();
        if (stage.FailDirectives is { Length: > 0 } fail) sequences.Add(new Sequence(fail, ++stepSerial));
        var target = stage.FailGoTo is { Length: > 0 } key ? Array.FindIndex(Definition.Stages, s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) : -1;
        StageIndex = target >= 0 ? target : StageIndex;
        StepIndex = 0;
        BeginStep(now);
        Host.Progress(this);
    }

    // ---------- Time ----------

    /// <summary>Runs due directives: the sequences in order (a Delay holds its sequence), the repeaters, the global rules, then the step change.</summary>
    public void Tick(long now)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var progressed = false;
            foreach (var sequence in sequences.ToList())
            {
                while (!sequence.Done && now >= sequence.WaitUntil)
                {
                    var d = sequence.List[sequence.Index++];
                    if (d.Repeats) { repeaters.Add(new Repeater(d, d.PreDelay ? now + d.Interval : now)); continue; }
                    if (d.Type == "Delay") { sequence.WaitUntil = now + d.Int(0); progressed = true; break; }
                    if (Holds(d.Conditions, now)) Execute(d, now);
                    progressed = true;
                }
                if (sequence.Done) sequences.Remove(sequence);
            }
            foreach (var repeater in repeaters.ToList())
            {
                if (now < repeater.NextMs) continue;
                if (Holds(repeater.Directive.Conditions, now)) Execute(repeater.Directive, now, repeater.Fired);
                repeater.Fired++;
                repeater.NextMs = now + Math.Max(1000, repeater.Directive.Interval);
                if (repeater.Directive.Loop > 0 && repeater.Fired >= repeater.Directive.Loop) repeaters.Remove(repeater);
            }
            if (!progressed) break;
        }
        if (!Finished && waves.Count > 0) WaveTick(now);
        if (Finished) return;
        HeldTick(now);
        if (Finished) return;
        // Client-side and untracked objectives time out as done.
        if (Step is { } step)
            // A timed-out objective can finish the step; the next step's objectives are not this loop's (its Goals are shorter: threw).
            for (var i = 0; i < step.Objectives.Length && i < fallbackAt.Length && ReferenceEquals(Step, step) && !Finished; i++)
                if (fallbackAt[i] != 0 && now >= fallbackAt[i] && i < Goals.Length && Goals[i] < step.Objectives[i].Count)
                {
                    fallbackAt[i] = 0;
                    Host.Unsupported(this, $"objective {step.Objectives[i].Type} {step.Objectives[i].Value(0)} finished by timeout");
                    Apply(o => ReferenceEquals(o, step.Objectives[i]), now, whole: true);
                }
        if (Definition.Rules is { } rules)
            for (var i = 0; i < rules.Length; i++)
                if (!firedRules.Contains(i) && rules[i].Conditions.Length > 0 && Holds(rules[i].Conditions, now))
                {
                    firedRules.Add(i);
                    sequences.Add(new Sequence(rules[i].Directives, stepSerial));
                }
        // A stage fails once per spell of its conditions (a death fails it when it happens, not every tick until the revival).
        // A step whose objectives are done goes on first: FierceHornCave_Normal's wave stage fails on "Lycan totem out of combat"
        // and its last step is the totem's kill, so the kill itself failed the stage and the wave began again (OURS: the order of
        // the two in one tick is not in the client data).
        if (Stage is { Fail.Length: > 0 } stage && !stepObjectivesDone && !StepGoalsMet())
        {
            var failing = Holds(stage.Fail, now);
            if (failing && !stageFailing) { stageFailing = true; FailStage(stage, now); return; }
            if (!failing) stageFailing = false;
        }
        if (stepObjectivesDone && !sequences.Any(s => s.Step == stepSerial)) NextStep(now);
    }

    // ---------- Conditions ----------

    /// <summary>Groups are alternatives, their members all hold (CANDIDATE; the data has single-member groups almost everywhere).</summary>
    public bool Holds(MapEventCondition[][]? groups, long now) => groups is not { Length: > 0 } || groups.Any(g => g.All(c => Condition(c, now)));

    bool Condition(MapEventCondition c, long now)
    {
        var spawner = c.Value(0);
        switch (c.Type)
        {
            case "ObjectiveComplete": return doneKeys.Contains(spawner);
            case "ObjectiveIncomplete": return !doneKeys.Contains(spawner);
            case "IncompleteQuestStep": return QuestStep(spawner) is { } q && Host.QuestStepActive(q.Quest, q.Step);
            case "NotIncompleteQuestStep": return QuestStep(spawner) is not { } nq || !Host.QuestStepActive(nq.Quest, nq.Step);
            case "NpcCombatState": return Host.NpcInCombat(spawner) == !string.Equals(c.Value(1), "None", StringComparison.OrdinalIgnoreCase);
            case "NpcDie": return Host.SpawnerExists(spawner) && !Host.SpawnerAlive(spawner);
            case "KillSpawner": return Host.SpawnerExists(spawner) && !Host.SpawnerAlive(spawner);
            case "NotExistSpawner": return !Host.SpawnerExists(spawner) || !Host.SpawnerAlive(spawner);
            case "PcDie": return Host.PlayerDead;
            case "DungeonState": return string.Equals(spawner, Status == Complete ? "Complete" : Host.DungeonStateName, StringComparison.OrdinalIgnoreCase);
            case "DistanceFar": return Host.DistanceTo(spawner) is { } far && far > Number(c.Value(1));
            case "DistanceClose": return Host.DistanceTo(spawner) is { } close && close <= Number(c.Value(1));
            case "InTime": return now - StartedMs <= Number(spawner);
            case "OutTime": return now - StartedMs > Number(spawner);
            case "StageClear": return clearedStages.Contains(spawner);
            case "CompareValue":
            {
                var index = (int)Number(spawner) - 1;
                var value = index >= 0 && index < Variables.Length ? Variables[index] : 0;
                var other = (int)Number(c.Value(2));
                return c.Value(1) switch { "Equal" => value == other, "NotEqual" => value != other, "Greater" => value > other, "Less" => value < other, "GreaterEqual" => value >= other, "LessEqual" => value <= other, _ => false };
            }
            case "TOD": case "DayOfWeek": return true;
            // The boss room's players left its aggro list (the boss reset: they ran, died or left): the stage fails and its fail
            // directives reset the room (audit 2026-11-13 G11: 82 uses in served events, all stage fails; never true before).
            case "RemoveFromAggroList": return Host.RemovedFromAggro(spawner);
            default: return false;   // FailBoardPuzzle, HpRemaining: not tracked
        }
    }

    static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>"110101202" = quest 1101012, step 02 (the quest step ids of QuestStep.dat).</summary>
    public static (int Quest, int Step)? QuestStep(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stepId) ? QuestBook.Embedded.StepOf(stepId) : null;

    // ---------- Directives ----------

    void Execute(MapEventDirective d, long now, int fired = 0)
    {
        switch (d.Type)
        {
            case "StartSpawner": foreach (var name in Split(d.Value(0))) Host.Spawn(name, d.Int(1)); break;
            case "EndSpawner": foreach (var name in Split(d.Value(0))) Host.Despawn(name, d.Int(1), false); break;
            case "KillSpawner": foreach (var name in Split(d.Value(0))) Host.Despawn(name, 0, true); break;
            case "DespawnAll":
                foreach (var name in Split(d.Value(0)).Concat(Split(d.Value(1)))) Host.Despawn(name, 0, false);
                break;
            case "SummonAroundTarget": Host.SummonAround(d.Value(0), d.Value(3), Math.Max(1, d.Int(4, 1)), d.Int(2, 300)); break;
            case "NpcMoveToWayPoint": Host.MoveNpc(d.Value(0), d.Value(1)); break;
            case "FollowPc": Host.FollowPc(d.Value(0), true); break;
            case "StopFollowPc": Host.FollowPc(d.Value(0), false); break;
            case "NpcRotate": Host.RotateNpc(d.Value(0), d.Value(1)); break;
            case "PlayNpcTalk": Host.Talk(d.Value(0)); break;
            case "PlayHurryNpcTalk":
            {
                var talks = Split(d.Value(0));
                if (talks.Length > 0) Host.Talk(talks[fired % talks.Length]);
                break;
            }
            case "PlayChatbubble": Host.ChatBubble(d.Value(0), d.Value(1)); break;
            case "PlaySocialAction": Host.SocialAction(d.Value(0), d.Int(1), false); break;
            case "StopSocialAction": Host.SocialAction(d.Value(0), d.Int(1), true); break;
            case "SetAggroPc": Host.SetAggroPc(d.Value(0)); break;
            case "SetAggroNpc": Host.SetAggroNpc(d.Value(0), d.Value(1)); break;
            case "ChangeRelationshipEntity": Host.SetRelationship(d.Value(0), d.Value(1)); break;
            // Values: point, TeleportEffect name, subzone, a cutscene before the move (123 uses; the lake cave's exit
            // Cutscene_L_A_Seq_1291), a cutscene on arrival (51 uses; its boss room Cutscene_L_A_Seq_1290), a bool. Never both.
            case "PCTeleport": Host.TeleportPc(d.Value(0), d.Value(1), d.Value(3), d.Value(4)); break;
            case "MapExit": Host.MapExit(d.Value(0), d.Value(2)); break;
            case "DungeonClear": Host.DungeonClear(this); break;
            case "DungeonFail": Status = Expired; Host.DungeonFail(this); break;
            case "SetMapEnvState": Host.MapEnvState(d.Int(0)); break;
            case "SetMapEnvSetting": Host.MapEnvSetting(d.Int(0), d.Int(1)); break;
            case "Cutscene": Host.Cutscene(d.Value(0)); break;
            case "ShowGuide": Host.ShowGuide(d.Value(0)); break;
            case "ShowNote": Host.ShowNote(d.Value(0)); break;
            case "BroadCastingMessage": case "ShowMessage": Host.Message(d.Value(0), d.Value(1)); break;
            case "AddAbnormal": Host.Abnormal(d.Int(0), d.Int(1, -1), d.Value(2), false); break;
            case "RemoveAbnormal": Host.Abnormal(d.Int(0), 0, d.Value(1), true); break;
            case "ChangeEnvObjState": Host.EnvObjState(d.Value(0), d.Value(1)); EnvObjStateChanged(d.Value(0), d.Value(1), now); break;
            case "DynamicTerrainBlock": Host.DynamicTerrain(d.Value(0), d.Flag(1)); break;
            case "SetValue": SetVariable(d.Int(0) - 1, d.Int(1), false); break;
            case "AddValue": SetVariable(d.Int(0) - 1, d.Int(1), true); break;
            case "StartFocusCamera": Host.FocusCamera(d.Value(0), d.Value(2)); break;
            case "EnvObjChangePhase": Host.EnvObjChangePhase(d.Value(0), d.Int(1)); break;
            case "SetEnvObjCanInteract": Host.SetEnvObjCanInteract(d.Value(0), d.Flag(1)); break;
            case "StartWave": StartWave(d.Value(0)); break;
            case "EndWave": waves.Remove(d.Value(0)); break;
            case "ChangeBT": foreach (var name in Split(d.Value(0))) Host.ChangeBehaviour(name, d.Value(1)); break;
            case "NpcChangePhase": foreach (var name in Split(d.Value(0))) Host.NpcPhase(name, d.Int(1)); break;
            case "CameraShake": case "PlayAct": case "NpcMoveforSeamlessCutscene":
                break;   // client-side presentation, or NPC details the server does not model; the event goes on
            default: Host.Unsupported(this, $"directive {d.Type} {d.Value(0)}"); break;
        }
    }

    void SetVariable(int index, int value, bool add)
    {
        if (index < 0 || index >= Variables.Length) return;
        Variables[index] = add ? Variables[index] + value : value;
        Host.Progress(this);
    }

    static string[] Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
