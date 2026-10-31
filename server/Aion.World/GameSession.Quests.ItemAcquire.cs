using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Item.dat GetQuest use effect or Quest.dat acquire_items: accept the quest and consume one scroll.</summary>
    async Task<List<byte[]>> TryQuestAcquireFromItemAsync(
        INativeCharacterStore store,
        Guid character,
        ItemState row,
        ItemUse? use,
        long now,
        bool reply,
        CancellationToken ct)
    {
        if (questLog is null || resources is null) return [];
        string? code = null;
        if (use is { Effect: "GetQuest" } && use.Values is [var questRef, ..])
        {
            if (int.TryParse(questRef, out var questId) && questLog.Book.ById.TryGetValue(questId, out var byId))
                code = byId;
            else if (questLog.Book.Find(questRef) is not null)
                code = questRef;
        }
        if (code is null)
        {
            foreach (var candidate in questLog.Book.QuestsByAcquireItem(row.TemplateId))
            {
                if (questLog.CanAccept(candidate, level, QuestConditionsHold))
                {
                    code = candidate;
                    break;
                }
            }
        }
        if (code is null || !questLog.CanAccept(code, level, QuestConditionsHold)) return [];
        if (!await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct)) return [];
        storedItems.Remove(row.WireId);
        var saved = await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct);
        LogQuestEvent("quest_accept", code, 0, QuestLog.Incomplete);
        var replies = QuestAcceptPackets(saved, 0);
        replies.AddRange(ApplyQuestWorldGating(now));
        if (reply) replies.Insert(0, RidePackets.ItemUseReply(0, WireKey(row), row.TemplateId));
        replies.Add(RidePackets.ItemUsed(WireKey(row), row.TemplateId));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        return replies;
    }
}
