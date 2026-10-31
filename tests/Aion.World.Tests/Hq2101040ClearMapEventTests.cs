using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class Hq2101040ClearMapEventTests
{
    const long Now = 1_760_002_200_000;

    [Fact]
    public async Task HQ004_layer_clear_map_event_counts_for_step_5()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 4, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        Assert.Equal(5, session.Quests!.Get("HQ2101040")!.Step);

        var vol = MapEventTables.Embedded.Volume(110007)!;
        var cx = vol.Points.Average(p => p[0]);
        var cy = vol.Points.Average(p => p[1]);
        var z = (vol.ZMin + vol.ZMax) / 2;
        session.Teleport(1100, new Vector3d(cx - 4000, cy, z), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(new Vector3d(cx, cy, z)), Now + 500);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 510);
        if (session.MapId != 110007)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(110007);
            session.EnterDungeonById(dungeonId, Now + 520);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 530);
        }

        Assert.Equal(110007, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ004_MapEvent", Now + 600, attempts: 24);
        Assert.NotNull(run);
        var finished = await MapEventScenarioHelpers.PlayDarkHq004ScriptAsync(session, run!, Now + 700, null);
        await MapEventScenarioHelpers.FlushQuestMapEventClearsAsync(session, Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5200);
        var q = session.Quests!.Get("HQ2101040")!;
        Assert.True(finished || run!.Status == MapEventRun.Complete || q.Step > 5 || q.Goals[4] > 0,
            $"run={run!.Status} step={q.Step} goals={string.Join(',', q.Goals)}");
    }

    [Fact]
    public async Task HQ004_clear_still_exits_the_layer_after_the_quest_step_moves_on()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 4, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(110007);
        session.EnterDungeonById(dungeonId, Now + 20);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 30);
        Assert.Equal(110007, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ004_MapEvent", Now + 600, attempts: 24);
        Assert.NotNull(run);
        // Play the script up to its last step (AskNpc Elvida), as the live run did.
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        var t = Now + 700;
        for (var tick = 0; tick < 180 && !(run!.StepOrder == 6 && run.OpenAskNpcIds().Any()); tick++, t += 1000)
        {
            await session.TickAsync(t);
            foreach (var (_, objective) in run.Open.ToList())
                if (objective.Type is "CompleteNpcTalk" or "CompleteCutscene" or "CloseGuide" or "CloseNote")
                    await MapEventScenarioHelpers.ReportClientMapObjectiveAsync(session, run, objective, t, null);
            await MapEventScenarioHelpers.KillHostilesOnMapAsync(session, skillId, t, null, "M_D0_HQ004_DaevaAss");
            if (run.StepOrder == 5)
                _ = await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, capture: null);
        }
        Assert.Equal(6, run!.StepOrder);
        // The talk: DungeonClear pays the quest at once (its step moves past the event's start condition), then Delay 2000, MapExit.
        run.Talked(2951086, "N_D_Starter_HQ004_Elvida_01", null, false, t);
        for (var dt = 0L; dt <= 3000 && session.MapId == 110007; dt += 200)
            await session.TickAsync(t + dt);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 3100);
        Assert.True(session.Quests!.Get("HQ2101040")!.Step > 5);
        Assert.True(session.MapId != 110007, $"map={session.MapId} run={run.Status} step={run.StepOrder} quest={session.Quests!.Get("HQ2101040")!.Step}");
    }
}
