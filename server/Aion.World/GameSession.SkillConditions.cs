using Aion.Protocol.Native;

namespace Aion.World;

// SkillCond.dat (SkillCondTableItem): conditional skills greyed until an abnormal group or effect type is active on Self or Target.
// Skill.dat: CanUseWhenElementalSummonSpawned, CanUseWhenPeaceState, CanUseSkillCostType. Prerequisite casts apply skill_cond_grant.
partial class GameSession
{
    /// <summary>Every cast rule the client enforces before accept (SkillCond + Skill.dat use fields).</summary>
    bool UseConditionMet(SkillDefinition? definition, long now, int castTargetKey = 0, int skillId = 0) =>
        definition is not null && SkillCooldown.IsBasicAutoAttack(definition)
        || SpiritConditionMet(definition, skillId)
            && PeaceConditionMet(definition)
            && ResourceConditionMet(definition)
            && SkillCondMet(definition, now, castTargetKey);

    bool PeaceConditionMet(SkillDefinition? definition) =>
        definition is not { NeedsPeace: true } || !inCombat;

    bool ResourceConditionMet(SkillDefinition? definition) =>
        resources is null || SkillResourceThreshold.Met(definition?.NeedsResource, resources.Hp, resources.HpMax, resources.Mp, resources.MpMax, resources.Ap, resources.ApMax);

    /// <summary>Whether the required abnormal state for a conditional skill is present (CondSelectTarget Self / Target).</summary>
    bool SkillCondMet(SkillDefinition? definition, long now, int castTargetKey = 0)
    {
        if (definition?.SkillCond is not { } cond) return true;
        return cond.SelectTarget?.Equals("Target", StringComparison.OrdinalIgnoreCase) == true
            ? SkillCondMetOnTarget(cond, now, castTargetKey)
            : SkillCondMetOnSelf(cond);
    }

    bool SkillCondMet(SkillDefinition? definition) => SkillCondMet(definition, 0, 0);

    bool SkillCondMetOnSelf(SkillCondNeed cond) => cond.Type switch
    {
        "HasAbnormalGroupId" when cond.AbnormalGroup is int group and > 0 => HasAbnormalGroup(group),
        "HasAbnormalEffectType" when cond.AbnormalEffect is { Length: > 0 } effect => HasAbnormalEffect(effect),
        _ => false,
    };

    bool SkillCondMetOnTarget(SkillCondNeed cond, long now, int castTargetKey)
    {
        var key = castTargetKey != 0 ? castTargetKey : selectedTarget;
        if (key >= SharedWorld.FirstPlayerKey && key != playerKey)
        {
            if (PvpVictimOf(key) is { Session: { dead: false } } victim)
                return PlayerTargetSkillCond(victim.Session, cond, now);
            return false;
        }
        if (targets?.TryGetValue(key, out var npc) != true || npc is null || npc.Dead || npc.OwnerKey != 0) return false;
        if (cond.Type == "HasAbnormalGroupId" && cond.AbnormalGroup is int group && group > 0)
            return npcBuffs.TryGetValue(npc.Key, out var list) && list.Any(b => b.AbnormalId == group || AbnormalStackRules.GroupOf(b.AbnormalId) == group);
        if (cond.Type == "HasAbnormalEffectType" && cond.AbnormalEffect is { Length: > 0 } effect)
            return NpcTargetHasAbnormalEffect(npc.Key, effect, now);
        return false;
    }

    static bool PlayerTargetSkillCond(GameSession target, SkillCondNeed cond, long now) => cond.Type switch
    {
        "HasAbnormalGroupId" when cond.AbnormalGroup is int group && group > 0 => target.PlayerHasAbnormalGroup(group, null),
        "HasAbnormalEffectType" when cond.AbnormalEffect is { Length: > 0 } effect => target.PlayerHasAbnormalEffect(effect, now, null),
        _ => false,
    };

    bool HasAbnormalEffect(string effectType) =>
        buffs.Any(b => AbnormalSpecs.Info(b.AbnormalId)?.Effects.Any(e => e.Type == effectType) == true);

    /// <summary>After accept: apply the buff a prerequisite skill grants (Precision from Marking Shot on the caster, toggles on self, …).</summary>
    List<byte[]> ApplySkillCondGrant(int skillId, SkillDefinition definition, long now, CastRequest request)
    {
        if (definition.SkillCondGrant is not { } grant) return [];
        if (grant.SelectTarget?.Equals("Target", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!TryGetCombatTarget(request.TargetKey, out var target) || target.Dead) return [];
            var targetReplies = new List<byte[]>();
            ApplyNpcDebuff(target, grant.Abnormal, grant.DurationMs, now, targetReplies, skillId: skillId);
            return targetReplies;
        }
        var speedBefore = BuffStat("MoveSpeed");
        var replies = new List<byte[]>();
        AddBuff(grant.Abnormal, grant.DurationMs, skillId, now, replies, path: SelfAbnormalPath.SkillCondGrant);
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }
}
