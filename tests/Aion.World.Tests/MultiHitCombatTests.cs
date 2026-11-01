using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Multi-swing skills (dual wield, combos) must emit one 0x3804 per damage row with a shared action uid.</summary>
public sealed class MultiHitCombatTests
{
    static readonly long Now = 1_700_000_000_000;

    [Fact]
    public void Dual_row_skills_have_two_damage_rows_in_embedded_data()
    {
        var skill = GameData.Embedded.Skill(11010000);
        Assert.NotNull(skill);
        Assert.Equal(2, skill!.Damage.Length);
        Assert.Equal(1101000011, skill.Damage[0].EffectId);
        Assert.Equal(1101000021, skill.Damage[1].EffectId);
        var hits = Combat.SkillHits(GameData.Embedded, 11010000, "Gladiator", 25, 110160001, 0, new Random(4));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Cast_at_training_dummy_emits_two_skill_hits_with_matching_action_uid()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(4));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var beforeHp = dummy.Hp;
        const int skillId = 11010000;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(dummy.Key).F32(0)
            .Vec3(dummy.Position).U8(9).ToArray();
        var castAt = Now + 1000;
        var replies = await session.HandleAsync(cast, castAt);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x3804);
        var landed = await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, NpcAi.HitDelayMs + SkillTiming.MultiHitStaggerMs + 80);
        var hitPackets = landed.Where(p => GameSessionTestHelpers.Op(p) == 0x3804).ToList();
        Assert.Equal(2, hitPackets.Count);
        byte actionUid = hitPackets[0][ParseSkillEffectActionUidOffset(hitPackets[0])];
        Assert.Equal(actionUid, hitPackets[1][ParseSkillEffectActionUidOffset(hitPackets[1])]);
        Assert.True(beforeHp > dummy.Hp);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x8d00);
    }

    [Fact]
    public void SkillHit_matches_official_assassin_dual_wield_capture()
    {
        var expected = PlayerPackets.SkillEffect(37476, 5861, 13030000, 1, 1303000011, 1, 21, 10000, 2);
        var built = GamePackets.SkillHit(37476, 5861, 13030000, 1303000011, 21, actionUid: 1, seq: 1);
        Assert.Equal(expected, built);
        var second = GamePackets.SkillHit(37476, 5861, 13030000, 1303000021, 21, actionUid: 1, seq: 2);
        Assert.Equal(PlayerPackets.SkillEffect(37476, 5861, 13030000, 1, 1303000021, 2, 21, 10000, 2), second);
    }

    /// <summary>After opcode and keys, player 0x3804 switch 04 places action uid at a fixed offset for these fixtures.</summary>
    static int ParseSkillEffectActionUidOffset(byte[] packet)
    {
        var i = 2;
        while ((packet[i] & 0x80) != 0) i++;
        i += 3;
        while ((packet[i] & 0x80) != 0) i++;
        i += 5;
        return i;
    }
}
