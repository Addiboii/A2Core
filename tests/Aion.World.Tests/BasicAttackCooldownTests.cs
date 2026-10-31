using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Auto-attack GCD vs multi-hit swing timing and held-attack request spam.</summary>
public sealed class BasicAttackCooldownTests
{
    static readonly long Now = 1_700_000_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public void Dual_wield_basic_lockout_covers_last_hit_and_gcd()
    {
        var skill = GameData.Embedded.Skill(13010000);
        Assert.NotNull(skill);
        var swingEnd = SkillTiming.HitLandMs(skill!, skill!.Damage.Length - 1);
        Assert.Equal(NpcAi.HitDelayMs + SkillTiming.MultiHitStaggerMs, swingEnd);
        Assert.Equal(Math.Max(SkillCooldown.BasicAttackGcdMs, swingEnd), SkillCooldown.LockoutMs(skill!));
    }

    [Fact]
    public void Real_skill_keeps_dat_cooldown_after_swing()
    {
        var skill = GameData.Embedded.Skill(11290000)!;
        Assert.Equal(20_000, SkillCooldown.LockoutMs(skill));
        var map = new Dictionary<int, long> { [11290000] = Now + 20_000 };
        Assert.True(SkillCooldown.CooldownReady(Now + 20_000 - 100, map, 11290000, noCool: false));
        Assert.False(SkillCooldown.CooldownReady(Now + 20_000 - SkillCooldown.CheckToleranceMs - 50, map, 11290000, noCool: false));
        Assert.True(SkillCooldown.CooldownReady(Now, map, 999999, noCool: false));
    }

    [Fact]
    public async Task Auto_attack_chain_accepts_each_gcd_window()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(8));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11020000;
        var skill = GameData.Embedded.Skill(skillId)!;
        var period = SkillCooldown.LockoutMs(skill);
        for (var swing = 0; swing < 3; swing++)
        {
            var castAt = Now + 1000 + swing * period;
            var replies = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt);
            Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
            await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        }
    }

    [Theory]
    [InlineData("Gladiator", 11020000)]
    [InlineData("Templar", 12010000)]
    [InlineData("Assassin", 13010000)]
    [InlineData("Chanter", 18010000)]
    [InlineData("Cleric", 17010000)]
    [InlineData("Ranger", 14020000)]
    [InlineData("Elementalist", 16010000)]
    [InlineData("Sorcerer", 15210000)]
    public async Task Basic_atk_request_starts_the_fix_slot_root_for_every_class(string className, int root)
    {
        var data = GameData.Embedded;
        var skill = data.Skill(root)!;
        Assert.True(SkillCooldown.IsBasicAutoAttack(skill));
        Assert.Equal(SkillCooldown.BasicAttackGcdMs, SkillCooldown.EffectiveMs(skill));
        Assert.Equal(Math.Max(SkillCooldown.BasicAttackGcdMs, SkillTiming.HitLandMs(skill, skill.Damage.Length - 1)),
            SkillCooldown.LockoutMs(skill, projectile: true));

        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == className);
        Assert.Equal(root, data.Profile(creation, 1)!.DefaultBar().Single(s => s.Slot == 0).SkillId);
        var store = new FakeCharacterStore();
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("BasicHero").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];

        var atk = new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(dummy.Key).ToArray();
        static ushort? AtkResult(List<byte[]> replies) =>
            replies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs) is { } p ? BitConverter.ToUInt16(p, 2) : null;
        var rootBytes = BitConverter.GetBytes(root);
        var castAt = Now + 1000;
        var first = await session.HandleAsync(atk, castAt);
        Assert.Equal((ushort)0, AtkResult(first));
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3802 && p.AsSpan().IndexOf(rootBytes) > 0);
        var resend = await session.HandleAsync(atk, castAt + 17);
        Assert.Equal((ushort)1, AtkResult(resend));
        Assert.DoesNotContain(resend, p => GameSessionTestHelpers.Op(p) == 0x3802);
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        var next = await session.HandleAsync(atk, castAt + SkillCooldown.LockoutMs(skill, projectile: true));
        Assert.Equal((ushort)0, AtkResult(next));
        Assert.Contains(next, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Dat_cooldown_skill_still_refuses_early_recast_with_cast_reply()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(9));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = GameSession.CombatDodge;
        var castAt = Now + 1000;
        var selfCast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(session.PlayerKey).F32(0)
            .Vec3(session.Position ?? Vector3d.Zero).U8(9).ToArray();
        var ok = await session.HandleAsync(selfCast, castAt);
        Assert.Contains(ok, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var early = await session.HandleAsync(selfCast, castAt + 100);
        Assert.Contains(early, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }
}
