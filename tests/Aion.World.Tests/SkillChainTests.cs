using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillChainTests
{
    const long Now = 1_760_000_200_000;

    static async Task<GameSession> GladiatorWorldAsync(FakeCharacterStore store, int level = 40)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = level }, new Random(42)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public void DefaultContextSkills_follow_dat_chain_next()
    {
        var data = GameData.Embedded;
        var ctx = data.DefaultContextSkills(11_020_000);
        Assert.Equal([11_020_000, 11_030_000, 11_040_000, 0], ctx);
    }

    [Fact]
    public void DefaultContextSkills_lists_sibling_branches()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(11_999_001);
        Assert.Equal([11_999_001, 11_999_002, 11_999_003, 0], ctx);
    }

    [Fact]
    public void DefaultSkillCustomizing_uses_effect_filter_ids()
    {
        var rows = GameData.Embedded.DefaultSkillCustomizing(11_170_000);
        Assert.Contains(rows, r => r.CondId == 111_700_001 && r.SkillId == 11_170_000);
    }

    [Fact]
    public void ChainSkill_packets_match_schema()
    {
        var active = GamePackets.ChainSkillActiveList(11_020_000, [11_030_000]);
        Assert.Equal(0x18, active[0]);   // official 2026-10-30: 18 38 <prev> 01 <next>
        Assert.Equal(0x38, active[1]);
        Assert.Equal(11_020_000, BitConverter.ToInt32(active, 2));
        Assert.Equal(1, active[6]);
        Assert.Equal(11_030_000, BitConverter.ToInt32(active, 7));

        var off = GamePackets.ChainSkillDeactiveList([11_030_000]);
        Assert.Equal(0x19, off[0]);   // official 2026-10-30: 19 38 01 <skill>
        Assert.Equal(0x38, off[1]);
        Assert.Equal(1, off[2]);
    }

    [Fact]
    public async Task Root_cast_opens_chain_window_for_follow_up()
    {
        var session = await GladiatorWorldAsync(new FakeCharacterStore());
        var now = Now + 10_000;
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(11_020_000).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, now);
        var ops = replies.Select(p => p[0] | p[1] << 8).ToList();
        // Official order: 0x3802 start, 0x3818 chain open, 0x383b, then the 0x3801 accept.
        Assert.True(ops.IndexOf(0x3802) < ops.IndexOf(0x3818) && ops.IndexOf(0x3818) < ops.IndexOf(0x383b)
            && ops.IndexOf(0x383b) < ops.IndexOf(0x3801));
        Assert.True(session.ActiveChainSkills.ContainsKey(11_030_000));

        var lock1 = SkillCooldown.LockoutMs(GameData.Embedded.Skill(11_020_000)!, projectile: false);
        var follow = new PacketWriter().U16(0x3800).U16(0).I32(11_030_000).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var ok = await session.HandleAsync(follow, now + lock1);
        Assert.Contains(ok, p => (p[0] | p[1] << 8) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var next = ok.Select(p => p[0] | p[1] << 8).ToList();
        // Step 2: its own 0x3818 (to step 3), then 0x3819 consuming it, both before the accept.
        Assert.True(next.IndexOf(0x3802) < next.IndexOf(0x3818) && next.IndexOf(0x3818) < next.IndexOf(0x3819)
            && next.IndexOf(0x3819) < next.IndexOf(0x383b) && next.IndexOf(0x383b) < next.IndexOf(0x3801));
    }

    [Fact]
    public async Task Third_chain_step_is_accepted_right_after_the_second()
    {
        // Live 2026-10-30 15:45 Ranger: step 3 (parent is the chain-only step 2) was refused and the client stopped attacking.
        var session = await GladiatorWorldAsync(new FakeCharacterStore());
        var now = Now + 10_000;
        byte[] Cast(int id) => new PacketWriter().U16(0x3800).U16(0).I32(id).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var data = GameData.Embedded;
        var l1 = SkillCooldown.LockoutMs(data.Skill(11_020_000)!, projectile: false);
        var l2 = SkillCooldown.LockoutMs(data.Skill(11_030_000)!, projectile: false);
        await session.HandleAsync(Cast(11_020_000), now);
        await session.HandleAsync(Cast(11_030_000), now + l1);
        var third = await session.HandleAsync(Cast(11_040_000), now + l1 + l2);
        Assert.Contains(third, p => (p[0] | p[1] << 8) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public async Task Casts_resent_every_frame_are_not_all_accepted()
    {
        // Live 2026-10-30 16:08 Ranger: the client resends 0x3800 every ~17 ms; official acks without a new SkillStart (not 0x1813).
        var session = await GladiatorWorldAsync(new FakeCharacterStore());
        var now = Now + 10_000;
        byte[] Cast(int id) => new PacketWriter().U16(0x3800).U16(0).I32(id).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        static bool Accepted(List<byte[]> r) => r.Any(p => (p[0] | p[1] << 8) == 0x3801 && GameSession.CastResultCode(p) == 0);
        static bool Started(List<byte[]> r) => r.Any(p => (p[0] | p[1] << 8) == 0x3802);
        var first = await session.HandleAsync(Cast(11_020_000), now);
        Assert.True(Accepted(first));
        Assert.True(Started(first));
        var resend = await session.HandleAsync(Cast(11_030_000), now + 17);
        Assert.DoesNotContain(resend, p => (p[0] | p[1] << 8) == 0x3801);
        Assert.False(Started(resend));
        var snipe = GameData.Embedded.Skill(11_020_000)!;
        var lockout = SkillCooldown.LockoutMs(snipe, projectile: false);
        var follow = await session.HandleAsync(Cast(11_030_000), now + lockout);
        Assert.True(Accepted(follow));
        Assert.True(Started(follow));
    }

    [Theory]
    [InlineData(14_020_000, 14_030_000)]   // Ranger Snipe -> Rapid Fire
    [InlineData(14_030_000, 14_040_000)]   // Rapid Fire -> Spiral Arrow
    [InlineData(11_020_000, 11_030_000)]   // Gladiator Keen Strike -> Rupture Strike
    public void Every_class_chain_step_is_exported(int parent, int child)
    {
        var data = GameData.Embedded;
        Assert.Equal(parent, data.Skill(child)?.ChainPrev);
        Assert.Contains(child, data.Skill(parent)?.ChainNext ?? []);
    }

    [Fact]
    public async Task Chain_follow_up_rejected_without_parent_cast()
    {
        var session = await GladiatorWorldAsync(new FakeCharacterStore());
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(11_030_000).U8(2).Varint(1)
            .F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 10_000);
        Assert.Contains(replies, p => (p[0] | p[1] << 8) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }

    [Fact]
    public async Task QuickSlotContextSkill_persists_on_slot()
    {
        var store = new FakeCharacterStore();
        var session = await GladiatorWorldAsync(store);

        var set = new PacketWriter().U16(0x5601).U8(1).U8(3).U8(0).I64(0).I32(0).I32(11_020_000)
            .U8(4).I32(11_020_000).I32(11_030_000).I32(0).I32(0).U8(0).I32(0).U8(0).ToArray();
        await session.HandleAsync(set, Now);

        var update = new PacketWriter().U16(0x5607).U8(3).U8(2).I32(11_040_000).ToArray();
        var rs = (await session.HandleAsync(update, Now)).First();
        Assert.Equal(0x08, rs[0]);
        Assert.Equal(0x56, rs[1]);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(11_040_000, BitConverter.ToInt32(rs, 6));
        Assert.Equal(11_040_000, store.QuickSlots[3].ContextSkills![2]);
    }

    [Fact]
    public async Task QuickSlotContextSkill_swap_moves_displaced_skill()
    {
        var store = new FakeCharacterStore();
        var session = await GladiatorWorldAsync(store);
        var set = new PacketWriter().U16(0x5601).U8(1).U8(3).U8(0).I64(0).I32(0).I32(11_020_000)
            .U8(4).I32(11_020_000).I32(11_030_000).I32(11_040_000).I32(0).U8(0).I32(0).U8(0).ToArray();
        await session.HandleAsync(set, Now);
        var update = new PacketWriter().U16(0x5607).U8(3).U8(2).I32(11_030_000).ToArray();
        var rs = (await session.HandleAsync(update, Now)).First();
        Assert.Equal(1, rs[10]); // is_swap
        Assert.Equal(11_030_000, store.QuickSlots[3].ContextSkills![2]);
        Assert.Equal(11_040_000, store.QuickSlots[3].ContextSkills![1]);
    }

    [Fact]
    public async Task SkillCustomizingUpdate_persists_rows()
    {
        var store = new FakeCharacterStore();
        var session = await GladiatorWorldAsync(store);
        var body = new PacketWriter().U16(0x36EE).Varint(1).I32(111_700_001).I32(11_170_000).U8(1).ToArray();
        var rs = (await session.HandleAsync(body, Now)).Single();
        Assert.Equal(0xEF, rs[0]);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Single(session.SkillCustomizing);
    }

    [Fact]
    public void ChainSkillActiveList_wire_includes_all_branches()
    {
        var pkt = GamePackets.ChainSkillActiveList(11_999_001, [11_999_002, 11_999_003]);
        Assert.Equal(2, pkt[6]);
        Assert.Equal(11_999_002, BitConverter.ToInt32(pkt, 7));
        Assert.Equal(11_999_003, BitConverter.ToInt32(pkt, 11));
    }
}
