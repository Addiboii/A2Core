using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class FieldCombatKillTests
{
    const long Now = 1_760_080_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public async Task Basic_attack_on_gravi_with_HQ1102230_active()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 10_000, race: 1, level: 24);
        session.NoCool = true;
        await session.QuestCommandAsync("QuestAdd", "HQ1102230", default);
        const int map = 1010;
        session.Teleport(map, GameSessionTestHelpers.MapStartOrDefault(map), Now + 10_010);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10_020);
        _ = session.QuestWorldGatingForTests(Now + 10_030);
        await session.TickAsync(Now + 10_040);
        var prey = session.Targets!.Values.First(t => t.NpcId == 2101125 && !t.Dead);
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        await MapEventScenarioHelpers.SyncPositionAsync(session, prey.Position with { X = prey.Position.X + 100 }, Now + 10_100);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        prey.Immune = false;
        prey.Relationship = 6;
        prey.Hp = 1;
        var castAt = Now + 10_200;
        await session.HandleAsync(Cast(skillId, prey.Key, prey.Position), castAt);
        var replies = await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        Assert.True(replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3804), "expected 0x3804 hit");
        var record = session.Quests!.Get("HQ1102230")!;
        Assert.True(prey.Dead || record.Step > 1 || record.Goals.Any(g => g > 0), $"hp={prey.Hp} step={record.Step} goals={string.Join(',', record.Goals)}");
    }

    [Fact]
    public async Task Basic_attack_on_Verteron_field_mob_schedules_hits_and_drops_hp()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 11);
        session.NoCool = true;
        const int map = 1010;
        session.Teleport(map, GameSessionTestHelpers.MapStartOrDefault(map), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.TickAsync(Now + 30);
        _ = session.QuestWorldGatingForTests(Now + 40);
        var prey = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2100002 && !t.Dead)
            ?? session.Targets!.Values.First(t => !t.Dead && t.Hostile);
        var hp0 = prey.Hp;
        Assert.True(session.Targets!.ContainsKey(prey.Key));
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        await MapEventScenarioHelpers.SyncPositionAsync(session, prey.Position with { X = prey.Position.X + 100 }, Now + 100);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        var castAt = Now + 200;
        var cast = await session.HandleAsync(Cast(skillId, prey.Key, prey.Position), castAt);
        var tick = await session.TickAsync(castAt + MapEventScenarioHelpers.SkillHitsLandMs(skillId) + 200);
        var all = cast.Concat(tick).ToList();
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        Assert.True(all.Any(p => GameSessionTestHelpers.Op(p) == 0x3804), "expected 0x3804 hit");
        Assert.True(prey.Hp < hp0 || prey.Dead, $"hp {hp0} -> {prey.Hp}");
    }
}
