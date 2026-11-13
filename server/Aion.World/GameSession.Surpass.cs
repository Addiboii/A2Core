using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Enhance Potential (Surpass). Live opcodes in the 0x56 run (catalogue index + 0x1ebe, as ItemImprint 0x5629/0x562a):
// ItemSurpassEnchant_RQ 0x5627 -> RS 0x5628. Request OBSERVED (our trace 2026-11-07 14:56, the user's tries): equip uid i64,
// the client's costs [ComponentItem: origin item id i32, replaced id i32, items [{key i64, count i64}]], e.g. Potential
// Stone 537030001 x1 and Abyss Points 930100003 x100000. Chances, costs and stats are the client's ItemSurpass table for the
// item's SurpassGroup (tools/data/enhance.json; it was encrypted before the full export): row = surpass level, SuccessProb +
// the correction built up, FailCorrectionProb per failure, CostGold Kina and CostItems charged from the player's stacks (the
// client's list names the same items), the level's AdditionalStat is what the item gives. Reply (usmap): result u16 (as the
// other item replies), success bool, equip uid i64, surpass level u8, after the item update (official order of the item
// replies). The level and the failure correction are upgrades.surpass / surpass_bonus; the item record carries both.
public sealed partial class GameSession
{
    public const ushort ItemSurpassEnchantRq = 0x5627, ItemSurpassEnchantRs = 0x5628;
    /// <summary>The Enhance Potential stats of an item at its level.</summary>
    static IReadOnlyList<(string Stat, int Value)> SurpassStats(ItemState item) =>
        EnchantOf(item, "surpass") is > 0 and var level ? Enhance.SurpassStats(ItemDB.Embedded.Of(item.TemplateId)?.SurpassGroup, level) : [];

    async Task<List<byte[]>> SurpassAsync(byte[] plain, CancellationToken ct)
    {
        var key = BitConverter.ToInt64(plain, 2);
        byte[] Reply(int result, bool success, int level) =>
            PacketWriter.Opcode(ItemSurpassEnchantRs).U16(result).Bool(success).I64(key).U8((byte)level).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(EResult.Item_SurpassEnchant_Fail_NotEnchantableItem, false, 0)];
        var level = EnchantOf(row, "surpass");
        var group = ItemDB.Embedded.Of(row.TemplateId)?.SurpassGroup;
        if (!Enhance.HasSurpass(group)) return [Reply(EResult.Item_SurpassEnchant_Fail_NotEnchantableItem, false, level)];
        if (Enhance.Surpass(group, level) is not { } step) return [Reply(EResult.Item_SurpassEnchant_Fail_MaxEnchantLevel, false, level)];
        if (SoulBindCount(row) == 0) return [Reply(EResult.Item_SurpassEnchant_Fail_NeedSoulBind, false, level)];
        if (TableCosts(step.Kina, step.Costs, row.InstanceId, out var failure) is not { } costs) return [Reply(failure, false, level)];
        var bonus = EnchantOf(row, "surpass_bonus");
        var success = EnchantRoll() < step.Success + bonus;
        var after = success ? level + 1 : level;
        var done = await Stored(async () => await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after,
            success ? 0 : bonus + step.FailCorrection, ct, "surpass") ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.Item_SurpassEnchant_Fail_NotEnchantableItem, false, level)];
        List<byte[]> replies = [.. await InventoryChangesAsync(ct, 0, success ? DecreaseItemEnchant : DecreaseItemEnchantFail), Reply(0, success, after)];
        if (success && row.Location == "equipment") replies.AddRange(RefreshStats());
        return replies;
    }

    public IReadOnlyCollection<InventoryItem> InventoryItemsForChecks() => inventory.Values;

    /// <summary>The request's ComponentItem list as (item id, stack key, count); null when it does not parse.</summary>
    static List<(int ItemId, long Key, long Count)>? ReadCostItems(byte[] plain, int offset)
    {
        try
        {
            var r = new PacketReader(plain) { Offset = offset };
            var list = new List<(int, long, long)>();
            for (var n = r.Varint(); n > 0; n--)
            {
                int origin = r.I32(), replaced = r.I32();
                for (var m = r.Varint(); m > 0; m--) list.Add((replaced != 0 ? replaced : origin, r.I64(), r.I64()));
            }
            return list;
        }
        catch (Exception e) when (e is IncompleteException or ArgumentOutOfRangeException or InvalidDataException) { return null; }
    }
}
