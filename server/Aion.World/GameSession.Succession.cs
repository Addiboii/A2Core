using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Succession (inheritance) and soul add, from the client's ItemSuccession, ItemSuccessionProb and ItemSoulAdd tables
// (tools/data/enhance.json; Item.dat SuccessionGroup / SoulAddGroup). Live opcodes in the 0x56 run (catalogue + 0x1ebe, the run
// AccountContentsInfoList_NT 0x56b6 is OBSERVED in). Nothing of either was recorded, so both are CANDIDATE from the usmap:
// - ItemSuccession_RQ 0x56b0 {dst item key i64, src item key i64, cost items [ComponentItem]} -> RS 0x56b1 {result u16,
//   is_success bool, SuccessionCountInfo {succession, advanced succession, restore succession, restore advanced} i32 x4}.
//   The row is ItemSuccession[dst SuccessionGroup][src SuccessionGroup] at the character's succession count (the last row covers
//   higher counts): its CostGold Kina and CostItem are charged from the player's stacks, the chance is ItemSuccessionProb
//   Common at that count (all 10000 today). The source item is used up and its enchant level (amplification included, capped
//   at what the target can reach) and enchant bonus pass to the target (CANDIDATE: what passes). GlobalSetting
//   succession_max_count 0 = no limit. The counts are setting "succession_count".
// - ItemSoulAdd_RQ 0x56b2 {uid i64, cost item id i32} -> RS 0x56b3 {result u16, is_success bool, uid i64}: the item's SoulAddGroup
//   row: SuccessProb, CostGold Kina and CostItemCount of CostItem (the client names the item); on success one more random stat
//   from the item's own soul bind pool (upgrades.soul_add_stats, shown in the record's soul add array; CANDIDATE), up to two
//   (the 2S and 3S stones: the second and third soul). The item must be soul-bound.
public sealed partial class GameSession
{
    public const ushort ItemSuccessionRq = 0x56b0, ItemSuccessionRs = 0x56b1, ItemSoulAddRq = 0x56b2, ItemSoulAddRs = 0x56b3;
    public const int MaxSoulAdds = 2;

    /// <summary>Successions done: [common, advanced] (setting "succession_count").</summary>
    int[] successionCounts = [0, 0];
    public IReadOnlyList<int> SuccessionCountsForChecks => successionCounts;

    async Task<List<byte[]>> SuccessionAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 18) return [];
        long dstKey = BitConverter.ToInt64(plain, 2), srcKey = BitConverter.ToInt64(plain, 10);
        byte[] Reply(int result, bool success) => PacketWriter.Opcode(ItemSuccessionRs).U16(result).Bool(success)
            .I32(successionCounts[0]).I32(successionCounts[1]).I32(0).I32(0).ToArray();
        if (Store is not { } store || CharacterId is not { } character || dstKey == srcKey
            || !storedItems.TryGetValue(dstKey, out var dst) || dst.Location is not ("bag" or "equipment")
            || !storedItems.TryGetValue(srcKey, out var src) || src.Location != "bag")
            return [Reply(EResult.ItemSuccession_Fail_InvalidMaterial, false)];
        if (Enhance.SuccessionGroupOf(dst.TemplateId) is not { } group || Enhance.SuccessionGroupOf(src.TemplateId) is not { } source
            || Enhance.Succession(group, source, successionCounts[0]) is not { } cost)
            return [Reply(EResult.ItemSuccession_Fail_InvalidMaterial, false)];
        if (TableCosts(cost.Kina, cost.Costs, dst.InstanceId, out var failure) is not { } costs) return [Reply(failure, false)];
        if (costs.Any(c => c.Item1 == src.InstanceId)) return [Reply(EResult.Item_Common_Fail_NotEnoughCountItem, false)];
        costs.Add((src.InstanceId, src.Revision, 1));
        var success = EnchantRoll() < Enhance.SuccessionChance("Common", successionCounts[0]);
        var dstMax = Math.Max(0, EnchantMax(dst.TemplateId)) + Enhance.ExceedMax(ItemDB.Embedded.Of(dst.TemplateId)?.ExceedEnchantGroup);
        var level = success ? Math.Min(EnchantOf(src), dstMax) : EnchantOf(dst);
        var bonus = success ? EnchantOf(src, "enchant_bonus") : EnchantOf(dst, "enchant_bonus");
        var done = await Stored(async () => await store.EnchantItemAsync(character, dst.InstanceId, dst.Revision, costs, level, bonus, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.ItemSuccession_Fail_InvalidMaterial, false)];
        if (success)
        {
            successionCounts[0]++;
            await SaveSettingAsync("succession_count", successionCounts, ct);
        }
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemEnchant);
        replies.Add(Reply(0, success));
        if (dst.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }

    async Task<List<byte[]>> SoulAddAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 14) return [];
        var key = BitConverter.ToInt64(plain, 2);
        var costItem = BitConverter.ToInt32(plain, 10);
        byte[] Reply(int result, bool success) => PacketWriter.Opcode(ItemSoulAddRs).U16(result).Bool(success).I64(key).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(EResult.Item_Enchant_InternalError, false)];
        if (Enhance.SoulAddOf(Enhance.SoulAddGroupOf(row.TemplateId)) is not { } step || ItemDB.Embedded.Of(row.TemplateId) is not { } item)
            return [Reply(EResult.Item_Enchant_NotEnchantableItem, false)];
        if (SoulBindCount(row) == 0) return [Reply(EResult.Item_Enchant_NotEnchantableItem_NeedSoulBind, false)];
        var added = UpgradeList(row, "soul_add_stats");
        if (added.Count >= MaxSoulAdds) return [Reply(EResult.Item_Enchant_NotEnchantableItem, false)];
        // The row names one stone (2S); the 3S stone of tier 2 is accepted when the client names it (CANDIDATE: tiers by the item).
        var stone = costItem != 0 && ItemDB.Embedded.Of(costItem)?.Use?.Effect == "SoulAdd" ? costItem : step.ItemId;
        if (TableCosts(step.Kina, [(stone, step.Count)], row.InstanceId, out var failure) is not { } costs) return [Reply(failure, false)];
        var success = EnchantRoll() < step.Success;
        var have = UpgradeList(row, "soul_bind_stats").Concat(added).Select(s => s.Stat).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roll = success ? StatPools.Embedded.SoulBind(item with { RandomStats = item.RandomStats.Where(r => !have.Contains(r.Name)).ToList() }, 1, rng) : [];
        var done = await Stored(async () =>
        {
            if (!await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, added.Count + (success ? 1 : 0), 0, ct, "soul_add")) return [];
            if (success && roll.Count > 0
                && !await store.SetItemUpgradeAsync(character, row.InstanceId, "soul_add_stats", JsonSerializer.Serialize(added.Concat(roll).Select(s => new object[] { s.Stat, s.Value })), ct))
                return [];
            return [[]];
        });
        if (done.Count == 0) return [Reply(EResult.Item_Enchant_InternalError, false)];
        var replies = await InventoryChangesAsync(ct, 0, success ? DecreaseItemEnchant : DecreaseItemEnchantFail);
        replies.Add(Reply(0, success));
        if (success && row.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }
}
