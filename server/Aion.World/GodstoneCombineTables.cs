using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Godstone combine rules (tools/data/godstone_combine.json).</summary>
public sealed class GodstoneCombineTables
{
    public sealed record GradeRule(int MaterialCount, int SuccessPct, int RewardGrade);

    sealed record File([property: JsonPropertyName("grades")] Dictionary<string, JsonElement> Grades);

    readonly Dictionary<int, GradeRule> grades;

    GodstoneCombineTables(File file)
    {
        grades = file.Grades.ToDictionary(p => int.Parse(p.Key), p =>
        {
            var row = p.Value;
            return new GradeRule(row.GetProperty("material_count").GetInt32(), row.GetProperty("success_pct").GetInt32(),
                row.GetProperty("reward_grade").GetInt32());
        });
    }

    static readonly Lazy<GodstoneCombineTables> embedded = new(() =>
    {
        using var stream = typeof(GodstoneCombineTables).Assembly.GetManifestResourceStream("godstone_combine.json")
            ?? throw new InvalidOperationException("Missing embedded game data godstone_combine.json");
        return new GodstoneCombineTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty godstone_combine.json"));
    });

    public static GodstoneCombineTables Embedded => embedded.Value;

    public GradeRule? OfGrade(int grade) => grades.GetValueOrDefault(grade);
}
