using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class HeldKeyChainCastTests
{
    const long Now = 1_760_007_000_000;

    static byte[] Cast(int skillId) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();

    static long HeldChainDrainAt(GameSession session, long castAt) =>
        castAt + session.HeldCastLockoutRemainingMs(castAt + 1) + 1;

    static async Task HoldCastWireUntil(GameSession session, byte[] wire, long fromMs, long untilMs, List<byte[]>? into = null)
    {
        for (var at = fromMs; at <= untilMs; at += 17)
        {
            var replies = await session.HandleAsync(wire, at);
            into?.AddRange(replies);
        }
    }

    static async Task<GameSession> RangerAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(0)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("HeldRanger").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Held_resends_during_swing_are_silent_without_3801()
    {
        var session = await RangerAsync();
        var t = Now + 5000;
        var first = await session.HandleAsync(Cast(14_020_000), t);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        for (var i = 1; i <= 8; i++)
        {
            var spam = await session.HandleAsync(Cast(14_020_000), t + i * 17);
            Assert.Contains(spam, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
            Assert.DoesNotContain(spam, p => GameSessionTestHelpers.Op(p) == 0x3802);
        }
    }

    [Fact]
    public async Task Accepted_step_includes_383b_combat_on_ack()
    {
        var session = await RangerAsync();
        var replies = await session.HandleAsync(Cast(14_020_000), Now + 6000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x383b);
    }

    [Fact]
    public async Task Early_chain_cast_is_queued_then_accepts_after_swing_lockout()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var rapid = data.ChainFollowUpIds(14_020_000).First();
        var snipe = data.Skill(14_020_000)!;
        _ = SkillCooldown.LockoutMs(snipe, projectile: true);
        var t = Now + 8000;
        await session.HandleAsync(Cast(14_020_000), t);
        var early = await session.HandleAsync(Cast(rapid), t + 40);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3801);

        var drainAt = HeldChainDrainAt(session, t);
        var replies = new List<byte[]>();
        await HoldCastWireUntil(session, Cast(rapid), t + 40, drainAt, replies);
        replies.AddRange(await session.TickAsync(drainAt));
        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Non_chain_skill_during_lockout_gets_refusal_not_silence()
    {
        var session = await RangerAsync();
        const int tempest = 14_340_000;
        var t = Now + 9000;
        await session.HandleAsync(Cast(14_020_000), t);
        var other = await session.HandleAsync(Cast(tempest), t + 30);
        Assert.Contains(other, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }

    [Fact]
    public async Task Basic_attack_resend_maps_silent_cast_to_3825_result_1()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await RangerAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var atk = new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(dummy.Key).ToArray();
        var t = Now + 10_000;
        await session.HandleAsync(atk, t);
        var resend = await session.HandleAsync(atk, t + 17);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(resend.Single(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
    }

    [Fact]
    public async Task Replacing_queued_chain_cast_refuses_the_prior_request()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var rapid = data.ChainFollowUpIds(14_020_000).First();
        var t = Now + 11_000;
        await session.HandleAsync(Cast(14_020_000), t);
        var firstQueue = new PacketWriter().U16(0x3800).U16(0).I32(rapid).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        await session.HandleAsync(firstQueue, t + 40);
        var replaceQueue = new PacketWriter().U16(0x3800).U16(16).I32(rapid).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).U8(9).ToArray();
        var replace = await session.HandleAsync(replaceQueue, t + 60);
        Assert.Single(replace, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }

    [Fact]
    public async Task Charge_toggle_follow_up_during_lockout_gets_cast_reply()
    {
        var session = await RangerAsync();
        var t = Now + 12_000;
        static byte[] CastToggle(int skillId, byte toggle) =>
            new PacketWriter().U16(0x3800).U16(8).I32(skillId).U8(2).Varint(1).F32(0).F32(100).F32(200).F32(300).U8(toggle).ToArray();
        await session.HandleAsync(CastToggle(14_020_000, 0x01), t);
        var charge = await session.HandleAsync(CastToggle(14_020_000, 0xE1), t + 900);
        Assert.NotEmpty(charge);
        Assert.Contains(charge, p => GameSessionTestHelpers.Op(p) == 0x3801);
    }

    [Fact]
    public async Task Queued_chain_produces_exactly_one_3801_on_drain()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var rapid = data.ChainFollowUpIds(14_020_000).First();
        var t = Now + 13_000;
        await session.HandleAsync(Cast(14_020_000), t);
        var early = await session.HandleAsync(Cast(rapid), t + 40);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3801);
        var drainAt = HeldChainDrainAt(session, t);
        var replies = new List<byte[]>();
        await HoldCastWireUntil(session, Cast(rapid), t + 40, drainAt, replies);
        replies.AddRange(await session.TickAsync(drainAt));
        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public async Task Queued_chain_is_abandoned_after_held_key_release_without_extra_swing()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var rapid = data.ChainFollowUpIds(14_020_000).First();
        var t = Now + 15_000;
        await session.HandleAsync(Cast(14_020_000), t);
        await session.HandleAsync(Cast(rapid), t + 40);
        var drainAt = HeldChainDrainAt(session, t);
        var tick = await session.TickAsync(drainAt + GameSession.HeldCastActiveMs + 50);
        Assert.DoesNotContain(tick, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }

    [Fact]
    public async Task Movement_cancels_queued_chain_without_drain_accept()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var rapid = data.ChainFollowUpIds(14_020_000).First();
        var t = Now + 16_000;
        await session.HandleAsync(Cast(14_020_000), t);
        await session.HandleAsync(Cast(rapid), t + 40);
        var at = session.Position ?? Vector3d.Zero;
        var move = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at with { X = at.X + 200 }), t + 100);
        Assert.Contains(move, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        var drainAt = HeldChainDrainAt(session, t);
        var tick = await session.TickAsync(drainAt);
        Assert.DoesNotContain(tick, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Well_formed_cast_replies_are_never_empty()
    {
        var session = await RangerAsync();
        var t = Now + 14_000;
        foreach (var replies in new[]
                 {
                     await session.HandleAsync(Cast(14_020_000), t),
                     await session.HandleAsync(Cast(14_020_000), t + 17),
                 })
            Assert.NotEmpty(replies);
    }

    [Fact]
    public void Cast_not_ready_refusal_uses_result_1()
    {
        var refuse = GamePackets.CastReply(false, 14_020_000, 0, false, 0, false, refusal: GameSession.CastNotReadyResult);
        Assert.Equal((ushort)1, GameSession.CastResultCode(refuse));
    }
}
