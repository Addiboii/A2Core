using Aion.Protocol.Native;

namespace Aion.World;

// Amplification (the client's ExceedEnchant): ItemExceedEnchant_RQ 0x5624 {equip uid i64, cost items [{origin component i32,
// replaced component i32, items [{key i64, count i64}]}]} (OBSERVED live 2026-11-06: 36 bytes, one Amplify Stone 516530001
// stack x6) -> 0x5625 {result, equip uid, current enchant level u8, item id} (usmap; the same form as the OBSERVED
// ItemEnchant_RS). The level continues upgrades.enchant past the Enchant table's maximum (official gear at +16..+20 is seen
// in the recordings). Everything else is the client's ExceedEnchant table (tools/data/enhance.json) for the item's
// ExceedEnchantGroup, row = exceed level (enchant - maximum): SuccessProb + the failure correction built up (upgrades.
// enchant_bonus), FailCorrectionProb per failure, FailPenalty as EnchantOutcome (0 in every row: level kept), CostGold Kina and CostItems (the Amplify Stones),
// charged from the player's stacks either way; the level's AdditionalStat is what the item gains (EnchantTables.Effect).
public sealed partial class GameSession
{
    public const ushort ItemExceedEnchantRq = 0x5624, ItemExceedEnchantRs = 0x5625;
    static EnhanceTables Enhance => EnhanceTables.Embedded;

    /// <summary>The Enchant table's maximum for the item (its last enchantable level + 1), or -1 when it cannot be enchanted.</summary>
    static int EnchantMax(int itemId)
    {
        var level = 0;
        while (Enchants.Next(itemId, level) is not null) level++;
        return level == 0 ? -1 : level;
    }

    /// <summary>The Kina and item costs of a table row from the player's bag stacks (not the enhanced item); null when short.</summary>
    List<(Guid, long, int)>? TableCosts(long kina, IEnumerable<(int ItemId, int Count)> items, Guid except, out int failure)
    {
        var costs = new List<(Guid, long, int)>();
        failure = 0;
        if (kina > 0)
        {
            if (CostStacks(KinaItemId, kina, except) is not { } paid) { failure = EResult.Item_Common_Fail_NotEnoughCountMoney; return null; }
            costs.AddRange(paid);
        }
        foreach (var (itemId, count) in items)
        {
            if (CostStacks(itemId, count, except) is not { } taken) { failure = EResult.Item_Common_Fail_NotEnoughCountItem; return null; }
            costs.AddRange(taken);
        }
        return costs;
    }

    async Task<List<byte[]>> AmplifyAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var key = BitConverter.ToInt64(plain, 2);
        byte[] Reply(int result, int level, int itemId) => PacketWriter.Opcode(ItemExceedEnchantRs).U16(result).I64(key).U8(level).I32(itemId).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(EResult.Item_Enchant_InternalError, 0, 0)];
        var level = EnchantOf(row);
        var max = EnchantMax(row.TemplateId);
        var group = ItemDB.Embedded.Of(row.TemplateId)?.ExceedEnchantGroup;
        if (max < 0 || level < max || Enhance.Exceed(group, level - max) is not { } step)
            return [Reply(EResult.Item_Enchant_NotEnchantableItem, level, row.TemplateId)];
        if (TableCosts(step.Kina, step.Costs, row.InstanceId, out var failure) is not { } costs) return [Reply(failure, level, row.TemplateId)];
        var bonus = EnchantOf(row, "enchant_bonus");
        // Every ExceedEnchant row has FailPenalty 0: a failure keeps the level (834, OBSERVED official 0x5626 at +15 and +17).
        var outcome = EnchantOutcome.Of(level, max, step.Success, step.FailPenalty, bonus, EnchantRoll(), EnchantBreakable);
        var after = outcome.After;
        var done = await Stored(async () =>
            await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after, outcome.Success ? 0 : bonus + step.FailCorrection, ct)
            && (!outcome.Destroyed || await DestroyEnchantedAsync(store, character, row.InstanceId, ct)) ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.Item_Enchant_InternalError, level, row.TemplateId)];
        // Official order (ItemEnchant_RS, 13 of 13): the item changes (0x561b) first, then the reply; amplify tries are broadcast
        // with the same 0x5626 (OBSERVED: 834 at 15 -> 15 and 17 -> 17).
        var replies = await InventoryChangesAsync(ct, 0, outcome.Success ? DecreaseItemEnchant : DecreaseItemEnchantFail);
        replies.Add(Reply(outcome.Result, after, row.TemplateId));
        var notice = ItemEnchantNotice(playerKey, outcome.Result, level, after);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        replies.Add(notice);
        if ((after != level || outcome.Destroyed) && row.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }
}
