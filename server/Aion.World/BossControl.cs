namespace Aion.World;

/// <summary>
/// Boss stun control (user 2026-11-08: a big group must not stun-lock a boss): diminishing stuns with an immunity, and a stagger
/// gauge that every stun attempt fills; a full gauge drops the boss for a damage window. The state lives on the Target, so every
/// player on a shared map fills the same gauge. Numbers: server-rules.xml bosses (PROVISIONAL).
/// </summary>
public static class BossControl
{
    /// <summary>
    /// A player's stun or hold of <paramref name="durationMs"/> on a boss: the duration it really gets (0 = shrugged off) and
    /// whether this one is the stagger (the boss collapses for stagger_ms, vulnerable).
    /// </summary>
    public static (int DurationMs, bool Stagger) OnCrowdControl(Target t, int durationMs, long now, ServerRules? rules = null)
    {
        rules ??= ServerRules.Embedded;
        // The gauge drains between attempts, then fills with this one.
        if (t.StaggerAt != 0) t.StaggerGauge = Math.Max(0, t.StaggerGauge - (now - t.StaggerAt) / 1000.0 * rules.BossStaggerDecayPerS);
        t.StaggerAt = now;
        if (now >= t.StaggerImmuneUntil)
        {
            t.StaggerGauge += rules.BossStaggerPerCc;
            if (t.StaggerGauge >= 100)
            {
                t.StaggerGauge = 0;
                t.StaggerImmuneUntil = now + rules.BossStaggerMs + rules.BossStaggerImmuneMs;
                t.VulnerableUntil = now + rules.BossStaggerMs;
                t.CcCount = 0;
                t.CcImmuneUntil = 0;
                return (rules.BossStaggerMs, true);
            }
        }
        if (now < t.CcImmuneUntil) return (0, false);
        if (now - t.CcWindowStart > rules.BossCcWindowMs) { t.CcWindowStart = now; t.CcCount = 0; }
        t.CcCount++;
        var duration = (int)(durationMs / Math.Pow(2, t.CcCount - 1));   // each one in the window half as long
        if (t.CcCount >= rules.BossCcMax)
        {
            t.CcImmuneUntil = now + Math.Max(0, duration) + rules.BossCcImmuneMs;
            t.CcCount = 0;
        }
        return (duration >= 300 ? duration : 0, false);
    }

    /// <summary>A hit on a staggered boss: stagger_damage_percent more.</summary>
    public static int Vulnerable(Target t, int amount, long now, ServerRules? rules = null) =>
        amount > 0 && now < t.VulnerableUntil ? (int)Math.Round(amount * (1 + (rules ?? ServerRules.Embedded).BossStaggerDamagePercent / 100.0)) : amount;
}
