using Aion.Protocol.Native;

namespace Aion.World;

// Debuffs that change a monster's numbers, and damage over time (skill guide, tools/data/skill-guide.json; user request 2026-11-08:
// "debuffs that really lower a monster's defense or attack, and damage over time that ticks"). Until now a debuff skill only put
// its icon on the monster and a DoT skill hit once.
public sealed partial class GameSession
{
    /// <summary>A damage-over-time effect running on a monster: who cast it, how much per tick, when the next tick and the end are.</summary>
    /// <summary>
    /// A damage-over-time effect running on a monster: who cast it, how much per tick, when the next tick and the end are. With
    /// <see cref="AbnormalUid"/> the ticks are the official 0x3805 SkillEffectByAbnormal_NT of the skill's DoT abnormal (its uid
    /// and SkillAbnormalEffect id, the table's tick interval); without one (a guide-only DoT, no client abnormal) a plain hit.
    /// </summary>
    sealed record Dot(int TargetKey, int Skill, int EffectId, int PerTick, int IntervalMs, long UntilMs, int AbnormalUid = 0)
    {
        public long NextAt { get; set; }
    }

    readonly List<Dot> dots = [];

    /// <summary>The guide's stat names on the monster side: Defense, Attack, MoveSpeed, Tolerance (all damage taken), Tol:Fire...</summary>
    internal static string? MonsterStat(string guideStat)
    {
        var s = guideStat.Trim();
        if (s.Contains("Incoming Heal", StringComparison.OrdinalIgnoreCase) || s.Contains("Potion", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("PvP", StringComparison.OrdinalIgnoreCase)) return null;   // players only
        foreach (var el in new[] { "Fire", "Water", "Earth", "Wind" })
            if (s.StartsWith(el, StringComparison.OrdinalIgnoreCase) && s.Contains("Tolerance", StringComparison.OrdinalIgnoreCase)) return "Tol:" + el;
        if (s.Contains("Tolerance", StringComparison.OrdinalIgnoreCase)) return "Tolerance";
        if (s.Contains("Defense", StringComparison.OrdinalIgnoreCase)) return "Defense";
        if (s.Contains("Move Speed", StringComparison.OrdinalIgnoreCase) || s.Contains("Movement Speed", StringComparison.OrdinalIgnoreCase)) return "MoveSpeed";
        if (s.Contains("Attack", StringComparison.OrdinalIgnoreCase) || s.Contains("Damage Boost", StringComparison.OrdinalIgnoreCase)
            || s.Contains("damage dealt", StringComparison.OrdinalIgnoreCase)) return "Attack";
        return null;
    }

    /// <summary>A guide debuff on a monster the skill hit (each stat refreshed per skill, not stacked).</summary>
    void GuideDebuff(int skill, Target target, long now)
    {
        if (Guide.Of(skill) is not { DebuffMs: > 0 } g || g.DebuffStats.Count == 0) return;
        foreach (var (name, percent) in g.DebuffStats)
            if (MonsterStat(name) is { } stat) target.AddMod(stat, percent, now + g.DebuffMs, skill);
    }

    /// <summary>The monster's defense with its debuffs (Defense -30% = 0.7 x).</summary>
    internal static int EffectiveDefense(Target target, long now) =>
        (int)Math.Max(0, Math.Round(target.Defense * (1 + target.ModPercent("Defense", now) / 100.0)));

    /// <summary>Damage taken scaled by the monster's lowered tolerances (all damage, and the skill's element).</summary>
    internal static int TakenAfterMods(Target target, int amount, int skill, long now)
    {
        if (amount <= 0) return amount;
        var factor = 1 - target.ModPercent("Tolerance", now) / 100.0;
        if (ClientElement(skill) is { } el) factor *= 1 - target.ModPercent("Tol:" + el, now) / 100.0;
        return Math.Max(1, (int)Math.Round(amount * factor));
    }

    /// <summary>Starts (or refreshes) a skill's damage over time on a monster it hit: per tick the guide amount at the skill's level.</summary>
    void GuideDot(int skill, Target target, int firstHit, int effectId, long now)
    {
        // The DoT abnormal the cast just put on the monster (TargetDebuffs runs first): client table Dot_* effect (OFFICIAL).
        var abnormal = Behaviour.Of(skill)?.Abnormals is { } rows
            ? npcBuffs.GetValueOrDefault(target.Key)?.FirstOrDefault(b => rows.Any(r => r.Length > 3 && r[3] == b.AbnormalId) && AbnormalTable.Embedded.Of(b.AbnormalId)?.Dot is not null)
            : null;
        var guide = Guide.Of(skill);
        if (guide is not { DotMs: > 0 } && abnormal is null) return;
        var level = CombatSkillLevel(skill);
        var perTick = guide is { DotAmounts.Count: > 0 } ? guide.DotAmounts[Math.Min(level, guide.DotAmounts.Count) - 1]
            : Math.Max(1, firstHit / 10);   // no amount in the text: a tenth of the hit per tick (OURS, CANDIDATE)
        dots.RemoveAll(d => d.TargetKey == target.Key && d.Skill == skill);
        // The client tables' tick (SkillAbnormalEffectLv Dot_NormalCalc at the applied level: interval, flat + attack ratio): OFFICIAL.
        if (abnormal is not null && ClientDotTick(skill, abnormal.AbnormalId, target, now) is { } client)
        {
            dots.Add(new Dot(target.Key, skill, client.EffectId, client.PerTick, client.IntervalMs, abnormal.ExpireAt, abnormal.Uid) { NextAt = now + client.IntervalMs });
            return;
        }
        if (abnormal is not null && AbnormalTable.Embedded.Of(abnormal.AbnormalId)?.Dot is { } row)
        {
            // Official: a tick every values[0] ms (1000, some 2000) while the abnormal lasts.
            var every = Math.Max(250, row.Int(0) > 0 ? row.Int(0) : 1000);
            dots.Add(new Dot(target.Key, skill, row.Id, perTick, every, abnormal.ExpireAt, abnormal.Uid) { NextAt = now + every });
            return;
        }
        var interval = Math.Max(250, guide!.DotIntervalMs);
        dots.Add(new Dot(target.Key, skill, effectId, perTick, interval, now + guide.DotMs) { NextAt = now + interval });
    }

    /// <summary>Due damage-over-time ticks: the hit, the monster's HP, a kill like any other.</summary>
    List<byte[]> DotTick(long now)
    {
        var replies = DelayedCastTick(now);   // abnormals that cast a skill when they run out (GameSession.ClientSkills.cs)
        if (dots.Count == 0) return replies;
        foreach (var d in dots.ToList())
        {
            if (targets is null || !targets.TryGetValue(d.TargetKey, out var t) || t.Dead) { dots.Remove(d); continue; }
            while (d.NextAt <= now && d.NextAt <= d.UntilMs && !t.Dead)
            {
                d.NextAt += d.IntervalMs;
                var amount = t.Immune || t.Untouchable || t.HitGauge ? 0 : TakenAfterMods(t, d.PerTick, d.Skill, now);
                t.Hp = Math.Max(HpFloor(t), t.Hp - amount);
                replies.Add(d.AbnormalUid != 0 ? RidePackets.AbnormalDamage(t.Key, playerKey, d.AbnormalUid, d.EffectId, amount, d.Skill)
                    : GamePackets.SkillHit(t.Key, playerKey, d.Skill, d.EffectId, amount, false, 1));
                replies.AddRange(AfterNpcDamage(t, d.Skill, amount, now));
            }
            // The DoT ends with its abnormal (expired, or removed by anything else).
            var gone = d.AbnormalUid != 0 && npcBuffs.GetValueOrDefault(t.Key)?.Any(b => b.Uid == d.AbnormalUid) != true;
            if (d.NextAt > d.UntilMs || t.Dead || gone) dots.Remove(d);
        }
        return replies;
    }
}
