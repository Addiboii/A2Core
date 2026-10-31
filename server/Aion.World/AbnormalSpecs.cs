namespace Aion.World;

/// <summary>
/// What an abnormal does: the SkillAbnormalEffect rows of the item abnormals (boxes.json) or of a class-skill abnormal
/// (skill-areas.json abnormal_effects: exported from the client tables, else improvised by tools/skill_effect_overrides.py).
/// Row layouts are in tools/skill_effect_overrides.py; the types are the client's EAbnormalEffectType names.
/// </summary>
public static class AbnormalSpecs
{
    /// <summary>Effect types that stop the target acting or moving at all.</summary>
    public static readonly HashSet<string> HoldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stun", "Hold", "Stone", "Sleep", "Aerial", "Fear", "Knockdown", "KnockdownInstant", "Frozen", "Root", "BlockMovePosition", "Paralysis", "Bind", "Stagger",
    };

    public static readonly HashSet<string> SilenceTypes = new(StringComparer.OrdinalIgnoreCase) { "Silence", "Blockade", "BlockSkill", "BlockActive" };

    static readonly HashSet<string> DebuffTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stun", "Hold", "Stone", "Sleep", "Aerial", "Fear", "Knockdown", "KnockdownInstant", "Frozen", "Root", "BlockMovePosition", "Paralysis", "Bind", "Stagger",
        "Silence", "Blockade", "BlockSkill", "BlockActive", "Slow", "Snare", "Taunt", "Blind", "Poison", "Bleed", "Knockback",
        "Dot_Dmg", "Dot_TargetHP", "Dot_TargetMaxHP", "Dot_NormalCalc",
    };

    /// <summary>The rows of an abnormal: item abnormals first, then class-skill abnormals; null when neither table knows it.</summary>
    public static AbnormalInfo? Info(int abnormalId) => RewardBoxes.Embedded.Abnormal(abnormalId) ?? SkillAreas.Abnormal(abnormalId);

    public static IEnumerable<string> Types(int abnormalId) => Info(abnormalId)?.Effects.Select(e => e.Type) ?? [];

    /// <summary>A debuff (crowd control, slow, silence, damage over time, a negative stat): what a cleanse removes.</summary>
    public static bool Debuff(int abnormalId)
    {
        if (RewardBoxes.Embedded.Debuff(abnormalId)) return true;
        return Info(abnormalId)?.Effects.Any(e => DebuffTypes.Contains(e.Type)
            || e.Type == "StatChange" && e.Values.Count >= 2 && int.TryParse(e.Values[1], out var value) && value < 0) == true;
    }

    /// <summary>A crowd-control abnormal (hold, slow, root, silence, taunt): what Dispel_Cc removes.</summary>
    public static bool CrowdControl(int abnormalId) =>
        Info(abnormalId)?.Effects.Any(e => HoldTypes.Contains(e.Type) || SilenceTypes.Contains(e.Type)
            || e.Type is "Slow" or "Snare" or "Taunt" or "Blind" or "Knockback") == true;

    static int Value(IReadOnlyList<string> values, int index) => index < values.Count && int.TryParse(values[index], out var v) ? v : 0;

    /// <summary>A displacement an abnormal asks for: knockback (distance cm, ms) or airborne (height cm, ms).</summary>
    public sealed record Displace(DisplaceKind Kind, int Amount, int Ms);

    public enum DisplaceKind { Knockback, Aerial }

    /// <summary>What a monster does under an abnormal; the rows the server models (the rest only show the icon).</summary>
    public sealed record NpcModel(bool Held, bool Silence, int SlowPermille, bool Taunt, IReadOnlyList<DotSpec> Dots, IReadOnlyList<Displace> Moves);

    /// <summary>Damage over time on a monster: every <paramref name="IntervalMs"/> a share (<paramref name="PermilleOfMax"/> of 10000 of the maximum HP in total, capped).</summary>
    public sealed record DotSpec(int EffectId, int IntervalMs, int RatioTotal, int Cap);

    public static NpcModel Npc(int abnormalId)
    {
        bool held = false, silence = false, taunt = false;
        var slow = 0;
        List<DotSpec> dots = [];
        List<Displace> moves = [];
        foreach (var (id, type, values) in Info(abnormalId)?.Effects ?? [])
        {
            if (HoldTypes.Contains(type)) held = true;
            if (SilenceTypes.Contains(type)) silence = true;
            switch (type)
            {
                case "Slow": slow = Math.Max(slow, Value(values, 0)); break;
                case "Snare": slow = 10_000; break;
                case "Taunt": taunt = true; break;
                case "Dot_TargetMaxHP" when Value(values, 0) > 0: dots.Add(new(id, Value(values, 0), Value(values, 1), Value(values, 2))); break;
                case "Knockback": moves.Add(new(DisplaceKind.Knockback, Value(values, 0), Math.Max(100, Value(values, 1)))); break;
                case "Aerial": moves.Add(new(DisplaceKind.Aerial, Value(values, 0), Math.Max(100, Value(values, 1)))); break;
            }
        }
        return new(held, silence, Math.Min(10_000, slow), taunt, dots, moves);
    }
}
