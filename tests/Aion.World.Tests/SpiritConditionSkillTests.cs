using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Spiritmaster (Elementalist) Jointstrike: Curse 16140000 is greyed by the client until a spirit is out (Skill.dat
/// CanUseWhenElementalSummonSpawned, from the usmap SkillTableItem). The groups are not exported, so any spirit counts (OURS,
/// use_condition_source "override"); it is also an E stack entry after Rapid Scattershot.
/// </summary>
public sealed class SpiritConditionSkillTests
{
    const long Now = 1_760_003_000_000;
    const int Jointstrike = 16_140_000;

    static byte[] Cast(int skill, int target = 0) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(int level = 45, int creation = 6)
    {
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Spirit").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        return (session, entry);
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public void Jointstrike_carries_its_use_condition_and_damage()
    {
        var definition = GameSessionTestHelpers.LiveGameData.Skill(Jointstrike)!;
        Assert.Equal(["Fire", "Water", "Wind", "Earth", "Ancient"], definition.NeedsSpirit!);
        Assert.Equal("override", definition.UseConditionSource);
        Assert.NotEmpty(definition.Damage);
        Assert.Equal(16_140_000, GameSessionTestHelpers.LiveGameData.LearnedSkills("Elementalist", 4).Single(k => k.SkillId == Jointstrike).SkillId);
    }

    [Fact]
    public void Summon_cast_skills_do_not_carry_a_spirit_prerequisite()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        foreach (var id in new[] { 16_100_000, 16_110_000, 16_120_000, 16_130_000 })
            Assert.Null(data.Skill(id)?.NeedsSpirit);
    }

    [Fact]
    public void Only_jointstrike_and_spirit_management_skills_require_a_spirit_out()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var summonCasts = new HashSet<int> { 16_100_000, 16_110_000, 16_120_000, 16_130_000 };
        var conditioned = Enumerable.Range(11_000_000 / 10, 8_000_000 / 10).Select(i => i * 10)
            .Where(id => !summonCasts.Contains(id) && data.Skill(id)?.NeedsSpirit is { Length: > 0 }).ToList();
        Assert.Contains(Jointstrike, conditioned);
        Assert.All(summonCasts, id => Assert.DoesNotContain(id, conditioned));
    }

    [Theory]
    [InlineData(16_100_000)]
    [InlineData(16_110_000)]
    public async Task Summon_is_accepted_with_no_spirit_out(int summonSkill)
    {
        var (session, _) = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var replies = await session.HandleAsync(Cast(summonSkill, dummy.Key), Now + 60_000);
        Assert.True(Accepted(replies), "summon cast must not require an existing spirit");
    }
    [Theory]
    [InlineData(6)]
    [InlineData(14)]
    public async Task Both_spiritmaster_profiles_have_jointstrike_in_the_skill_list_and_e_stack(int creation)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var (_, entry) = await EnterAsync(creation: creation);
        var bar = data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 7).SkillId;
        Assert.Equal([16_340_000, Jointstrike, 16_040_000, 0], data.DefaultContextSkills(bar));
        var needle = BitConverter.GetBytes(Jointstrike);
        Assert.Contains(entry.Where(p => GameSessionTestHelpers.Op(p) == 0x5100), p => Enumerable.Range(0, p.Length - 3).Any(i => p.AsSpan(i, 4).SequenceEqual(needle)));
    }

    [Fact]
    public async Task Jointstrike_is_refused_without_a_spirit_and_costs_nothing()
    {
        var (session, _) = await EnterAsync();
        var replies = await session.HandleAsync(Cast(Jointstrike, Combat.TrainingDummyKey), Now + 60_000);
        Assert.False(Accepted(replies));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) != 0);
    }

    [Theory]
    [InlineData(16_100_000, 1)]
    [InlineData(16_110_000, 2)]
    [InlineData(16_120_000, 3)]
    [InlineData(16_130_000, 4)]
    public async Task Jointstrike_is_usable_with_every_spirit_and_the_spirit_strikes_with_it(int summonSkill, int group)
    {
        var (session, _) = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var summon = await session.HandleAsync(Cast(summonSkill, dummy.Key), Now + 60_000);
        Assert.True(Accepted(summon), "the summon cast is accepted");
        Assert.DoesNotContain(summon, p => GameSessionTestHelpers.Op(p) == 0xe239);
        var icon = session.ElementalActivateTick(Now + 60_001);
        var spirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Contains(icon, p => GameSessionTestHelpers.Op(p) == 0xe239 && p[2] == GamePackets.ElementalSummonWireMask && p[3] == group
            && new PacketReader(p) { Offset = 4 }.Varint() == (uint)spirit.Key);
        Assert.Contains(icon, p => GameSessionTestHelpers.Op(p) == 0x382a && session.ActiveBuffs.Contains(GameSession.SpiritSummonStateAbnormal));

        var joint = await session.HandleAsync(Cast(Jointstrike, dummy.Key), Now + 61_000);
        Assert.True(Accepted(joint), "Jointstrike is accepted while the spirit is out");
        var hpBefore = dummy.Hp;
        for (var t = Now + 61_100; t <= Now + 66_000 && dummy.Hp == hpBefore; t += 100) await session.TickAsync(t);
        Assert.True(dummy.Hp < hpBefore, "the strike lands");
    }

    [Fact]
    public async Task Jointstrike_is_refused_again_once_the_spirit_is_dismissed()
    {
        var (session, _) = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(Cast(16_100_000, dummy.Key), Now + 60_000);
        Assert.True(Accepted(await session.HandleAsync(Cast(Jointstrike, dummy.Key), Now + 61_000)));
        var back = await session.HandleAsync(Cast(GameSession.ReturnAllSpiritsSkill, dummy.Key), Now + 70_000);
        Assert.True(Accepted(back), "Return: All Spirits is accepted");
        Assert.Contains(back, p => GameSessionTestHelpers.Op(p) == 0xe23a);
        Assert.False(Accepted(await session.HandleAsync(Cast(Jointstrike, dummy.Key), Now + 90_000)));
    }

    [Fact]
    public async Task Jointstrike_from_the_e_slot_context_is_accepted_with_a_spirit()
    {
        var (session, _) = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(Cast(16_110_000, dummy.Key), Now + 60_000);
        var replies = await session.HandleAsync(Cast(Jointstrike, dummy.Key), Now + 90_000);
        Assert.True(Accepted(replies));
        var rapidWithoutStagger = await session.HandleAsync(Cast(16_340_000, dummy.Key), Now + 120_000);
        Assert.False(Accepted(rapidWithoutStagger), "Rapid Scattershot needs Stagger on the target (SkillCond)");
        dummy.StaggerUntil = Now + 160_000;
        dummy.Held = true;
        var rapid = await session.HandleAsync(Cast(16_340_000, dummy.Key), Now + 150_000);
        Assert.True(Accepted(rapid), "Rapid Scattershot is accepted while the target is staggered");
    }
}
