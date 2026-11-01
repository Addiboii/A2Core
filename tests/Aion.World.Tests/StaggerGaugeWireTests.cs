using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StaggerGaugeWireTests
{
    static ushort Op(byte[] p) => p.Length >= 2 ? (ushort)(p[0] | (p[1] << 8)) : (ushort)0;

    static Target Boss(string subType = "HeroMonster", int grade = 5) => new()
    {
        Key = 2001, Name = "Lake Guardian", Hp = 3000, HpMax = 3000, Position = Vector3d.Zero,
        NpcId = 2701034, Level = 5, SubType = subType, Grade = grade,
    };

    [Fact]
    public void Init_boss_sets_full_groggy_guard()
    {
        var t = Boss();
        StaggerGaugeCombat.InitBossGauge(t);
        Assert.Equal(StaggerGaugeCombat.DefaultGaugeMax, t.GroggyMax);
        Assert.Equal(t.GroggyMax, t.GroggyEnergy);
        Assert.Equal(0, t.StaggerGauge);
    }

    [Fact]
    public void Normal_monster_has_no_boss_gauge()
    {
        var t = Boss(subType: "NormalMonster", grade: 3);
        Assert.False(StaggerGaugeCombat.HasBossGauge(t));
        StaggerGaugeCombat.InitBossGauge(t);
        Assert.Equal(0, t.GroggyMax);
    }

    [Fact]
    public void Gauge_damage_drains_groggy_energy_and_sends_update()
    {
        var t = Boss();
        StaggerGaugeCombat.InitBossGauge(t);
        var replies = StaggerGaugeCombat.AddGaugeDamage(t, 3, 1, 1000, session: null, firstRow: true);
        Assert.Equal(3, t.StaggerGauge);
        Assert.Equal(t.GroggyMax - 3, t.GroggyEnergy);
        Assert.Single(replies);
        Assert.Equal(0xe005, Op(replies[0]));
    }

    [Fact]
    public void Gauge_break_sends_activated_groggy_and_stagger_abnormal()
    {
        var t = Boss();
        StaggerGaugeCombat.InitBossGauge(t);
        t.StaggerGauge = StaggerGaugeCombat.DefaultGaugeMax - 3;
        StaggerGaugeCombat.SyncGroggyFromGauge(t);
        var replies = StaggerGaugeCombat.AddGaugeDamage(t, 3, 1, 1000, session: null, firstRow: true);
        Assert.Contains(replies, p => Op(p) == 0x382a);
        Assert.Contains(replies, p => p.SequenceEqual(GamePackets.UpdateGroggyInfo(t.Key, 100, 0, activated: true)));
        Assert.True(t.StaggerUntil > 1000);
        Assert.Equal(StaggerGaugeCombat.DefaultGaugeMax, t.StaggerGauge);
    }

    [Fact]
    public void Lake_guardian_spawn_has_full_groggy_and_gladiator_hits_deplete_wire_energy()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = Combat.MapTargets(data, 210016).Values.Single(t => t.NpcId == 2701034);
        Assert.True(StaggerGaugeCombat.HasBossGauge(boss));
        Assert.Equal(StaggerGaugeCombat.DefaultGaugeMax, boss.GroggyEnergy);
        Assert.Equal(0, boss.StaggerGauge);
        var appear = StaggerGaugeCombat.NpcAppearPackets(boss.Appearance(), boss);
        Assert.Equal(0xe005, Op(appear[1]));
        Assert.Equal(100, StaggerGaugeCombat.WireGroggyEnergy(boss));
        const int sampleHit = 100;
        var dmg = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, sampleHit);
        Assert.True(dmg > 3);
        var hit = StaggerGaugeCombat.AddGaugeDamage(boss, dmg, 1, 1000, null, true);
        Assert.Single(hit);
        Assert.Equal(100 - dmg, StaggerGaugeCombat.WireGroggyEnergy(boss));
        Assert.Equal(GamePackets.UpdateGroggyInfo(boss.Key, 100, 100 - dmg), hit[0]);
    }

    [Fact]
    public void Stagger_tick_refills_groggy_bar()
    {
        var t = Boss();
        StaggerGaugeCombat.InitBossGauge(t);
        t.StaggerUntil = 500;
        t.StaggerGauge = 40;
        StaggerGaugeCombat.SyncGroggyFromGauge(t);
        var replies = StaggerGaugeCombat.StaggerTick(new Dictionary<int, Target> { [t.Key] = t }, 600);
        Assert.Equal(0, t.StaggerGauge);
        Assert.Equal(t.GroggyMax, t.GroggyEnergy);
        Assert.Single(replies);
        Assert.Equal(0xe005, Op(replies[0]));
    }

    [Fact]
    public void Npc_appear_includes_groggy_for_boss()
    {
        var t = Boss();
        StaggerGaugeCombat.InitBossGauge(t);
        var packets = StaggerGaugeCombat.NpcAppearPackets(t.Appearance(), t);
        Assert.Equal(2, packets.Count);
        Assert.Equal(0x3641, Op(packets[0]));
        Assert.Equal(0xe005, Op(packets[1]));
    }
}
