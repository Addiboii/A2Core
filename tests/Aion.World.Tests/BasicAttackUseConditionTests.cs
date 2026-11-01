using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// FixSlot chain-root basics (left click / 0x3824) must not be gated by Skill.dat use fields (#239–#241) or SkillCond grey rules.
/// </summary>
public sealed class BasicAttackUseConditionTests
{
    static readonly long Now = 1_760_003_500_000;

    static readonly (string Class, int Root)[] ClassBasics =
    [
        ("Gladiator", 11_020_000),
        ("Templar", 12_010_000),
        ("Assassin", 13_010_000),
        ("Ranger", 14_020_000),
        ("Sorcerer", 15_210_000),
        ("Elementalist", 16_010_000),
        ("Cleric", 17_010_000),
        ("Chanter", 18_010_000),
    ];

    static int LightCreation(string className) =>
        Enumerable.Range(1, 64).First(c => GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1 } p && p.Class == className);

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static byte[] BasicAtk(int targetKey) =>
        new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(targetKey).ToArray();

    static bool CastAccepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static ushort? BasicAccepted(List<byte[]> replies) =>
        replies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs) is { } p
            ? BitConverter.ToUInt16(p, 2)
            : null;

    [Fact]
    public void Every_class_fix_slot_basic_has_no_use_condition_or_skill_cond_gates()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        foreach (var (className, root) in ClassBasics)
        {
            var skill = data.Skill(root)!;
            Assert.True(SkillCooldown.IsBasicAutoAttack(skill), $"{className} {root}");
            Assert.Null(skill.NeedsSpirit);
            Assert.False(skill.NeedsPeace);
            Assert.Null(skill.NeedsResource);
            Assert.Null(skill.SkillCond);
        }
    }

    [Theory]
    [MemberData(nameof(ClassBasicCases))]
    public async Task Basic_attack_is_accepted_in_combat_via_skill_start_and_basic_rq(string className, int root)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var creation = LightCreation(className);
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("BasicHero").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var skill = data.Skill(root)!;
        var castAt = Now + 1000;

        var first = await session.HandleAsync(Cast(root, dummy.Key, dummy.Position), castAt);
        Assert.True(CastAccepted(first), $"{className} first basic SkillStart must accept");

        var lockout = SkillCooldown.LockoutMs(skill, projectile: true);
        var inCombat = await session.HandleAsync(Cast(root, dummy.Key, dummy.Position), castAt + lockout);
        Assert.True(CastAccepted(inCombat), $"{className} basic SkillStart must accept while in combat");

        var viaBasic = await session.HandleAsync(BasicAtk(dummy.Key), castAt + lockout * 2);
        Assert.Equal((ushort)0, BasicAccepted(viaBasic));
    }

    public static IEnumerable<object[]> ClassBasicCases() =>
        ClassBasics.Select(c => new object[] { c.Class, c.Root });
}
