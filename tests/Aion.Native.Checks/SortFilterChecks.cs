using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// fix/sort-filter (a2core-log/sort-filter-2026-11-13.md): the official sort order (ItemSortOrder) against every recorded bag sort, the
/// ItemSort_RQ replies byte for byte for the bag and the warehouses, the new slots stored, stacks merged by a sort (CANDIDATE), by
/// ItemStore (OBSERVED) and by MergeStorageItem (CANDIDATE), and the legion warehouse sort (CANDIDATE).
/// </summary>
static class SortFilterChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
    static ItemState Item(int template, long quantity, string location, int? slot, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, location, slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire)
        { Acquired = DateTimeOffset.UnixEpoch.AddSeconds(wire) };

    /// <summary>The items of every official bag sort, in the order the server laid them out (the 0x561b before each 0x563f).</summary>
    static readonly (string Where, int[] Ids)[] OfficialSorts =
    [
        ("105448 #28032", [930100011, 930100023, 110130033, 210230080, 210260031, 210660030, 210730081, 210730080, 530116001, 531360001, 503700031, 503700032,
            630840002, 630900003, 631900007]),
        ("105448 #34899", [210730081, 210730080, 530116001, 532760001, 533460001, 503700031, 503700032, 630840002, 630900003, 631900007, 632460001]),
        ("115817 #84754", [930100001, 930100003, 930100011, 930100023, 110130033, 210230080, 210730081, 210730080, 530146001, 530116001, 535400001, 530560001,
            531360001, 502760004, 532760001, 533460001, 503700031, 503700032, 503700033, 535106002, 630840002, 630543001, 630554009, 632540011, 632520088,
            630900003, 632460001]),
        ("115817 #208100", [930100001, 930100003, 930100024, 930100011, 930100017, 930100023, 930100019, 110230074, 310130049, 310450018, 311050002, 531110002,
            531110003, 530146001, 530116006, 530126003, 530116001, 530116010, 530126005, 530116014, 535200001, 531060025, 531060003, 531060015, 531060004,
            531060016, 531060026, 531060027, 531060028, 530560001, 531351001, 511360003, 533560001, 533560004, 531860002, 531860004, 531860006, 511490001,
            502760004, 532760001, 533460001, 503700031, 630840002, 631900013]),
        ("145442 #42652", [930100031, 930100001, 930100003, 930100011, 930100023, 110130033, 210230080, 210730081, 210730080, 530146001, 530116006, 530116001,
            535400001, 531060003, 502760004, 532760001, 533460001, 503700031, 503700032, 503700033, 535106002, 630840002, 632540011, 632520088, 630610001,
            631900007, 632460001]),
    ];

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- The order ----------
        foreach (var (where, ids) in OfficialSorts)
        {
            var shuffled = ids.Reverse().Concat(ids).Distinct().ToList();
            var ours = shuffled.OrderBy(ItemSortOrder.KeyOf).ToList();
            check(ours.SequenceEqual(ids), $"ItemSortOrder lays out the official sort {where} exactly ({ids.Length} items: type, category, grade, cooltime group, effect, level, id)");
        }
        check(ItemSortOrder.Compare(930100031, 930100001) < 0 && ItemSortOrder.Compare(930100001, 110130033) < 0 && ItemSortOrder.Compare(110130033, 530116001) < 0
              && ItemSortOrder.Compare(530116001, 630840002) < 0 && ItemSortOrder.Compare(632540011, 632520088) < 0 && ItemSortOrder.Compare(210730081, 210730080) < 0,
              "tradable Kina before bound Kina, currencies before gear before usables before materials; Legend before Rare; item level 41 before 36");

        // ---------- The bag ----------
        var store = new FakeStore();
        var session = await storedWorldSession("SortAudit", store);
        session.BagSlotsOverride = 400;
        store.Items.RemoveAll(i => i.Location == "bag");   // the starter items aside: only the items below
        store.Items.AddRange([
            Item(630840002, 5, "bag", 30, 9001),     // Misc
            Item(531060003, 30, "bag", 31, 9002),    // Scroll (Common)
            Item(530116001, 20, "bag", 32, 9003),    // Potion
            Item(535200001, 1, "bag", 33, 9004),     // Scroll (Special)
            Item(110130033, 1, "bag", 34, 9005),     // Greatsword
            Item(531060025, 9, "bag", 35, 9006),     // Scroll, magic_buff_potion
        ]);
        await session.InventoryChangesAsync(default);
        var before = session.InventoryItemsForChecks().ToDictionary(i => i.Key);
        var sorted = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.Bag).ToArray(), now);
        var bag = store.Items.Where(i => i.Location == "bag" && i.Slot is not null).OrderBy(i => i.Slot).ToList();
        check(sorted.Count == 2 && Op(sorted[0]) == 0x561b && sorted[1].SequenceEqual(Convert.FromHexString("3f5640020000000001"))
              && bag.Select(i => i.Slot!.Value).SequenceEqual(Enumerable.Range(0, bag.Count))
              && bag.Select(i => i.TemplateId).SequenceEqual(bag.Select(i => i.TemplateId).OrderBy(ItemSortOrder.KeyOf)),
              "ItemSort_RQ (bag): 0x561b first, then 0x563f byte for byte as official (3f56 4002 00000000 01: 576 Item_Sort_Success); slots 0.. stored in order");
        var after = session.InventoryItemsForChecks().Where(i => i.Container == InventoryItem.Bag).ToList();
        var moved = after.Where(i => before.TryGetValue(i.Key, out var b) && b.Slot != i.Slot).OrderBy(i => i.Slot).ToList();
        check(moved.Count > 0 && sorted[0].SequenceEqual(GamePackets.ItemModificationList(moved.Select(i => new ItemModification(1, i)).ToList())),
              $"the sort's 0x561b holds just the moved items, in new-slot order, type 1 update, no reasons (as the 7 official sorts; {moved.Count} moved)");
        var scrolls = bag.Where(i => i.WireId is 9002 or 9004 or 9006).Select(i => i.TemplateId).ToList();
        check(scrolls.SequenceEqual([535200001, 531060025, 531060003]) && store.Arrangements.Any(a => a.Reason == "item_sort"),
              "scrolls: the Special one first, then cooltime group magic_buff_potion before magic_scroll (official #208100); stored as one arrangement");
        await session.HandleAsync([0x10, 0x56], now);
        check(session.InventoryItemsForChecks().Where(i => i.Container == InventoryItem.Bag).All(i => store.Items.Any(s => s.WireId == i.Key && s.Slot == i.Slot)),
              "after reloading (0x5610) the bag shows the stored slots: the sort persists");
        var again = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.Bag).ToArray(), now);
        check(again.Count == 1 && Res(again[0]) == EResult.Item_Sort_Success, "sorting a sorted bag moves nothing: only 0x563f 576");

        // Two stacks of one item: merged by the sort (CANDIDATE), the oldest stack keeps the count.
        store.Items.AddRange([Item(530116001, 7, "bag", 60, 9010), Item(531060003, 9_995, "bag", 61, 9011)]);
        await session.InventoryChangesAsync(default);
        var potionKey = store.Items.Single(i => i.WireId == 9003);
        var merged = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.Bag).ToArray(), now);
        check(store.Items.Single(i => i.Location == "bag" && i.TemplateId == 530116001) is { WireId: 9003, Quantity: 27 }
              && store.Items.Where(i => i.Location == "bag" && i.TemplateId == 531060003).Select(i => i.Quantity).Order().SequenceEqual([25L, 10_000L])
              && Res(merged[^1]) == EResult.Item_Sort_Success
              && merged.Any(b => Op(b) == 0x561b && b.AsSpan().IndexOf(new PacketWriter().U8(2).U8(0).I64(9010).ToArray()) > 0),
              "ItemSort merges stacks of one item up to MaxStackCount (20 + 7 = 27; 30 + 9995 = 10000 + 25), the emptied stack removed (type 2)");

        // ---------- The warehouses ----------
        store.Items.AddRange([Item(630840002, 2, "character_storage", 4, 9020), Item(530116001, 3, "character_storage", 2, 9021), Item(110130033, 1, "character_storage", 0, 9022)]);
        await session.InventoryChangesAsync(default);
        var tab = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.CharacterStorage).ToArray(), now);
        var wh = store.Items.Where(i => i.Location == "character_storage").OrderBy(i => i.Slot).ToList();
        check(tab[^1].SequenceEqual(Convert.FromHexString("3f564002000000000f")) && Op(tab[0]) == 0x561b
              && wh.Select(i => (i.TemplateId, i.Slot)).SequenceEqual([(110130033, (int?)0), (530116001, 1), (630840002, 2)]),
              "ItemSort_RQ for the character warehouse (15): the same order on its slots 0.. (CANDIDATE: never recorded), 576");
        check(Res((await session.HandleAsync(new PacketWriter().U16(GameSession.ItemSortRq).I32(0).U8(InventoryItem.Equipped).ToArray(), now))[0]) == EResult.Item_Sort_Fail,
              "a container that cannot be sorted (equipped, 11) is refused: 577 Item_Sort_Fail");

        // ItemStore onto an existing warehouse stack (OBSERVED 115817 #968581): the warehouse stack grows (increase 15 kItemStore), the bag
        // stack goes (type 2, decrease 25 kItemStore), the 0x561b before 0x5643.
        store.Items.AddRange([Item(531360001, 4, "storage", 7, 9030), Item(531360001, 5, "bag", 70, 9031)]);
        await session.InventoryChangesAsync(default);
        var bagStone = session.InventoryItemsForChecks().Single(i => i.Key == 9031);
        var whStone = session.InventoryItemsForChecks().Single(i => i.Key == 9030);
        var stored = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemStoreRq).I32(0).Varint(1).I64(9031).I64(5).U8(InventoryItem.Storage).ToArray(), now);
        check(stored[0].SequenceEqual(GamePackets.ItemModificationList([new ItemModification(1, whStone with { Count = 9 }, Increase: 15),
                  new ItemModification(2, bagStone with { Count = 0, Detail = null }, Decrease: 25)]))
              && Op(stored[1]) == GameSession.ItemStoreRs && Res(stored[1]) == EResult.Item_Storage_Store_Success
              && store.Items.Single(i => i.WireId == 9030).Quantity == 9 && store.Items.All(i => i.WireId != 9031),
              "ItemStore onto the warehouse's stack of the item: one stack of 9, the bag stack gone; 0x561b (update +15, remove -25) then 0x5643 1024, as official");

        // MergeStorageItem (CANDIDATE 0xffb1): listed bag stacks onto the warehouse's stacks of the same item; one without a stack there fails.
        store.Items.AddRange([Item(530116001, 3, "storage", 8, 9040), Item(530116001, 2, "bag", 71, 9041)]);
        await session.InventoryChangesAsync(default);
        var bagPotion = store.Items.Single(i => i.Location == "bag" && i.TemplateId == 530116001 && i.WireId == 9003);
        var mergeRq = new PacketWriter().U16(GameSession.MergeStorageItemRq).I32(0).Varint(1).I64(9041).U8(InventoryItem.Bag).U8(InventoryItem.Storage).ToArray();
        var mergedIn = await session.HandleAsync(mergeRq, now);
        check(Op(mergedIn[^1]) == GameSession.MergeStorageItemRs && Res(mergedIn[^1]) == EResult.Item_Storage_Store_Success
              && mergedIn[^1].AsSpan(2).SequenceEqual(new PacketWriter().U16(EResult.Item_Storage_Store_Success).I32(0).Varint(1).I64(9041).U8(1).U8(2).ToArray())
              && store.Items.Single(i => i.WireId == 9040).Quantity == 5 && store.Items.All(i => i.WireId != 9041) && Op(mergedIn[0]) == 0x561b,
              "MergeStorageItem_RQ: the bag stack goes onto the warehouse stack (3 + 2), answered after the 0x561b with the request's keys");
        var noStack = await session.HandleAsync(new PacketWriter().U16(GameSession.MergeStorageItemRq).I32(0).Varint(1).I64(9005).U8(1).U8(2).ToArray(), now);
        check(noStack.Count == 1 && Res(noStack[0]) == EResult.Item_Storage_Store_Fail && store.Items.Single(i => i.WireId == 9005).Location == "bag",
              "an item the warehouse has no stack of is not merged (1025)");

        // Take out onto the bag's stack (CANDIDATE: as the store, increase 16 / decrease 34 kItemTakeOut).
        var taken = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemTakeOutRq).I32(0).Varint(1).I64(9040).I64(5).U8(InventoryItem.Storage).ToArray(), now);
        check(Res(taken.First(b => Op(b) == GameSession.ItemTakeOutRs)) == EResult.Item_Storage_TakeOut_Success
              && store.Items.Single(i => i.WireId == bagPotion.WireId).Quantity == bagPotion.Quantity + 5 && store.Items.All(i => i.WireId != 9040),
              "ItemTakeOut onto the bag's stack of the item: the warehouse stack goes, the bag stack grows by 5");

        // A stack with nothing to merge onto moves whole (OBSERVED 115817 #977578: same key) onto the first free slot (CANDIDATE).
        var parted = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemStoreRq).I32(0).Varint(1).I64(9005).I64(1).U8(InventoryItem.Storage).ToArray(), now);
        check(Res(parted.First(b => Op(b) == GameSession.ItemStoreRs)) == EResult.Item_Storage_Store_Success
              && store.Items.Single(i => i.WireId == 9005).Location == "storage" && store.Items.Single(i => i.WireId == 9005).Slot == 0,
              "a whole stack with nothing to merge onto keeps its key and takes the warehouse's first free slot");

        // ---------- ItemPlan rules ----------
        // A one-slot bag holding 9,999 potions: 1 more fits onto the stack, the other 4 need a slot there is none of.
        var bagStack = Item(530116001, 9_999, "bag", 0, 1);
        var whStack = Item(530116001, 5, "storage", 0, 2);
        var plan = new ItemPlan([bagStack, whStack]);
        var planOk = !plan.Transfer(whStack.InstanceId, 5, "bag", 1);
        var fits = new ItemPlan([bagStack, whStack]);
        check(planOk && fits.Transfer(whStack.InstanceId, 1, "bag", 1)
              && fits.Result().OrderBy(r => r.Item == bagStack.InstanceId ? 0 : 1).Select(r => r.Quantity).SequenceEqual([10_000L, 4L]),
              "a transfer fills the receiving stack to MaxStackCount and needs a free slot only for the rest");
        check(ItemPlan.Stackable(Item(530116001, 1, "bag", 0, 1), Item(530116001, 1, "bag", 1, 2)) && !ItemPlan.Stackable(Item(110130033, 1, "bag", 0, 1), Item(110130033, 1, "bag", 1, 2))
              && !ItemPlan.Stackable(Item(530116001, 1, "bag", 0, 1), Item(530126003, 1, "bag", 1, 2)),
              "stacks merge only for one stacking item (gear never)");

        // ---------- The legion warehouse ----------
        var legionSort = await session.HandleAsync(new PacketWriter().U16(GameSession.GuildStorageItemSortRq).I32(0).ToArray(), now);
        check(legionSort.Count == 1 && Op(legionSort[0]) == GameSession.GuildStorageItemSortRs && Res(legionSort[0]) == EResult.Guild_RequestWithNoGuild,
              "GuildStorageItemSort_RQ is answered (CANDIDATE 0x8aac {result, last sorted time}); without a legion Guild_RequestWithNoGuild");
        session.ExitSharedWorld();
    }
}
