using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Spot-checks quest-reference.json samples per goal type (non-HQ, cap 45).</summary>
public sealed class QuestReferenceObjectiveTests
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> GoalTypeSamples()
    {
        var path = QuestReferencePath();
        if (!File.Exists(path)) yield break;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var quest in doc.RootElement.GetProperty("quests").EnumerateArray())
        {
            if (quest.GetProperty("type").GetString() is "Hero") continue;
            if (!quest.GetProperty("supported").GetBoolean()) continue;
            var code = quest.GetProperty("code").GetString()!;
            foreach (var step in quest.GetProperty("steps").EnumerateArray())
            {
                foreach (var obj in step.GetProperty("objectives").EnumerateArray())
                {
                    var kind = obj.GetProperty("type").GetString()!;
                    if (!byType.TryGetValue(kind, out var list))
                        byType[kind] = list = [];
                    if (list.Count < 2 && !list.Contains(code, StringComparer.Ordinal))
                        list.Add(code);
                }
            }
        }
        foreach (var (kind, codes) in byType.OrderBy(kv => kv.Key))
            foreach (var code in codes)
                yield return new object[] { kind, code };
    }

    static string QuestReferencePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "quest-reference.json"),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "quest-reference.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "tools", "data", "quest-reference.json")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }

    [Theory]
    [MemberData(nameof(GoalTypeSamples))]
    public void Reference_quest_objectives_map_to_embedded_definition(string goalType, string code)
    {
        var embedded = QuestBook.Embedded.Find(code);
        Assert.NotNull(embedded);
        Assert.Contains(embedded!.Steps.SelectMany(s => s.Objectives), o => o.Type == goalType);
    }

}
