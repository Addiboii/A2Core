using System.Text.Json;

namespace Aion.World;

/// <summary>
/// SkillAbnormal.dat stacking fields (tools/data/abnormal-stack-rules.json, tools/abnormal_stack_rules.py): GroupId,
/// AbnormalOverlapCount and AbnormalOverlapTimeType of the abnormals that are not the plain case (own group, one at a time,
/// overlap None). Idea from A2Core PR #4 (Artezianec); values from the client table.
/// </summary>
public static class AbnormalStackRules
{
    /// <summary>EAbnormalOverlapTimeType: None and Renew restart the time, Longer keeps the later end, Separate stacks.</summary>
    public sealed record Rule(int Group, int OverlapCount, string OverlapTime);

    static readonly Lazy<Dictionary<int, Rule>> rules = new(() =>
    {
        using var stream = typeof(AbnormalStackRules).Assembly.GetManifestResourceStream("abnormal-stack-rules.json");
        if (stream is null) return [];
        using var doc = JsonDocument.Parse(stream);
        var found = new Dictionary<int, Rule>();
        foreach (var row in doc.RootElement.GetProperty("abnormals").EnumerateObject())
            found[int.Parse(row.Name, System.Globalization.CultureInfo.InvariantCulture)] =
                new Rule(row.Value[0].GetInt32(), row.Value[1].GetInt32(), row.Value[2].GetString() ?? "None");
        return found;
    });

    /// <summary>The rule of an abnormal: the table's, else its own group (or the boxes.json group), one at a time, overlap None.</summary>
    public static Rule For(int abnormalId, int? fallbackGroup = null) =>
        rules.Value.TryGetValue(abnormalId, out var rule) ? rule : new Rule(fallbackGroup ?? abnormalId, 1, "None");
}
