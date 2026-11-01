namespace Aion.World;

/// <summary>
/// When a class skill may put an abnormal on the caster (0x382a). Official: attack skills (damage, traps, zones, enemy debuffs)
/// never self-buff on hit; toggles and explicit self effects use <see cref="GameSession.ApplySkillCondGrant"/> on cast or
/// abnormal-only / friendly skills at resolve time. Chain accept uses the same bar as combat hit (Defiance-style buffs only).
/// </summary>
public static class SkillSelfAbnormalPolicy
{
    /// <summary>Hit-time <see cref="GameSession.ApplyCasterSkillAbnormals"/> and zone/trap follow-up hits.</summary>
    public static bool MayApplyOnCombatHit(GameData data, int skillId) => MayApplyCasterAreaRows(data, skillId);

    /// <summary>Chain parent on cast accept (<see cref="GameSession.ChainCasterAbnormalReplies"/>).</summary>
    public static bool MayApplyOnChainAccept(GameData data, int skillId) => MayApplyCasterAreaRows(data, skillId);

    static bool MayApplyCasterAreaRows(GameData data, int skillId)
    {
        if (data.IsBasicAttackChainSkill(skillId)) return false;
        if (GameSession.CastArea(data, skillId) is not { } area || !area.CasterDebuffRows.Any()) return false;
        if (data.Skill(skillId) is not { } def) return false;
        if (PcTraps.For(skillId) is not null || PcZones.For(skillId) is not null) return false;
        // Attack skills still skip inferred self buffs on hit, but explicit caster_debuff rows (caster EffectTarget) apply.
        if (def.Damage.Length > 0) return true;
        return string.Equals(def.Target, "Self", StringComparison.Ordinal) || SkillEffects.IsFriendly(skillId);
    }

    /// <summary>Central gate before <see cref="GameSession.AddBuff"/> sends 0x382a to the player.</summary>
    public static bool TryAllowPlayerBuff(GameData data, int abnormalId, int skillId, SelfAbnormalPath path, out string? denyReason)
    {
        denyReason = null;
        switch (path)
        {
            case SelfAbnormalPath.Unrestricted:
            case SelfAbnormalPath.PlayerDebuff:
            case SelfAbnormalPath.Scripted:
            case SelfAbnormalPath.DeathProtection:
            case SelfAbnormalPath.Dodge:
            case SelfAbnormalPath.Item:
            case SelfAbnormalPath.SkillCondGrant:
                return true;
            case SelfAbnormalPath.FriendlyReceive:
                if (skillId == 0) return true;
                if (MayApplyOnCombatHit(data, skillId)) return true;
                if (data.Skill(skillId) is { Damage.Length: 0 } && SkillEffects.IsFriendly(skillId)) return true;
                denyReason = "friendly_receive";
                return false;
            case SelfAbnormalPath.CombatHit:
                if (MayApplyOnCombatHit(data, skillId)) return true;
                denyReason = "combat_hit";
                return false;
            case SelfAbnormalPath.ChainAccept:
                if (MayApplyOnChainAccept(data, skillId)) return true;
                denyReason = "chain_accept";
                return false;
            default:
                denyReason = "unknown_path";
                return false;
        }
    }
}
