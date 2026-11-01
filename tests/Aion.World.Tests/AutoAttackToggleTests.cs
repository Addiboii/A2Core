using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Right-click auto-attack-on-target (0x3800 change_toggle) must always get a terminal 0x3801.</summary>
public sealed class AutoAttackToggleTests
{
    static readonly long Now = 1_700_000_200_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at, ushort mask = 16, byte toggle = 0, byte uid = 9)
    {
        var w = new PacketWriter().U16(0x3800).U16(mask).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at);
        if ((mask & 8) != 0) w.U8(toggle);
        if ((mask & 16) != 0) w.U8(uid);
        return w.ToArray();
    }

    static bool CastAccepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool CastRejected(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);

    [Fact]
    public async Task Toggle_on_during_basic_lockout_still_gets_accepted_cast_reply_without_skill_start()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(21));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11020000;
        var firstAt = Now + 100;
        Assert.True(CastAccepted(await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), firstAt)));
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, firstAt);
        var rmbAt = firstAt + 50;
        var toggleOn = Cast(skillId, dummy.Key, dummy.Position, mask: 24, toggle: 1);
        var replies = await session.HandleAsync(toggleOn, rmbAt);
        Assert.True(CastAccepted(replies));
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Toggle_off_ends_the_in_progress_basic_swing()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(22));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11020000;
        var castAt = Now + 200;
        var start = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position, mask: 24, toggle: 1), castAt);
        Assert.True(CastAccepted(start));
        Assert.Contains(start, p => GameSessionTestHelpers.Op(p) == 0x3802);
        var stop = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position, mask: 24, toggle: 0), castAt + 30);
        Assert.True(CastAccepted(stop));
        Assert.Contains(stop, p => GameSessionTestHelpers.Op(p) == 0x3806);
    }

    [Fact]
    public async Task Skill_charging_start_0x381a_gets_381b_and_never_empty()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(24));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11020000;
        var charging = new PacketWriter().U16(GameSession.SkillChargingStartRq).U16(0).I32(skillId).U8(2).Varint(dummy.Key)
            .F32(0).Vec3(dummy.Position).ToArray();
        Assert.InRange(charging.Length, 26, 28);
        var replies = await session.HandleAsync(charging, Now + 400);
        Assert.NotEmpty(replies);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SkillChargingStartRs && BitConverter.ToUInt16(p, 2) == 0);
    }

    [Fact]
    public async Task Basic_cooldown_spam_without_toggle_gets_rejected_cast_reply()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(23));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11020000;
        var castAt = Now + 300;
        Assert.True(CastAccepted(await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt)));
        var early = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt + 50);
        Assert.True(CastRejected(early));
    }
}
