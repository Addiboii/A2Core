using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Bag and warehouse slot extensions (tools/data/slot-extensions.json, tools/slot_extension_data.py: InventorySlot,
/// StorageExtensionCost and the Item.dat vouchers of build 26262663; default/max line counts GlobalSetting 2.0.6).
/// Per EItemContainerType (1 kInventory, 2 kStorage, 15 kCharacterStorage): default and max lines, the Kina ladder and which
/// count picks its step ('gold_extensions' or 'line'), and the voucher items that open one line.
/// </summary>
public sealed class SlotExtensionTables
{
    public sealed record Container(int DefaultLines, int MaxLines, bool TierByLine, IReadOnlyList<long> GoldCosts)
    {
        /// <summary>Extensions a container can take in all (gold and vouchers): max lines - default lines.</summary>
        public int MaxExtensions => Math.Max(0, MaxLines - DefaultLines);
    }

    readonly Dictionary<int, Container> containers = [];
    readonly Dictionary<int, int> vouchers = [];

    SlotExtensionTables(JsonElement root)
    {
        foreach (var p in root.GetProperty("containers").EnumerateObject())
            containers[EmbeddedJson.Int(p.Name)] = new Container(p.Value.GetProperty("default_lines").GetInt32(), p.Value.GetProperty("max_lines").GetInt32(),
                p.Value.GetProperty("gold_tier").GetString() == "line", [.. p.Value.GetProperty("gold_costs").EnumerateArray().Select(c => c.GetInt64())]);
        foreach (var p in root.GetProperty("vouchers").EnumerateObject()) vouchers[EmbeddedJson.Int(p.Name)] = p.Value.GetInt32();
    }

    static readonly Lazy<SlotExtensionTables> embedded = new(() => new SlotExtensionTables(EmbeddedJson.Load("slot-extensions.json")));
    public static SlotExtensionTables Embedded => embedded.Value;

    public Container? Of(int container) => containers.GetValueOrDefault(container);
    /// <summary>The container a voucher item extends (Item.dat UseEffect Extend*Slot), or 0.</summary>
    public int VoucherFor(int templateId) => vouchers.GetValueOrDefault(templateId);

    /// <summary>
    /// The Kina for the next gold extension, chosen by the server from the stored counts (CANDIDATE: the bag by its gold
    /// extensions, a warehouse by the line it opens); null past the ladder or the maximum.
    /// </summary>
    public long? NextGoldCost(int container, int byGold, int byItem)
    {
        if (Of(container) is not { } c || byGold + byItem >= c.MaxExtensions) return null;
        var step = c.TierByLine ? byGold + byItem : byGold;
        return step >= 0 && step < c.GoldCosts.Count ? c.GoldCosts[step] : null;
    }
}

// ItemContainerSlotExtend RQ/RS/NT 0x8d4f/0x8d50/0x8d51: developer 0x398a-0x398c + 0x53c5, the 0x8d run's offset (between
// the recorded BroadcastSealStoneChange_NT 0x8d4e and NpcFirstAggro_NT 0x8d52). CANDIDATE until a live reply works. Layouts
// are the usmap structs (CANDIDATE): RQ {npc key i32, EItemContainerType u8, item uid i64, cost index u8}; RS {result,
// npc key, container, item uid, cost index, extend count by item u8, by gold u8}; NT {container, item uid, by item, by gold}.
// The result is u16 as in the recorded replies (ItemUse_RS, ItemLooting_RS); the usmap says i32.
public sealed partial class GameSession
{
    public const ushort ItemContainerSlotExtendRq = 0x8d4f, ItemContainerSlotExtendRs = 0x8d50, ItemContainerSlotExtendNt = 0x8d51;
    static SlotExtensionTables SlotTables => SlotExtensionTables.Embedded;

    /// <summary>Extensions by EItemContainerType: lines bought with Kina, lines opened with vouchers.</summary>
    readonly Dictionary<int, (int ByGold, int ByItem)> slotExtensions = [];
    public IReadOnlyDictionary<int, (int ByGold, int ByItem)> SlotExtensions => slotExtensions;

    async Task LoadSlotExtensionsAsync(CancellationToken ct)
    {
        slotExtensions.Clear();
        if (Economy is not { } economy) return;
        foreach (var e in await economy.SlotExtensionsAsync(Me, ct)) slotExtensions[e.Container] = (e.ByGold, e.ByItem);
    }

    /// <summary>
    /// The six u8 counts ending 0x5611 ItemDepotList_RS, in the usmap order: inventory by item, inventory by gold, storage
    /// (CANDIDATE: by gold, the field has no suffix), storage by item, character storage by gold, character storage by item.
    /// All zero without extensions (the recorded lists).
    /// </summary>
    byte[] SlotExtensionTail()
    {
        static byte B(int n) => (byte)Math.Clamp(n, 0, 255);
        var (inv, storage, chars) = (slotExtensions.GetValueOrDefault(InventoryItem.Bag), slotExtensions.GetValueOrDefault(InventoryItem.Storage),
            slotExtensions.GetValueOrDefault(CharacterStorageContainer));
        return [B(inv.ByItem), B(inv.ByGold), B(storage.ByGold), B(storage.ByItem), B(chars.ByGold), B(chars.ByItem)];
    }

    const int CharacterStorageContainer = 15;   // EItemContainerType kCharacterStorage (usmap)

    /// <summary>
    /// ItemContainerSlotExtend_RQ: with an item uid, a voucher of that container (Item.dat UseEffect) opens one line; without,
    /// one line for Kina. The server picks the step from the stored counts and ignores the request's cost index; the store
    /// re-checks the counts and the maximum inside the transaction that pays.
    /// </summary>
    async Task<List<byte[]>> ExtendSlotsAsync(byte[] plain, CancellationToken ct)
    {
        int npc = BitConverter.ToInt32(plain, 2), container = plain[6];
        var uid = BitConverter.ToInt64(plain, 7);
        byte requestedIndex = plain[15];
        var (byGold, byItem) = slotExtensions.GetValueOrDefault(container);
        byte[] Reply(int result, byte index) => PacketWriter.Opcode(ItemContainerSlotExtendRs).U16(result).I32(npc).U8(container).I64(uid).U8(index)
            .U8(Math.Clamp(slotExtensions.GetValueOrDefault(container).ByItem, 0, 255)).U8(Math.Clamp(slotExtensions.GetValueOrDefault(container).ByGold, 0, 255)).ToArray();
        if (Economy is not { } economy || SlotTables.Of(container) is not { } table) return [Reply(1, requestedIndex)];
        var byVoucher = uid != 0;
        List<(Guid Item, long Revision, int Count)>? costs;
        var index = (byte)Math.Clamp(table.TierByLine ? byGold + byItem : byGold, 0, 255);
        if (byVoucher)
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag" || SlotTables.VoucherFor(row.TemplateId) != container || row.Quantity < 1)
                return [Reply(1, requestedIndex)];
            if (byGold + byItem >= table.MaxExtensions) return [Reply(StoreResult("max"), requestedIndex)];
            costs = [(row.InstanceId, row.Revision, 1)];
            index = requestedIndex;
        }
        else
        {
            if (SlotTables.NextGoldCost(container, byGold, byItem) is not { } kina) return [Reply(StoreResult("max"), index)];
            if (CostStacks(KinaItemId, kina) is not { } stacks) return [Reply(StoreResult("money"), index)];
            costs = stacks;
        }
        var outcome = "none";
        await Stored(async () => { outcome = await economy.ExtendSlotsAsync(Me, container, byVoucher, byGold, byItem, table.MaxExtensions, costs, ct); return []; });
        if (outcome != "ok")
        {
            await Stored(async () => { await LoadSlotExtensionsAsync(ct); return []; });
            return [Reply(StoreResult(outcome), index)];
        }
        slotExtensions[container] = byVoucher ? (byGold, byItem + 1) : (byGold + 1, byItem);
        var (gold, item) = slotExtensions[container];
        var nt = PacketWriter.Opcode(ItemContainerSlotExtendNt).U8(container).I64(uid).U8(Math.Min(item, 255)).U8(Math.Min(gold, 255)).ToArray();
        return [Reply(0, index), .. await InventoryChangesAsync(ct, 0, byVoucher ? DecreaseItemUse : (byte)0), nt];
    }
}
