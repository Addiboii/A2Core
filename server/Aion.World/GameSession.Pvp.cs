using Aion.Protocol.Native;

namespace Aion.World;

// Player-vs-player combat resolution (user request 2026-11-08): the server works out the attacker's power against the defender's
// resist across every combat stat, so builds play off each other - PvP Add/Amplify Damage vs PvP Damage Defense / Decrease Damage,
// Accuracy (penetration) vs Evasion (dodge), Critical vs Critical Resist, Block vs Block Pierce, Physic vs Magic lines, plus the
// weapon and skill damage. Used for duels, the arena and faction (Abyss) hits; NPC combat is unchanged.
//
// The exact retail coefficients are not recovered, so the divisors/caps below are OURS and tunable in one place. Every stat is the
// full total from all sources (base, gear, enchant, ride, buffs, creature board, titles, manastone/soulbind, collection).
public sealed partial class GameSession
{
    // Stat units per step of chance/effect (OURS, PROVISIONAL). Critical uses /1000 like the PvE crit (weapon.Critical/1000).
    public const double CritPerPoint = 1000, EvaPerPoint = 2000, BlockPerPoint = 2000, PerMille = 10_000;
    public const double DodgeCap = 0.60, BlockCap = 0.50, BlockReduction = 0.50, CritFloor = 0.03;

    /// <summary>The full total of a stat from every source (base, gear, enchant, ride, buff, board, title, manastone/collection).</summary>
    public int CombatStat(string name) =>
        data.BaseStats(className, level).GetValueOrDefault(name)
        + EquippedItems().Sum(i => i.Stat(name)) + EnchantStat(name) + RideStat(name)
        + BuffStat(name) + BoardStat(name) + TitleStat(name) + ExtraStat(name);

    static int RollPair(int?[] pair, Random rng)
    {
        var lo = pair.Length > 0 ? pair[0] ?? 0 : 0;
        var hi = pair.Length > 1 ? pair[1] ?? lo : lo;
        return lo == hi ? lo : rng.Next(Math.Min(lo, hi), Math.Max(lo, hi) + 1);
    }

    /// <summary>
    /// The hits of a skill from this player against <paramref name="defender"/>, with every PvP stat applied. A dodged hit lands
    /// for 0 (a miss the client shows). Order per damage row: base damage, dodge (accuracy vs evasion), critical (crit vs resist,
    /// amplified), block, PvP power vs resist, then defense mitigation.
    /// </summary>
    List<Hit> PvpHits(GameSession defender, int skill)
    {
        if (data.Skill(skill) is not { Damage.Length: > 0 } definition) return [];
        var magic = ClientDamageLine(skill) == "Magic";
        var pm = magic ? "Magic" : "Physic";
        var weapon = ScaledWeapon();   // with the skin collection's weapon-type damage
        var fixing = data.BaseStats(className, level).GetValueOrDefault("FixingDamage") + Bonus("FixingDamage") + Bonus(pm + "Damage")
            + Bonus("WeaponFixingDamage") + Bonus(pm + "WeaponFixingDamage");   // accessories' and soul binds' Attack (user 2026-11-08)
        // Front / back (the defender's facing): attacker's Front/BackAttack lines against the defender's.
        var side = position is { } me && defender.position is { } them && defender.worldPlayer is { } dwp ? FacingSide(them, dwp.Heading, me) : 0;
        string? lineSide = side > 0 ? "Back" : side < 0 ? "Front" : null;

        // Attacker (this): accuracy = penetration of evasion; critical, crit amplify, PvP power, block pierce.
        double accuracy = weapon.Accuracy + S("Accuracy") + S(pm + "Accuracy") + S("SkillAccuracy") + S("PvPAccuracy") + S("PvP" + pm + "Accuracy");
        double critical = weapon.Critical + S("Critical") + S(pm + "Critical") + S("SkillCritical") + S("PvPCritical") + (lineSide is { } c1 ? S(c1 + "AttackCritical") : 0);
        double critAmplify = S("AmplifyCriticalDamage") + S("Amplify" + pm + "CriticalDamage");
        double critAdd = S("CriticalAddDamage") + S("Critical" + pm + "AddDamage");
        // Every general damage line counts in PvP too (user 2026-11-08): all/physic-magic/skill amplify, the PvP lines, the worn
        // weapon type's PvP amplify (PvPAmplifySwordDamage...), and the flat PvP add damage.
        double power = S("DamageRatio") + S("PvPAmplifyDamage") + S("PvPAmplify" + pm + "Damage") + S("PvPAmplifySkillDamage") + S("PvPAmplifyWeaponDamage")
            + S("AmplifyAllDamage") + S("Amplify" + pm + "Damage") + S("AmplifySkillDamage") + S("Amplify" + pm + "SkillDamage") + S("AmplifyWeaponDamage")
            + (lineSide is { } a1 ? S("Amplify" + a1 + "Attack") : 0)
            + (data.WeaponType(equippedWeapon) is { } wtype ? S("PvPAmplify" + wtype + "Damage") : 0);
        double addFlat = S("PvPAddDamage") + S("PvP" + pm + "AddDamage") + S(pm + "SkillDamage") + S("SkillDamage") + (lineSide is { } f1 ? S(f1 + "AttackDamage") : 0);
        double defensePierce = S("DefensePierce") + S(pm + "DefensePierce") + S("PvPDefensePierce") + S("IgnoreDefense") + S("PvPIgnoreDefense");
        double pierce = S("WeaponBlockPierce") + S("ShieldBlockPierce") + S("BlockPierce") + S("PvPBlockPierce");
        int S(string n) => CombatStat(n);

        // Defender: evasion, crit resist, crit-damage reduction, PvP resist, flat defenses, block.
        double evasion = defender.CombatStat("Evasion") + defender.CombatStat(pm + "Evasion") + defender.CombatStat("SkillEvasion")
            + defender.CombatStat("PvPEvasion") + defender.CombatStat("PvP" + pm + "Evasion") + defender.CombatStat("ArmorEvasion");
        double critResist = defender.CombatStat("CriticalResist") + defender.CombatStat(pm + "CriticalResist")
            + defender.CombatStat("SkillCriticalResist") + defender.CombatStat("PvPCriticalResist") + (lineSide is { } r1 ? defender.CombatStat(r1 + "AttackCriticalResist") : 0);
        critResist *= 1 + Math.Max(0, defender.CombatStat("CriticalResistRatio")) / PerMille;   // Critical Resist % (buffs, DEX)
        // The other percentages PcStatSecond gives (DEX/AGI/Freedom/Death/Space; CANDIDATE: the same form as Critical Resist %).
        evasion *= 1 + Math.Max(0, defender.CombatStat("EvasionRatio")) / PerMille;
        accuracy *= 1 + Math.Max(0, S("AccuracyRatio")) / PerMille;
        critical *= 1 + Math.Max(0, S("CriticalRatio")) / PerMille;
        double critReduce = defender.CombatStat("DecreaseCriticalDamage") + defender.CombatStat("Decrease" + pm + "CriticalDamage");
        double decrease = defender.CombatStat("PvPDecreaseDamage") + defender.CombatStat("PvPDecrease" + pm + "Damage")
            + defender.CombatStat("PvPDecreaseSkillDamage") + defender.CombatStat("PvPDecreaseWeaponDamage")
            + defender.CombatStat("DecreaseDamage") + defender.CombatStat("Decrease" + pm + "Damage") + defender.CombatStat("DecreaseSkillDamage")
            + defender.CombatStat("Decrease" + pm + "SkillDamage") + defender.CombatStat("DecreaseWeaponDamage")
            + (lineSide is { } d1 ? defender.CombatStat("Decrease" + d1 + "Attack") : 0);
        double critAddDefense = defender.CombatStat("CriticalDamageDefense") + defender.CombatStat("Critical" + pm + "DamageDefense");
        double defenseFlat = defender.Defense + defender.CombatStat(pm + "DamageDefense") + defender.CombatStat("SkillDefense")
            + defender.CombatStat(pm + "SkillDefense") + defender.CombatStat("PvPDamageDefense") + defender.CombatStat("PvP" + pm + "DamageDefense");
        double block = (defender.CombatStat("WeaponBlock") + defender.CombatStat("ShieldBlock") + defender.CombatStat("Block") + defender.CombatStat("PvPBlock"))
            * (1 + Math.Max(0, defender.CombatStat("BlockRatio")) / PerMille);   // Block % (DEX, Space; CANDIDATE)
        defenseFlat = Math.Max(0, defenseFlat - defensePierce + (lineSide is { } e1 ? defender.CombatStat(e1 + "AttackDefense") : 0));

        var dodgeChance = Math.Clamp((evasion - accuracy) / EvaPerPoint, 0, DodgeCap);
        var critChance = Math.Clamp((critical - critResist) / CritPerPoint, CritFloor, Combat.CriticalCap);
        var blockChance = Math.Clamp((block - pierce) / BlockPerPoint, 0, BlockCap);
        var powerMul = 1 + power / PerMille;
        // Iron Wall less the attacker's Ignore Iron Wall reduces the hit further.
        var resistMul = Math.Max(0.1, 1 - decrease / PerMille) * Math.Max(0.1, 1 - Math.Max(0, defender.CombatStat("IronWall") - S("IgnoreIronWall")) / PerMille);
        // Perfect (against Perfect Resist): this cast takes the weapon's maximum; Hard Hit (against its Resist): x1.5 per hit in PvP.
        weapon = PerfectRoll(weapon, (int)Math.Max(0, S("Perfect") - defender.CombatStat("PerfectResist")));
        var hardHit = (int)Math.Max(0, S("HardHit") - defender.CombatStat("HardHitResist"));
        var mitigation = Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, defenseFlat));

        var hits = new List<Hit>(definition.Damage.Length);
        foreach (var row in definition.Damage)
        {
            if (rng.NextDouble() < dodgeChance) { hits.Add(new Hit(0, false, row.EffectId)); continue; }   // dodged: a miss
            var (flat, ratio) = row.Pvp is { } p ? (p.Flat, p.Ratio) : (row.Flat, row.Ratio);
            // The learned level's PvP values from the client tables (SkillEffectLv [21..24]); the guide's level scale only without them.
            var levelled = ClientSkills.Of(skill)?.Effects.FirstOrDefault(e => e.Id == row.EffectId && e.Type == "Damage") is { } effect
                ? ClientSkills.DamageAt(effect, CombatSkillLevel(skill)) : ((int, int, int, int, int, int, int, int, int)?)null;
            if (levelled is { } lv)
            {
                var usePvp = lv.Item6 > 0 || lv.Item8 > 0;
                flat = usePvp ? new int?[] { lv.Item5, lv.Item6 } : new int?[] { lv.Item1, lv.Item2 };
                ratio = usePvp ? new int?[] { lv.Item7, lv.Item8 } : new int?[] { lv.Item3, lv.Item4 };
            }
            var attack = rng.Next(Math.Min(weapon.Min, weapon.Max), Math.Max(weapon.Min, weapon.Max) + 1) + fixing;
            double raw = (RollPair(flat, rng) + attack * RollPair(ratio, rng) / 10_000.0) / (levelled?.Item9 ?? 1) + addFlat;   // [4] split (SkillClient.DamageAt)
            if (levelled is null) raw *= SkillGuide.Embedded.Of(skill)?.LevelScale(CombatSkillLevel(skill)) ?? 1.0;   // the skill's learned level
            var critical2 = rng.NextDouble() < critChance;
            if (critical2) raw = raw * Combat.CriticalMultiplier * Math.Max(0.1, 1 + (critAmplify - critReduce) / PerMille) + Math.Max(0, critAdd - critAddDefense);
            if (hardHit > 0 && rng.Next(10_000) < hardHit) raw *= HardHitPvp;
            if (rng.NextDouble() < blockChance) raw *= 1 - defender.BlockCut();   // the defender's weapon / shield block reduction
            raw *= powerMul * resistMul * mitigation;
            hits.Add(new Hit(Math.Max(1, (int)Math.Round(raw)), critical2, row.EffectId));
        }
        // Additional Hit (against Additional Hit Resist): 1-4 small extra hits, through the same multipliers.
        var extra = (int)Math.Max(0, S("AdditionalHitRate") - defender.CombatStat("AdditionalHitResistRate"));
        var before = hits.Count;
        hits = SpecialHits(hits, 0, 1, extra, weapon);
        for (var i = before; i < hits.Count; i++) hits[i] = hits[i] with { Amount = Math.Max(1, (int)Math.Round(hits[i].Amount * powerMul * resistMul * mitigation)) };
        return hits;
    }
}
