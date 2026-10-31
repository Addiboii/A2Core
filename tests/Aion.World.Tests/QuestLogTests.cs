using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestLogTests
{
    static QuestBook SampleBook()
    {
        var opening = new QuestDefinition(
            Id: 1100010, Title: "Open", Type: "Hero", Race: "Light", Level: 1, Map: 1000,
            Acquire: "None", StartNpc: null, Complete: "Auto", FinishNpc: null, GiveUp: false,
            Prerequisites: [], Previous: [], Next: "HQ1101010",
            Steps: [new QuestStep(1, [new QuestObjective("ClearMapEvent", Target: "Ev", Count: 1)], Id: 110001001)],
            Rewards: new QuestRewards(Exp: 1, Kina: 0));
        var next = new QuestDefinition(
            Id: 1101010, Title: "Next", Type: "Hero", Race: "Light", Level: 1, Map: 1000,
            Acquire: "Auto", StartNpc: null, Complete: "Auto", FinishNpc: null, GiveUp: false,
            Prerequisites: ["HQ1100010"], Previous: ["HQ1100010"], Next: null,
            Steps: [new QuestStep(1, [new QuestObjective("AskNpc", Npc: 10, Count: 1)], Id: 110101001)],
            Rewards: null);
        return new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["HQ1100010"] = opening,
            ["HQ1101010"] = next,
        });
    }

    [Fact]
    public void CanAccept_requires_prerequisite_rewarded()
    {
        var book = SampleBook();
        var log = new QuestLog(book, race: 1);
        Assert.False(log.CanAccept("HQ1101010", level: 1));
        log.Set(QuestLog.New("HQ1100010", book.Find("HQ1100010")!) with { Status = QuestLog.Rewarded });
        Assert.True(log.CanAccept("HQ1101010", level: 1));
    }

    [Fact]
    public void UnifiedObjectiveScore_sums_recommended_level_of_rewarded_quests()
    {
        var rewarded = new QuestDefinition(
            Id: 1, Title: "A", Type: "Hero", Race: "Light", Level: 10, Map: 0,
            Acquire: "Auto", StartNpc: null, Complete: "Auto", FinishNpc: null, GiveUp: false,
            Prerequisites: [], Previous: [], Next: null,
            Steps: [new QuestStep(1, [new QuestObjective("PCLevel", Count: 1)])], Rewards: null,
            RecommendedLevel: 320);
        var book = new QuestBook(new Dictionary<string, QuestDefinition> { ["Q1"] = rewarded });
        var log = new QuestLog(book, race: 1);
        Assert.Equal(0, log.UnifiedObjectiveScore());
        log.Set(QuestLog.New("Q1", rewarded) with { Status = QuestLog.Rewarded });
        Assert.Equal(320, log.UnifiedObjectiveScore());
    }

    [Fact]
    public void AutoAcquirable_lists_auto_quests_when_prereqs_met()
    {
        var book = SampleBook();
        var log = new QuestLog(book, race: 1);
        Assert.Empty(log.AutoAcquirable(1));
        log.Set(QuestLog.New("HQ1100010", book.Find("HQ1100010")!) with { Status = QuestLog.Rewarded });
        Assert.Contains("HQ1101010", log.AutoAcquirable(1));
    }

    [Fact]
    public void ClearedMapEvent_advances_matching_objective()
    {
        var book = SampleBook();
        var log = new QuestLog(book, race: 1);
        log.Set(QuestLog.New("HQ1100010", book.Find("HQ1100010")!));
        var changed = log.ClearedMapEvent("Ev", level: 1);
        Assert.Single(changed);
        Assert.Equal(QuestLog.Complete, changed[0].Status);
    }

    [Fact]
    public void EnterVolumePC_respects_objective_value_in_predicate()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["V"] = new QuestDefinition(1, "T", "Hero", "Light", 1, 1000, "Auto", null, "Auto", null, false, [], [], null,
                [new QuestStep(1, [new QuestObjective("EnterVolumePC", Target: "Trig", Value: 500, Count: 1)])], null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("V", book.Find("V")!));
        var obj = log.OpenAreaObjectives().Single();
        Assert.Equal(500, obj.Value);
        Assert.Empty(log.Entered(_ => false, 1));
        Assert.Single(log.Entered(o => o.Target == "Trig", 1));
    }

    [Fact]
    public void UsedObject_and_Killed_update_goals()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["O"] = new QuestDefinition(1, "T", "Hero", "Light", 1, 1000, "Auto", null, "Auto", null, false, [], [], null,
                [new QuestStep(1, [
                    new QuestObjective("UseEnvObj", Envs: [1200530], Count: 1),
                    new QuestObjective("KillNpc", Npcs: [2101164], Count: 2),
                ])], null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("O", book.Find("O")!));
        log.UsedObject(1200530, 1);
        Assert.Equal(1, log.Get("O")!.Goals[0]);
        log.Killed(2101164, 1);
        log.Killed(2101164, 1);
        Assert.Equal(QuestLog.Complete, log.Get("O")!.Status);
    }

    [Fact]
    public void PendingKillNpcIds_only_future_steps()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["W"] = new QuestDefinition(1101018, "T", "Hero", "Light", 1, 1000, "Npc", 1, "Npc", 1, false, [], [], null,
                [
                    new QuestStep(1, [new QuestObjective("AskNpc", Npc: 1, Count: 1)]),
                    new QuestStep(2, [new QuestObjective("KillNpc", Npcs: [2101164], Count: 4)]),
                    new QuestStep(4, [new QuestObjective("KillNpc", Npcs: [2290017], Count: 3)]),
                ], null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("W", book.Find("W")!) with { Step = 2 });
        Assert.Contains(2290017, log.PendingKillNpcIds());
        Assert.Contains(2101164, log.OpenKillNpcIds());
        log.Set(log.Get("W")! with { Step = 4, Goals = new int[6] });
        Assert.DoesNotContain(2290017, log.PendingKillNpcIds());
        Assert.Contains(2290017, log.OpenKillNpcIds());
    }

    [Fact]
    public void ProtectedTalkNpcIds_covers_quest_npcs()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["W"] = new QuestDefinition(1, "T", "Hero", "Light", 1, 1000, "Npc", 10, "Npc", 11, false, [], [], null,
                [new QuestStep(1, [new QuestObjective("AskNpc", Npc: 2931095, Count: 1)], Npc: 2931047)], null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("W", book.Find("W")!));
        var ids = log.ProtectedTalkNpcIds().ToHashSet();
        Assert.Contains(10, ids);
        Assert.Contains(11, ids);
        Assert.Contains(2931095, ids);
        Assert.Contains(2931047, ids);
    }

    [Fact]
    public void OpenKillNpcIds_lists_incomplete_kill_objectives_only()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["K"] = new QuestDefinition(1, "T", "Hero", "Light", 1, 1000, "Auto", null, "Auto", null, false, [], [], null,
                [new QuestStep(1, [new QuestObjective("KillNpc", Npcs: [100, 101], Count: 3)])], null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("K", book.Find("K")!));
        Assert.Equal(new[] { 100, 101 }, log.OpenKillNpcIds().OrderBy(x => x).ToArray());
        log.Killed(100, 1);
        Assert.Equal(new[] { 100, 101 }, log.OpenKillNpcIds().OrderBy(x => x).ToArray());
        log.Killed(100, 1);
        log.Killed(100, 1);
        Assert.Empty(log.OpenKillNpcIds());
        Assert.Equal(QuestLog.Complete, log.Get("K")!.Status);
    }

    [Fact]
    public void ClearQuestExplorationType_requires_rewarded_exploration_subtype()
    {
        var seal = new QuestDefinition(1, "Seal site", "Exploration", "Light", 1, 1010, "Auto", null, "UI", null, false, [], [], null,
            [new QuestStep(1, [new QuestObjective("ClearMapEvent", Target: "Seal_Test_MapEvent", Count: 1)])], null);
        var gauge = new QuestDefinition(1702010, "Gauge", "Daevagauge", "Light", 1, 0, "Auto", null, "None", null, false,
            ["AQ1602000"], [], null,
            [new QuestStep(1, [
                new QuestObjective("ClearQuestExplorationType", Target: "Seal", Count: 1),
            ])], null);
        Assert.Equal("Seal", QuestBook.ExplorationSubtype(seal));
        var book = new QuestBook(new Dictionary<string, QuestDefinition> { ["EQ1"] = seal, ["GQ1702010"] = gauge });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("GQ1702010", gauge));
        log.Advance(log.Get("GQ1702010")!, 1);
        Assert.Equal(QuestLog.Incomplete, log.Get("GQ1702010")!.Status);
        log.Set(QuestLog.New("EQ1", seal) with { Status = QuestLog.Complete });
        var advanced = log.Advance(log.Get("GQ1702010")!, 1);
        log.Set(advanced);
        Assert.Equal(QuestLog.Complete, log.Get("GQ1702010")!.Status);
    }

    [Fact]
    public void Recheck_advances_ClearQuest_when_dependency_reaches_complete()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("HQ1102070", book.Find("HQ1102070")!) with { Step = 7 });
        log.Set(QuestLog.New("EQ1820001", book.Find("EQ1820001")!) with { Status = QuestLog.Complete, Step = 1, Goals = [1, 0, 0, 0, 0, 0] });
        var changed = log.Recheck(13);
        Assert.Contains(changed, q => q.Code == "HQ1102070");
        Assert.True(log.Get("HQ1102070")!.Step >= 8);
    }

    [Fact]
    public void OpenClearQuestFor_false_when_gate_quest_already_complete()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("HQ1102070", book.Find("HQ1102070")!) with { Step = 7 });
        log.Set(QuestLog.New("EQ1820001", book.Find("EQ1820001")!) with { Status = QuestLog.Complete, Step = 1 });
        Assert.False(log.OpenClearQuestFor("EQ1820001"));
    }

    [Fact]
    public void OpenClearQuestFor_true_while_gate_quest_incomplete()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("HQ1102070", book.Find("HQ1102070")!) with { Step = 7 });
        log.Set(QuestLog.New("EQ1820001", book.Find("EQ1820001")!));
        Assert.True(log.OpenClearQuestFor("EQ1820001"));
    }

    [Fact]
    public void CompleteStep_jumps_to_next_step()
    {
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
        {
            ["Q"] = new QuestDefinition(1, "T", "Hero", "Light", 1, 1000, "Npc", 1, "Npc", 1, false, [], [], null,
                [new QuestStep(1, [new QuestObjective("AskNpc", Npc: 1, Count: 1)], Id: 101),
                 new QuestStep(2, [new QuestObjective("AskNpc", Npc: 2, Count: 1)], Id: 102)],
                null),
        });
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("Q", book.Find("Q")!));
        var stepped = log.CompleteStep("Q", level: 1);
        Assert.NotNull(stepped);
        Assert.Equal(2, stepped!.Step);
    }
}
