using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Every class: the E slot (QuickSlot 8, hotbar slot 7) carries a context stack of real, listed, castable skills, and a chain root
/// in the stack opens the combo prompt (0x3802, 0x3818, 0x3801). The stacks are OURS (tools/data/pc-context-stacks.json): the client
/// ContextChangeSkillList is not in the repo.
/// </summary>
public sealed class ESlotAuditTests
{
    const long Now = 1_760_002_000_000;

    /// <summary>Rage Burst is in the community Gladiator stack but no table unlocks it, so it is kept off the skill list on purpose.</summary>
    static readonly HashSet<int> NotAcquirable = [11_390_000];

    public static TheoryData<int, string> Classes => new()
    {
        { 1, "Gladiator" }, { 2, "Templar" }, { 3, "Assassin" }, { 4, "Cleric" },
        { 5, "Ranger" }, { 6, "Elementalist" }, { 7, "Sorcerer" }, { 8, "Chanter" },
    };

    static byte[] Cast(int skill) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(1).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<(GameSession Session, List<byte[]> Entry, StarterProfile Profile)> EnterAsync(int creation, int level)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("EAudit").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        return (session, entry, data.Profile(creation, 1)!);
    }

    static int[] ContextAtSlot(byte[] bar, int slot)
    {
        for (var i = 0; i < bar[2]; i++)
        {
            var off = 3 + i * 42;
            if (bar[off + 1] == slot)
                return [.. Enumerable.Range(0, 4).Select(j => BitConverter.ToInt32(bar, off + 20 + j * 4))];
        }
        throw new InvalidOperationException($"slot {slot} missing from hotbar");
    }

    static bool ListContains(byte[] skillList, int skill)
    {
        var needle = BitConverter.GetBytes(skill);
        for (var i = 0; i + 4 <= skillList.Length; i++)
            if (skillList.AsSpan(i, 4).SequenceEqual(needle)) return true;
        return false;
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public void E_stack_has_at_least_two_skills_acquired_by_level_five(int creation, string cls)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var bar = data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 7).SkillId;
        var stack = data.DefaultContextSkills(bar);
        Assert.Equal(4, stack.Count);
        Assert.Equal(bar, stack[0]);
        Assert.Equal(stack.Count(id => id > 0), stack.Where(id => id > 0).Distinct().Count());
        var learned = data.LearnedSkills(cls, 5).Select(k => k.SkillId).ToHashSet();
        Assert.True(stack.Count(learned.Contains) >= 2, $"{cls} E stack {string.Join(",", stack)} has fewer than two skills by level 5");
        foreach (var id in stack.Where(id => id > 0))
        {
            var def = data.Skill(id);
            Assert.NotNull(def);
            Assert.Equal("Active", def!.Type);
        }
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task E_stack_is_on_the_hotbar_and_in_the_skill_list_at_level_forty_five(int creation, string cls)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var (_, entry, profile) = await EnterAsync(creation, 45);
        var barSkill = profile.DefaultBar().Single(s => s.Slot == 7).SkillId;
        var hotbar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);
        var context = ContextAtSlot(hotbar, 7);
        Assert.Equal(data.DefaultContextSkills(barSkill), context);
        var list = entry.Where(p => GameSessionTestHelpers.Op(p) == 0x5100).ToList();
        Assert.NotEmpty(list);
        foreach (var id in context.Where(id => id > 0 && !NotAcquirable.Contains(id)))
            Assert.True(list.Any(p => ListContains(p, id)), $"{cls}: E context skill {id} is missing from the 0x5100 skill list (the slot would be grey)");
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Every_castable_e_stack_skill_is_accepted(int creation, string cls)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var (session, _, profile) = await EnterAsync(creation, 45);
        var barSkill = profile.DefaultBar().Single(s => s.Slot == 7).SkillId;
        var at = Now + 60_000;
        foreach (var id in data.DefaultContextSkills(barSkill).Where(id => id > 0 && !NotAcquirable.Contains(id)))
        {
            // A spirit-conditioned entry is greyed until a spirit is out: SpiritConditionSkillTests covers its cast.
            if (data.Skill(id)!.NeedsSpirit is { Length: > 0 }) continue;
            // SkillCond.dat conditional skills need a prerequisite buff: SkillCondSkillTests covers them.
            if (data.Skill(id)!.SkillCond is not null) continue;
            if (data.Skill(id)!.SkillCond is not null) continue;
            var replies = await session.HandleAsync(Cast(id), at);
            Assert.True(replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0),
                $"{cls}: E stack skill {id} was rejected");
            at += 120_000;
        }
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task Chain_roots_in_the_e_stack_open_the_combo_prompt_in_wire_order(int creation, string cls)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var (session, _, profile) = await EnterAsync(creation, 45);
        var barSkill = profile.DefaultBar().Single(s => s.Slot == 7).SkillId;
        var at = Now + 60_000;
        foreach (var id in data.DefaultContextSkills(barSkill).Where(id => id > 0 && !NotAcquirable.Contains(id)))
        {
            var followUps = data.ChainFollowUpIds(id);
            if (followUps.Count == 0) continue;
            var replies = await session.HandleAsync(Cast(id), at);
            var ops = replies.Select(p => (int)GameSessionTestHelpers.Op(p)).ToList();
            Assert.True(ops.Contains(0x3818), $"{cls}: casting {id} did not open the combo prompt");
            Assert.True(ops.IndexOf(0x3802) < ops.IndexOf(0x3818) && ops.IndexOf(0x3818) < ops.IndexOf(0x3801), $"{cls}: {id} wire order");
            Assert.True(session.ActiveChainSkills.ContainsKey(followUps[0]));
            var follow = await session.HandleAsync(Cast(followUps[0]), at + 250);
            Assert.Contains(follow, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
            at += 120_000;
        }
    }

    [Fact]
    public async Task Sorcerer_ice_chain_from_e_opens_cold_wave()
    {
        var (session, entry, _) = await EnterAsync(7, 45);
        var context = ContextAtSlot(entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600), 7);
        Assert.Equal([15_010_000, 15_090_000, 15_040_000, 0], context);
        var replies = await session.HandleAsync(Cast(15_090_000), Now + 60_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3818 && BitConverter.ToString(p).Contains(BitConverter.ToString(BitConverter.GetBytes(15_100_000))));
        Assert.True(session.ActiveChainSkills.ContainsKey(15_100_000));
    }
}
