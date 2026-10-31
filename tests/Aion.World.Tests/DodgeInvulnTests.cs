using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DodgeInvulnTests
{
    const long Now = 1_760_010_000_000;
    static readonly Vector3d At = new(100, 200, 300);

    static byte[] DodgeCast() =>
        new PacketWriter().U16(0x3800).U16(16).I32(GameSession.CombatDodge).U8(2).Varint(1).F32(0).Vec3(At).U8(9).ToArray();

    [Fact]
    public void Dodge_abnormal_200_grants_negative_skill_immunity_on_the_buff()
    {
        var combat = new AbnormalCombat.BuffCombat();
        foreach (var (_, type, values) in AbnormalSpecs.Info(GameSession.DodgeAbnormal)!.Effects)
            AbnormalCombat.ApplyRow(0, type, values, combat, GameSession.DodgeAbnormalMs, Now, new Random(1), 5000);
        Assert.True(combat.ImmuneDamage);
    }

    [Fact]
    public async Task Dodge_i_frames_block_npc_damage()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 45);
        session.Teleport(1010, At, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var hp = session.Resource!.Hp;
        await session.HandleAsync(DodgeCast(), Now + 100);
        Assert.True(session.DodgeInvulnerable(Now + 150));
        session.NpcHitsPlayer(500, Now + 150, killerKey: Combat.TrainingDummyKey);
        Assert.Equal(hp, session.Resource.Hp);
    }

    [Fact]
    public async Task Dodge_i_frames_block_npc_debuffs()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now + 10_000, level: 45);
        session.Teleport(1010, At, Now + 10_000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10_000);
        await session.HandleAsync(DodgeCast(), Now + 10_100);
        var blocked = session.TryApplyPlayerDebuff(18017800, 5000, Now + 10_150);
        Assert.Contains(blocked, p => GameSessionTestHelpers.Op(p) == 0x382d);
    }
}
