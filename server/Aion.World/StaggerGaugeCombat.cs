using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// NPC stagger gauge (client 그로기 게이지): skills deal gauge damage; at max the target enters Stagger (EAbnormalEffectType) briefly.
/// Values from tools/data/stagger-gauge-overrides.json on skills; improvised when tables are missing.
/// </summary>
public static class StaggerGaugeCombat
{
    public const int DefaultGaugeMax = 100;
    public const int StaggerAbnormalId = 100_000_401;
    public const int StaggerDurationMs = 4000;

    static int uidSeed = 3_000_000;

    public static int GaugeMax(Target target) =>
        target.StaggerGaugeMax > 0 ? target.StaggerGaugeMax : DefaultGaugeMax;

    public static int GaugeDamage(GameData data, int skillId) =>
        data.Skill(skillId)?.StaggerGauge ?? 0;

    /// <summary>Add gauge on a hostile NPC hit (row 0 only). Returns packets to broadcast (gauge break uses 0x382a).</summary>
    public static List<byte[]> AddGaugeDamage(
        Target victim,
        int gaugeDamage,
        int casterKey,
        long now,
        GameSession? session,
        bool firstRow)
    {
        if (gaugeDamage <= 0 || !firstRow || victim.Dead || victim.OwnerKey != 0) return [];
        if (victim.StaggerUntil > now) return [];
        victim.StaggerGaugeMax = GaugeMax(victim);
        victim.StaggerGauge = Math.Min(GaugeMax(victim), victim.StaggerGauge + gaugeDamage);
        if (victim.StaggerGauge < GaugeMax(victim)) return [];
        victim.StaggerGauge = 0;
        return session is not null
            ? session.BreakNpcStagger(victim, now)
            : BreakStaggerShared(victim, casterKey, now);
    }

    static List<byte[]> BreakStaggerShared(Target victim, int casterKey, long now)
    {
        victim.StaggerUntil = now + StaggerDurationMs;
        victim.Held = true;
        var uid = Interlocked.Increment(ref uidSeed);
        return
        [
            PlayerPackets.AbnormalAdd(victim.Key,
            [
                new AbnormalWire(uid, StaggerAbnormalId, StaggerDurationMs, now + StaggerDurationMs,
                    CasterKey: casterKey, RangeBase: victim.Position),
            ]),
        ];
    }

    public static void StaggerTick(IReadOnlyDictionary<int, Target>? targets, long now)
    {
        if (targets is null) return;
        foreach (var t in targets.Values)
        {
            if (t.StaggerUntil <= 0 || now < t.StaggerUntil) continue;
            t.StaggerUntil = 0;
            if (t.Held) t.Held = false;
        }
    }

    /// <summary>PvP player target: gauge toward Stagger abnormal (row 0 hits only).</summary>
    public static List<byte[]> AddPlayerGaugeDamage(GameSession victim, int gaugeDamage, int casterKey, long now, bool firstRow)
    {
        if (gaugeDamage <= 0 || !firstRow || victim.Dead || victim.StaggerUntil > now) return [];
        victim.StaggerGaugeMax = victim.StaggerGaugeMax > 0 ? victim.StaggerGaugeMax : DefaultGaugeMax;
        victim.StaggerGauge = Math.Min(victim.StaggerGaugeMax, victim.StaggerGauge + gaugeDamage);
        if (victim.StaggerGauge < victim.StaggerGaugeMax) return [];
        victim.StaggerGauge = 0;
        return victim.BreakPlayerStagger(now, casterKey);
    }
}
