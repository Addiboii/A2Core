using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ESlotSkillTests
{
    const long Now = 1_760_000_500_000;

    public static TheoryData<int, string, int> Classes => new()
    {
        { 1, "Gladiator", 11_280_000 }, { 2, "Templar", 12_340_000 }, { 3, "Assassin", 13_340_000 },
        { 4, "Cleric", 17_370_000 }, { 5, "Ranger", 14_330_000 }, { 6, "Elementalist", 16_340_000 },
        { 7, "Sorcerer", 15_010_000 }, { 8, "Chanter", 18_300_000 },
    };

    static async Task<(GameSession Session, List<byte[]> Entry)> EnterAsync(FakeCharacterStore store, int creation, int level)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = level, WorldId = store.WorldId }, new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("EHero").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        return (session, entry);
    }

    static int[] ContextAtSlot(byte[] bar, int slot)
    {
        for (var i = 0; i < bar[2]; i++)
        {
            var off = 3 + i * 42;
            if (bar[off + 1] != slot) continue;
            Assert.Equal((byte)4, bar[off + 19]);
            return [.. Enumerable.Range(0, 4).Select(j => BitConverter.ToInt32(bar, off + 20 + j * 4))];
        }
        throw new InvalidOperationException($"slot {slot} missing from hotbar");
    }

    static int CastSkillForESlot(GameData data, string cls, int level, int barSkill) =>
        data.FirstLearnedESlotComboSkill(cls, level) ?? barSkill;

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task E_slot_skill_is_granted_bound_castable_and_upgradable(int creation, string cls, int skill)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        Assert.Equal(cls, data.Profile(creation, 1)!.Class);
        Assert.Contains((7, skill), data.Profile(creation, 1)!.DefaultBar());
        Assert.DoesNotContain(data.LearnedSkills(cls, 4), k => k.SkillId == skill);
        Assert.Contains(data.LearnedSkills(cls, 5), k => k.SkillId == skill);

        var definition = data.Skill(skill)!;
        Assert.Equal((0, 0, 0, 0), (definition.ChainPrev, definition.CooldownMs, definition.Cost.Mp, definition.Cost.Sp));
        Assert.Equal((1, 5), data.SkillLevelCost(skill, 2));
        Assert.Equal((4, 29), data.SkillLevelCost(skill, 10));

        var store = new FakeCharacterStore { Progress = (5, 0) };
        var (session, entry) = await EnterAsync(store, creation, 5);
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == 0x5100 && ContainsI32(p, skill));
        var bar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);
        Assert.Contains(Enumerable.Range(0, bar[2]), i => bar[3 + i * 42 + 1] == 7 && BitConverter.ToInt32(bar, 3 + i * 42 + 15) == skill);

        var castId = CastSkillForESlot(data, cls, 5, skill);
        Assert.True(castId > 0);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(castId).U8(2).Varint(1).F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 10_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray(), Now + 20_000);
        Assert.Equal(0, BitConverter.ToUInt16(up[0], 2));
        Assert.Equal(2, session.SkillLevel(skill));
    }

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task E_slot_skill_is_bound_but_locked_before_level_five(int creation, string cls, int skill)
    {
        var store = new FakeCharacterStore { Progress = (4, 0) };
        var (session, entry) = await EnterAsync(store, creation, 4);
        Assert.Equal(cls, GameSessionTestHelpers.LiveGameData.Profile(creation, 1)!.Class);
        var bar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);
        Assert.Contains(Enumerable.Range(0, bar[2]), i => bar[3 + i * 42 + 1] == 7 && BitConverter.ToInt32(bar, 3 + i * 42 + 15) == skill);

        var cast = new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(1).F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 10_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) != 0);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray(), Now + 20_000);
        Assert.Equal(GameSession.SkillAcquireNotFoundData, BitConverter.ToUInt16(up[0], 2));
    }

    /// <summary>ECharacterClass has no Spiritmaster: the Elementalist (16xxxxxx, Orb) is the Spiritmaster.</summary>
    [Theory]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    [InlineData(14, 1)]
    [InlineData(14, 2)]
    public void Spiritmaster_is_the_elementalist_profile_with_rapid_scattershot_on_e(int creation, int gender)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var profile = data.Profile(creation, gender)!;
        Assert.Equal("Elementalist", profile.Class);
        Assert.Equal("Spiritmaster Weapon Equip", data.Skill(16_000_000)!.Name);
        Assert.Contains(16_000_000, profile.Skills);
        Assert.Contains((0, 16_010_000), profile.DefaultBar());
        Assert.Equal("FixSlot", data.Skill(16_010_000)!.ContextRegister);
        Assert.Contains((7, 16_340_000), profile.DefaultBar());
        Assert.Equal("Rapid Scattershot", data.Skill(16_340_000)!.Name);
    }

    [Fact]
    public void Every_playable_creation_profile_has_a_full_bar_with_a_level_five_e_skill()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var classes = new HashSet<string>();
        for (var creation = 1; creation <= 64; creation++)
            for (var gender = 1; gender <= 2; gender++)
            {
                if (data.Profile(creation, gender) is not { } profile || profile.DefaultBar().Count < 8) continue;
                classes.Add(profile.Class);
                Assert.Equal(Enumerable.Range(0, 8), profile.DefaultBar().Select(s => s.Slot).Order());
                var e = profile.DefaultBar().Single(s => s.Slot == 7).SkillId;
                Assert.DoesNotContain(data.LearnedSkills(profile.Class, 4), k => k.SkillId == e);
                Assert.Contains(data.LearnedSkills(profile.Class, 5), k => k.SkillId == e);
                Assert.Equal((1, 5), data.SkillLevelCost(e, 2));
            }
        Assert.Equal(["Assassin", "Chanter", "Cleric", "Elementalist", "Gladiator", "Ranger", "Sorcerer", "Templar"], classes.Order());
    }

    [Theory]
    [InlineData(6, 20)]
    [InlineData(14, 20)]
    [InlineData(6, 30)]
    [InlineData(1, 20)]
    [InlineData(4, 20)]
    public async Task Existing_character_past_the_grant_level_gets_the_e_skill_at_login(int creation, int level)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var cls = data.Profile(creation, 1)!.Class;
        var skill = data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 7).SkillId;
        var store = new FakeCharacterStore { Progress = (level, 0) };
        var (session, entry) = await EnterAsync(store, creation, level);
        Assert.Contains(entry, p => GameSessionTestHelpers.Op(p) == 0x5100 && ContainsI32(p, skill));
        var bar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);
        Assert.Contains(Enumerable.Range(0, bar[2]), i => bar[3 + i * 42 + 1] == 7 && BitConverter.ToInt32(bar, 3 + i * 42 + 15) == skill);

        var castId = CastSkillForESlot(data, cls, level, skill);
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(castId).U8(2).Varint(1).F32(0).F32(100).F32(200).F32(300).ToArray();
        var replies = await session.HandleAsync(cast, Now + 10_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(skill).ToArray(), Now + 20_000);
        Assert.Equal(0, BitConverter.ToUInt16(up[0], 2));
        Assert.Equal(2, session.SkillLevel(skill));
    }

    [Theory]
    [InlineData("Assassin", 13_340_000, new[] { 13_340_000, 13_350_000, 13_100_000, 13_130_000 })]
    [InlineData("Gladiator", 11_280_000, new[] { 11_280_000, 11_390_000, 11_170_000, 11_010_000 })]
    [InlineData("Ranger", 14_330_000, new[] { 14_330_000, 14_110_000, 14_090_000, 14_130_000 })]
    [InlineData("Templar", 12_340_000, new[] { 12_340_000, 12_010_000, 12_100_000, 0 })]
    [InlineData("Elementalist", 16_340_000, new[] { 16_340_000, 16_140_000, 16_040_000, 0 })]
    [InlineData("Sorcerer", 15_010_000, new[] { 15_010_000, 15_090_000, 15_040_000, 0 })]
    [InlineData("Cleric", 17_370_000, new[] { 17_370_000, 17_060_000, 17_040_000, 17_080_000 })]
    [InlineData("Chanter", 18_300_000, new[] { 18_300_000, 18_040_000, 18_090_000, 18_060_000 })]
    public void E_slot_default_context_is_the_class_combo_stack(string cls, int barSkill, int[] expected)
    {
        Assert.NotNull(GameSessionTestHelpers.LiveGameData.ESlotComboStack(cls));
        Assert.Equal(expected, GameData.Embedded.DefaultContextSkills(barSkill));
    }

    [Theory]
    [InlineData(5, "Ranger", 14_330_000)]
    [InlineData(3, "Assassin", 13_340_000)]
    [InlineData(1, "Gladiator", 11_280_000)]
    public async Task Hotbar_slot_seven_carries_the_combo_stack_in_context(int creation, string cls, int barSkill)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var (_, entry) = await EnterAsync(store, creation, 10);
        var bar = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x5600);
        Assert.Equal(data.DefaultContextSkills(barSkill), ContextAtSlot(bar, 7));
        Assert.Equal(cls, data.Profile(creation, 1)!.Class);
    }

    [Fact]
    public void Q_slot_snipe_still_uses_chain_next_context()
    {
        Assert.Equal([14_020_000, 14_030_000, 14_040_000, 0], GameData.Embedded.DefaultContextSkills(14_020_000));
    }

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }
}
