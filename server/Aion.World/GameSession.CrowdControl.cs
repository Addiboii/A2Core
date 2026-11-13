using Aion.Protocol.Native;

namespace Aion.World;

// Shared skill cooldown groups and the player's crowd control (authority review 2026-11-09: neither was enforced).
//  - Cooldowns run per Skill.dat GroupCoolTimeId (skill-behaviour.json group_cooldown, OFFICIAL client data): the levels and
//    specialisations of one skill (11050000, 11050010 ... share 11050000) and the few skills that share a group cool down
//    together, as the client shows them and as the official 0x3847 SkillCooltimeUpdate_NT names them (by group). Charge skills
//    keep their base skill's key, the sprint dash its dodge's.
//  - A player under an abnormal whose SkillAbnormalEffect blocks acting (Stun, Sleep, Stone, Frozen, Fear, KnockdownInstant,
//    Stiffen, Aerial, Move_CannotUseSkill, BlockSkill / BlockActive: silence) cannot cast: the cast is refused with EResult
//    6152 Skill_Abnormal. Skills with Skill.dat IgnoreCannotUseSkill (the escape skills, 290 player skills: client-combat.json
//    cc_free) still go. CANDIDATE: the refusal code (6152 is the official code of an NPC cast broken by crowd control, 0x380c;
//    no official sample of a refused player cast under a stun) and which effect types block (no table says it; names).
public sealed partial class GameSession
{
    /// <summary>The SkillAbnormalEffect types that stop the player from casting (CANDIDATE list, see above).</summary>
    public static readonly IReadOnlySet<string> CastBlockingEffects = new HashSet<string>(StringComparer.Ordinal)
    {
        "Stun", "Sleep", "Stone", "Frozen", "Fear", "DeletableFear", "KnockdownInstant", "Stiffen", "Aerial",
        "Move_CannotUseSkill", "BlockSkill", "BlockActive",
    };

    /// <summary>The key a skill's cooldown runs under: a charge skill's base, else its group (GroupCoolTimeId), else itself.</summary>
    int CooldownKeyOf(int skill) => ChargeBaseOf(skill) ?? (Behaviour.Of(skill)?.GroupCooldown is > 0 and var group ? group : skill);

    /// <summary>Whether an active abnormal on the player blocks casting <paramref name="skill"/> now (an IgnoreCannotUseSkill skill never).</summary>
    bool CcBlocksCast(int skill, long now)
    {
        if (buffs.Count == 0 || Combats.CcFree(skill)) return false;
        foreach (var b in buffs)
        {
            if (b.DurationMs >= 0 && b.ExpireAt <= now) continue;
            var types = AbnormalTable.Embedded.Of(b.AbnormalId)?.Effects.Select(e => e.Type) ?? Boxes.Abnormal(b.AbnormalId)?.Effects.Select(e => e.Type) ?? [];
            if (types.Any(CastBlockingEffects.Contains)) return true;
        }
        return false;
    }

    internal bool CcBlocksCastForChecks(int skill, long now) => CcBlocksCast(skill, now);
    internal int CooldownKeyForChecks(int skill) => CooldownKeyOf(skill);
    internal void AddBuffForChecks(int abnormalId, int durationMs, int skill, long now) => AddBuff(abnormalId, durationMs, skill, now, []);
    internal void ClearBuffsForChecks() => buffs.Clear();
}
