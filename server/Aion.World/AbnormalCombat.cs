namespace Aion.World;

/// <summary>
/// Combat-relevant SkillAbnormalEffect rows from tools/data/boxes.json (client SkillAbnormalEffect.dat export).
/// Values are read as EffectValueList strings; semantics are CANDIDATE where not observed on wire.
/// </summary>
static class AbnormalCombat
{
    public sealed class DotRow
    {
        public required int EffectId, IntervalMs, PerTick;
        public required int Remaining;
        public required long NextAt;
    }

    /// <summary>Per-buff combat state derived from abnormal effect rows.</summary>
    public sealed class BuffCombat
    {
        public bool Held, Silence, ImmuneDamage;
        /// <summary>A Snare: the player cannot move but may still cast.</summary>
        public bool Rooted;
        public bool Taunt;
        public int BarrierAmount, BarrierEffectId;
        public int ReflectPermille, ReflectCap;
        public int ReflectSkillId;
        public HashSet<string> ImmuneEffectTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ImmuneDebuffCategory;
        public List<DotRow> Dots { get; } = [];
    }

    static int V(IReadOnlyList<string> values, int index) =>
        index < values.Count && int.TryParse(values[index], out var v) ? v : 0;

    static int Roll(int low, int high, Random rng) =>
        rng.Next(Math.Min(low, high), Math.Max(low, high) + 1);

    /// <summary>Apply one abnormal effect row to a buff's combat state (called from AddBuff).</summary>
    public static void ApplyRow(int effectId, string type, IReadOnlyList<string> values, BuffCombat combat, int durationMs, long now, Random rng, int hpMax)
    {
        switch (type)
        {
            case "Stun" or "Hold" or "Stone" or "Sleep" or "Aerial" or "Fear" or "Knockdown" or "KnockdownInstant" or "Frozen" or "Root" or "BlockMovePosition" or "Paralysis" or "Bind":
                combat.Held = true;
                break;
            case "Silence" or "Blockade" or "BlockSkill" or "BlockActive":
                combat.Silence = true;
                break;
            case "Snare":
                combat.Rooted = true;
                break;
            case "Taunt":
                combat.Taunt = true;
                break;
            case "ImmuneDecreaseHP" or "Invincible":
                combat.ImmuneDamage = true;
                break;
            case "ImmuneSkillEffectGroup":
                // Dodge abnormal 200 (OBSERVED on every official dodge): ImmuneSkillEffectGroup with polarity Negative
                // blocks hostile skill effects (damage and debuffs) for the abnormal's duration.
                if (values.Count > 5 && values[5].Equals("Negative", StringComparison.OrdinalIgnoreCase))
                    combat.ImmuneDamage = true;
                break;
            case "HpBarrier":
                combat.BarrierEffectId = effectId;
                combat.BarrierAmount = Roll(V(values, 2), V(values, 3), rng);
                if (combat.BarrierAmount <= 0) combat.BarrierAmount = Math.Max(V(values, 2), V(values, 3));
                break;
            case "DamageReflect":
                // CANDIDATE dat layout: [trigger permille, reflect cap, damage permille, skill name, ...].
                combat.ReflectPermille = V(values, 2) > 0 ? V(values, 2) : V(values, 0);
                combat.ReflectCap = V(values, 1);
                combat.ReflectSkillId = 0;
                break;
            case "ImmuneAbnormal":
                foreach (var part in (values.Count > 5 ? values[5] : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (part.Length > 0) combat.ImmuneEffectTypes.Add(part);
                if (values.Count > 6 && values[6] is "Debuff" or "DeBuff") combat.ImmuneDebuffCategory = true;
                break;
            case "Dot_TargetMaxHP" when V(values, 0) > 0:
            {
                var interval = V(values, 0);
                var ratio = V(values, 1);
                var cap = V(values, 2);
                var total = (int)Math.Min(cap > 0 ? cap : int.MaxValue, (long)hpMax * ratio / 10_000);
                combat.Dots.Add(MakeDot(effectId, interval, total, durationMs, now, perTick: false));
                break;
            }
            case "Dot_TargetHP" or "Dot_Dmg" when V(values, 0) > 0:
            {
                var interval = V(values, 0);
                var amount = Roll(V(values, 1), V(values, 2), rng);
                combat.Dots.Add(MakeDot(effectId, interval, amount, durationMs, now, perTick: true));
                break;
            }
        }
    }

    static DotRow MakeDot(int effectId, int interval, int amount, int durationMs, long now, bool perTick)
    {
        var ticks = Math.Max(1, durationMs / interval);
        var per = perTick ? amount : amount / ticks;
        return new DotRow { EffectId = effectId, IntervalMs = interval, PerTick = per, Remaining = per * ticks, NextAt = now + interval };
    }

    /// <summary>Held / silence / immunity flags for scripted NPC abnormals (MapEvents, godstones).</summary>
    public static (bool Held, bool Immune) NpcFlags(int abnormalId)
    {
        var held = false;
        var immune = false;
        foreach (var (_, type, _) in AbnormalSpecs.Info(abnormalId)?.Effects ?? [])
            switch (type)
            {
                case "Stun" or "Hold" or "Stone" or "Sleep" or "Aerial" or "Fear" or "Knockdown" or "KnockdownInstant" or "Frozen" or "Root" or "BlockMovePosition" or "Paralysis" or "Bind":
                    held = true; break;
                case "ImmuneSkillEffectGroup" or "ImmuneDecreaseHP" or "BlockTargeting" or "Invincible":
                    immune = true; break;
            }
        return (held, immune);
    }

    /// <summary>Whether a debuff abnormal's effect types are blocked by active immunity buffs.</summary>
    public static bool BlocksDebuffEffects(IEnumerable<BuffCombat> immunities, AbnormalInfo info, out int blockedEffectId)
    {
        blockedEffectId = 0;
        var list = immunities.ToList();
        if (list.Any(c => c.ImmuneDamage))
        {
            blockedEffectId = info.Effects.Count > 0 ? info.Effects[0].Id : 0;
            return true;
        }
        var types = list.SelectMany(c => c.ImmuneEffectTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var category = list.Any(c => c.ImmuneDebuffCategory);
        foreach (var (id, type, _) in info.Effects)
        {
            if (category && types.Count == 0) { blockedEffectId = id; return true; }
            if (types.Contains(type)) { blockedEffectId = id; return true; }
        }
        return false;
    }
}
