using Aion.Application.Gameplay;

namespace Aion.World;

public sealed partial class GameSession
{
    const int MonolithTokenLight = 631900001;
    const int MonolithTokenDark = 631900007;
    /// <summary>Quest move-point markers and the Monolith they pair with (cm, horizontal).</summary>
    const double MonolithMarkerRange = 5000;

    static int MonolithTokenForRace(string? race) =>
        string.Equals(race, "Dark", StringComparison.OrdinalIgnoreCase) ? MonolithTokenDark : MonolithTokenLight;

    bool MonolithNearMovePoint(string movePoint, int map, WorldObject monolith)
    {
        if (QuestTeleportResolver.ForNamedPoint(movePoint, map, data, "monolith-marker") is { } dest)
        {
            var dx = dest.At.X - monolith.At.X;
            var dy = dest.At.Y - monolith.At.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= MonolithMarkerRange;
        }
        // QuestMove_* names are often absent from map-points; using the placed Monolith env obj is enough.
        return true;
    }

    (QuestRecord Record, QuestObjective Objective)? FindMonolithCollectObjective(WorldObject monolith)
    {
        if (questLog is null) return null;
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Incomplete))
        {
            if (questLog.CurrentStep(record) is not { } step) continue;
            var book = questLog.Book.Find(record.Code);
            if (book is null) continue;
            for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
            {
                var o = step.Objectives[i];
                if (o.Type != "CollectItem" || o.MovePoint is null || o.Item is null || record.Goals[i] >= o.Count) continue;
                var map = o.Map ?? book.Map;
                if (map != worldMap || !MonolithNearMovePoint(o.MovePoint, map, monolith)) continue;
                return (record, o);
            }
        }
        return null;
    }

    /// <summary>
    /// Empyrean Monolith resonate (HQ1102125 step 4, HQ2102105 step 5): consume one race Monolith token and grant the
    /// CollectItem reward; wire MonolithResonate is not captured so this runs on InteractEnvObj completion.
    /// </summary>
    async Task<List<byte[]>> MonolithResonateAsync(WorldObject monolith, CancellationToken ct)
    {
        if (!monolith.Monolith || questLog is null || Store is not { } store || CharacterId is not { } character)
            return [];
        if (FindMonolithCollectObjective(monolith) is not { } match) return [];
        var book = questLog.Book.Find(match.Record.Code)!;
        var tokenId = MonolithTokenForRace(book.Race);
        var token = storedItems.Values.FirstOrDefault(i => i.Location == "bag" && i.Quantity > 0 && i.TemplateId == tokenId);
        if (token is null) return [];
        var rewardId = match.Objective.Item!.Value;
        var granted = await Stored(async () =>
        {
            if (!await store.ConsumeItemAsync(character, token.InstanceId, token.Revision, 1, ct)) return [];
            return await store.GrantLootAsync(character, [(rewardId, 1)], ct) ? [[]] : [];
        });
        if (granted.Count == 0) return [];
        var replies = await InventoryChangesAsync(ct, IncreaseEnvObjReward, 0);
        var held = inventory.Values.Where(i => i.ItemId == rewardId).Sum(i => i.Count);
        replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Holding(rewardId, held, level), ct)));
        return replies;
    }
}
