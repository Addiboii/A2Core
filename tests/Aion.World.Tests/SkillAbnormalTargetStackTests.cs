using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Skill effect abnormals: enemy rows on targets only, caster rows on self, AbnormalTable renew stacking.</summary>
public sealed class SkillAbnormalTargetStackTests
{
    const long Now = 1_760_010_000_000;
    const int RendingBlow = 11_012_350;
    const int MockingBlade = 11_290_000;
    const int WardingStrike = 12_350_000;

    static byte[] Cast(int skillId, int targetKey) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(new Vector3d(100, 200, 300)).U8(9).ToArray();

    static async Task<(GameSession Session, Target Mob)> SetupAsync(string className)
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == className);
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Abn").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var at = session.Position!.Value;
        var mob = map[880_401] = new Target
        {
            Key = 880_401, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 600 }, Direction = 0, Radius = 100, RunSpeed = 600, WalkSpeed = 200,
        };
        return (session, mob);
    }

    static Task<(GameSession Session, Target Mob)> SetupGladiatorAsync() => SetupAsync("Gladiator");

    static int AbnormalAddCount(IEnumerable<byte[]> packets) =>
        packets.Count(p => GameSessionTestHelpers.Op(p) == 0x382a);

    static int AbnormalChangeCount(IEnumerable<byte[]> packets) =>
        packets.Count(p => GameSessionTestHelpers.Op(p) == 0x382b);

    static int AbnormalPacketKey(byte[] packet) => (int)new PacketReader(packet) { Offset = 2 }.Varint();

    [Fact]
    public void Skill_areas_split_mocking_blade_keeps_enemy_debuffs_on_target_only()
    {
        var area = SkillAreas.For(MockingBlade);
        Assert.NotNull(area);
        Assert.DoesNotContain(area!.CasterDebuffRows, r => r.Abnormal == 112900001);
        Assert.Contains(area.TargetDebuffRows, r => r.Abnormal == 100000321);
    }

    [Fact]
    public void Basic_attack_skill_exports_no_caster_debuff_rows()
    {
        Assert.False(SkillAreas.For(RendingBlow)?.CasterDebuffRows.Any() ?? false);
    }

    [Fact]
    public async Task Basic_attack_does_not_add_caster_abnormals_after_the_hit()
    {
        var (session, mob) = await SetupGladiatorAsync();
        var before = session.ActiveBuffs.Count;
        await session.HandleAsync(Cast(RendingBlow, mob.Key), Now + 10_000);
        await session.TickAsync(Now + 12_000);
        Assert.Equal(before, session.ActiveBuffs.Count);
    }

    [Fact]
    public void Basic_attack_chain_skills_are_detected_for_official_self_abnormal_rule()
    {
        var data = GameData.Embedded;
        Assert.True(data.IsBasicAttackChainSkill(14_020_000));
        Assert.True(data.IsBasicAttackChainSkill(14_030_000));
        Assert.True(data.IsBasicAttackChainSkill(14_040_000));
        Assert.True(data.IsBasicAttackChainSkill(14_022_350));
        Assert.False(data.IsBasicAttackChainSkill(12_350_000));
        Assert.False(data.IsBasicAttackChainSkill(11_290_000));
    }

    [Fact]
    public async Task Official_ranger_basic_chain_hits_do_not_add_self_abnormals()
    {
        var (session, mob) = await SetupAsync("Ranger");
        var before = session.ActiveBuffs.Count;
        var data = GameSessionTestHelpers.LiveGameData;
        var t = Now + 20_000;
        var gap = SkillCooldown.MinCastGapMs + 50;
        foreach (var skill in new[] { 14_020_000, 14_030_000, 14_040_000 })
        {
            await session.HandleAsync(Cast(skill, mob.Key), t);
            var tick = await session.TickAsync(t + 2_000);
            Assert.Equal(before, session.ActiveBuffs.Count);
            Assert.Equal(0, AbnormalAddCount(tick));
            t += gap;
        }
    }

    const int MarkingShot = 14_090_000;
    const int PrecisionAbnormal = 140_900_001;

    [Fact]
    public async Task Skill_cond_grant_on_cast_adds_caster_precision_not_on_target()
    {
        var (session, mob) = await SetupAsync("Ranger");
        var markReplies = await session.HandleAsync(Cast(MarkingShot, mob.Key), Now + 10_000);
        await session.TickAsync(Now + 10_500);
        Assert.Contains(PrecisionAbnormal, session.ActiveBuffs);
        Assert.False(session.NpcHasAbnormalGroup(mob.Key, PrecisionAbnormal, null));
        Assert.Contains(markReplies, p => GameSessionTestHelpers.Op(p) == 0x382a && AbnormalPacketKey(p) == session.PlayerKey);
    }

    [Fact]
    public async Task Toggle_self_buff_recast_refreshes_one_instance_with_change_not_stack()
    {
        var (session, mob) = await SetupAsync("Ranger");
        await session.HandleAsync(Cast(MarkingShot, mob.Key), Now + 10_000);
        await session.TickAsync(Now + 10_500);
        Assert.Contains(PrecisionAbnormal, session.ActiveBuffs);
        await session.HandleAsync(Cast(MarkingShot, mob.Key), Now + 15_000);
        var second = await session.TickAsync(Now + 15_500);
        Assert.Contains(PrecisionAbnormal, session.ActiveBuffs);
        Assert.True(AbnormalChangeCount(second) >= 1 || AbnormalAddCount(second) == 0);
    }
}
