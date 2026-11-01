using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Verteron hero segment (HQ1102020 Odyle, HQ1102230 Gravi) after #106 walk-in / layer fixes land.</summary>
public sealed class Hq110VerteronSegmentTests
{
    const long Now = 1_760_070_000_000;

    [Fact]
    public async Task HQ1102020_step2_odyle_env_and_kalgol_kills()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 11);
        session.NoCool = true;
        await session.QuestCommandAsync("QuestAdd", "HQ1102020", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102020", default);
        var record = session.Quests!.Get("HQ1102020")!;
        Assert.Equal(2, record.Step);
        var step = session.Quests!.CurrentStep(record)!;
        Assert.Equal("UseEnvObj", step.Objectives[0].Type);
        Assert.Equal("KillNpc", step.Objectives[1].Type);
        var okEnv = await CampaignPlaythroughHelpers.UseQuestEnvForTestAsync(session, "HQ1102020", step.Objectives[0], Now + 100);
        Assert.True(okEnv, $"env goal={record.Goals[0]}");
        record = session.Quests!.Get("HQ1102020")!;
        var killIds = step.Objectives[1].Npcs!;
        var okKill = await CampaignPlaythroughHelpers.KillQuestNpcsForTestAsync(session, "HQ1102020", Now + 5000, killIds, step.Objectives[1].Count, 1010);
        Assert.True(okKill, $"kill goals={string.Join(',', session.Quests!.Get("HQ1102020")!.Goals)}");
    }

    [Fact]
    public async Task HQ1102230_gravi_kill_objective_credits()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 120_000, race: 1, level: 24);
        session.NoCool = true;
        await session.QuestCommandAsync("QuestAdd", "HQ1102230", default);
        var step = session.Quests!.CurrentStep(session.Quests!.Get("HQ1102230")!)!;
        var ok = await CampaignPlaythroughHelpers.KillQuestNpcsForTestAsync(
            session, "HQ1102230", Now + 130_000, step.Objectives[0].Npcs!, step.Objectives[0].Count, 1010);
        var record = session.Quests!.Get("HQ1102230")!;
        Assert.True(ok, $"goals={string.Join(',', record.Goals)} step={record.Step}");
        Assert.True(record.Step >= 2, $"expected step 1 kill credit, step={record.Step}");
    }

    [Fact]
    public async Task HQ1102230_steps2_volume_and_step3_tent_map_event()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 120_000, race: 1, level: 24);
        session.NoCool = true;
        await session.QuestCommandAsync("QuestAdd", "HQ1102230", default);
        var record = session.Quests!.Get("HQ1102230")!;
        var step1 = session.Quests!.CurrentStep(record)!;
        var okKill = await CampaignPlaythroughHelpers.KillQuestNpcsForTestAsync(
            session, "HQ1102230", Now + 130_000, step1.Objectives[0].Npcs!, step1.Objectives[0].Count, 1010);
        Assert.True(okKill, $"step1 goals={string.Join(',', session.Quests!.Get("HQ1102230")!.Goals)}");
        record = session.Quests!.Get("HQ1102230")!;
        Assert.Equal(2, record.Step);
        var step2 = session.Quests!.CurrentStep(record)!;
        Assert.Equal("EnterVolumePC", step2.Objectives[0].Type);
        var okVol = await CampaignPlaythroughHelpers.EnterQuestVolumeForTestAsync(
            session, "HQ1102230", step2.Objectives[0], Now + 200_000);
        Assert.True(okVol, $"volume goal={record.Goals[0]} step={session.Quests!.Get("HQ1102230")!.Step}");
        record = session.Quests!.Get("HQ1102230")!;
        Assert.Equal(3, record.Step);
        var step3 = session.Quests!.CurrentStep(record)!;
        Assert.Equal("ClearMapEvent", step3.Objectives[0].Type);
        const string tentEvent = "World_L_A_Layer_TF_TentOfCochirun_MapEvent";
        var okEvent = await CampaignPlaythroughHelpers.ClearQuestMapEventForTestAsync(
            session, "HQ1102230", tentEvent, Now + 300_000);
        record = session.Quests!.Get("HQ1102230")!;
        Assert.True(okEvent || record.Goals[0] >= 1 || record.Status >= QuestLog.Complete,
            $"map event cleared={okEvent} goals={string.Join(',', record.Goals)} status={record.Status}");
    }
}
