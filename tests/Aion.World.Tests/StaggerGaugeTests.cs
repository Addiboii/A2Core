using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Spiritmaster stagger gauge and Rapid Scattershot SkillCond (HasAbnormalEffectType Stagger on target).</summary>
public sealed class StaggerGaugeTests
{
    const long Now = 1_760_004_000_000;
    const int Combustion = 16_040_000;
    const int RapidScattershot = 16_340_000;
    const int Dummy = Combat.TrainingDummyKey;

    static byte[] Cast(int skill, int target = Dummy) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> EnterElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Stagger").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static async Task AfterHitsAsync(GameSession session, long castAt) =>
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, NpcAi.HitDelayMs + 80);

    [Fact]
    public void Rapid_scattershot_requires_stagger_on_target_in_data()
    {
        var skill = GameSessionTestHelpers.LiveGameData.Skill(RapidScattershot)!;
        Assert.Equal("HasAbnormalEffectType", skill.SkillCond!.Type);
        Assert.Equal("Stagger", skill.SkillCond.AbnormalEffect);
        Assert.Equal("Target", skill.SkillCond.SelectTarget);
        Assert.Equal(3, GameSessionTestHelpers.LiveGameData.Skill(Combustion)!.StaggerGauge);
    }

    [Fact]
    public async Task Rapid_scattershot_refused_without_stagger()
    {
        var session = await EnterElementalistAsync();
        var replies = await session.HandleAsync(Cast(RapidScattershot), Now + 60_000);
        Assert.False(Accepted(replies));
    }

    [Fact]
    public async Task Rapid_scattershot_accepted_when_cast_target_is_staggered()
    {
        var session = await EnterElementalistAsync();
        var t = Now + 60_000;
        session.Targets![Dummy].StaggerUntil = t + 5000;
        session.Targets![Dummy].Held = true;
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot), t)));
    }

    [Fact]
    public async Task Combustion_increases_target_stagger_gauge()
    {
        var session = await EnterElementalistAsync();
        var t = Now + 60_000;
        var cast = await session.HandleAsync(Cast(Combustion), t);
        Assert.True(Accepted(cast));
        await AfterHitsAsync(session, t);
        Assert.Equal(3, session.Targets![Dummy].StaggerGauge);
    }
}
