namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Elevate gather test products (63101 / 63102).</summary>
    internal static bool IsElevateGatherQuestItem(int itemId) => itemId is >= 631010000 and < 631030000;

    /// <summary>Elevate craft test products (63103–63107).</summary>
    internal static bool IsElevateCraftQuestItem(int itemId) => itemId is >= 631030000 and < 631080000;

    internal static string? ElevateCraftWorktable(int itemId) =>
        IsElevateCraftQuestItem(itemId) ? (itemId / 10_000 % 10) switch
        {
            3 => "Blacksmith",
            4 => "Tailor",
            5 => "Jewelcraft",
            6 => "Alchemy",
            7 => "Cook",
            _ => null,
        } : null;

    bool TryElevateMasteryCollect(Func<int, bool> itemFilter, out int itemId, out int stepId)
    {
        itemId = 0;
        stepId = 0;
        if (questLog is null) return false;
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Incomplete))
        {
            if (questLog.Book.Find(record.Code) is not { Type: "GatherCraftMastery" } def) continue;
            if (def.Race == "Light" && profileRace != 1 || def.Race == "Dark" && profileRace != 2) continue;
            if (questLog.CurrentStep(record) is not { } step) continue;
            for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
            {
                var o = step.Objectives[i];
                if (o.Type != "CollectItem" || o.Item is not { } id || !itemFilter(id) || record.Goals[i] >= o.Count)
                    continue;
                itemId = id;
                stepId = step.Id;
                return true;
            }
        }
        return false;
    }

    /// <summary>Open gather elevate CollectItem (MQ1902010 / MQ2902010 step 2).</summary>
    bool TryElevateGatherCollect(out int itemId, out int stepId) =>
        TryElevateMasteryCollect(IsElevateGatherQuestItem, out itemId, out stepId);

    /// <summary>Open craft elevate CollectItem (MQ1902020–060 / MQ2902020–060 step 2).</summary>
    bool TryElevateCraftCollect(out int itemId, out int stepId) =>
        TryElevateMasteryCollect(IsElevateCraftQuestItem, out itemId, out stepId);

    /// <summary>Quest-only specialty recipes (LearnType not Auto in dat) while the matching CollectItem step is open.</summary>
    bool AllowsElevateCraftRecipe(CraftTables.Recipe recipe) =>
        TryElevateCraftCollect(out var item, out _) && recipe.Product == item && recipe.Race == profileRace;

    long HeldCount(int itemId) => inventory.Values.Where(i => i.ItemId == itemId).Sum(i => i.Count);
}
