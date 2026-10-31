using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// ChainSkillActiveList_NT (wire 0x3818) drives the combo prompt UI (next skill icon + key hint).
/// </summary>
public sealed class ChainComboPromptTests
{
    const long Now = 1_760_001_000_000;

    public static TheoryData<int, int, int> BarSkillsWithChain => new()
    {
        { 3, 13_010_000, 13_030_000 }, // Assassin Quick Slice -> Swift Slice step 1
        { 3, 13_100_000, 13_110_000 }, // Assassin Savage Roar -> Back Kick
        { 1, 11_020_000, 11_030_000 }, // Gladiator Keen Strike -> Rupture
        { 5, 14_020_000, 14_030_000 }, // Ranger Snipe -> Rapid Fire
    };

    static async Task<GameSession> AssassinSessionAsync(int level = 45)
    {
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainHero").U8(1).U32(3).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task<GameSession> EnterAsync(int creation, int level)
    {
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainHero").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Theory]
    [MemberData(nameof(BarSkillsWithChain))]
    public async Task Cast_with_chain_next_emits_3818_before_accept(int creation, int root, int firstChain)
    {
        var session = await EnterAsync(creation, 45);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(root).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 20_000);
        var ops = replies.Select(p => GameSessionTestHelpers.Op(p)).ToList();
        Assert.Contains((ushort)0x3818, ops);
        Assert.True(ops.IndexOf(0x3802) < ops.IndexOf(0x3818) && ops.IndexOf(0x3818) < ops.IndexOf(0x3801));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && PacketContains(p, root) && PacketContains(p, firstChain));
        Assert.True(session.ActiveChainSkills.ContainsKey(firstChain));
    }

    [Fact]
    public async Task Chain_follow_up_accepted_while_window_open()
    {
        var session = await AssassinSessionAsync(45);
        var root = 13_100_000;
        var next = 13_110_000;
        byte[] Cast(int id) => new PacketWriter().U16(0x3800).U16(0).I32(id).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var t0 = Now + 30_000;
        await session.HandleAsync(Cast(root), t0);
        var follow = await session.HandleAsync(Cast(next), t0 + 250);
        Assert.Contains(follow, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public void Chain_follow_up_ids_align_spec_variants()
    {
        var data = GameData.Embedded;
        Assert.Equal([14_030_010], data.ChainFollowUpIds(14_020_010));
        Assert.Equal([13_110_000], data.ChainFollowUpIds(13_100_000));
    }

    [Fact]
    public async Task Spec_variant_cast_emits_3818_with_matching_chain_step()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId }, new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Ranger").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var root = 14_020_010;
        var next = 14_030_010;
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(root).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 40_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && PacketContains(p, root) && PacketContains(p, next));
    }

    [Fact]
    public void Skill_list_at_45_includes_chain_only_steps_for_assassin_bar()
    {
        var data = GameData.Embedded;
        var profile = data.Profile(3, 1)!;
        var ids = data.SkillListForClient("Assassin", 45, profile).Select(k => k.SkillId).ToHashSet();
        Assert.Contains(13_030_000, ids);
        Assert.Contains(13_110_000, ids);
    }

    static bool PacketContains(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }
}
