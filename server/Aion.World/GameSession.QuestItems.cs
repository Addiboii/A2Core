using Aion.Application.Gameplay;

namespace Aion.World;

// Collect objectives (QuestStep GoalList CollectItem {item name, count}): the step's goal follows the count of the item held in the bag
// (Quests.cs Holding, set not added). Before 2026-11-13 nothing called Holding: every collect step stayed at 0 (live Axel3, HQ1102125
// step 3 "Monolith_token_L_s_01a_b" 631900001 x1 - the Empyrean Trace gave x3 and the journal still showed 0). Checked after every bag
// change (InventoryChangesAsync), after quest changes (a new step may count items already held: HQ1102125 step 4) and at the login.
// CANDIDATE: no official collect step recorded; the count is the bag's stacks (warehouse and account storage do not count).
public sealed partial class GameSession
{
    /// <summary>The quests whose collect goals change for these stored rows; rounds, since a finished step may open another collect step.</summary>
    List<QuestRecord> HeldItemQuests(IEnumerable<ItemState> rows)
    {
        if (questLog is null) return [];
        var held = rows.Where(r => r.Location == "bag").GroupBy(r => r.TemplateId).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
        var changed = new Dictionary<string, QuestRecord>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < 4; round++)
        {
            var any = false;
            foreach (var item in questLog.CollectedItemIds())
                foreach (var record in questLog.Holding(item, held.GetValueOrDefault(item), level)) { changed[record.Code] = record; any = true; }
            if (!any) break;
        }
        return [.. changed.Values];
    }
}
