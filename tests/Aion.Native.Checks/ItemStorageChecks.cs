using System.Text.Json;
using System.Text.RegularExpressions;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// The item storage audit (feature/item-storage, a2core-log/item-storage-audit-2026-11-11.md): Item.dat rules (ItemRules.cs), the
/// official bytes of a period item and of self-using items (decoded from the recordings 2026-11-06), stack limits against
/// migrations 034 + 056, and a stored session: pets granted by the hundred to a full bag, a crystal stack, a title, the default
/// wing, bag extensions, period items, bag room per path and the warehouse rules.
/// </summary>
static class ItemStorageChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static ItemState Item(int template, long quantity, string location, int? slot, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, location, slot, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Item.dat rules ----------
        check(ItemRules.IsAutoUse(534760052) && ItemRules.IsAutoUse(534660001) && ItemRules.IsAutoUse(531900009) && ItemRules.IsAutoUse(532560091)
              && ItemRules.IsAutoUse(532400001) && ItemRules.IsAutoUse(533700027) && ItemRules.IsAutoUse(534000001) && ItemRules.IsAutoUse(535300006)
              && ItemRules.IsAutoUse(535710001) && ItemRules.IsAutoUse(533900001) && ItemRules.IsAutoUse(531900010)
              && !ItemRules.IsAutoUse(503700038) && !ItemRules.IsAutoUse(530126003) && !ItemRules.IsAutoUse(512400003) && !ItemRules.IsAutoUse(930100001),
              "self-using items: pets, pet souls, soul crystals, titles, the default wing, auto boxes, bag extensions, Quna, skins, Ascension, stigma shards; "
              + "not a battle pass unlock (no server side yet), a potion, a manual wing or Kina");
        var left = ItemDB.Embedded.All.Where(i => i.AutoUseWhenAcquired && !ItemRules.IsAutoUse(i.Id)).Select(i => i.Use?.Effect ?? "-").Distinct().Order().ToList();
        check(ItemDB.Embedded.All.Count(i => i.AutoUseWhenAcquired) == 3396
              && left.SequenceEqual(["AlchemyMastery", "BattlePassExp", "BattlePassUnlock", "BlacksmithingMastery", "CookingMastery", "ExtractForceMastery",
                  "JewelcraftingMastery", "Skill", "Subscribe", "TailoringMastery"]),
              $"Item.dat: 3396 AutoUseWhenAcquire items; only the battle pass, Elevate mastery, Subscribe and no-effect Skill ones wait in the bag ({string.Join(",", left)})");
        check(ItemRules.StackLimit(Currencies.Gold) == long.MaxValue && ItemRules.StackLimit(110120003) == 1 && ItemRules.StackLimit(533700077) == 999
              && ItemRules.StackLimit(534660001) == 9_999_999 && ItemRules.StackLimit(534760052) == 1 && ItemRules.StackLimit(1) == 1,
              "stack limits: a currency has none (one bag stack), equipment 1, Shulak Loot Bundle 999, pet souls 9,999,999, pets 1");
        // game.item_stacks against Item.dat: every stacking Usable/Misc item (034 missed 533700077, added by 056).
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "database", "migrations"))) root = root.Parent;
        var stacks = new Dictionary<int, int>();
        if (root is not null)
            foreach (var file in new[] { "034_item_stacks.sql", "092_item_stack_audit.sql" })
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root.FullName, "database", "migrations", file)), @"\((\d{9}), (\d+)\)"))
                    stacks[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
        var stacking = ItemDB.Embedded.All.Where(i => i.Type is "Usable" or "Misc" && i.MaxStack > 1).ToList();
        var wrong = stacking.Where(i => stacks.GetValueOrDefault(i.Id) != i.MaxStack).Select(i => i.Id).ToList();
        check(root is not null && stacking.Count == 2016 && wrong.Count == 0,
              $"game.item_stacks (migrations 034 + 056) has every stacking item's MaxStackCount ({stacking.Count} items; wrong: {string.Join(",", wrong.Take(5))})");
        check(ItemRules.RaceAllows(532400001, 1) && !ItemRules.RaceAllows(532400001, 2) && ItemRules.RaceAllows(534760052, 2) && ItemRules.RaceAllows(930100001, 2),
              "Item.dat ItemRace: a Light wing is not for Dark characters; All and None items are for both");
        check(!ItemRules.AllowedIn(502530001, "storage") && !ItemRules.AllowedIn(502530001, "character_storage") && !ItemRules.AllowedIn(532560091, "storage")
              && ItemRules.AllowedIn(Currencies.GoldNonbinding, "storage") && ItemRules.AllowedIn(Currencies.GoldNonbinding, "character_storage")
              && ItemRules.AllowedIn(110120003, "storage") == ItemDB.Embedded.Of(110120003)!.CanStorage
              && ItemRules.AllowedIn(110120003, "guild_storage") == ItemDB.Embedded.Of(110120003)!.CanGuildStorage && ItemRules.AllowedIn(502530001, "bag"),
              "warehouses: CanStorage / CanCharacterStorage / CanGuildStorage (a title item in none; tradable Kina in both, OBSERVED in containers 2 and 15)");

        // ---------- Official bytes ----------
        // A period item (105448 #2432: 2 party dungeon reward tickets, achievement attendance 79): FixedLimitTime 639330336000000000
        // read as Korean time = 1797404400000, the fixed flag set.
        check(ItemRules.FixedExpiry(503700031) == 1797404400000 && ItemRules.FixedExpiry(930100019) == 1797404400000 && ItemRules.FixedExpiry(530126003) is null
              && GamePackets.ItemModificationList([new ItemModification(0, new InventoryItem(0x00011600000b8227, 503700031, 2, InventoryItem.Bag, 11,
                     ExpireTime: ItemRules.FixedExpiry(503700031)!.Value, ExpireFixed: true), Increase: 79)])
                 .SequenceEqual(Convert.FromHexString("1b5601000027820b00001601003fda051e0200000000000000010b00000002008099a67da20100004f0000")),
              "a period item's end as official: FixedLimitTime as Korean time, the fixed-expiry flag (105448 #2432 rebuilt byte for byte)");
        var since = DateTimeOffset.FromUnixTimeMilliseconds(now);
        check(ItemRules.Expiry(502760001, since) == (now + 10080 * 60_000L, false) && ItemRules.Expiry(502760001, null) is null
              && ItemRules.Expired(530800025, null, now) && !ItemRules.Expired(503700031, null, now),
              "MaintainTime: 10080 minutes after the acquisition, not fixed (CANDIDATE); a FixedLimitTime of 2000 has ended");
        // Self-using items (115817 #150807, #150813; 105448 #78420): Add to container 4 (20 All crystals, reason 72), 0x9011 {6: 20} reason 2,
        // a title item's Remove from container 4 with decrease 38.
        check(GamePackets.ItemModificationList([new ItemModification(0, new InventoryItem(0x0001350000_0c77a9, 531900009, 20, 4, 0), Increase: 72)])
                  .SequenceEqual(Convert.FromHexString("1b56010000a9770c00003501006926b41f1400000000000000040000000000000000000000000000480000"))
              && RidePackets.CrystalsGained(new Dictionary<int, int> { [6] = 20 }, RidePackets.ItemReason).SequenceEqual(Convert.FromHexString("119001061400000002"))
              && GamePackets.ItemModificationList([new ItemModification(2, new InventoryItem(0x00013d0000068267, 532560091, 0, 4, 0), Decrease: 38)])
                  .SequenceEqual(Convert.FromHexString("1b5601020067820600003d0100db38be1f0000000000000000040000000000000000000000000000002600")),
              "self-using items as official: Add to container 4, the crystal notice, Remove from container 4 with decrease 38 (byte for byte)");

        // ---------- A stored session ----------
        var store = new FakeStore();
        var session = await storedWorldSession("ItemAudit", store);
        var race = session.BotRace;
        // 120 pets (Item.dat MaxStackCount 1: a row each) granted to a full bag before the login: not listed, used at once in one go.
        store.Items.Add(Item(530126003, 10, "bag", 0, 6999));
        store.Items.AddRange(Enumerable.Range(0, 120).Select(i => Item(534760052, 1, "bag", i < 20 ? 1600 + i : null, 7000 + i)));
        session.BagSlotsOverride = 1;
        var depot = await session.HandleAsync([0x10, 0x56], now);
        var listed = depot.First(b => Op(b) == 0x5611);
        check(listed.AsSpan().IndexOf(BitConverter.GetBytes(534760052)) < 0 && store.Items.All(i => i.TemplateId != 534760052)
              && session.RideStates.TryGetValue(1053, out var crab) && crab.Owned && crab.SoulPoints == 119
              && depot.Any(b => Op(b) == 0x9009) && depot.Any(b => b.SequenceEqual(RidePackets.SoulPoints([(1053, 119)])))
              && depot.Count(b => Op(b) == 0x5616) == 0,
              "120 pets left in a full bag (live: 100 waited past its end, 95 at slots 1600+): not in the login list, used at once - "
              + "the ride (0x9009) and 119 soul points in one 0x900d, no ItemUse_NT");
        // A soul crystal stack arriving: Add to container 4, used whole (one 0x9011), Remove with decrease 38.
        session.BagSlotsOverride = 400;
        store.Items.Add(Item(531900009, 20, "bag", null, 7200));
        var crystalsBefore = session.Crystals.GetValueOrDefault(GameSession.AllCrystals);
        var crystals = await session.InventoryChangesAsync(default);
        check(crystals.Any(b => b.SequenceEqual(GamePackets.ItemModificationList([new ItemModification(0, new InventoryItem(7200, 531900009, 20, 4, 0), Increase: 24)])))
              && crystals.Any(b => b.SequenceEqual(RidePackets.CrystalsGained(new Dictionary<int, int> { [GameSession.AllCrystals] = 20 }, RidePackets.ItemReason)))
              && crystals.Any(b => b.SequenceEqual(GamePackets.ItemModificationList([new ItemModification(2, new InventoryItem(7200, 531900009, 0, 4, 0), Decrease: 38)])))
              && store.Items.All(i => i.WireId != 7200) && session.Crystals.GetValueOrDefault(GameSession.AllCrystals) == crystalsBefore + 20,
              "a stack of 20 All crystals as official: Add to container 4, one 0x9011 {6: 20} reason 2, Remove from container 4 decrease 38");
        // A title item of the character's race: the title acquired, the item gone; a second copy is used up without a second title.
        var titleItem = ItemDB.Embedded.WithEffect("GetTitle").First(i => i.AutoUseWhenAcquired && i.Race == (race == 2 ? "Dark" : "Light")
            && i.Use!.Values is [var t, ..] && TitleTables.Embedded.Of(int.Parse(t)) is { } title && (title.Race == 0 || title.Race == race));
        var titleId = int.Parse(titleItem.Use!.Values[0]);
        store.Items.Add(Item(titleItem.Id, 1, "bag", null, 7300));
        var titled = await session.InventoryChangesAsync(default);
        store.Items.Add(Item(titleItem.Id, 1, "bag", null, 7301));
        var again = await session.InventoryChangesAsync(default);
        check(session.OwnedTitles.Contains(titleId) && store.Items.All(i => i.TemplateId != titleItem.Id) && titled.Any(b => Op(b) == GameSession.TitleAcquireNt)
              && !again.Any(b => Op(b) == GameSession.TitleAcquireNt),
              "a title item uses itself (the title acquired, the item gone); a second copy is used up without a second title (CANDIDATE)");
        // The default wing of the race (Wing_L/D_Default_01, AutoUseWhenAcquire): registered at once.
        var wingItem = race == 2 ? 532400002 : 532400001;
        store.Items.Add(Item(wingItem, 1, "bag", null, 7400));
        var winged = await session.InventoryChangesAsync(default);
        check(session.RegisteredWings.Contains(int.Parse(ItemDB.Embedded.Of(wingItem)!.Use!.Values[0])) && store.Items.All(i => i.WireId != 7400)
              && winged.Any(b => Op(b) == 0x5672), "the default wing uses itself as it arrives (no 5 s cast): registered, the wing list sent");
        // A bag extension item: one more line of 5 slots, counted as an extension by item.
        var slotsBefore = session.SlotCapacity(InventoryItem.Bag);
        store.Items.Add(Item(534000001, 1, "bag", null, 7500));
        var extended = await session.InventoryChangesAsync(default);
        check(session.SlotCapacity(InventoryItem.Bag) == slotsBefore + GameSession.SlotsPerLine && session.SlotExtends[0] == 1 && store.Items.All(i => i.WireId != 7500)
              && extended.Any(b => Op(b) == GameSession.SlotExtendNt), "a bag extension item uses itself: one more line (0x8d51, CANDIDATE)");
        // Period items: one long ended (Candy_Polymorph, FixedLimitTime 2000) goes at the next minute's check (decrease 29, CANDIDATE);
        // a live one shows its end in the bag.
        store.Items.AddRange([Item(530800025, 1, "bag", null, 7600), Item(503700031, 2, "bag", null, 7601)]);
        var periodShown = await session.InventoryChangesAsync(default);
        var ended = await session.TickAsync(now + 3 * GameSession.ExpiryCheckMs);
        check(periodShown.Any(b => b.AsSpan().IndexOf(BitConverter.GetBytes(1797404400000L)) >= 0) && store.Items.All(i => i.WireId != 7600)
              && store.Items.Any(i => i.WireId == 7601) && ended.Any(b => Op(b) == 0x561b && b[^2] == 29),
              "an ended period item is removed (0x561b decrease 29 kPeriodItemExpired); a live one carries its end");
        var expiredUse = await session.HandleAsync(new PacketWriter().U16(0x5612).I64(7600).ToArray(), now + 3 * GameSession.ExpiryCheckMs + 10);
        check(expiredUse.Count == 1, "a removed period item cannot be used");

        // ---------- Bag room ----------
        var bagStacks = store.Items.Count(i => i.Location == "bag" && ItemRules.NeedsBagSlot(i.TemplateId));
        session.BagSlotsOverride = bagStacks;
        store.Items.Add(Item(Currencies.Gold, 100, "bag", null, 7700));
        await session.InventoryChangesAsync(default);
        session.BagSlotsOverride = bagStacks + 1;
        check(session.BagRoomFor([(Currencies.Gold, 1_000_000)]) && session.BagRoomFor([(Currencies.GoldCombined, 5_000)]) && session.BagRoomFor([(534760052, 3)])
              && session.BagRoomFor([(930200001, 50)]) && !session.BagRoomFor([(110120003, 1)]) && session.BagRoomFor([(110120003, 1)], freed: 1)
              && !session.BagRoomFor([(Currencies.GoldNonbinding, 1)]) && !session.BagRoomFor([(530126003, ItemRules.StackLimit(530126003))]),
              "a full bag: more Kina tops its stack up (no limit; before: every Kina counted as a slot), self-using and account items need no slot; "
              + "new gear or a new currency stack needs one, unless the request frees one");
        // A warehouse take-out into a full bag is refused.
        store.Items.Add(Item(530116001, 5, "character_storage", 0, 7800));
        await session.InventoryChangesAsync(default);
        var takeOut = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemTakeOutRq).I32(0).Varint(1).I64(7800).I64(5).U8(InventoryItem.CharacterStorage).ToArray(), now);
        check(BitConverter.ToUInt16(takeOut[0], 2) == EResult.Item_Common_Fail_NotEnoughSlotCountContainer && store.Items.Single(i => i.WireId == 7800).Location == "character_storage",
              "taking an item out of the warehouse into a full bag is refused (Item_Common_Fail_NotEnoughSlotCountContainer)");
        // A self-using item is never stored in a warehouse (ItemRules.AllowedIn); a title kept for a manual use neither (CanStorage 0).
        store.Items.Add(Item(502530001, 1, "bag", null, 7900));
        session.BagSlotsOverride = 400;
        await session.InventoryChangesAsync(default);
        var stored = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemStoreRq).I32(0).Varint(1).I64(7900).I64(1).U8(InventoryItem.Storage).ToArray(), now);
        check(BitConverter.ToUInt16(stored[0], 2) == EResult.Item_Storage_Store_Fail && store.Items.Single(i => i.WireId == 7900).Location == "bag",
              "an item Item.dat keeps out of the account warehouse (CanStorage 0) is refused (Item_Storage_Store_Fail)");
        session.ExitSharedWorld();
    }
}
