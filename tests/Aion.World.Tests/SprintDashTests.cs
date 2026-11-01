using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Shift sprint (0x3704) must not start NormalDodge / 0x3802; the client auto-sends CombatDodge right after sprint on.</summary>
public sealed class SprintDashTests
{
    static readonly long Now = 1_760_004_000_000;

    static byte[] SprintOn() => new PacketWriter().U16(0x3704).I64(1).ToArray();

    static byte[] DodgeCast() =>
        new PacketWriter().U16(0x3800).U16(16).I32(GameSession.CombatDodge).U8(2).Varint(1).F32(0)
            .Vec3(new Vector3d(100, 0, 0)).U8(9).ToArray();

    [Fact]
    public async Task Sprint_auto_dodge_accepts_normal_dodge_variant_zero_bisect()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var spBefore = session.Resource!.Sp;
        var on = await session.HandleAsync(SprintOn(), Now);
        Assert.Equal([0x8d03, 0x3741], on.Select(p => GameSessionTestHelpers.Op(p)).ToArray());
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Equal([0x3801, 0x8d00, 0x3802], dash.Select(p => GameSessionTestHelpers.Op(p)).ToArray());
        Assert.Equal(GameSession.NormalDodge, BitConverter.ToInt32(dash[2], 4));
        Assert.Equal(spBefore - GameSession.NormalDodgeSp, session.Resource.Sp);
    }

    [Fact]
    public async Task Sprint_auto_dodge_refusal_still_sends_dodge_result_on_cost_block()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        session.SetResource('S', GameSession.NormalDodgeSp - 1);
        await session.HandleAsync(SprintOn(), Now);
        var dash = await session.HandleAsync(DodgeCast(), Now + 4);
        Assert.Single(dash);
        Assert.Equal(GamePackets.DodgeResult(GameSession.CombatDodge, 1), dash[0]);
        Assert.DoesNotContain(dash, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }
}
