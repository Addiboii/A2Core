using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;

namespace Aion.World;

// tools/data/quests.json (tools/quests.py from the client's Quest/QuestStep/QuestReward tables).
public sealed record QuestObjective(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("npc")] int? Npc = null,
    [property: JsonPropertyName("npcs")] int[]? Npcs = null,
    [property: JsonPropertyName("item")] int? Item = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("count")] int Count = 1,
    [property: JsonPropertyName("optional")] bool Optional = false,
    [property: JsonPropertyName("level")] int? Level = null,
    [property: JsonPropertyName("envs")] int[]? Envs = null,
    [property: JsonPropertyName("chance")] int Chance = 0,
    [property: JsonPropertyName("map")] int? Map = null,
    [property: JsonPropertyName("move_point")] string? MovePoint = null,
    [property: JsonPropertyName("flag")] string? Flag = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null)
{
    /// <summary>The raw GoalList value at <paramref name="index"/> (KillPC / KillPCAbyssGrade keep their value array), "" when absent.</summary>
    public string Raw(int index) =>
        Value is { ValueKind: JsonValueKind.Array } array && index < array.GetArrayLength() && array[index] is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";

    public int RawInt(int index) =>
        int.TryParse(Raw(index), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>
    /// QuestStep GoalList KillPC {kill type "LastHit,Contribute", count, map} and KillPCAbyssGrade {grade from, grade to, kill type, count,
    /// map} (Table_QuestStep, the 16 UQ Abyss duty quests): the count the table asks for (quests.json kept it at 1 before 2026-11-13).
    /// </summary>
    public int PlayerKillCount => Type switch
    {
        "KillPC" => Math.Max(1, RawInt(1)),
        "KillPCAbyssGrade" => Math.Max(1, RawInt(3)),
        _ => Count,
    };

    /// <summary>
    /// Whether a player kill counts: the objective's map (0 = any) and, for KillPCAbyssGrade, the victim's Abyss grade within the
    /// range (ERankingGrade numbers: 3-4 Generals and Marshals, 5-9 the star officers, 10-14 the soldier classes; 19 an unranked Trainee).
    /// CANDIDATE: the kill type "LastHit,Contribute" is read as the killer only (no contribution share for the party).
    /// </summary>
    public bool CountsPlayerKill(int map, int victimGrade) => Type switch
    {
        "KillPC" => RawInt(2) is var m && (m == 0 || m == map),
        "KillPCAbyssGrade" => (RawInt(4) is var m && (m == 0 || m == map)) && victimGrade >= RawInt(0) && victimGrade <= Math.Max(RawInt(0), RawInt(1)),
        _ => false,
    };
}

public sealed record QuestStep(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("objectives")] QuestObjective[] Objectives,
    [property: JsonPropertyName("npc")] int? Npc = null,
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("unified_score")] long UnifiedScore = 0);

public sealed record QuestRewards(
    [property: JsonPropertyName("exp")] long Exp = 0,
    [property: JsonPropertyName("kina")] long Kina = 0,
    [property: JsonPropertyName("items")] long[][]? Items = null,
    [property: JsonPropertyName("select")] long[][]? Select = null,
    [property: JsonPropertyName("class_items")] Dictionary<string, long[][]>? ClassItems = null,
    [property: JsonPropertyName("random")] string? Random = null)
{
    /// <summary>
    /// Table_QuestClassReward's items for a class (14 Hero quests: HQ1102010 gives the Gladiator 110150028, the Cleric 110750028 ...),
    /// paid with the quest's own items; none for another class or an unknown one.
    /// </summary>
    public IEnumerable<(int Item, long Count)> ClassItemsFor(string? className) =>
        className is { Length: > 0 } && ClassItems is { } byClass
        && byClass.FirstOrDefault(c => string.Equals(c.Key, className, StringComparison.OrdinalIgnoreCase)).Value is { } items
            ? items.Where(i => i.Length == 2 && i[1] > 0).Select(i => ((int)i[0], i[1])) : [];
}

public sealed record QuestDefinition(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("race")] string Race,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("acquire")] string Acquire,
    [property: JsonPropertyName("start_npc")] int? StartNpc,
    [property: JsonPropertyName("complete")] string Complete,
    [property: JsonPropertyName("finish_npc")] int? FinishNpc,
    [property: JsonPropertyName("giveup")] bool GiveUp,
    [property: JsonPropertyName("prerequisites")] string[]? Prerequisites,
    [property: JsonPropertyName("previous")] string[]? Previous,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("steps")] QuestStep[] Steps,
    [property: JsonPropertyName("rewards")] QuestRewards? Rewards,
    [property: JsonPropertyName("conditions")] QuestCondition[]? Conditions = null,
    [property: JsonPropertyName("finish_map")] int? FinishMap = null,
    [property: JsonPropertyName("finish_teleport")] string? FinishTeleport = null,
    [property: JsonPropertyName("exploration")] string? Exploration = null,
    [property: JsonPropertyName("repeat")] string? Repeat = null);

/// <summary>An AcquirableConditions row other than ClearQuest (those are Prerequisites): IncompleteQuest, ClearQuestStep, SubZone, CloseEnvObj, the masteries.</summary>
public sealed record QuestCondition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value);

public sealed record QuestUnlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("light")] string? Light,
    [property: JsonPropertyName("dark")] string? Dark);

/// <summary>Every client quest by code, and the quest-gated contents (ContentsUnlock rows).</summary>
public sealed class QuestBook
{
    sealed record File([property: JsonPropertyName("quests")] Dictionary<string, QuestDefinition> Quests,
                       [property: JsonPropertyName("unlocks")] QuestUnlock[] Unlocks);

    static readonly Lazy<QuestBook> embedded = new(() =>
    {
        using var stream = typeof(QuestBook).Assembly.GetManifestResourceStream("quests.json")
            ?? throw new InvalidOperationException("quests.json is not embedded");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("quests.json");
        return new QuestBook(file.Quests, file.Unlocks);
    });

    public static QuestBook Embedded => embedded.Value;

    public QuestBook(IReadOnlyDictionary<string, QuestDefinition> quests, IReadOnlyList<QuestUnlock>? unlocks = null)
    {
        quests = WithPlayerKillCounts(quests);
        Quests = quests;
        Unlocks = unlocks ?? [];
        ById = quests.ToDictionary(q => q.Value.Id, q => q.Key);
        stepsById = quests.Values.SelectMany(q => q.Steps.Where(s => s.Id != 0).Select(s => (s.Id, Step: (q.Id, s.Order))))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Step);
    }

    readonly Dictionary<int, (int Quest, int Step)> stepsById;

    /// <summary>KillPC / KillPCAbyssGrade objectives take their count from the table's value array (QuestObjective.PlayerKillCount).</summary>
    static IReadOnlyDictionary<string, QuestDefinition> WithPlayerKillCounts(IReadOnlyDictionary<string, QuestDefinition> quests)
    {
        static bool PlayerKill(QuestObjective o) => o.Type is "KillPC" or "KillPCAbyssGrade" && o.Count != o.PlayerKillCount;
        if (!quests.Values.Any(q => q.Steps.Any(s => s.Objectives.Any(PlayerKill)))) return quests;
        return quests.ToDictionary(q => q.Key, q => q.Value.Steps.Any(s => s.Objectives.Any(PlayerKill))
            ? q.Value with { Steps = q.Value.Steps.Select(s => s with { Objectives = s.Objectives.Select(o => PlayerKill(o) ? o with { Count = o.PlayerKillCount } : o).ToArray() }).ToArray() }
            : q.Value);
    }

    /// <summary>
    /// The items a turn-in takes back: CollectItem objectives whose third value is "TRUE" (the 12 GatherCraftMastery quests' Elevate_*
    /// test pieces x1; quests.py "flag", CANDIDATE reading: the four rows without it keep their items).
    /// </summary>
    public static IEnumerable<(int Item, int Count)> TakenAtTurnIn(QuestDefinition quest) =>
        quest.Steps.SelectMany(s => s.Objectives)
            .Where(o => o.Type == "CollectItem" && o.Item is > 0 && string.Equals(o.Flag, "TRUE", StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => o.Item!.Value).Select(g => (g.Key, g.Max(o => o.Count)));

    /// <summary>
    /// A QuestStep row ID (as conditions name it) as quest id and step order: the exported ID, else quest id * 100 + order
    /// (71 of 2553 rows differ: HQ1101040's step 1 is 110104002, which the formula read as a step 2 that does not exist).
    /// </summary>
    public (int Quest, int Step)? StepOf(int stepId) =>
        stepsById.TryGetValue(stepId, out var known) ? known : stepId > 100 ? (stepId / 100, stepId % 100) : null;

    public IReadOnlyDictionary<string, QuestDefinition> Quests { get; }
    public IReadOnlyList<QuestUnlock> Unlocks { get; }
    public IReadOnlyDictionary<int, string> ById { get; }

    public QuestDefinition? Find(string code) => Quests.GetValueOrDefault(code);

    public static bool RaceMatches(QuestDefinition quest, int race) =>
        quest.Race == "All" || (quest.Race == "Light" && race == 1) || (quest.Race == "Dark" && race == 2);
}

/// <summary>
/// One character's quests (game rules only; packets and storage are the session's): what can be
/// accepted, progress from kills, NPC talks and items, step advance and completion. EQuestStatus:
/// 2 Incomplete, 3 Complete (reward pending), 4 Rewarded. Each step's objectives map to the six
/// goal counters in order.
/// </summary>
public sealed class QuestLog(QuestBook book, int race)
{
    public const int Incomplete = 2, Complete = 3, Rewarded = 4;
    readonly Dictionary<string, QuestRecord> quests = [];

    public QuestBook Book => book;
    public IReadOnlyCollection<QuestRecord> All => quests.Values;
    public QuestRecord? Get(string code) => quests.GetValueOrDefault(code);
    public QuestRecord? ByUid(long uid) => quests.Values.FirstOrDefault(q => q.Uid == uid);

    public void Load(IEnumerable<QuestRecord> records)
    {
        quests.Clear();
        foreach (var record in records) quests[record.Code] = record;
    }

    public void Set(QuestRecord record) => quests[record.Code] = record;
    public void Remove(string code) => quests.Remove(code);

    bool Done(string code) => quests.TryGetValue(code, out var q) && q.Status >= Complete;

    /// <summary>Whether the character may take the quest now (race, level, not taken, earlier quests done).</summary>
    public bool CanAccept(string code, int level)
    {
        // RepeatType Infinitely (572 DutyScroll quests): a rewarded one may be taken again (audit 2026-11-13 quests G4).
        if (book.Find(code) is not { } quest || quests.TryGetValue(code, out var held) && !(held.Status == Rewarded && quest.Repeat == "Infinitely")) return false;
        if (!QuestBook.RaceMatches(quest, race) || level < quest.Level || quest.Steps.Length == 0) return false;
        return (quest.Prerequisites ?? []).All(Done) && (quest.Previous ?? []).All(p => !book.Quests.ContainsKey(p) || Done(p));
    }

    /// <summary>Quests an NPC offers now (its start NPC, acquired by talking).</summary>
    public IEnumerable<string> OfferedBy(int npcId, int level) =>
        book.Quests.Where(q => q.Value.StartNpc == npcId && q.Value.Acquire == "Npc" && CanAccept(q.Key, level)).Select(q => q.Key);

    /// <summary>Quests that start by themselves now (acquire Auto).</summary>
    public IEnumerable<string> AutoAcquirable(int level) =>
        book.Quests.Where(q => q.Value.Acquire == "Auto" && CanAccept(q.Key, level)).Select(q => q.Key);

    public static QuestRecord New(string code, QuestDefinition quest) => new(code, quest.Id, 0, Incomplete, 1, new int[6]);

    public QuestStep? CurrentStep(QuestRecord record) =>
        book.Find(record.Code)?.Steps.FirstOrDefault(s => s.Order == record.Step);

    /// <summary>Counts an event against every active quest's current step; the records that changed.</summary>
    public List<QuestRecord> Progress(Func<QuestObjective, bool> matches, int amount = 1, int level = 0)
    {
        var changed = new List<QuestRecord>();
        foreach (var record in quests.Values.Where(q => q.Status == Incomplete).ToList())
        {
            if (CurrentStep(record) is not { } step) continue;
            var goals = (int[])record.Goals.Clone();
            var touched = false;
            for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
            {
                var objective = step.Objectives[i];
                if (goals[i] >= objective.Count || !matches(objective)) continue;
                goals[i] = Math.Min(objective.Count, goals[i] + amount);
                touched = true;
            }
            if (!touched) continue;
            var next = Advance(record with { Goals = goals }, level);
            quests[record.Code] = next;
            changed.Add(next);
        }
        return changed;
    }

    /// <summary>
    /// A kill: KillNpc counts; CollectFakeItem (a quest item the client never shows in the bag) drops with the
    /// objective's chance per 10,000 (CANDIDATE: the value's unit) from the listed NPCs.
    /// </summary>
    public List<QuestRecord> Killed(int npcId, int level, Func<int, bool>? drops = null)
    {
        drops ??= chance => Random.Shared.Next(10_000) < chance;
        return Progress(o => (o.Npcs ?? []).Contains(npcId) && (o.Type == "KillNpc" || o.Type == "CollectFakeItem" && drops(o.Chance)), level: level);
    }

    /// <summary>Objectives the client finishes by itself (a cutscene talk, a note read, a fake item used) and reports with 0x9230.</summary>
    public static readonly HashSet<string> ClientObjectiveTypes = ["CompleteCelviceTalk", "CloseNote", "UseFakeItem"];

    /// <summary>The client reports objective <paramref name="index"/> of the quest's current step done; null when that is not its to decide.</summary>
    public QuestRecord? ClientCompleted(long uid, int index, int level)
    {
        if (ByUid(uid) is not { Status: Incomplete } record || CurrentStep(record) is not { } step) return null;
        if (index < 0 || index >= Math.Min(6, step.Objectives.Length) || !ClientObjectiveTypes.Contains(step.Objectives[index].Type)) return null;
        if (record.Goals[index] >= step.Objectives[index].Count) return record;
        var goals = (int[])record.Goals.Clone();
        goals[index] = step.Objectives[index].Count;
        return quests[record.Code] = Advance(record with { Goals = goals }, level);
    }

    /// <summary>After a quest finished: steps waiting on other quests (ClearQuest, ClearQuestType, ClearQuestExplorationType) are re-evaluated.</summary>
    public List<QuestRecord> Recheck(int level)
    {
        var changed = new List<QuestRecord>();
        foreach (var record in quests.Values.Where(q => q.Status == Incomplete).ToList())
        {
            var next = Advance(record, level);
            if (ReferenceEquals(next, record)) continue;
            quests[record.Code] = next;
            changed.Add(next);
        }
        return changed;
    }

    /// <summary>
    /// A player of the other race killed (the Abyss) on <paramref name="map"/>: KillPC objectives of that map, and KillPCAbyssGrade ones
    /// whose grade range holds the victim's Abyss grade (before 2026-11-13 any kill counted every one, and their counts read as 1).
    /// </summary>
    public List<QuestRecord> KilledPlayer(int level, int map, int victimGrade) =>
        Progress(o => o.Type is "KillPC" or "KillPCAbyssGrade" && o.CountsPlayerKill(map, victimGrade), level: level);

    /// <summary>A map event cleared (an instance layer's boss): ClearMapEvent objectives naming it.</summary>
    public List<QuestRecord> ClearedMapEvent(string mapEvent, int level) =>
        Progress(o => o.Type == "ClearMapEvent" && o.Target == mapEvent, level: level);

    /// <summary>
    /// The step talk with an NPC finished (QuestStepNpcConversation_RQ 0x8d2b kind 1, sent by the client when the objective's talk
    /// ends, or when the player comes close to a CloseNpc objective's NPC): AskNpc and CloseNpc objectives naming it. OBSERVED: every official AskNpc progress (28 in the 2026-11-06/07
    /// recordings) follows the 0x8d2c reply, never the dialog's opening (0xe241); see ClosedDialog for the close.
    /// </summary>
    public List<QuestRecord> Talked(int npcId, int level) =>
        Progress(o => o.Type is "AskNpc" or "CloseNpc" && o.Npc == npcId, level: level);

    /// <summary>
    /// The player closed an NPC's dialog (FinishNpcInteraction_RQ 0xe242): CloseNpc objectives naming it. CloseNpc means "come close
    /// to the NPC" and the client reports it with the step talk (0x8d2b, Talked; OBSERVED for map events); a dialog closed next to
    /// the NPC counts it too. CANDIDATE fallback (no official quest CloseNpc recorded).
    /// </summary>
    public List<QuestRecord> ClosedDialog(int npcId, int level) =>
        Progress(o => o.Type == "CloseNpc" && o.Npc == npcId, level: level);

    /// <summary>Using a quest object (UseEnvObj names EnvObjData ids).</summary>
    public List<QuestRecord> UsedObject(int env, int level) =>
        Progress(o => o.Type == "UseEnvObj" && (o.Envs ?? []).Contains(env), level: level);

    /// <summary>An open UseEnvObj objective (current step, goal not reached) wants this EnvObjData template.</summary>
    public bool WantsUse(int env) =>
        quests.Values.Any(q => q.Status == Incomplete && CurrentStep(q) is { } step
            && step.Objectives.Take(Math.Min(6, q.Goals.Length)).Select((o, i) => (o, i))
                .Any(x => x.o.Type == "UseEnvObj" && (x.o.Envs ?? []).Contains(env) && q.Goals[x.i] < x.o.Count));

    /// <summary>The area objectives (EnterVolumePC, EnterSubZone) of active quests' current steps, still to do.</summary>
    public IEnumerable<QuestObjective> OpenAreaObjectives() =>
        quests.Values.Where(q => q.Status == Incomplete).SelectMany(record =>
            CurrentStep(record) is { } step
                ? step.Objectives.Take(6).Where((o, i) => o.Type is "EnterVolumePC" or "EnterSubZone" && record.Goals[i] < o.Count)
                : []);

    /// <summary>The map events (ClearMapEvent targets) active quests' current steps still wait for.</summary>
    public IEnumerable<string> OpenMapEventTargets() =>
        quests.Values.Where(q => q.Status == Incomplete).SelectMany(record =>
            CurrentStep(record) is { } step
                ? step.Objectives.Take(6).Where((o, i) => o.Type == "ClearMapEvent" && o.Target is not null && record.Goals[i] < o.Count).Select(o => o.Target!)
                : []);

    /// <summary>Being in an area: every open area objective <paramref name="inside"/> accepts counts once.</summary>
    public List<QuestRecord> Entered(Func<QuestObjective, bool> inside, int level) =>
        Progress(o => o.Type is "EnterVolumePC" or "EnterSubZone" && inside(o), level: level);

    /// <summary>Binding to a Kibelisk (EngraveTeleportArtifact, target TeleportArtifact_id).</summary>
    public List<QuestRecord> Engraved(string artifact, int level) =>
        Progress(o => o.Type == "EngraveTeleportArtifact" && o.Target == artifact, level: level);

    /// <summary>The items the open collect objectives (current steps of incomplete quests) count.</summary>
    public List<int> CollectedItemIds() =>
        quests.Values.Where(q => q.Status == Incomplete).Select(CurrentStep).OfType<QuestStep>()
            .SelectMany(s => s.Objectives.Take(6)).Where(o => o.Type == "CollectItem" && o.Item is not null).Select(o => o.Item!.Value).Distinct().ToList();

    /// <summary>Collect objectives follow the item count held (set, not added).</summary>
    public List<QuestRecord> Holding(int itemId, long count, int level)
    {
        var changed = new List<QuestRecord>();
        foreach (var record in quests.Values.Where(q => q.Status == Incomplete).ToList())
        {
            if (CurrentStep(record) is not { } step) continue;
            var goals = (int[])record.Goals.Clone();
            var touched = false;
            for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
                if (step.Objectives[i] is { Type: "CollectItem" } o && o.Item == itemId && goals[i] != (int)Math.Min(o.Count, count))
                {
                    goals[i] = (int)Math.Min(o.Count, count);
                    touched = true;
                }
            if (!touched) continue;
            var next = Advance(record with { Goals = goals }, level);
            quests[record.Code] = next;
            changed.Add(next);
        }
        return changed;
    }

    /// <summary>
    /// Moves past every finished step: the next step starts with fresh counters, the last one
    /// completes the quest. Level and cleared-quest objectives are read from the character's state;
    /// the others count events. GM QuestCompleteStep moves past anything the server cannot observe.
    /// </summary>
    /// <summary>
    /// The states passed through on the way to a step change, by quest code: the step with its goal reached, before the
    /// next step (or the completion). OBSERVED: the official sends QuestUpdateProgress_NT twice on a step change — first
    /// {status 2, step N, goal 1}, then {status 2, step N+1, goals 0}; on the last step {2, N, 1} then {3, N, 1} — and the
    /// client needs the first to mark the step done (live 2026-10-18: without it Robin's cutscene talk repeated forever).
    /// </summary>
    public Dictionary<string, List<QuestRecord>> Milestones { get; } = new(StringComparer.OrdinalIgnoreCase);

    public QuestRecord Advance(QuestRecord record, int level)
    {
        while (record.Status == Incomplete && CurrentStep(record) is { } step && StepDone(step, record.Goals, level))
        {
            Milestones.TryAdd(record.Code, []);
            Milestones[record.Code].Add(record);
            var steps = book.Find(record.Code)!.Steps;
            var index = Array.FindIndex(steps, s => s.Order == step.Order);
            record = index + 1 < steps.Length
                ? record with { Step = steps[index + 1].Order, Goals = new int[6] }
                : record with { Status = Complete };
        }
        return record;
    }

    /// <summary>Takes (and clears) the milestones of a quest.</summary>
    public List<QuestRecord> TakeMilestones(string code) => Milestones.Remove(code, out var list) ? list : [];

    bool StepDone(QuestStep step, int[] goals, int level)
    {
        // The Ascension Gauge's step (bUnifiedObjectives): its goals are points, done when they add up to UnifiedObjectiveScore
        // (2,100,000,000 for GQ1702010/GQ2702010: in practice never; the official gauge stayed at status 2 at 2,720 points).
        if (step.UnifiedScore > 0 && IsGauge(step)) return goals.Take(6).Sum(g => (long)g) >= step.UnifiedScore;
        var required = step.Objectives.Select((o, i) => (o, i)).Where(x => !x.o.Optional && x.i < 6).ToList();
        if (required.Count == 0) return false;
        return required.All(x => x.o.Type switch
        {
            "PCLevel" => level >= (x.o.Level ?? int.MaxValue),
            // The named quest turned in (Rewarded), as official (rec 20261107-012837: HQ1101021 step 1 "ClearQuest AQ1602000" moved
            // with AQ1602000's reward at Ashar, then DreamCrossroad_L_01 and its exit). Complete alone moved it at the last light
            // mirror and the exit event sent the player out before the Ashar talk (live 2026-11-12 zenyc). Exploration quests are
            // rewarded at their completion (PaysAtCompletion), so EQ1820001 still moves HQ1102070 at once.
            "ClearQuest" => x.o.Target is { } code && quests.TryGetValue(code, out var cleared) && cleared.Status >= Rewarded,
            "ClearQuestType" => quests.Values.Count(q => q.Status >= Complete && book.Find(q.Code)?.Type == x.o.Target) >= x.o.Count,
            // Quest.dat ExplorationType of finished exploration quests. (The only rows of these two types are the Ascension Gauge's,
            // whose unified step counts points instead, above.)
            "ClearQuestExplorationType" => quests.Values.Count(q => q.Status >= Complete && book.Find(q.Code)?.Exploration == x.o.Target) >= x.o.Count,
            _ => goals[x.i] >= x.o.Count,
        });
    }

    // ---------- The Ascension Gauge (Quest.dat type Daevagauge: GQ1702010 Elyos, GQ2702010 Asmodian) ----------
    // OBSERVED in the official recordings (2026-11-06/07; QuestList 0x8d09 at each login and QuestUpdateProgress 0x8d10 after each
    // reward): the gauge's five goals are POINTS, not quest counts. Each quest of a goal's kind that is REWARDED after the gauge
    // started adds the goal's table value (QuestStep GoalList Value "10" for all five) to that goal:
    //   goal 1 ClearQuestType Hero, 2 District, 3 Ascension, 4 ClearQuestExplorationType Seal, 5 OccupationTerritory.
    //   - new Elyos character: the gauge came (0x8d11, all 0) right after AQ1602000's reward; the six Hero quests rewarded before
    //     never counted; HQ1101021 (taken before, rewarded after) gave 10, then HQ1101030/040/050/070 20, 30, 40, 50.
    //   - an Asmodian at [820, 580, 30, 1020, 270]: its list held 58 rewarded District quests (580), AQ2602010/020/030 rewarded
    //     but AQ2602000 (the gauge's own prerequisite) not counted (30), and 102 Seal + 27 OccupationTerritory explorations.
    //   - no cap: 1,020 Seal points (102 quests); the step's UnifiedObjectiveScore 2,100,000,000 is never reached (status 2).
    //   - the update: 0x8d10 {gauge uid, status 2, step 1, the five totals} right after the reward reply 0x8d0f and before the
    //     next quest's 0x8d11 (Hero 230 -> 240 at 20261106-145442 #38929/#38942/#39367); a reward of another kind sends none.
    // CANDIDATE: that the per-quest amount is the table value rather than a constant 10 (every row says 10).
    // The total is what the Ascension quests' UnifiedObjectiveScore acquirable condition reads (AQ1602010 needs 320, AQ1602030
    // 600, AQ1602020 1490, AQ1602040 1740; the Asmodian ones 340/610/1500/1750): CANDIDATE reading, consistent with the
    // Asmodian at 2,720 having AQ2602010/030/020 done and not AQ2602040 (its other condition, HQ2103580 taken, did not hold).

    static bool IsGauge(QuestStep step) => step.Objectives.Length > 0
        && step.Objectives.All(o => o.Type is "ClearQuestType" or "ClearQuestExplorationType");

    static bool CountsFor(QuestObjective objective, QuestDefinition? quest) => quest is not null && objective.Type switch
    {
        "ClearQuestType" => quest.Type == objective.Target,
        "ClearQuestExplorationType" => quest.Exploration == objective.Target,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="quest"/> was rewarded after the gauge started, i.e. after the gauge's prerequisite (AQ1602000 /
    /// AQ2602000) was rewarded: by the rows' times (a Rewarded row is not written again); rows without a time (not read from the
    /// database) by uid order instead (CANDIDATE fallback: a quest taken before and rewarded after the start is then missed).
    /// </summary>
    static bool RewardedAfter(QuestRecord quest, QuestRecord start) =>
        quest.UpdatedAt is { } at && start.UpdatedAt is { } started ? at > started : quest.Uid > start.Uid;

    /// <summary>The gauge's goals as they should be now: per goal, its value for each counted quest rewarded after the start.</summary>
    public int[] GaugeGoals(QuestDefinition gauge, QuestStep step)
    {
        var starts = (gauge.Prerequisites ?? []).Select(Get).OfType<QuestRecord>().Where(q => q.Status == Rewarded).ToList();
        var rewarded = quests.Values.Where(q => q.Status == Rewarded && starts.All(start => q.Code != start.Code && RewardedAfter(q, start))).ToList();
        var goals = new int[6];
        for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
        {
            var objective = step.Objectives[i];
            var count = rewarded.Count(q => CountsFor(objective, book.Find(q.Code)));
            goals[i] = (int)Math.Min(int.MaxValue, (long)count * objective.Count);
        }
        return goals;
    }

    /// <summary>
    /// The active gauge quests whose goals differ from the rewarded quests (after a reward, and at login for characters whose gauge
    /// counted quests before 2026-11-12 or not at all); the records with the new goals, already set in the log.
    /// </summary>
    public List<QuestRecord> GaugeRecount(int level)
    {
        var changed = new List<QuestRecord>();
        foreach (var record in quests.Values.Where(q => q.Status == Incomplete).ToList())
        {
            if (book.Find(record.Code) is not { Type: "Daevagauge" } gauge || CurrentStep(record) is not { UnifiedScore: > 0 } step || !IsGauge(step)) continue;
            var goals = GaugeGoals(gauge, step);
            if (goals.SequenceEqual(record.Goals)) continue;
            var next = Advance(record with { Goals = goals }, level);
            quests[record.Code] = next;
            changed.Add(next);
        }
        return changed;
    }

    /// <summary>The gauge's total points (its goals added up; 0 without a gauge): what UnifiedObjectiveScore conditions compare.</summary>
    public long GaugeScore() =>
        quests.Values.Where(q => book.Find(q.Code) is { Type: "Daevagauge" } g && QuestBook.RaceMatches(g, race)).Sum(q => q.Goals.Take(6).Sum(x => (long)x));

    /// <summary>An AcquirableConditions UnifiedObjectiveScore row: the gauge holds at least that many points.</summary>
    public bool ScoreReached(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var needed) && GaugeScore() >= needed;

    /// <summary>GM QuestCompleteStep: the current step's objectives are met.</summary>
    public QuestRecord? CompleteStep(string code, int level)
    {
        if (Get(code) is not { Status: Incomplete } record || CurrentStep(record) is not { } step) return null;
        var goals = new int[6];
        for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++) goals[i] = step.Objectives[i].Count;
        var steps = book.Find(code)!.Steps;
        var index = Array.FindIndex(steps, s => s.Order == step.Order);
        var next = index + 1 < steps.Length ? record with { Step = steps[index + 1].Order, Goals = new int[6] } : record with { Goals = goals, Status = Complete };
        return quests[code] = Advance(next, level);
    }

    /// <summary>GM QuestComplete: every step done, reward pending.</summary>
    public QuestRecord? CompleteAll(string code)
    {
        if (Get(code) is not { Status: Incomplete } record || book.Find(code) is not { } quest) return null;
        return quests[code] = record with { Step = quest.Steps.Length == 0 ? 1 : quest.Steps[^1].Order, Status = Complete };
    }

    /// <summary>
    /// Contents unlocked by finished quests (ContentsUnlock quest gates), as EContentsUnlockType names. A gate opens when the
    /// quest is REWARDED (official sends 0xe258 after the reward reply). Counting Complete too made the reward see nothing new,
    /// so 0xe258 never went out (live 2026-11-07: zenyc finished HQ1102011, the pet menu stayed locked until a relog).
    /// </summary>
    public IEnumerable<string> UnlockedContents() =>
        book.Unlocks.Where(u => (race == 2 ? u.Dark : u.Light) is { } code && quests.TryGetValue(code, out var q) && q.Status >= Rewarded)
            .Select(u => u.Type);
}
