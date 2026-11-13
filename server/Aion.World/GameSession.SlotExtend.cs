using Aion.Protocol.Native;

namespace Aion.World;

// Bag/storage expansion. ItemContainerSlotExtend_RQ 0x8d4f {npc key i32, EItemContainerType u8, item uid i64, cost idx u8}
// → _RS 0x8d50 {result, npc key, container, item uid, cost idx, extend count by item u8, by gold u8} and _NT 0x8d51
// {container, item uid, by item, by gold}. Opcodes: usmap 0x398a-0x398c + 0x53c5, the offset of the OBSERVED neighbours
// 0x8d4e BroadcastSealStoneChange_NT and 0x8d52 NpcFirstAggro_NT; the live client sends a 16-byte 0x8d4f (2 + 4 + 1 + 8 + 1).
// Fields: usmap; result u16 as every observed RS (CANDIDATE), an EResult code (the client shows its message: our old
// small codes read as kFail_ContentsBan/kFail_AccountBan, "unavailable due to account restrictions"). The counts persist in character setting "slot_extends" and
// go out in ItemDepotList_RS's six counts. Paying with an item spends the bag item the client names (it only offers
// that with an expansion ticket; ticket ids are not in our tables).
//
// Sizes and prices are the client's tables: one expansion is one slot line of 5 (GlobalSetting inventory_slot_count_per_line /
// storage_slot_count_per_line 5). The bag starts with 8 lines (inventory_default_slot_line_count), both warehouses with 5
// (storage_default_slot_line_count, character_storage_default_slot_line_count); each goes up to 80 lines (InventorySlot
// MaxSlotLineCount, storage_max_slot_line_count, character_storage_max_slot_line_count). Kina (GoldCombined) per gold
// expansion: the bag's n-th from InventorySlot SlotExtensionCostList (10 steps), a warehouse's next line from
// StorageExtensionCost (SlotLineNum 1-36, SlotExtensionCostList for the account warehouse, CharStorageSlotExtensionCostList
// for the character warehouse - the same prices; lines 1-5 are the free default ones). No storing or taking-out fee exists
// in the tables (only the guild storage has one, guild_storage_cost_store/takeout 0).
public sealed partial class GameSession
{
    public const ushort SlotExtendRq = 0x8d4f, SlotExtendRs = 0x8d50, SlotExtendNt = 0x8d51;
    /// <summary>GlobalSetting inventory_slot_count_per_line / storage_slot_count_per_line.</summary>
    public const int SlotsPerLine = 5;
    /// <summary>GlobalSetting inventory_default_slot_line_count; InventorySlot MaxSlotLineCount.</summary>
    public const int BagDefaultLines = 8, BagMaxLines = 80;
    /// <summary>GlobalSetting storage_default_slot_line_count / character_storage_default_slot_line_count and the max_ ones.</summary>
    public const int StorageDefaultLines = 5, StorageMaxLines = 80;
    /// <summary>InventorySlot SlotExtensionCostList (GoldCombined): Kina for the bag's 1st..10th gold expansion.</summary>
    public static readonly long[] BagExtendKina = [10_000, 20_000, 40_000, 80_000, 120_000, 180_000, 250_000, 400_000, 600_000, 1_000_000];
    /// <summary>StorageExtensionCost SlotExtensionCostList / CharStorageSlotExtensionCostList (GoldCombined, identical): Kina for
    /// warehouse line 1..36 (index 0 = SlotLineNum 1; the first five are the default lines).</summary>
    public static readonly long[] StorageExtendKina = [1, 1, 1, 1, 1, 1_000, 1_000, 1_500, 2_000, 2_500, 3_500, 5_000, 6_500, 9_000, 15_000,
        20_000, 25_000, 35_000, 45_000, 60_000, 80_000, 150_000, 150_000, 200_000, 300_000, 400_000, 550_000, 700_000, 950_000,
        1_300_000, 1_800_000, 2_500_000, 3_300_000, 4_500_000, 6_200_000, 8_400_000];
    const byte DecreaseSlotExtend = 0;   // EDecreaseReason not known for this (CANDIDATE)

    /// <summary>ItemDepotList_RS order: inventory by item, inventory by gold, storage (gold), storage by item,
    /// character storage by gold, character storage by item.</summary>
    int[] slotExtends = new int[6];
    public IReadOnlyList<int> SlotExtends => slotExtends;

    /// <summary>The (by item, by gold) indexes into slotExtends for an EItemContainerType; null: not expandable.</summary>
    static (int Item, int Gold)? SlotExtendIndex(byte container) => container switch
    {
        InventoryItem.Bag => (0, 1),                 // kInventory
        InventoryItem.Storage => (3, 2),             // kStorage
        InventoryItem.CharacterStorage => (5, 4),    // kCharacterStorage
        _ => null,
    };

    /// <summary>The EItemContainerType of a stored location (bag 1, account warehouse 2, character warehouse 15).</summary>
    public static byte ContainerOf(string location) => location switch
    {
        "storage" => InventoryItem.Storage,
        "character_storage" => InventoryItem.CharacterStorage,
        _ => InventoryItem.Bag,
    };

    /// <summary>The stored location of a bag or warehouse EItemContainerType; null for the others.</summary>
    public static string? LocationOf(byte container) => container switch
    {
        InventoryItem.Bag => "bag",
        InventoryItem.Storage => "storage",
        InventoryItem.CharacterStorage => "character_storage",
        _ => null,
    };

    /// <summary>Slot lines a container has now: its default lines plus the expansions (each one line), up to its maximum.</summary>
    int SlotLines(byte container)
    {
        if (SlotExtendIndex(container) is not { } at) return 0;
        var (initial, max) = container == InventoryItem.Bag ? (BagDefaultLines, BagMaxLines) : (StorageDefaultLines, StorageMaxLines);
        return Math.Min(max, initial + slotExtends[at.Item] + slotExtends[at.Gold]);
    }

    /// <summary>Slots a bag or warehouse has now (lines × 5).</summary>
    public int SlotCapacity(byte container) => SlotLines(container) * SlotsPerLine;

    /// <summary>Kina for the next gold expansion of a container (client tables); null when the table has no further step.</summary>
    long? NextExtendKina(byte container)
    {
        if (SlotExtendIndex(container) is not { } at) return null;
        if (container == InventoryItem.Bag)
            return slotExtends[at.Gold] < BagExtendKina.Length && SlotLines(container) < BagMaxLines ? BagExtendKina[slotExtends[at.Gold]] : null;
        var line = SlotLines(container) + 1;   // StorageExtensionCost SlotLineNum of the line bought
        return line <= StorageExtendKina.Length && line <= StorageMaxLines ? StorageExtendKina[line - 1] : null;
    }

    async Task<List<byte[]>> SlotExtendAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        var uid = BitConverter.ToInt64(plain, 7);
        var costIdx = plain[15];
        var index = SlotExtendIndex(container);
        byte[] Reply(int result)
        {
            var (byItem, byGold) = index is { } i ? (slotExtends[i.Item], slotExtends[i.Gold]) : (0, 0);
            return PacketWriter.Opcode(SlotExtendRs).U16(result).I32(npc).U8(container).I64(uid).U8(costIdx)
                .U8((byte)byItem).U8((byte)byGold).ToArray();
        }
        if (index is not { } at || Store is not { } store || CharacterId is not { } character) return [Reply(EResult.Item_Container_Slot_Extend_Fail)];
        var slot = uid != 0 ? at.Item : at.Gold;
        var maxLines = container == InventoryItem.Bag ? BagMaxLines : StorageMaxLines;
        if (SlotLines(container) >= maxLines) return [Reply(EResult.Item_Container_Slot_Extend_Fail_OverMaxExtendCount)];

        List<(Guid Item, long Revision, int Count)> costs;
        if (uid != 0)
        {
            if (!storedItems.TryGetValue(uid, out var ticket) || ticket.Location != "bag") return [Reply(EResult.Item_Container_Slot_Extend_Fail_InvalidItem)];
            costs = [(ticket.InstanceId, ticket.Revision, 1)];
        }
        else if (NextExtendKina(container) is not { } price) return [Reply(EResult.Item_Container_Slot_Extend_Fail_OverMaxExtendCount)];
        else if (CostStacks(KinaItemId, price) is { } kina) costs = kina;
        else return [Reply(EResult.Item_Common_Fail_NotEnoughCountMoney)];

        var paid = await Stored(async () =>
        {
            foreach (var (item, revision, count) in costs)
                if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
            return [[]];
        });
        if (paid.Count == 0) return [Reply(EResult.Item_Container_Slot_Extend_Fail), .. await InventoryChangesAsync(ct, 0, 0)];

        slotExtends[slot]++;
        await SaveSettingAsync("slot_extends", slotExtends, ct);
        var nt = PacketWriter.Opcode(SlotExtendNt).U8(container).I64(uid)
            .U8((byte)slotExtends[at.Item]).U8((byte)slotExtends[at.Gold]).ToArray();
        return [Reply(EResult.Item_Container_Slot_Extend_Success), nt, .. await InventoryChangesAsync(ct, 0, DecreaseSlotExtend)];
    }
}
