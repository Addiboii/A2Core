using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Regression: PR #77 must not leave the hero unable to cast at all.</summary>
public sealed class SkillCastRegressionTests
{
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool CastAccepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public async Task First_basic_attack_always_gets_an_accepted_cast_reply()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(11));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var replies = await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), Now + 50);
        Assert.NotEmpty(replies);
        Assert.True(CastAccepted(replies));
    }

    [Fact]
    public async Task Basic_attack_accepts_at_attack_speed_intervals()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(12));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var skill = GameData.Embedded.Skill(11020000)!;
        var period = SkillCooldown.LockoutMs(skill);
        for (var i = 0; i < 4; i++)
        {
            var castAt = Now + 500 + i * period;
            var replies = await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), castAt);
            Assert.True(CastAccepted(replies), $"swing {i} at {castAt} should be accepted");
            await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        }
    }

    [Fact]
    public async Task Mp_skill_with_zero_dat_cooldown_is_not_perma_locked()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(13));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11010000;
        var firstAt = Now + 500;
        Assert.True(CastAccepted(await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), firstAt)));
        // Held for its swing (both rows) so a resent request cannot restart it mid-combo; free again right after.
        Assert.False(CastAccepted(await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), firstAt + 200)));
        var secondAt = firstAt + SkillCooldown.LockoutMs(GameData.Embedded.Skill(skillId)!);
        Assert.True(CastAccepted(await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), secondAt)));
    }

    [Fact]
    public async Task Next_cast_does_not_drop_the_previous_skills_remaining_rows()
    {
        // Live 2026-10-30 (Templar): Pummel's rows at 600/1000/1400 ms were cleared by the next basic swing, so only one landed.
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(15));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var multi = GameData.Embedded.Skill(11010000)!;
        Assert.True(multi.Damage.Length >= 2);
        var castAt = Now + 500;
        var firstRow = castAt + SkillTiming.HitLandMs(multi, 0);
        var packets = new List<byte[]>();
        packets.AddRange(await session.HandleAsync(Cast(11010000, dummy.Key, dummy.Position), castAt));
        packets.AddRange(await session.TickAsync(firstRow));
        // A basic swing between the rows.
        packets.AddRange(await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), firstRow + 10));
        for (var t = firstRow + 100; t <= castAt + 4000; t += 100) packets.AddRange(await session.TickAsync(t));
        Assert.Equal(multi.Damage.Length + 1, packets.Count(p => GameSessionTestHelpers.Op(p) == 0x3804));
    }

    [Fact]
    public async Task Dat_cooldown_skill_accepts_after_cooldown_elapses()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(14));
        const int skillId = GameSession.CombatDodge;
        var skill = GameData.Embedded.Skill(skillId)!;
        var castAt = Now + 1000;
        var selfCast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(session.PlayerKey).F32(0)
            .Vec3(session.Position ?? Vector3d.Zero).U8(9).ToArray();
        Assert.True(CastAccepted(await session.HandleAsync(selfCast, castAt)));
        var early = await session.HandleAsync(selfCast, castAt + 100);
        Assert.Contains(early, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        var readyAt = castAt + SkillCooldown.LockoutMs(skill);
        Assert.True(CastAccepted(await session.HandleAsync(selfCast, readyAt)));
    }
}
