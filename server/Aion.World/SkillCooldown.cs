namespace Aion.World;

/// <summary>
/// Skill cooldowns from Skill.dat (class-skills.json cooldown_ms) plus playability rules the export omits.
/// NeedCoolTime is often 0 for chain basics; the client still sends a 1000 ms cooltime on SkillStart_NT
/// (attack_speed 10000, OBSERVED Gladiator Keen Strike chain-20261012-231745).
/// </summary>
public static class SkillCooldown
{
    public const int BasicAttackGcdMs = 1000;

    /// <summary>Early cast requests while auto-attack is held (client/server clock and latency).</summary>
    public const int CheckToleranceMs = 200;
    /// <summary>Least time between two accepted non-dodge casts (official Tempest Shot: 229 ms at the least).</summary>
    public const int MinCastGapMs = 200;

    /// <summary>
    /// Chain-free free basics (auto attack); not chain follow-ups or MP actives. Melee roots and their customizing variants
    /// are MainSelf; the Cleric, Ranger, Elementalist and Sorcerer roots are MainTarget, so the client's
    /// SkillContextRegisterType FixSlot (the PCContextSkillSlot quick slot 1 skill of each class) marks them.
    /// </summary>
    public static bool IsBasicAutoAttack(SkillDefinition skill) =>
        skill.ChainPrev == 0 && skill.CooldownMs == 0 && skill.Type == "Active" && skill.Damage.Length > 0
        && skill.Cost.Mp == 0 && skill.Cost.Sp == 0 && skill.Cost.Hp == 0
        && (string.Equals(skill.Target, "MainSelf", StringComparison.Ordinal) || IsFixSlot(skill));

    public static bool IsFixSlot(SkillDefinition skill) => string.Equals(skill.ContextRegister, "FixSlot", StringComparison.Ordinal);

    /// <summary>Cooltime field on SkillStart_NT (what the client UI counts down).</summary>
    public static int EffectiveMs(SkillDefinition? skill)
    {
        if (skill is null) return 0;
        if (skill.CooldownMs > 0) return skill.CooldownMs;
        if (skill.Type != "Active" || skill.Damage.Length == 0) return 0;
        if (skill.Cost.Mp > 0 || skill.Cost.Sp > 0 || skill.Cost.Hp > 0) return 0;
        if (string.Equals(skill.Target, "MainSelf", StringComparison.Ordinal) || IsBasicAutoAttack(skill)) return BasicAttackGcdMs;
        return 0;
    }

    /// <summary>A cooldown less CoolTimeDecrease basis points (20000 ms at 300 = 19400, OBSERVED official).</summary>
    public static int Reduced(int cooldownMs, int decreaseBp) =>
        cooldownMs <= 0 || decreaseBp <= 0 ? cooldownMs : (int)((long)cooldownMs * Math.Max(0, 10_000 - decreaseBp) / 10_000);

    internal static int SwingEndMs(SkillDefinition skill) =>
        skill.Damage.Length > 0 ? SkillTiming.HitLandMs(skill, skill.Damage.Length - 1) : 0;

    /// <summary>
    /// Milliseconds after cast accept before the server allows the next use of the cooldown group.
    /// Dat NeedCoolTime and chain-free basics get a server lockout; zero-cooldown multi-hit actives are held until their last row lands,
    /// so a held key (the client resends 0x3800 every ~20-700 ms) cannot restart the swing and pay MP again mid-combo (Templar Pummel).
    /// </summary>
    /// <summary>
    /// Zero-cooldown projectile actives (SkillProjectile.dat): the shot leaves at release, so no hold until the arrows land.
    /// OBSERVED official 2026-10-30 Ranger (2 captures): Tempest Shot 14340000 re-accepted 229 ms apart at the least (median 523 ms).
    /// </summary>
    public const int ProjectileRecastMs = 200;

    public static int LockoutMs(SkillDefinition skill, bool projectile)
    {
        if (projectile && skill.CooldownMs == 0 && !IsBasicAutoAttack(skill)) return ProjectileRecastMs;
        return LockoutMs(skill);
    }

    public static int LockoutMs(SkillDefinition skill)
    {
        var swingEnd = SwingEndMs(skill);
        if (skill.CooldownMs > 0) return Math.Max(skill.CooldownMs, swingEnd);
        if (IsBasicAutoAttack(skill)) return Math.Max(BasicAttackGcdMs, swingEnd);
        if (skill.Damage.Length > 1) return Math.Max(EffectiveMs(skill), swingEnd);
        return EffectiveMs(skill);
    }

    public static bool CooldownReady(long now, IReadOnlyDictionary<int, long> cooldowns, int cooldownKey, bool noCool)
    {
        if (noCool) return true;
        if (!cooldowns.TryGetValue(cooldownKey, out var readyAt)) return true;
        return now + CheckToleranceMs >= readyAt;
    }
}
