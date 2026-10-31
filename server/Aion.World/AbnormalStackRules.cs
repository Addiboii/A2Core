using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// AbnormalTableItem overlap / replace rules (tools/data/abnormal-stack-rules.json, tools/abnormal_stack_rules.py).
/// </summary>
public static class AbnormalStackRules
{
    public sealed record Rule(
        [property: JsonPropertyName("group")] int Group,
        [property: JsonPropertyName("overlap_count")] int OverlapCount,
        [property: JsonPropertyName("overlap_time")] string OverlapTime,
        [property: JsonPropertyName("replace")] string Replace);

    sealed record RuleFile([property: JsonPropertyName("abnormals")] Dictionary<string, Rule>? Abnormals);

    static readonly Lazy<RuleFile> file = new(() =>
    {
        using var stream = typeof(AbnormalStackRules).Assembly.GetManifestResourceStream("abnormal-stack-rules.json");
        return stream is null ? new RuleFile(null) : JsonSerializer.Deserialize<RuleFile>(stream) ?? new RuleFile(null);
    });

    public static Rule For(int abnormalId)
    {
        if (file.Value.Abnormals?.TryGetValue(abnormalId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var rule) == true)
            return rule;
        var group = RewardBoxes.Embedded.Abnormal(abnormalId)?.Group ?? abnormalId;
        return new Rule(group, 1, "Renew", "Replace");
    }

    public static int GroupOf(int abnormalId) => For(abnormalId).Group;
}
