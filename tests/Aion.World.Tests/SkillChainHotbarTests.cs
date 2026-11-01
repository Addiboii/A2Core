using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Chain parents with export caster_debuffs (Defiance): client hotbar swap may need 0x382a before 0x3818.
/// Attack chain parents (Savage Roar) open 0x3818 without caster self abnormals (official buff bar).
/// FixSlot basic chains send no caster self abnormal on hit or accept.
/// </summary>
public sealed class SkillChainHotbarTests
{
    const long Now = 1_760_008_000_000;

    static byte[] Cast(int skill, int target = Combat.TrainingDummyKey) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> RangerAsync(int level = 45)
    {
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(2)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainBar").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task<GameSession> AssassinAsync(int level = 45)
    {
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(4)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainBar").U8(1).U32(3).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static int OpIndex(List<byte[]> replies, ushort op) =>
        replies.FindIndex(p => GameSessionTestHelpers.Op(p) == op);

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    [Fact]
    public void Defiance_exports_chain_follow_up_and_caster_rows_savage_roar_does_not()
    {
        var data = GameData.Embedded;
        Assert.Equal([14_370_010], data.ChainFollowUpIds(14_260_010));
        Assert.Equal([13_110_000], data.ChainFollowUpIds(13_100_000));
        Assert.True(SkillAreas.For(14_260_010)!.CasterDebuffRows.Any());
        Assert.False(SkillAreas.For(13_100_000)!.CasterDebuffRows.Any());
    }

    [Fact]
    public async Task Ranger_defiance_self_abnormal_precedes_chain_nt_on_accept()
    {
        const int defiance = 14_260_010;
        const int follow = 14_370_010;
        const int selfAbnormal = 100_200_001;
        var session = await RangerAsync();
        var replies = await session.HandleAsync(
            new PacketWriter().U16(0x3800).U16(0).I32(defiance).U8(2).Varint(session.PlayerKey)
                .F32(0).F32(100).F32(200).F32(300).ToArray(),
            Now + 20_000);
        var abn = OpIndex(replies, 0x382a);
        var chain = OpIndex(replies, 0x3818);
        Assert.True(abn >= 0 && chain >= 0, "expected 0x382a and 0x3818 on accept");
        Assert.True(abn < chain, "self abnormal must precede chain open");
        Assert.True(chain < OpIndex(replies, 0x3801));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, defiance) && ContainsI32(p, follow));
        Assert.Contains(selfAbnormal, session.ActiveBuffs);
        Assert.True(session.ActiveChainSkills.ContainsKey(follow));
    }

    [Fact]
    public async Task Assassin_savage_roar_opens_chain_without_caster_self_abnormal()
    {
        const int root = 13_100_000;
        const int follow = 13_110_000;
        var session = await AssassinAsync();
        var before = session.ActiveBuffs.Count;
        var replies = await session.HandleAsync(Cast(root), Now + 25_000);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Equal(before, session.ActiveBuffs.Count);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && ContainsI32(p, root) && ContainsI32(p, follow));
        Assert.True(session.ActiveChainSkills.ContainsKey(follow));
    }

    [Fact]
    public async Task Assassin_fixslot_basic_chain_no_caster_abnormal_on_accept_or_hit()
    {
        var session = await AssassinAsync();
        var t = Now + 28_000;
        var before = session.ActiveBuffs.Count;
        var replies = await session.HandleAsync(Cast(13_010_000), t);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Equal(before, session.ActiveBuffs.Count);
        var tick = await session.TickAsync(t + 2_000);
        Assert.DoesNotContain(tick, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Equal(before, session.ActiveBuffs.Count);
    }

    [Fact]
    public async Task Snipe_chain_still_has_no_caster_abnormal_on_accept_or_hit()
    {
        var session = await RangerAsync();
        var t = Now + 30_000;
        var before = session.ActiveBuffs.Count;
        var replies = await session.HandleAsync(Cast(14_020_000), t);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818);
        Assert.Equal(before, session.ActiveBuffs.Count);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x382a);
        var tick = await session.TickAsync(t + 2_000);
        Assert.Equal(before, session.ActiveBuffs.Count);
        Assert.DoesNotContain(tick, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }
}
