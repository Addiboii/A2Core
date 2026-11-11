using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// SkillEffect.dat SkillEffectConditionList per effect row (tools/data/skill-effect-conditions.json, tools/skill_effect_conditions.py,
/// OBSERVED from the build 26262663 table): the type, its values as the table's strings, and Negate when the row's
/// ConditionalExpression is false. UNUSED so far: no server path evaluates effect conditions yet (Combat.SkillHits applies every
/// Damage row of a skill; e.g. 16040000's main-target and NOT main-target rows both land on the one target).
/// </summary>
public static class SkillEffectConditions
{
    public sealed record Condition(string Type, IReadOnlyList<string> Values, bool Negate);

    sealed record Tables(Dictionary<int, Condition[]> Effects, Dictionary<int, int[]> Skills);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(SkillEffectConditions).Assembly.GetManifestResourceStream("skill-effect-conditions.json");
        if (stream is null) return new([], []);
        using var doc = JsonDocument.Parse(stream);
        var effects = new Dictionary<int, Condition[]>();
        foreach (var row in doc.RootElement.GetProperty("effects").EnumerateObject())
            effects[int.Parse(row.Name, CultureInfo.InvariantCulture)] = row.Value[1].EnumerateArray()
                .Select(c => new Condition(c[0].GetString() ?? "", c[1].EnumerateArray().Select(v => v.GetString() ?? "").ToArray(), c[2].GetBoolean()))
                .ToArray();
        var skills = doc.RootElement.GetProperty("skills").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray());
        return new(effects, skills);
    });

    /// <summary>The conditions of a SkillEffect row (empty: unconditional).</summary>
    public static IReadOnlyList<Condition> Of(int effectId) => tables.Value.Effects.TryGetValue(effectId, out var c) ? c : [];

    /// <summary>The effect rows of a skill that carry conditions.</summary>
    public static IReadOnlyList<int> EffectsOf(int skillId) => tables.Value.Skills.TryGetValue(skillId, out var e) ? e : [];

    /// <summary>How many effect rows carry conditions.</summary>
    public static int Count => tables.Value.Effects.Count;
}
