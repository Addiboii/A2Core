using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>NpcData NpcSubType as a fighting rank: how smart the NPC fights (OURS, user rule 2026-11-07: smarter than official).</summary>
public enum NpcRank { Normal, Elite, Hero }

/// <summary>
/// Monster tactics (OURS, user request 2026-11-07: "AI for all of them, what to use when, chase, smarter than official"). The NPC's
/// client data says what each skill can do (Skill.dat range, follow range, cooldown, casting time, damage factors; SkillCasterTeleport
/// leaps in npc-teleports.json); these rules decide WHEN to use it, by rank:
/// <list type="bullet">
/// <item>every ready skill in reach is scored each tick (damage, range against the victim's distance, leaps to close in or back off,
/// casts held until the victim stands still, no repeated special), the best one is used;</item>
/// <item>bosses and elites (Hero/Elite) open with specials sooner, use their mechanics as their HP crosses 75/50/25 %, enrage below
/// 30 % (shorter cooldowns, faster), and turn on whoever hurts them most;</item>
/// <item>all monsters lead a moving victim (aim where it is going), speed up when kept out of reach, call their kind for help, and
/// weak ones may run for help at low HP once per fight.</item>
/// </list>
/// Every number here is OURS and PROVISIONAL (gamedata has no AI tables); tune in one place.
/// Since 2026-11-13 (server-rules.xml monster_ai, user: "keep the bosses as official, rest of monsters sounds fun") bosses
/// (IsBoss) use none of this while boss_official is on, and the other monsters only while fun_monsters is on (NpcAi.Ours).
/// </summary>
public static class NpcTactics
{
    public static NpcRank RankOf(string? subType) => subType switch
    {
        "HeroMonster" or "LegendMonster" => NpcRank.Hero,
        "EliteMonster" => NpcRank.Elite,
        _ => NpcRank.Normal,
    };

    /// <summary>
    /// A boss by the client's NpcData (server-rules.xml monster_ai): NpcSubType HeroMonster / LegendMonster (Rank Hero; every
    /// named monster, bNamed, is one), or a boss stagger gauge (NpcGroggyType GroggyGuard / GroggyAttack, incl. 19 EliteMonster
    /// bosses) or bUseBossBerserk (both: ClientCombat boss_npcs). 1376 NPC types in the 2026-11-09 tables.
    /// </summary>
    public static bool IsBoss(Target target) => target.Rank == NpcRank.Hero || target.NpcId != 0 && ClientCombat.Embedded.Boss(target.NpcId) is not null;

    /// <summary>A special's first use: this share of its cooldown into the fight (the basic attack opens).</summary>
    public static double OpeningShare(NpcRank rank) => rank switch { NpcRank.Hero => 0.35, NpcRank.Elite => 0.6, _ => 1.0 };

    /// <summary>Below this HP share bosses and elites enrage.</summary>
    public const double EnrageBelow = 0.30;
    public static double EnragedCooldown(NpcRank rank) => rank switch { NpcRank.Hero => 0.7, NpcRank.Elite => 0.85, _ => 1.0 };
    public static int EnragedSpeed(NpcRank rank) => rank switch { NpcRank.Hero => 2000, NpcRank.Elite => 1000, _ => 0 };

    /// <summary>The HP shares at which a boss's mechanic is forced (each once per fight).</summary>
    public static readonly double[] Phases = [0.75, 0.50, 0.25];

    /// <summary>Kept out of reach this long while chasing: the NPC speeds up by its rank's boost (MoveSpeed stat, x(1 + v/10000)).</summary>
    public const int CatchUpAfterMs = 2500;
    public static int CatchUpBoost(NpcRank rank) => rank switch { NpcRank.Hero => 5000, NpcRank.Elite => 3500, _ => 2000 };

    /// <summary>How far ahead a chase aims along the victim's movement (seconds of its speed, at most LeadMax units).</summary>
    public const double LeadSeconds = 0.6, LeadMax = 350.0;

    /// <summary>A victim slower than this (units/s) stands still: a cast is safe to start.</summary>
    public const double StillSpeed = 60.0;

    /// <summary>Call for help: the same NPC kind within HelpRange joins (Hero: any monster within HeroHelpRange), at most HelpMax.</summary>
    public const double HelpRange = 800.0, HeroHelpRange = 1200.0;
    public static int HelpMax(NpcRank rank) => rank switch { NpcRank.Hero => 5, NpcRank.Elite => 4, _ => 3 };

    /// <summary>Normal monsters below FleeBelow HP may run (FleeChance, once per fight) FleeDistance away for at most FleeMs, calling help.</summary>
    public const double FleeBelow = 0.2, FleeChance = 0.3, FleeDistance = 600.0;
    public const int FleeMs = 3000;

    /// <summary>Bosses/elites re-check their hate table this often and turn on a player with HateSwitch times the victim's hate.</summary>
    public const int HateCheckMs = 6000;
    public const double HateSwitch = 1.1;

    public enum LeapKind { None, Forward, Back, Home }

    /// <summary>A Teleport_Caster skill's direction: onto/through the target or toward it (Forward), away from it (Back), to the spawn.</summary>
    public static LeapKind KindOf(int skillId)
    {
        if (NpcTeleports.Of(skillId) is not { } leap) return LeapKind.None;
        if (leap.Anchor.StartsWith("Spawn", StringComparison.Ordinal)) return LeapKind.Home;
        if (leap.Anchor.StartsWith("Target", StringComparison.Ordinal)) return LeapKind.Forward;
        var angle = Math.Abs(GamePackets.PyMod(leap.Angle + 180, 360) - 180);
        return angle >= 90 ? LeapKind.Back : LeapKind.Forward;
    }

    /// <summary>What the scorer knows about the moment.</summary>
    public readonly record struct Situation(NpcRank Rank, double Distance, double MeleeReach, double HpShare, bool ForcePhase,
        double VictimSpeed, int LastSpecial, bool RangedFighter);

    /// <summary>
    /// A ready, in-reach skill's worth now (higher is better; NaN: do not use it now). The basic attack is worth 1.
    /// </summary>
    public static double Score(int skillId, NpcSkill skill, bool basic, in Situation s, double jitter)
    {
        var factor = (Math.Min(skill.DamageMin, skill.DamageMax) + Math.Max(skill.DamageMin, skill.DamageMax)) / 20000.0;
        if (basic) return 1.0 + jitter * 0.5;
        var score = skill.Effect != 0 ? Math.Max(1.05, factor) : 0.0;
        // A mechanic (no damage, a cast: a boss's roar, its helpers): when ready, and always at a phase.
        if (skill.Effect == 0) score = s.ForcePhase ? 10.0 : 1.6;
        switch (KindOf(skillId))
        {
            case LeapKind.Forward:
                // Closing in: only when the victim is beyond melee reach (a leap onto a player in reach wastes it).
                if (s.Distance <= s.MeleeReach + 100) return double.NaN;
                score += 2.0;
                break;
            case LeapKind.Back:
                // Backing off: a ranged fighter whose victim came close, or a hurt boss/elite.
                var crowded = s.Distance <= s.MeleeReach + 50;
                if (!(crowded && (s.RangedFighter || s.Rank != NpcRank.Normal && s.HpShare < 0.5))) return double.NaN;
                score += 1.5;
                break;
            case LeapKind.Home:
                return double.NaN;   // a reset move, left to the scripts
        }
        // A ranged skill is worth more against a victim out of melee reach.
        if (skill.Range >= 500 && s.Distance > s.MeleeReach) score += 0.5;
        // Long casts: elites and bosses wait for the victim to stand still; normal monsters cast anyway.
        if (skill.Cast >= 1000 && s.Rank != NpcRank.Normal)
            score += s.VictimSpeed <= StillSpeed ? 0.4 : -0.5;
        // Variety: the same special twice in a row is worth less.
        if (skillId == s.LastSpecial) score -= 0.6;
        return score + jitter * (s.Rank == NpcRank.Normal ? 0.6 : 0.25);
    }

    /// <summary>A boss re-checking its targets turns on a random other attacker this often (keeps every player busy).</summary>
    public const double BossRandomTarget = 0.15;

    /// <summary>A ranged fighter with its victim within KiteWithin steps back KiteDistance (at most every KiteEveryMs, KiteMs long).</summary>
    public const double KiteWithin = 250.0, KiteDistance = 400.0;
    public const int KiteEveryMs = 6000, KiteMs = 1500;

    /// <summary>How random the pick among scored skills is: higher picks lower scores more often (normal monsters are wilder).</summary>
    public static double Temperature(NpcRank rank) => rank switch { NpcRank.Hero => 0.35, NpcRank.Elite => 0.45, _ => 0.6 };

    /// <summary>
    /// The skill to use among the scored options: a forced one (score 10+) always; otherwise random, each weighted by
    /// exp((score - best) / temperature), so the best is likeliest but every usable skill comes up (user rule 2026-11-07).
    /// </summary>
    public static int Pick(IReadOnlyList<double> scores, NpcRank rank, double roll)
    {
        var best = 0;
        for (var i = 1; i < scores.Count; i++) if (scores[i] > scores[best]) best = i;
        if (scores[best] >= 10) return best;
        var t = Temperature(rank);
        var weights = scores.Select(x => Math.Exp((x - scores[best]) / t)).ToArray();
        var at = roll * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            at -= weights[i];
            if (at <= 0) return i;
        }
        return best;
    }
}
