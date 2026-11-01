using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// SkillCond.dat conditional skills (HasAbnormalGroupId / HasAbnormalEffectType): Ranger Suppressing Arrow needs Precision
/// from Marking Shot; the server applies the grant buff on accept and refuses the conditional cast without it.
/// </summary>
public sealed class SkillCondSkillTests
{
    const long Now = 1_760_004_000_000;
    const int MarkingShot = 14_090_000;
    const int SuppressingArrow = 14_070_000;
    const int PrecisionGroup = 14_090_0001;

    static int AbnormalPacketKey(byte[] packet) => (int)new PacketReader(packet) { Offset = 2 }.Varint();

    static byte[] Cast(int skill, int target = Combat.TrainingDummyKey) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> EnterRangerAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Cond").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public void Suppressing_arrow_and_marking_shot_carry_skill_cond_data()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var suppress = data.Skill(SuppressingArrow)!;
        var mark = data.Skill(MarkingShot)!;
        Assert.Equal("HasAbnormalGroupId", suppress.SkillCond!.Type);
        Assert.Equal(PrecisionGroup, suppress.SkillCond.AbnormalGroup);
        Assert.Equal("Self", suppress.SkillCond.SelectTarget);
        Assert.Equal("override", suppress.SkillCondSource);
        Assert.Equal(PrecisionGroup, mark.SkillCondGrant!.Abnormal);
        Assert.Equal("Self", mark.SkillCondGrant.SelectTarget);
        Assert.Equal(10_000, mark.SkillCondGrant.DurationMs);
    }

    [Fact]
    public async Task Suppressing_arrow_is_refused_without_precision()
    {
        var session = await EnterRangerAsync();
        var replies = await session.HandleAsync(Cast(SuppressingArrow), Now + 60_000);
        Assert.False(Accepted(replies));
    }

    [Fact]
    public async Task Marking_shot_applies_precision_on_caster_then_suppressing_arrow_is_accepted()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 60_000;
        var markReplies = await session.HandleAsync(Cast(MarkingShot, mob.Key), t);
        Assert.True(Accepted(markReplies));
        Assert.Contains(markReplies, p => GameSessionTestHelpers.Op(p) == 0x382a && AbnormalPacketKey(p) == session.PlayerKey);
        Assert.Contains(session.ActiveBuffs, id => id == PrecisionGroup);
        Assert.False(session.NpcHasAbnormalGroup(mob.Key, PrecisionGroup, null));
        var suppressReplies = await session.HandleAsync(Cast(SuppressingArrow, mob.Key), t + 500);
        Assert.True(Accepted(suppressReplies));
    }

    [Fact]
    public async Task Suppressing_arrow_skill_cond_checks_caster_precision_group()
    {
        var session = await EnterRangerAsync();
        var mob = session.Targets![Combat.TrainingDummyKey];
        var cond = new SkillEffectConditions.Condition("Caster_HasAbnormalGroupId", [PrecisionGroup.ToString()]);
        var ctx = session.BuildEffectConditionContext(null, null, SuppressingArrow, mob.Key, true, Now + 60_000);
        Assert.False(SkillEffectConditions.Met(cond, ctx));
        await session.HandleAsync(Cast(MarkingShot, mob.Key), Now + 60_000);
        Assert.True(SkillEffectConditions.Met(cond, ctx));
    }
}
