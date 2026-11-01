using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class Hq2101040LakeKillDebugTests
{
    const long Now = 1_760_002_100_000;

    [Fact]
    public async Task Lake_mobs_take_damage_on_kill_step_with_open_objective()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ2101040", default);
        for (var i = 0; i < 3; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101040", default);
        Assert.Equal(4, session.Quests!.Get("HQ2101040")!.Step);
        Assert.Contains(2404104, session.Quests!.OpenKillNpcIds());

        var spawn = GameData.Embedded.Spawns(1100).First(s => s.NpcId == 2404104);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        await MapEventScenarioHelpers.SyncPositionAsync(session, at, Now + 100);
        await session.TickAsync(Now + 200);

        var prey = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2404104 && !t.Dead);
        Assert.NotNull(prey);
        prey!.Hp = 1;
        prey.Immune = false;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var castAt = Now + 300;
        var cast = await MapEventScenarioHelpers.CombatCastAsync(session, skill, prey.Key, prey.Position, castAt);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == 0x3800 || GameSessionTestHelpers.Op(p) == 0x3804);
        Assert.True(prey.Dead || session.Quests!.Get("HQ2101040")!.Goals.Any(g => g > 0),
            $"prey dead={prey.Dead} goals={string.Join(',', session.Quests!.Get("HQ2101040")!.Goals)} weapon={session.EquippedWeapon} skill={skill}");
    }
}
