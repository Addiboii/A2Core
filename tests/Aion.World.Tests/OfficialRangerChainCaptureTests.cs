using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Official Ranger capture (2026-10-31, 35 chains): base chain ids only, fixed 0x3818/0x3819 layout and per-press ordering.
/// </summary>
public sealed class OfficialRangerChainCaptureTests
{
    const long Now = 1_760_006_000_000;
    const int Snipe = 14_020_000, RapidFire = 14_030_000, SpiralArrow = 14_040_000;

    static byte[] Cast(int skillId) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    static async Task<GameSession> RangerAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(0)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("CapRanger").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public void ChainFollowUpIds_uses_base_ids_even_at_high_mastery()
    {
        var data = GameData.Embedded;
        Assert.Equal([RapidFire], data.ChainFollowUpIds(Snipe, masteryLevel: 5));
        Assert.Equal([SpiralArrow], data.ChainFollowUpIds(RapidFire, masteryLevel: 5));
    }

    [Fact]
    public void ChainSkillActiveList_matches_official_snipe_bytes()
    {
        var expected = GamePackets.ChainSkillActiveList(Snipe, [RapidFire]);
        Assert.Equal([0x18, 0x38], expected.AsSpan(0, 2).ToArray());
        Assert.Equal(Snipe, BitConverter.ToInt32(expected, 2));
        Assert.Equal(1, expected[6]);
        Assert.Equal(RapidFire, BitConverter.ToInt32(expected, 7));
    }

    [Fact]
    public void ChainSkillDeactiveList_matches_official_rapid_fire_bytes()
    {
        var expected = GamePackets.ChainSkillDeactiveList([RapidFire]);
        Assert.Equal([0x19, 0x38, 0x01], expected.AsSpan(0, 3).ToArray());
        Assert.Equal(RapidFire, BitConverter.ToInt32(expected, 3));
    }

    [Fact]
    public async Task Three_step_chain_matches_official_reply_order()
    {
        var session = await RangerAsync();
        var data = GameSessionTestHelpers.LiveGameData;
        var t = Now + 20_000;
        var l1 = SkillCooldown.LockoutMs(data.Skill(Snipe)!, projectile: true);
        var l2 = SkillCooldown.LockoutMs(data.Skill(RapidFire)!, projectile: true);

        var step1 = await session.HandleAsync(Cast(Snipe), t);
        var ops1 = step1.Select(Op).ToList();
        // Official order: 0x3802 start, 0x3818 chain open, 0x383b, then the 0x3801 accept.
        Assert.True(ops1.IndexOf(0x3802) < ops1.IndexOf(0x3818) && ops1.IndexOf(0x3818) < ops1.IndexOf(0x383b)
            && ops1.IndexOf(0x383b) < ops1.IndexOf(0x3801));
        Assert.Equal(GamePackets.ChainSkillActiveList(Snipe, [RapidFire]), step1.Single(p => Op(p) == 0x3818));
        Assert.Equal((ushort)0, GameSession.CastResultCode(step1.Single(p => Op(p) == 0x3801)));

        var step2 = await session.HandleAsync(Cast(RapidFire), t + l1);
        var ops2 = step2.Select(Op).ToList();
        Assert.True(ops2.IndexOf(0x3802) < ops2.IndexOf(0x3818));
        Assert.True(ops2.IndexOf(0x3818) < ops2.IndexOf(0x3819));
        Assert.True(ops2.IndexOf(0x3819) < ops2.IndexOf(0x383b) && ops2.IndexOf(0x383b) < ops2.IndexOf(0x3801));
        Assert.Equal(GamePackets.ChainSkillActiveList(RapidFire, [SpiralArrow]), step2.Single(p => Op(p) == 0x3818));
        Assert.Equal(GamePackets.ChainSkillDeactiveList([RapidFire]), step2.Single(p => Op(p) == 0x3819));

        var step3 = await session.HandleAsync(Cast(SpiralArrow), t + l1 + l2);
        var ops3 = step3.Select(Op).ToList();
        Assert.DoesNotContain((ushort)0x3818, ops3);
        Assert.True(ops3.IndexOf(0x3802) < ops3.IndexOf(0x3819));
        Assert.True(ops3.IndexOf(0x3819) < ops3.IndexOf(0x383b) && ops3.IndexOf(0x383b) < ops3.IndexOf(0x3801));
        Assert.Equal(GamePackets.ChainSkillDeactiveList([SpiralArrow]), step3.Single(p => Op(p) == 0x3819));
    }

    [Fact]
    public async Task Resend_gets_3801_result_1_early_chain_is_deferred_until_lockout()
    {
        var session = await RangerAsync();
        var data = GameSessionTestHelpers.LiveGameData;
        var t = Now + 30_000;
        await session.HandleAsync(Cast(Snipe), t);
        var resend = await session.HandleAsync(Cast(Snipe), t + 17);
        Assert.Contains(resend, p => Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        var other = await session.HandleAsync(Cast(RapidFire), t + 30);
        Assert.DoesNotContain(other, p => Op(p) == 0x3801);
        var drainAt = t + session.HeldCastLockoutRemainingMs(t + 1) + 1;
        var replies = new List<byte[]>();
        for (var at = t + 30; at <= drainAt; at += 17)
            replies.AddRange(await session.HandleAsync(Cast(RapidFire), at));
        replies.AddRange(await session.TickAsync(drainAt));
        Assert.Contains(replies, p => Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }
}
