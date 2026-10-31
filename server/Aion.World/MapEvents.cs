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
        _ => 1,
    };
    /// <summary>Objectives only the client can finish (a cutscene, a note, a talk, a guide); it reports them with 0x9230.</summary>
    public bool ClientSide => Type is "CompleteCutscene" or "CloseNote" or "CompleteNpcTalk" or "CloseGuide";
    /// <summary>Objectives the server does not track yet (waves, puzzles, scores, items): finished after a grace period so the event goes on.</summary>
    public bool Unsupported => Type is "AchieveUnifiedObjectivesScore" or "ClearWave" or "ClearBoardPuzzle" or "GetAbnormal" or "PCSocial"
        or "UseUsableItemType" or "CollectItem" or "KillNpcCreatureType" or "UpdateEnvObjState";
}

public sealed record MapEventStep(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("pre")] MapEventDirective[] Pre,
    [property: JsonPropertyName("obj")] MapEventObjective[] Objectives,
    [property: JsonPropertyName("post")] MapEventDirective[] Post,
    [property: JsonPropertyName("fail")] MapEventCondition[][]? Fail = null,
    [property: JsonPropertyName("faildir")] MapEventDirective[]? FailDirectives = null);

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
    [property: JsonPropertyName("quest_gate")] System.Text.Json.JsonElement[][]? QuestGate = null,
    [property: JsonPropertyName("reward_light")] long[][]? RewardLight = null,
    [property: JsonPropertyName("reward_dark")] long[][]? RewardDark = null,
    [property: JsonPropertyName("arrival_cutscene")] string? ArrivalCutscene = null);

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
    /// <summary>Point in the polygon (even-odd) within [ZMin, ZMax].</summary>
    public bool Contains(Vector3d at)
    {
        if (at.Z < ZMin || at.Z > ZMax) return false;
        var inside = false;
        for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
        {
            var (xi, yi, xj, yj) = (Points[i][0], Points[i][1], Points[j][0], Points[j][1]);
            if (yi > at.Y != yj > at.Y && at.X < (xj - xi) * (at.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>Quest-gated instance layer walk-in on a field map (indexed bbox + polygon).</summary>
public sealed record WalkInLayer(int LayerMap, int DungeonId, LayerVolume Volume, double XMin, double YMin, double XMax, double YMax);

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
        [property: JsonPropertyName("teleport_effects")] Dictionary<string, int>? TeleportEffects = null);

    readonly File file;
    readonly Dictionary<int, int> dungeonByMap = [];
    readonly HashSet<int> served;
    readonly Dictionary<int, WalkInLayer[]> walkInByBase;

    MapEventTables(File file)
    {
        this.file = file;
        foreach (var name in file.Events.Keys) file.Events[name].Name = name;
        foreach (var (id, room) in file.Dungeons) dungeonByMap.TryAdd(room.Map, int.Parse(id, CultureInfo.InvariantCulture));
        served = [.. file.ServedMaps];
        var walkIn = new Dictionary<int, List<WalkInLayer>>();
        // Layers entered by talking to an EnterInstanceLayer NPC (NpcEntries) are not walk-in: Maard at Munihelle Forest
        // (HQ2101030 step 3) opens World_D_Starter_HQ001 by his dialog; walking into the layer's volume must not start it.
        // Generic rule: the client names an explicit entry for most layers; only layers with none are walk-in by volume.
        //  - an EnterInstanceLayer NPC (NpcEntries): his dialog opens the layer;
        //  - a base-map EnterInstanceLayer trigger actor (Entries): DungeonTrigger enters on its own (smaller) shape;
        //  - a placed EnvObj with Usage EnterInstanceLayer/EnterDungeon (world-objects portal): using it enters
        //    (HQ2101040 step 5: E_D1_HQ_SearchEffect_001 opens World_D_Starter_HQ004; the lake volume must not).
        var npcEntered = file.NpcEntries.Values.Select(e => e.Dungeon).Where(d => d != 0).ToHashSet();
        npcEntered.UnionWith(file.Entries.Values.SelectMany(l => l).Select(e => e.Dungeon).Where(d => d != 0));
        foreach (var (key, vol) in file.Volumes)
        {
            var layerMap = int.Parse(key, CultureInfo.InvariantCulture);
            var dungeonId = dungeonByMap.GetValueOrDefault(layerMap);
            if (dungeonId == 0 || npcEntered.Contains(dungeonId) || Dungeon(dungeonId) is not { QuestGate: { Length: > 0 } }) continue;
            if (WorldObjects.ObjectsOn(vol.Base).Any(o => o.Portal is { } portal && portal.Contains(dungeonId))) continue;
            var xs = vol.Points.Select(p => p[0]);
            var ys = vol.Points.Select(p => p[1]);
            var entry = new WalkInLayer(layerMap, dungeonId, vol, xs.Min(), ys.Min(), xs.Max(), ys.Max());
            if (!walkIn.TryGetValue(vol.Base, out var list)) walkIn[vol.Base] = list = [];
            list.Add(entry);
        }
        walkInByBase = walkIn.ToDictionary(p => p.Key, p => p.Value.ToArray());
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
    internal IReadOnlyCollection<int> ServedMaps => served;
    internal IEnumerable<KeyValuePair<string, NpcEntry>> AllNpcEntries => file.NpcEntries;
    internal IEnumerable<KeyValuePair<string, DungeonEntry[]>> AllEntries => file.Entries;
    internal IEnumerable<KeyValuePair<string, TeleportTarget>> AllTeleports => file.Teleports ?? [];

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
    /// <summary>Every dungeon of the export, by id.</summary>
    public IEnumerable<KeyValuePair<string, DungeonInfo>> AllDungeons => file.Dungeons;
    public IReadOnlyList<DungeonEntry> EntriesOn(int baseMap) => file.Entries.GetValueOrDefault(K(baseMap)) ?? [];
    /// <summary>The field map and centre of the trigger or portal that opens the dungeon, or null (an NPC or a door opens it).</summary>
    public (int BaseMap, Vector3d At)? EntryTriggerOf(int dungeonId)
    {
        foreach (var (key, entries) in file.Entries)
            if (entries.FirstOrDefault(e => e.Dungeon == dungeonId) is { Center: [var x, var y, var z] })
                return (int.Parse(key, CultureInfo.InvariantCulture), new Vector3d(x, y, z));
        return null;
    }
    public NpcEntry? EntryNpc(int npcId) => file.NpcEntries.GetValueOrDefault(K(npcId));

    /// <summary>Field NPCs whose EnterInstanceLayer opens the given map event (layer dungeon).</summary>
    public IEnumerable<int> FieldEntryNpcIds(string mapEventName)
    {
        if (Event(mapEventName) is not { Map: var eventMap }) yield break;
        foreach (var (key, entry) in file.NpcEntries)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var npcId)) continue;
            if (Dungeon(entry.Dungeon) is not { Map: var dMap }) continue;
            if (dMap == eventMap || EventsOn(dMap).Any(e => string.Equals(e.Name, mapEventName, StringComparison.OrdinalIgnoreCase)))
                yield return npcId;
        }
    }
    public IReadOnlyDictionary<string, double[]> Points(int mapId) => file.Points.GetValueOrDefault(K(mapId)) ?? new Dictionary<string, double[]>();
    public LayerVolume? Volume(int mapId) => file.Volumes.GetValueOrDefault(K(mapId));

    /// <summary>Instance layers whose volume overlays <paramref name="baseMap"/> (walk-in entry on the field map).</summary>
    public IEnumerable<(int LayerMap, LayerVolume Volume)> VolumesOverBase(int baseMap) =>
        file.Volumes.Select(kv => (LayerMap: int.Parse(kv.Key, CultureInfo.InvariantCulture), Volume: kv.Value)).Where(p => p.Volume.Base == baseMap);

    /// <summary>Quest-gated walk-in layers on a shared field map (pre-indexed).</summary>
    public ReadOnlySpan<WalkInLayer> WalkInOn(int baseMap) => walkInByBase.GetValueOrDefault(baseMap) ?? [];

    /// <summary>A Teleport.dat name a PCTeleport uses that is not a point of its own map: the target map and location.</summary>
    public TeleportTarget? Teleport(string name) => file.Teleports?.GetValueOrDefault(name);
    /// <summary>A PCTeleport effect name's TeleportEffect.dat id (the 0x3621 effect byte), 0 when unknown or empty.</summary>
    public int TeleportEffect(string name) => file.TeleportEffects?.GetValueOrDefault(name) ?? 0;
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
    void EnvObjChangePhase(string spawner, int phase);
    void SetEnvObjCanInteract(string spawner, bool canInteract);
    void DynamicTerrain(string spawner, bool block);
    void Progress(MapEventRun run);
    void Unsupported(MapEventRun run, string what);

    /// <summary>
    /// The step is starting. A kill or talk target that should already be in the world (not waiting for a later
    /// StartSpawner, not removed by EndSpawner) is brought back if it is dead, and a kill target that is not
    /// attackable is made so. The story quests share this runner, so a script whose spawn name does not match
    /// the placed actor still has something to fight or talk to.
    /// </summary>
    void EnsureStep(MapEventStep step, int[] goals) { }

    /// <summary>How many of these spawn names are already dead in this run (the player broke the stone before its step).</summary>
    int CountDead(string[] names) => 0;

    /// <summary>A UseEnvObj spawner was already consumed on this layer (early firewood before its step opened).</summary>
    bool EnvObjectUsed(string spawnName) => false;

    /// <summary>Verbose script tracing for starter HQ001 (live stall diagnosis).</summary>
    void ScriptBeat(MapEventRun run, string kind, string detail, long now) { }

    bool QuestStepActive(int questId, int step);
    /// <summary>When an event's exported start list is empty: whether it may join at map entry (quest step, field phase, etc.).</summary>
    bool MapEventStartAllowed(MapEventDefinition definition);
    bool NpcInCombat(string spawner);
    /// <summary>Any NPC of the spawner alive now.</summary>
    bool SpawnerAlive(string spawner);
    /// <summary>The spawner was ever spawned in this run (NotExistSpawner, KillSpawner conditions).</summary>
    bool SpawnerExists(string spawner);
    bool PlayerDead { get; }
    double? DistanceTo(string spawner);
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
    public int[] Variables { get; }
    public bool Finished => Status is Complete or Expired;

    /// <summary>The script is done (DungeonClear, or the last step's objectives met) but its closing directives (Delay, MapExit)
    /// still run: the quest step the clear pays moves past the event's start condition, and dropping the run then lost the
    /// MapExit (HQ004 left the player behind the layer wall until the instance fallback, live 2026-10-30).</summary>
    public bool WindingDown => (Finished || stepObjectivesDone) && sequences.Any(s => !s.Done);

    /// <summary>DungeonClear directive: the script is done; quest ClearMapEvent pays on the next flush (step may advance before MapExit).</summary>
    public void MarkDungeonClear(long now)
    {
        if (Status is Complete or Expired) return;
        Status = Complete;
        Goals = [];
        Host.Progress(this);
    }
    public MapEventStage? Stage => StageIndex < Definition.Stages.Length ? Definition.Stages[StageIndex] : null;
    public MapEventStep? Step => Stage is { } stage && StepIndex < stage.Steps.Length ? stage.Steps[StepIndex] : null;
    public int StageOrder => Stage?.Id ?? 0;
    public int StepOrder => Step?.Id ?? 0;
    /// <summary>
    /// The step as the client reads it in 0x922e / 0x922d: its 1-based place within the stage, not the script's step id. LIVE
    /// 2026-10-30 (HQ003, stage 2 = steps 3, 4, 5): sending id 3 showed Desc_5 ("Check the Prison Compound Map", the third
    /// step of the stage) while the server waited on step 3's trigger, so F on the note did nothing.
    /// </summary>
    public int WireStep => Step is null ? 0 : StepIndex + 1;
    /// <summary>Objectives of the current step still open (for markers and area checks).</summary>
    public IEnumerable<(int Index, MapEventObjective Objective)> Open =>
        Step is { } step ? step.Objectives.Select((o, i) => (i, o)).Where(x => x.i < Goals.Length && Goals[x.i] < x.o.Count) : [];

    /// <summary>KillNpc NPC ids on the current step that still need progress (scripted immunity must not block them).</summary>
    public IEnumerable<int> OpenKillNpcIds() =>
        Open.Where(x => x.Objective.Type == "KillNpc" && x.Objective.Npcs is { Length: > 0 } npcs).SelectMany(x => x.Objective.Npcs!);

    /// <summary>AskNpc / CloseNpc NPC ids on the current step (quest-style streaming and markers on large maps).</summary>
    public IEnumerable<int> OpenAskNpcIds() =>
        Open.Where(x => x.Objective.Type is "AskNpc" or "CloseNpc" && x.Objective.Npcs is { Length: > 0 } npcs).SelectMany(x => x.Objective.Npcs!);

    public void Start(long now)
    {
        if (Definition.Stages.Length == 0 || Definition.Stages[0].Steps.Length == 0) { Status = Complete; return; }
        BeginStep(now);
    }

    void BeginStep(long now)
    {
        var step = Step!;
        Goals = new int[step.Objectives.Length];
        repeaters.Clear();
        stepObjectivesDone = false;
        stepSerial++;
        Array.Fill(fallbackAt, 0);
        CreditDead(now);
        CreditUsedEnv(now);
        Host.EnsureStep(step, Goals);
        Host.ScriptBeat(this, "step-begin", $"stage={StageOrder} step={StepOrder} objectives={step.Objectives.Length}", now);
        if (step.Objectives.Any(o => o.Type is "AskNpc" or "CloseNpc"))
            Host.Progress(this);
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if ((o.ClientSide || o.Unsupported) && i < fallbackAt.Length) fallbackAt[i] = now + FallbackMs;
            // A CloseGuide objective shows its guide (the forced popup) when the step begins: OBSERVED 2026-10-19, ShowGuide (46) for
            // Tutorial_ForcedPopup_Fly_01 at the Subspace's first step and Tutorial_ForcedPopup_L1_ExitDungeon_01 with the Dream
            // Crossroad's join; the client reports CloseGuide (0x9230 kind 2) when the player closes it. Without it the player waited
            // for the 30 s fallback (live 2026-10-20).
            if (o.Type == "CloseGuide" && o.Value(0) is { Length: > 0 } popup) Host.ShowGuide(popup);
            if (o.Pre is { Length: > 0 } pre) sequences.Add(new Sequence(pre, stepSerial));
        }
        sequences.Add(new Sequence(step.Pre, stepSerial));
        Tick(now);
        CheckStep(now);
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
            if (Goals[i] >= o.Count)
            {
                Host.ScriptBeat(this, "objective-done", $"{o.Type} {o.Value(0)} goal={i} goals=[{string.Join(',', Goals)}]", now);
                ObjectiveDone(o, now);
            }
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
        // CoastalCave corpse/tablet steps pair UseEnvObj with CloseNote on the same step; the client reads the note during
        // the interaction (no separate ShowNote directive). Without crediting CloseNote here the step sat until the 30 s fallback.
        if (objective.Type != "UseEnvObj" || Step is not { } step) return;
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Type != "CloseNote" || i >= Goals.Length || Goals[i] >= o.Count) continue;
            if (o.Value(0) is { Length: > 0 } note) Host.ShowNote(note);
            Goals[i] = o.Count;
            Host.ScriptBeat(this, "objective-done", $"{o.Type} {o.Value(0)} goal={i} paired-with UseEnvObj", now);
            if (o.Key is { Length: > 0 } noteKey) doneKeys.Add(noteKey);
            if (o.Post is { Length: > 0 } notePost) sequences.Add(new Sequence(notePost, stepSerial));
        }
    }

    internal static bool NameMatches(string name, string spawnName)
    {
        if (string.Equals(name, spawnName, StringComparison.OrdinalIgnoreCase)) return true;
        if (spawnName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) return true;
        // Phase-two firewood placements keep the _001_2 spawner name (Dark starter HQ001, OBSERVED map data gap).
        if (name.EndsWith("_002", StringComparison.OrdinalIgnoreCase))
        {
            var phaseOneSecond = name[..^4] + "_001_2";
            if (string.Equals(spawnName, phaseOneSecond, StringComparison.OrdinalIgnoreCase)
                || spawnName.StartsWith(phaseOneSecond + "_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Any step of the event uses this env spawner on a UseEnvObj objective.</summary>
    public static bool ReferencesUseEnvObj(MapEventDefinition definition, string spawnName) =>
        definition.Stages.SelectMany(s => s.Steps).SelectMany(st => st.Objectives)
            .Any(o => o.Type == "UseEnvObj" && o.Names.Any(n => NameMatches(n, spawnName)));

    /// <summary>
    /// A kill the player already landed, before this step opened, fills the step. The stone in the forest can die
    /// during the earlier pack; Elvida still asks for it, and waiting for another blow leaves the quest stuck.
    /// Shared npc ids (two waves of the same monster) are not counted here: only the objective's own spawn names.
    /// </summary>
    void CreditDead(long now)
    {
        var step = Step!;
        var changed = false;
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Type != "KillNpc" || Goals[i] >= o.Count || o.Names.Length == 0) continue;
            var dead = Host.CountDead(o.Names);
            if (dead <= 0) continue;
            Goals[i] = Math.Min(o.Count, Goals[i] + dead);
            changed = true;
            if (Goals[i] >= o.Count) ObjectiveDone(o, now);
        }
        if (changed) Host.Progress(this);
    }

    /// <summary>Firewood destroyed before UseEnvObj step opens: count it when the step begins (same idea as CreditDead).</summary>
    void CreditUsedEnv(long now)
    {
        var step = Step!;
        var changed = false;
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Type != "UseEnvObj" || Goals[i] >= o.Count) continue;
            if (!o.Names.Any(Host.EnvObjectUsed)) continue;
            Goals[i] = o.Count;
            changed = true;
            Host.ScriptBeat(this, "objective-credit-early", $"UseEnvObj {o.Value(0)} index={i}", now);
            ObjectiveDone(o, now);
        }
        if (changed) Host.Progress(this);
    }

    static bool Names(MapEventObjective o, string spawnName) => o.Names.Any(n => NameMatches(n, spawnName));

    /// <summary>An NPC of this instance died (its NpcData id, spawn entry name); <paramref name="spawnerAlive"/>: others of its spawner still live.</summary>
    public bool Killed(int npcId, string spawnName, bool hostile, bool spawnerAlive, long now) =>
        Apply(o => o.Type switch
        {
            "KillNpc" => (o.Npcs ?? []).Contains(npcId) || Names(o, spawnName),
            "KillSpawner" => Names(o, spawnName) && !spawnerAlive,
            "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" => hostile,
            _ => false,
        }, now);

    /// <summary>
    /// The player talked to an NPC (the step talk, 0x8d2b kind 2; <paramref name="index"/> names the objective when the client said
    /// which) or closed its dialog (<paramref name="close"/>: CloseNpc objectives only).
    /// </summary>
    public bool Talked(int npcId, string spawnName, int? index, bool close, long now) =>
        Apply(o => o.Type == (close ? "CloseNpc" : "AskNpc") && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))
                   && (index is not { } i || Step!.Objectives.Length <= i || ReferenceEquals(Step.Objectives[i], o)), now);

    public bool UsedObject(string spawnName, long now) => Apply(o => o.Type == "UseEnvObj" && Names(o, spawnName), now);

    /// <summary>Whether a UseEnvObj objective of the current step still counts this spawner (the object despawns once used: OBSERVED for the phoenix eggs).</summary>
    public bool WantsObject(string spawnName) =>
        !Finished && Step is { } step && step.Objectives.Where((o, i) => i < Goals.Length && Goals[i] < o.Count && o.Type == "UseEnvObj" && Names(o, spawnName)).Any();

    /// <summary>Whether this spawner name appears on any UseEnvObj objective of the run (open or not yet reached).</summary>
    public bool ReferencesUseEnvObj(string spawnName) =>
        Definition.Stages.SelectMany(s => s.Steps).SelectMany(st => st.Objectives)
            .Any(o => o.Type == "UseEnvObj" && Names(o, spawnName));

    /// <summary>Where the player stands: EnterVolumePC (trigger names) and EnterSubZone objectives.</summary>
    public bool PositionAt(Func<string, bool> insideTrigger, Func<string, bool> insideSubzone, long now) =>
        Apply(o => o.Type == "EnterVolumePC" ? insideTrigger(o.Value(0)) : o.Type == "EnterSubZone" && insideSubzone(o.Value(0)), now);

    public bool CombatStarted(long now) => Apply(o => o.Type == "PCCombatStart", now);

    public bool NpcArrived(string spawnName, string route, long now)
    {
        var ok = Apply(o => o.Type == "NpcArriveWaypoint" && NameMatches(o.Value(0), spawnName)
                             && (o.Value(1).Length == 0 || string.Equals(o.Value(1), route, StringComparison.OrdinalIgnoreCase)), now);
        if (ok && Definition.Name.Contains("Starter_HQ001", StringComparison.OrdinalIgnoreCase))
            Host.ScriptBeat(this, "npc-arrive", $"{spawnName} route={route}", now);
        return ok;
    }

    /// <summary>An NPC's HP changed: HpRemaining objectives (percent) naming it.</summary>
    public bool HpChanged(int npcId, string spawnName, int percent, long now) =>
        Apply(o => o.Type == "HpRemaining" && ((o.Npcs ?? []).Contains(npcId) || Names(o, spawnName))
                   && int.TryParse(o.Value(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && percent <= limit, now);

    /// <summary>The client finished objective <paramref name="index"/> itself (0x9230 kind 2: cutscene, note, talk, guide).</summary>
    public bool ClientObjective(int index, long now) =>
        Step is { } step && index >= 0 && index < step.Objectives.Length && Apply(o => ReferenceEquals(o, step.Objectives[index]), now, whole: true);

    public bool CutsceneDone(string name, long now) => Apply(o => o.Type == "CompleteCutscene" && string.Equals(o.Value(0), name, StringComparison.OrdinalIgnoreCase), now);

    public bool EnvObjStateChanged(string spawnName, string state, long now) =>
        Apply(o => o.Type == "UpdateEnvObjState" && Names(o, spawnName) && string.Equals(o.Value(1), state, StringComparison.OrdinalIgnoreCase), now);

    // ---------- Steps ----------

    void CheckStep(long now)
    {
        if (Finished || stepObjectivesDone || Step is not { } step) return;
        var required = step.Objectives.Select((o, i) => (o, i)).Where(x => !x.o.Optional).ToList();
        if (required.Count == 0) required = step.Objectives.Select((o, i) => (o, i)).ToList();
        if (required.Any(x => Goals[x.i] < x.o.Count)) return;
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
        if (Finished) return;
        // Client-side and untracked objectives time out as done.
        if (Step is { } step)
            for (var i = 0; i < step.Objectives.Length && i < fallbackAt.Length; i++)
                if (fallbackAt[i] != 0 && now >= fallbackAt[i] && Goals[i] < step.Objectives[i].Count)
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
        if (Stage is { Fail.Length: > 0 } stage)
        {
            var failing = Holds(stage.Fail, now);
            if (failing && !stageFailing) { stageFailing = true; FailStage(stage, now); return; }
            if (!failing) stageFailing = false;
        }
        if (stepObjectivesDone && !sequences.Any(s => s.Step == stepSerial)) NextStep(now);
    }

    // ---------- Conditions ----------

    /// <summary>Whether an event's start conditions hold without starting a run (dungeon entry checks).</summary>
    public static bool StartHolds(MapEventDefinition definition, IMapEventHost host, long now)
    {
        if (definition.Start is { Length: > 0 } start)
        {
            var probe = new MapEventRun(definition, 0, host, now);
            return probe.Holds(start, now);
        }
        return host.MapEventStartAllowed(definition);
    }

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
            default: return false;   // FailBoardPuzzle, RemoveFromAggroList, HpRemaining: not tracked
        }
    }

    static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>"110101202" = quest 1101012, step 02 (the quest step ids of QuestStep.dat).</summary>
    public static (int Quest, int Step)? QuestStep(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stepId) ? QuestBook.Embedded.StepOf(stepId) : null;

    // ---------- Directives ----------

    void Execute(MapEventDirective d, long now, int fired = 0)
    {
        if (Definition.Name.Contains("Starter_HQ001", StringComparison.OrdinalIgnoreCase))
            Host.ScriptBeat(this, "directive", $"{d.Type} id={d.Id} v0={d.Value(0)} step={StepOrder}", now);
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
            case "CameraShake": case "PlayAct": case "ChangeBT": case "NpcMoveforSeamlessCutscene": case "NpcChangePhase":
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
