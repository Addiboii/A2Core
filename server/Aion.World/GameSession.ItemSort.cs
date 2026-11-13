using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Sorting and arranging items (fix/sort-filter, a2core-log/sort-filter-2026-11-13.md): ItemSort_RQ 0x563e for the bag and both
// warehouses, ItemStore/ItemTakeOut 0x5642/0x5644 with stack merging, MergeStorageItem_RQ and the legion warehouse sort. The
// inventory filter tabs (Table_InventoryFilter / ItemTypeFilter) are client-side only: no request reaches the server.
public sealed partial class GameSession
{
    // MergeStorageItem_RQ/RS: catalogue index 1536/1537 + 0xf9b1, the run of the OBSERVED 0xffa5/0xffa7/0xffab/0xffae (CANDIDATE: never
    // recorded). GuildStorageItemSort_RQ/RS: dev 0x3920/0x3921 + 0x518b, the offset of GuildStorageInfo_RQ 0x3915 -> 0x8aa0 (CANDIDATE).
    public const ushort MergeStorageItemRq = 0xffb1, MergeStorageItemRs = 0xffb2, GuildStorageItemSortRq = 0x8aab, GuildStorageItemSortRs = 0x8aac;

    /// <summary>GlobalSetting storage_max_exchange_count: stacks one store or take-out request may move.</summary>
    public const int StorageMaxExchangeCount = 100;

    // EIncreaseReason / EDecreaseReason (usmap). OBSERVED (115817 #968581, a store onto existing warehouse stacks): the warehouse
    // stack's update carries increase 15 kItemStore, the emptied bag stack's removal decrease 25 kItemStore; a stack moved whole
    // carries no reason (105448 #28032 sort, 115817 #977578 store). Take out (16 kItemTakeOut / 34 kItemTakeOut) and the sort's own
    // merges (14 / 24 kItemSort) are CANDIDATE, by the same enums.
    const byte IncreaseItemSort = 14, IncreaseItemStore = 15, IncreaseItemTakeOut = 16, DecreaseItemSort = 24, DecreaseItemStore = 25, DecreaseItemTakeOut = 34;

    /// <summary>The slots of a container: the bag's room (all of it when unlimited), a warehouse's bought lines.</summary>
    int CapacityOf(string location) => location == "bag" ? (BagSlots() > 0 ? BagSlots() : 1000) : SlotCapacity(ContainerOf(location));

    /// <summary>The stored bag and warehouse rows on the slots the client shows them on (a bag row without a stored slot has its shown one).</summary>
    ItemPlan PlanItems()
    {
        var shown = new Dictionary<Guid, int>();
        foreach (var (key, row) in storedItems)
            if (row.Slot is null && inventory.TryGetValue(key, out var item) && item.Container == ContainerOf(row.Location) && row.Location != "equipment")
                shown[row.InstanceId] = item.Slot;
        return new ItemPlan(storedItems.Values.Where(r => r.Location is "bag" or "storage" or "character_storage"), shown);
    }

    /// <summary>
    /// 0x563e ItemSort_RQ {npc key i32, EItemContainerType u8}: the container's items laid out in ItemSortOrder on slots 0, 1, 2, ...
    /// OBSERVED (7 official bag sorts, rec 20261106-105448/115817/145442): ItemModificationList_NT 0x561b first, holding every item
    /// whose slot changed (type 1 update, no reasons) in new-slot order, then 0x563f {result u16 576 Item_Sort_Success, npc key i32,
    /// container u8} (`3f56 4002 00000000 01`). Before, 0x563f came first with result 0 and the bag was ordered by item id.
    /// The warehouses (2 kStorage, 15 kCharacterStorage) sort the same way (CANDIDATE: never recorded). Stacks of one template are
    /// merged up to Item.dat MaxStackCount first (CANDIDATE: no recorded sort had two stacks of one item; a merged stack's update
    /// carries increase 14 and the emptied one's removal decrease 24 kItemSort, listed after the moves). Refused: 577 Item_Sort_Fail.
    /// </summary>
    async Task<List<byte[]>> SortContainerAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(ItemSortRs).U16(result).I32(npc).U8(container).ToArray();
        if (LocationOf(container) is not { } location || Store is not { } store || CharacterId is not { } character) return [Reply(EResult.Item_Sort_Fail)];
        var plan = PlanItems();
        plan.Sort(location, CapacityOf(location));
        var rows = plan.Result();
        if (rows.Count == 0) return [Reply(EResult.Item_Sort_Success)];   // already in order: nothing moves
        var done = await Stored(async () => await store.ArrangeItemsAsync(character, rows, "item_sort", ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.Item_Sort_Fail)];
        var changes = await InventoryChangesAsync(ct, IncreaseItemSort, DecreaseItemSort, m => m.Type == 2 ? long.MaxValue : m.Item.Slot, arranged: true);
        return [.. changes, Reply(EResult.Item_Sort_Success)];
    }

    /// <summary>
    /// 0x5642 ItemStore_RQ / 0x5644 ItemTakeOut_RQ {npc key i32, items map key i64 -> count i64, EItemContainerType u8}: items from the bag
    /// into a warehouse, or back (whole stacks or part of one), answered 0x5643 / 0x5645 with the same fields. The container picks the
    /// warehouse tab: 2 kStorage the account warehouse (location 'storage', shared by the account's characters on the server), 15
    /// kCharacterStorage the character's own (location 'character_storage'). Results are the client's EResult, as the official
    /// reply: 1024/1025 store, 1040/1041 take out, 1026 an equipped item, 264 not enough free slots.
    /// OBSERVED (115817 #968581, two stacks stored onto warehouse stacks of the same items; 105448 #170416 and 115817 #977578 moved
    /// whole): a stack goes onto the warehouse's stack of the same item (its count grows, increase 15; the bag stack is removed,
    /// decrease 25), else it keeps its key and moves whole; the 0x561b comes first (per stored entry: the warehouse stack, then the bag
    /// stack), then 0x5643 result 1024. Before, every entry became a stack of its own and 0x5643 came first.
    /// CANDIDATE: what does not fit onto the existing stacks takes the first free slot; a part of a stack becomes a stack of its own;
    /// take out works the same way into the bag (increase 16, decrease 34 kItemTakeOut).
    /// </summary>
    internal async Task<List<byte[]>> StoreOrTakeOutAsync(byte[] plain, bool store, CancellationToken ct)
    {
        int npc;
        var items = new List<(long Key, long Count)>();
        byte container;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            npc = (int)reader.U32();
            var count = reader.Varint();
            if (count is 0 or > 300) return [];
            for (var i = 0; i < count; i++) items.Add(((long)reader.U64(), (long)reader.U64()));
            container = reader.U8();
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(store ? ItemStoreRs : ItemTakeOutRs).U16(result).I32(npc).Varint(items.Count);
            foreach (var (key, count) in items) w.I64(key).I64(count);
            return w.U8(container).ToArray();
        }
        var fail = store ? EResult.Item_Storage_Store_Fail : EResult.Item_Storage_TakeOut_Fail;
        var warehouse = container is InventoryItem.Storage or InventoryItem.CharacterStorage ? LocationOf(container)! : null;
        if (Store is not { } st || CharacterId is not { } character || warehouse is null || items.Count > StorageMaxExchangeCount
            || items.Select(i => i.Key).Distinct().Count() != items.Count) return [Reply(fail)];
        var (from, to) = store ? ("bag", warehouse) : (warehouse, "bag");
        var rows = new List<(ItemState Row, long Count)>();
        foreach (var (key, count) in items)
        {
            if (!storedItems.TryGetValue(key, out var row) || count < 1 || count > row.Quantity) return [Reply(fail)];
            if (store && row.Location == "equipment") return [Reply(EResult.Item_Storage_Store_Fail_EquippedItem)];
            if (row.Location != from || store && !AllowedIn(row.TemplateId, warehouse)) return [Reply(fail)];
            rows.Add((row, count));
        }
        var plan = PlanItems();
        foreach (var (row, count) in rows)
            if (!plan.Transfer(row.InstanceId, count, to, CapacityOf(to))) return [Reply(EResult.Item_Common_Fail_NotEnoughSlotCountContainer)];
        var done = await Stored(async () => await st.ArrangeItemsAsync(character, plan.Result(), store ? "item_store" : "item_take_out", ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(fail)];
        // Per entry: the stacks of its item (the receiving stack before the emptied one), as the official list.
        var templates = rows.Select(r => r.Row.TemplateId).Distinct().ToList();
        var changes = await InventoryChangesAsync(ct, store ? IncreaseItemStore : IncreaseItemTakeOut, store ? DecreaseItemStore : DecreaseItemTakeOut,
            m => templates.IndexOf(m.Item.ItemId) is var at and >= 0 ? at * 2L + (m.Type == 2 ? 1 : 0) : long.MaxValue, arranged: true);
        return [.. changes, Reply(store ? EResult.Item_Storage_Store_Success : EResult.Item_Storage_TakeOut_Success), .. store ? Achieve("StorageStore") : []];
    }

    /// <summary>
    /// MergeStorageItem_RQ {npc key i32, items array of item keys i64, src EItemContainerType u8, dst EItemContainerType u8} (usmap; the
    /// warehouse's "merge" button): each listed stack of the source container goes onto the destination's stacks of the same item, as
    /// far as they have room; the rest stays where it was. Answered MergeStorageItem_RS {result u16, npc key, the keys, src, dst} after
    /// the 0x561b. CANDIDATE throughout (never recorded): the opcode pair (0xffb1/0xffb2), the results (store 1024/1025 into a
    /// warehouse, take out 1040/1041 into the bag; a request that moves nothing fails) and the reasons (as ItemStore / ItemTakeOut).
    /// </summary>
    async Task<List<byte[]>> MergeStorageItemAsync(byte[] plain, CancellationToken ct)
    {
        int npc;
        var keys = new List<long>();
        byte src, dst;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            npc = (int)reader.U32();
            var count = reader.Varint();
            if (count > 300) return [];
            for (var i = 0; i < count; i++) keys.Add((long)reader.U64());
            src = reader.U8();
            dst = reader.U8();
        }
        catch (ProtocolException) { return []; }
        var into = LocationOf(dst);
        var store = into is not "bag";
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(MergeStorageItemRs).U16(result).I32(npc).Varint(keys.Count);
            foreach (var key in keys) w.I64(key);
            return w.U8(src).U8(dst).ToArray();
        }
        var fail = store ? EResult.Item_Storage_Store_Fail : EResult.Item_Storage_TakeOut_Fail;
        if (Store is not { } st || CharacterId is not { } character || LocationOf(src) is not { } from || into is null || from == into || keys.Count == 0
            || keys.Count > StorageMaxExchangeCount || keys.Distinct().Count() != keys.Count) return [Reply(fail)];
        var plan = PlanItems();
        var templates = new List<int>();
        long moved = 0;
        foreach (var key in keys)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != from || !AllowedIn(row.TemplateId, into)) return [Reply(fail)];
            moved += plan.MergeInto(row.InstanceId, row.Quantity, into);
            templates.Add(row.TemplateId);
        }
        if (moved == 0) return [Reply(fail)];
        var done = await Stored(async () => await st.ArrangeItemsAsync(character, plan.Result(), "storage_merge", ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(fail)];
        var changes = await InventoryChangesAsync(ct, store ? IncreaseItemStore : IncreaseItemTakeOut, store ? DecreaseItemStore : DecreaseItemTakeOut,
            m => templates.IndexOf(m.Item.ItemId) is var at and >= 0 ? at * 2L + (m.Type == 2 ? 1 : 0) : long.MaxValue, arranged: true);
        return [.. changes, Reply(store ? EResult.Item_Storage_Store_Success : EResult.Item_Storage_TakeOut_Success)];
    }

    /// <summary>
    /// GuildStorageItemSort_RQ {npc key i32}: the legion warehouse is not built (always empty, GuildStorageInfo_RS), so there is nothing to
    /// order; answered {result u16, last sorted time i64} with 0 and now, or Guild_RequestWithNoGuild (CANDIDATE: opcode and result).
    /// </summary>
    List<byte[]> GuildStorageItemSort(long now) =>
        [PacketWriter.Opcode(GuildStorageItemSortRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0).I64(guild is null ? 0 : now).ToArray()];
}

/// <summary>
/// A working copy of the bag and warehouse rows for one request: sorted, stacked, moved and split here, then handed to the store as
/// ItemArrangement rows (only the rows that changed). Stacks merge only when Item.dat lets the item stack (MaxStackCount above 1), it
/// has no MaintainTime period (the period runs from each stack's own acquisition) and the rows carry the same stats and upgrades.
/// </summary>
public sealed class ItemPlan
{
    sealed class Row(ItemState original, int? slot)
    {
        public ItemState Original { get; } = original;
        public string Location { get; set; } = original.Location;
        public int? Slot { get; set; } = slot;
        public long Quantity { get; set; } = original.Quantity;
        public int? StartSlot { get; } = slot;
    }

    readonly Dictionary<Guid, Row> rows = [];
    readonly List<ItemArrangement> splits = [];
    /// <summary>A sort stores every slot it gave (a bag row without a stored slot keeps the one it is shown on from then on).</summary>
    bool sorted;

    /// <param name="items">The bag and warehouse rows.</param>
    /// <param name="shownSlots">Slots the client shows rows without a stored slot on.</param>
    public ItemPlan(IEnumerable<ItemState> items, IReadOnlyDictionary<Guid, int>? shownSlots = null)
    {
        foreach (var item in items)
            rows[item.InstanceId] = new Row(item, item.Slot ?? (shownSlots is not null && shownSlots.TryGetValue(item.InstanceId, out var shown) ? shown : null));
    }

    static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "" : e.GetRawText();

    /// <summary>Whether two rows may become one stack.</summary>
    public static bool Stackable(ItemState a, ItemState b) =>
        a.TemplateId == b.TemplateId && ItemRules.StackLimit(a.TemplateId) > 1 && ItemDB.Embedded.Of(a.TemplateId) is not { MaintainMinutes: > 0 }
        && Raw(a.RolledStats) == Raw(b.RolledStats) && Raw(a.Upgrades) == Raw(b.Upgrades);

    /// <summary>The rows that take a slot in the location (a self-using item or a never-an-item currency row waits apart).</summary>
    IEnumerable<Row> Placed(string location) => rows.Values.Where(r => r.Location == location && r.Quantity > 0 && ItemRules.NeedsBagSlot(r.Original.TemplateId));

    static IEnumerable<Row> InAcquisitionOrder(IEnumerable<Row> list) =>
        list.OrderBy(r => r.Original.Acquired ?? DateTimeOffset.MaxValue).ThenBy(r => r.Original.WireId).ThenBy(r => r.Original.InstanceId);

    int? FreeSlot(string location, int capacity)
    {
        var used = rows.Values.Where(r => r.Location == location && r.Quantity > 0 && r.Slot is not null).Select(r => r.Slot!.Value)
            .Concat(splits.Where(s => s.Location == location && s.Slot is not null).Select(s => s.Slot!.Value)).ToHashSet();
        for (var slot = 0; slot < capacity; slot++) if (!used.Contains(slot)) return slot;
        return null;
    }

    /// <summary>Puts as much of count (of the source row) as fits onto the location's stacks of the same item, lowest slot first; returns how many moved.</summary>
    public long MergeInto(Guid source, long count, string location)
    {
        var from = rows[source];
        var limit = ItemRules.StackLimit(from.Original.TemplateId);
        long moved = 0;
        foreach (var target in Placed(location).Where(r => r != from && Stackable(r.Original, from.Original)).OrderBy(r => r.Slot ?? int.MaxValue).ToList())
        {
            var room = Math.Min(limit - target.Quantity, count - moved);
            if (room <= 0) continue;
            target.Quantity += room;
            moved += room;
            if (moved == count) break;
        }
        from.Quantity -= moved;
        return moved;
    }

    /// <summary>
    /// count of the source row to the location: onto its stacks of the same item first, the rest as the row itself (all of it moved)
    /// or as a new stack (a part) on the first free slot. False when the rest finds no free slot.
    /// </summary>
    public bool Transfer(Guid source, long count, string location, int capacity)
    {
        var from = rows[source];
        var whole = count == from.Quantity;
        var rest = count - MergeInto(source, count, location);
        if (rest == 0) return true;
        if (FreeSlot(location, capacity) is not { } slot) return false;
        if (whole) { from.Location = location; from.Slot = slot; return true; }
        from.Quantity -= rest;
        splits.Add(new ItemArrangement(from.Original.InstanceId, from.Original.Revision, location, slot, rest, Split: true));
        return true;
    }

    /// <summary>
    /// ItemSort: stacks of one item merged (the oldest stacks fill first), then every placed row on slots 0, 1, 2, ... in ItemSortOrder
    /// (equal items in acquisition order); rows past the capacity wait without a slot.
    /// </summary>
    public void Sort(string location, int capacity)
    {
        foreach (var group in Placed(location).GroupBy(r => r.Original.TemplateId).Where(g => g.Count() > 1).ToList())
        {
            var stacks = InAcquisitionOrder(group).ToList();
            for (var i = 0; i < stacks.Count; i++)
                for (var j = i + 1; j < stacks.Count && stacks[i].Quantity > 0; j++)
                {
                    if (stacks[j].Quantity == 0 || !Stackable(stacks[i].Original, stacks[j].Original)) continue;
                    var room = Math.Min(ItemRules.StackLimit(stacks[i].Original.TemplateId) - stacks[i].Quantity, stacks[j].Quantity);
                    if (room <= 0) break;
                    stacks[i].Quantity += room;
                    stacks[j].Quantity -= room;
                }
        }
        var ordered = InAcquisitionOrder(Placed(location)).OrderBy(r => ItemSortOrder.KeyOf(r.Original.TemplateId)).ToList();   // stable
        foreach (var row in rows.Values.Where(r => r.Location == location)) row.Slot = null;
        for (var n = 0; n < ordered.Count; n++) ordered[n].Slot = n < capacity ? n : null;
        sorted = true;
    }

    /// <summary>The changed rows (moved, restacked or emptied) and the new stacks, for INativeCharacterStore.ArrangeItemsAsync.</summary>
    public List<ItemArrangement> Result()
    {
        var changed = rows.Values.Where(r => r.Location != r.Original.Location || r.Slot != r.StartSlot || sorted && r.Slot != r.Original.Slot || r.Quantity != r.Original.Quantity)
            .Select(r => new ItemArrangement(r.Original.InstanceId, r.Original.Revision, r.Location, r.Quantity == 0 ? null : r.Slot, r.Quantity)).ToList();
        return [.. changed, .. splits];
    }
}
