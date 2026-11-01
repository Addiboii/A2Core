using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Lake Guardian (2701034): full groggy on spawn, 0xe005 depletes on hits, Stagger at 0.</summary>
public sealed class LakeGuardianStaggerTests
{
    const long Now = 1_760_300_000_000;
    const int RendingBlow = 11010000;

    static async Task<(GameSession Session, Target Boss)> SessionOnLakeBossAsync()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.Teleport(210016, new Vector3d(7309.9, -6974.4, 84.0), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        var boss = session.Targets!.Values.Single(t => t.NpcId == 2701034);
        boss.Immune = false;
        boss.Relationship = 6;
        if (boss.Dead) boss.Hp = boss.HpMax;
        return (session, boss);
    }

    [Fact]
    public async Task Lake_guardian_groggy_depletes_on_hits_and_breaks_into_stagger()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var (session, boss) = await SessionOnLakeBossAsync();
        Assert.True(StaggerGaugeCombat.HasBossGauge(boss));
        Assert.Equal(100, StaggerGaugeCombat.WireGroggyEnergy(boss));
        Assert.False(boss.Dead);
        Assert.Equal(0, boss.OwnerKey);

        const int sampleHit = 100;
        var dmg = StaggerGaugeCombat.GaugeDamage(data, RendingBlow, boss, sampleHit);
        Assert.True(dmg > 3);
        var castAt = Now + 20_000;
        var hit = StaggerGaugeCombat.AddGaugeDamage(boss, dmg, session.PlayerKey, castAt, session, firstRow: true);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0xe005);
        Assert.Equal(dmg, boss.StaggerGauge);
        Assert.Equal(100 - dmg, StaggerGaugeCombat.WireGroggyEnergy(boss));

        var remaining = StaggerGaugeCombat.DefaultGaugeMax - boss.StaggerGauge;
        var breakReplies = StaggerGaugeCombat.AddGaugeDamage(boss, remaining, session.PlayerKey, castAt + 500, session, firstRow: true);
        Assert.Contains(breakReplies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Contains(breakReplies, p => p.SequenceEqual(GamePackets.UpdateGroggyInfo(boss.Key, 100, 0, activated: true)));
        Assert.True(boss.StaggerUntil > castAt);
        Assert.True(boss.Held);
    }
}
