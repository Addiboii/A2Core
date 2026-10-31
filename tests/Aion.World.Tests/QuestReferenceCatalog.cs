using System.Text.Json;

namespace Aion.World.Tests;

/// <summary>tools/data/quest-reference.json (client Quest*.dat + coverage flags).</summary>
public static class QuestReferenceCatalog
{
    static readonly Lazy<Root> Cached = new(Load);

    public static Root Reference => Cached.Value;

    public static IEnumerable<QuestRow> ElyosHeroChainRows()
    {
        var chain = new HashSet<string>(CampaignPlaythroughHelpers.LoadLightHeroChain(), StringComparer.Ordinal);
        return Reference.Quests.Where(q => chain.Contains(q.Code));
    }

    static Root Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "quest-reference.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "quest-reference.json"));
        return JsonSerializer.Deserialize<Root>(File.ReadAllText(path), JsonOptions)!;
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record Root(int Cap, QuestRow[] Quests);

    public sealed record QuestRow(
        string Code,
        int Id,
        string Acquire,
        string? Complete,
        int? StartNpc,
        int? FinishNpc,
        int Map,
        int Level,
        bool Supported,
        QuestStepRow[] Steps);

    public sealed record QuestStepRow(int Order, int Id, QuestObjectiveRow[] Objectives);

    public sealed record QuestObjectiveRow(
        string Type,
        string? Target,
        int? Npc,
        string? NpcName,
        int Count,
        int? Map,
        bool Supported);
}
