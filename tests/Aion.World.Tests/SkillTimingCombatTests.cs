using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillTimingCombatTests
{
    static readonly long Now = 1_700_000_000_000;

    [Fact]
    public async Task Skill_damage_lands_after_hit_delay_not_on_cast()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(4));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var hp = dummy.Hp;
        const int skillId = 11020000;
        var castAt = Now + 1000;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(dummy.Key).F32(0)
            .Vec3(dummy.Position).U8(9).ToArray();
        var immediate = await session.HandleAsync(cast, castAt);
        Assert.Equal(hp, dummy.Hp);
        Assert.DoesNotContain(immediate, p => GameSessionTestHelpers.Op(p) == 0x3804);
        var landed = await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x3804);
        Assert.True(dummy.Hp < hp);
    }
}
