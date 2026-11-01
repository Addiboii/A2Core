using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Boss stagger gauge (client 그로기 / thin bar under HP): skills deal gauge damage; at max the target enters Stagger (EAbnormalEffectType).
/// The client reads <see cref="GamePackets.UpdateGroggyInfo"/> (0xe005); guard energy drains as gauge fills.
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

    /// <summary>Reference HP budget for exported stagger_gauge table values (5000 HP sandbag × elite break fraction).</summary>
    public const int ReferenceHpBudgetForTable = 1750;

    /// <summary>
    /// Share of max HP that should be dealt (per break) before the groggy bar empties.
    /// Strong bosses ~15–20% (Legend/Hero); weaker elites ~30–40%. OURS when GroggyData max/recovery are not exported.
    /// </summary>
    public static double HpLossFractionPerStaggerBreak(Target target) => (target.SubType, target.Grade) switch
    {
        ("LegendMonster", _) => 0.175,
        ("HeroMonster", >= 5) => 0.20,
        ("HeroMonster", _) => 0.25,
        ("EliteMonster", _) => 0.35,
        _ when target.Grade >= 5 => 0.20,
        _ => 0.35,
    };

    public static int HpBudgetPerStaggerBreak(Target target)
    {
        var hpMax = target.HpMax > 0 ? target.HpMax : 1;
        return Math.Max(1, (int)Math.Round(hpMax * HpLossFractionPerStaggerBreak(target)));
    }

    static bool UsesHpScaledGauge(Target target) =>
        HasBossGauge(target) || target.StaggerGaugeMax > 0 || target.GroggyMax > 0;

    public static int GaugeDamage(GameData data, int skillId, Target? target = null, int hitDamage = 0)
    {
        if (data.Skill(skillId) is not { } def) return 0;
        var gaugeMax = target is not null ? GaugeMax(target) : DefaultGaugeMax;
        var hpBudget = target is not null ? HpBudgetPerStaggerBreak(target) : ReferenceHpBudgetForTable;

        if (hitDamage > 0 && target is not null && UsesHpScaledGauge(target))
            return Math.Max(1, (int)Math.Round(gaugeMax * (double)hitDamage / hpBudget));

        int baseGauge;
        if (def.StaggerGauge > 0)
            baseGauge = def.StaggerGauge;
        else if (!def.Type.Equals("Active", StringComparison.OrdinalIgnoreCase) || def.Damage.Length == 0)
            return 0;
        else
        {
            // SkillEffect GroggyGuardDecreaseEnergy rows are not fully exported; MP bands from wiki (OURS).
            baseGauge = def.Cost.Mp switch
            {
                <= 50 => 3,
                <= 200 => 7,
                _ => 10,
            };
        }

        if (target is null || hpBudget == ReferenceHpBudgetForTable) return baseGauge;
        return Math.Max(1, (int)Math.Round(baseGauge * (double)ReferenceHpBudgetForTable / hpBudget));
    }

    /// <summary>0xe005 _groggy_guard_energy: remaining bar (max when untouched, 0 when the bar breaks).</summary>
    public static int WireGroggyEnergy(Target target) =>
        target.StaggerGaugeMax > 0 || target.GroggyMax > 0
            ? Math.Max(0, GaugeMax(target) - target.StaggerGauge)
            : 0;

    /// <summary>Enable stagger gauge on bosses (NpcSubType / grade from tools/world_npcs.py).</summary>
    public static void InitBossGauge(Target target)
    {
        if (target.GroggyMax > 0) return;
        if (!HasBossGauge(target)) return;
        target.StaggerGaugeMax = DefaultGaugeMax;
        target.StaggerGauge = 0;
        SyncGroggyFromGauge(target);
    }

    public static bool HasBossGauge(Target target) =>
        target.OwnerKey == 0 && (target.SubType is "EliteMonster" or "HeroMonster" or "LegendMonster" || target.Grade >= 5);

    /// <summary>Client guard energy is full at 0 internal gauge and empty at break.</summary>
    public static void SyncGroggyFromGauge(Target target)
    {
        if (target.StaggerGaugeMax <= 0) return;
        target.GroggyMax = target.StaggerGaugeMax;
        target.GroggyEnergy = WireGroggyEnergy(target);
    }

    public static void SyncGaugeFromGroggy(Target target)
    {
        if (target.GroggyMax <= 0) return;
        target.StaggerGaugeMax = target.GroggyMax;
        target.StaggerGauge = Math.Max(0, target.GroggyMax - target.GroggyEnergy);
    }

    public static void ResetGauge(Target target)
    {
        if (target.GroggyMax <= 0 && target.StaggerGaugeMax <= 0) return;
        target.StaggerGauge = 0;
        if (target.StaggerGaugeMax <= 0) target.StaggerGaugeMax = DefaultGaugeMax;
        SyncGroggyFromGauge(target);
    }

    public static byte[]? GroggyUpdatePacket(Target target, bool activated = false)
    {
        if (target.GroggyMax <= 0 && target.StaggerGaugeMax <= 0) return null;
        var max = GaugeMax(target);
        if (target.GroggyMax <= 0) target.GroggyMax = max;
        var energy = WireGroggyEnergy(target);
        target.GroggyEnergy = energy;
        return GamePackets.UpdateGroggyInfo(target.Key, max, energy, activated);
    }

    public static void AppendGroggyWire(Target target, List<byte[]> replies, bool activated = false)
    {
        var pkt = GroggyUpdatePacket(target, activated);
        if (pkt is not null) replies.Add(pkt);
    }

    public static List<byte[]> NpcAppearPackets(NpcAppearance appear, Target target)
    {
        var replies = new List<byte[]> { GamePackets.NpcAppear(appear) };
        AppendGroggyWire(target, replies);
        return replies;
    }

    /// <summary>Add gauge on a hostile NPC hit (row 0 only). Returns packets to broadcast.</summary>
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
        InitBossGauge(victim);
        victim.StaggerGaugeMax = GaugeMax(victim);
        victim.StaggerGauge = Math.Min(GaugeMax(victim), victim.StaggerGauge + gaugeDamage);
        if (victim.GroggyMax > 0) SyncGroggyFromGauge(victim);
        if (victim.StaggerGauge < GaugeMax(victim))
            return GroggyUpdatePacket(victim) is { } pkt ? [pkt] : [];
        // Bar depleted (energy 0 on wire) before stagger; refill only after StaggerUntil in StaggerTick.
        var breakReplies = session is not null
            ? session.BreakNpcStagger(victim, now)
            : BreakStaggerShared(victim, casterKey, now);
        AppendGroggyWire(victim, breakReplies, activated: true);
        return breakReplies;
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

    /// <summary>Clear stagger state and refill the client gauge when the window ends.</summary>
    public static List<byte[]> StaggerTick(IReadOnlyDictionary<int, Target>? targets, long now)
    {
        if (targets is null) return [];
        var replies = new List<byte[]>();
        foreach (var t in targets.Values)
        {
            if (t.StaggerUntil <= 0 || now < t.StaggerUntil) continue;
            t.StaggerUntil = 0;
            if (t.Held) t.Held = false;
            ResetGauge(t);
            AppendGroggyWire(t, replies);
        }
        return replies;
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
