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
    [property: JsonPropertyName("chance")] int Chance = 0);

public sealed record QuestStep(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("objectives")] QuestObjective[] Objectives,
    [property: JsonPropertyName("npc")] int? Npc = null,
    [property: JsonPropertyName("id")] int Id = 0);

public sealed record QuestRewards(
    [property: JsonPropertyName("exp")] long Exp = 0,
    [property: JsonPropertyName("kina")] long Kina = 0,
    [property: JsonPropertyName("items")] long[][]? Items = null);

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
    [property: JsonPropertyName("conditions")] QuestCondition[]? Conditions = null);

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
        Quests = quests;
        Unlocks = unlocks ?? [];
        ById = quests.ToDictionary(q => q.Value.Id, q => q.Key);
        stepsById = quests.Values.SelectMany(q => q.Steps.Where(s => s.Id != 0).Select(s => (s.Id, Step: (q.Id, s.Order))))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Step);
    }

    readonly Dictionary<int, (int Quest, int Step)> stepsById;

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
        if (book.Find(code) is not { } quest || quests.ContainsKey(code)) return false;
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

    /// <summary>After a quest finished: steps waiting on other quests (ClearQuest, ClearQuestType) are re-evaluated.</summary>
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

    /// <summary>A player of the other race killed (the Abyss): KillPC and KillPCAbyssGrade objectives (the grade list is not matched).</summary>
    public List<QuestRecord> KilledPlayer(int level) => Progress(o => o.Type is "KillPC" or "KillPCAbyssGrade", level: level);

    /// <summary>A map event cleared (an instance layer's boss): ClearMapEvent objectives naming it.</summary>
    public List<QuestRecord> ClearedMapEvent(string mapEvent, int level) =>
        Progress(o => o.Type == "ClearMapEvent" && o.Target == mapEvent, level: level);

    public List<QuestRecord> Talked(int npcId, int level) =>
        Progress(o => o.Type is "AskNpc" or "CloseNpc" && o.Npc == npcId, level: level);

    /// <summary>Using a quest object (UseEnvObj names EnvObjData ids).</summary>
    public List<QuestRecord> UsedObject(int env, int level) =>
        Progress(o => o.Type == "UseEnvObj" && (o.Envs ?? []).Contains(env), level: level);

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
        var required = step.Objectives.Select((o, i) => (o, i)).Where(x => !x.o.Optional && x.i < 6).ToList();
        if (required.Count == 0) return false;
        return required.All(x => x.o.Type switch
        {
            "PCLevel" => level >= (x.o.Level ?? int.MaxValue),
            "ClearQuest" => x.o.Target is { } code && Done(code),
            "ClearQuestType" => quests.Values.Count(q => q.Status >= Complete && book.Find(q.Code)?.Type == x.o.Target) >= x.o.Count,
            _ => goals[x.i] >= x.o.Count,
        });
    }

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

    /// <summary>Contents unlocked by finished quests (ContentsUnlock quest gates), as EContentsUnlockType names.</summary>
    public IEnumerable<string> UnlockedContents() =>
        book.Unlocks.Where(u => (race == 2 ? u.Dark : u.Light) is { } code && Done(code)).Select(u => u.Type);
}
