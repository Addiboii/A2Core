using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1102070 Illusion Cavaliers: Ashar in Dream Crossroad, recovery-room wake, Nania on Verteron 1010.
/// </summary>
public sealed class IllusionCavaliersQuestTests
{
    const long Now = 1_760_004_200_000;

    static async Task<GameSession> InDreamCrossroadAsync(FakeCharacterStore store, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 13);
        await session.QuestCommandAsync("QuestAdd", "HQ1102070", default);
        var moved = session.EnterDungeonById(200001, now + 100);
        Assert.NotEmpty(moved);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 200);
        await session.TickAsync(now + 250);
        Assert.Equal(200001, session.MapId);
        return session;
    }

    [Fact]
    public void Step_2_resolves_quest_teleport_to_Nania_at_recovery_room()
    {
        var quest = QuestBook.Embedded.Find("HQ1102070")!;
        var step = quest.Steps.First(s => s.Order == 2);
        var dest = QuestTeleportResolver.Resolve(quest, step, GameData.Embedded)!;
        Assert.Equal(QuestTeleportKind.Field, dest.Kind);
        Assert.Equal(1010, dest.Map);
        Assert.Equal(2930000, dest.Npc);
    }

    [Fact]
    public async Task Step_1_spawns_Ashar_without_lobby_trigger()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadAsync(store, Now);
        _ = session.QuestWorldGatingForTests(Now + 300);
        Assert.Contains(session.Targets!.Values, t => t.NpcId == 2970018 && !t.Dead);
    }

    [Fact]
    public async Task Completing_step_1_leaves_Dream_Crossroad_for_recovery_room()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadAsync(store, Now + 10_000);
        _ = session.QuestWorldGatingForTests(Now + 10_100);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102070", default);
        Assert.Equal(2, session.Quests!.Get("HQ1102070")!.Step);
        var replies = session.QuestWorldGatingForTests(Now + 10_200);
        Assert.True(replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3621) || session.MapId == 1010);
        if (session.MapId == 200001)
        {
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10_300);
            await session.TickAsync(Now + 10_350);
        }
        Assert.Equal(1010, session.MapId);
        Assert.Equal(0, session.InstanceKey);
    }

    [Fact]
    public async Task Stuck_on_step_2_in_Dream_Crossroad_self_heals_to_recovery()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadAsync(store, Now + 20_000);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102070", default);
        Assert.Equal(2, session.Quests!.Get("HQ1102070")!.Step);
        session.Teleport(200001, new Vector3d(10, 9300, 90), Now + 20_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_150);
        session.QuestWorldGatingForTests(Now + 20_200);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_250);
        Assert.Equal(1010, session.MapId);
    }
}
