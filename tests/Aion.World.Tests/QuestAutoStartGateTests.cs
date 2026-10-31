using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Quests with AcquireType Npc must not enter the journal from chain reward or map arrival; map events must not reuse stale runs.
/// </summary>
public sealed class QuestAutoStartGateTests
{
    const long Now = 1_760_006_100_000;

    [Fact]
    public async Task HQ2101020_reward_does_not_auto_accept_Shadow_Forest()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ2101020", 2101020, 50, QuestLog.Complete, 2, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 3);
        Assert.Null(session.Quests!.Get("HQ2101030"));

        var packets = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32(50).I32(-1).ToArray(), Now + 10);
        Assert.Equal(QuestLog.Rewarded, session.Quests.Get("HQ2101020")!.Status);
        Assert.Null(session.Quests.Get("HQ2101030"));
        Assert.DoesNotContain(packets, p => GameSessionTestHelpers.Op(p) == 0x8d11);
    }

    [Fact]
    public async Task HQ2101030_CelviceTalk_0x9230_accepts_quest_id_or_uid()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 1000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        var record = session.Quests!.Get("HQ2101030")!;
        Assert.Equal(2, record.Step);

        var byQuestId = await session.HandleAsync(
            new PacketWriter().U16(0x9230).U8(1).I32(2101030).I32(0).ToArray(), Now + 1100);
        Assert.True(GameSessionTestHelpers.HasOpcode(byQuestId, 0x9231));
        Assert.Equal(3, session.Quests.Get("HQ2101030")!.Step);

        await session.QuestCommandAsync("QuestSetStep", "HQ2101030", default, 2);
        var byUid = await session.HandleAsync(
            new PacketWriter().U16(0x9230).U8(1).I32((int)session.Quests.Get("HQ2101030")!.Uid).I32(0).ToArray(), Now + 1200);
        Assert.True(GameSessionTestHelpers.HasOpcode(byUid, 0x9231));
        Assert.Equal(3, session.Quests.Get("HQ2101030")!.Step);
    }

    [Fact]
    public async Task HQ004_map_event_dropped_when_quest_step_no_longer_matches()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 2000, race: 2, level: 4, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 4; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        Assert.Equal(5, session.Quests!.Get("HQ2101040")!.Step);

        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(110007);
        session.EnterDungeonById(dungeonId, Now + 2100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2110);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ004_MapEvent", Now + 2200, attempts: 20);
        Assert.NotNull(run);

        await session.QuestCommandAsync("QuestSetStep", "HQ2101040", default, 4);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2300);

        Assert.Null(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "World_D_Starter_HQ004_MapEvent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task HQ004_map_event_does_not_start_on_load_at_step_4()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 3000, race: 2, level: 4, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 3; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        Assert.Equal(4, session.Quests!.Get("HQ2101040")!.Step);

        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(110007);
        var moved = session.EnterDungeonById(dungeonId, Now + 3100);
        Assert.Empty(moved);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 3110);

        Assert.Null(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "World_D_Starter_HQ004_MapEvent", StringComparison.OrdinalIgnoreCase)));
    }
}
