using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

// Staff grants in memory (IGameplayStore.StaffGrantedItemsAsync): the items the check marks as granted by staff.
sealed partial class FakeStore
{
    public HashSet<Guid> StaffGranted { get; } = [];

    public Task<IReadOnlyList<Guid>> StaffGrantedItemsAsync(Guid character, IReadOnlyList<Guid> items, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(items.Where(StaffGranted.Contains).Distinct().ToList());
}

/// <summary>
/// fix/grant-like-shop (USER 2026-11-13): staff-granted collection items are learned at once and worn in an empty slot, online and at
/// the next login; items from other sources keep waiting for a manual use; placeholder wings are refused (CollectionItems.cs,
/// GameSession.StaffGrants.cs).
/// </summary>
static class GrantCollectionChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static ItemState Item(int template, long quantity, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Item.dat rules ----------
        var placeholders = CollectionItems.Placeholders;
        check(placeholders.Count == 58 && placeholders.All(id => ItemDB.Embedded.Of(id) is { Type: "Usable", Use: null, UsableCategory: "GetWing" })
              && new[] { 512400026, 512400034, 532400044, 532400056, 532400112, 532400132, 532400134, 562400001 }.All(CollectionItems.IsPlaceholder)
              && !CollectionItems.IsPlaceholder(532400022) && !CollectionItems.IsPlaceholder(532400001)
              && !CollectionItems.IsPlaceholder(532000010) && !CollectionItems.IsPlaceholder(581000009) && !CollectionItems.IsPlaceholder(533700311)
              && !CollectionItems.IsPlaceholder(110120003),
              "placeholders: the 58 wing items without a use effect (sorace's PartyDungeon / cube / BMshop / BattlePass / Contents / CLB / Ascension "
              + "wings, zenyc's event wing); a real wing item, tickets, battle pass unlocks, boxes and equipment are not");
        check(CollectionItems.LearnEffect(532400022) == "GetWing" && CollectionItems.LearnEffect(512400026) is null && CollectionItems.LearnEffect(530126003) is null
              && ItemDB.Embedded.All.Count(i => CollectionItems.LearnEffect(i.Id) is not null) == 96 + 1508 + 119 + 1464 + 17 + 159,
              "collection items: GetWing 96, GetSkin 1508, GetSkinSet 119, GetTitle 1464, GetSocialAction 17, GetDeco 159");
        check(CollectionItems.SkinEquipSlots("Torso") is [3] && CollectionItems.SkinEquipSlots("Cape") is [7] && CollectionItems.SkinEquipSlots("Sword") is [8]
              && CollectionItems.SkinEquipSlots("Greatsword") is [10] && CollectionItems.SkinEquipSlots("Earring") is [20, 21]
              && CollectionItems.SkinEquipSlots("HeadWear") is [27] && CollectionItems.SkinEquipSlots("None").Count == 0,
              "skin categories to ESkinEquipSlot (usmap): armour 1-7, weapons by their main slot, earrings left then right");

        // ---------- Online: the grant arrives as an inventory change ----------
        var store = new FakeStore();
        var session = await storedWorldSession("GrantShop", store);
        var race = session.BotRace;
        var raceName = race == 2 ? "Dark" : "Light";
        await session.HandleAsync([0x10, 0x56], now);
        var wingItems = ItemDB.Embedded.WithEffect("GetWing")
            .Where(i => !i.AutoUseWhenAcquired && (i.Race == raceName || i.Race == "All") && int.TryParse(i.Use!.Values[0], out var w) && !session.RegisteredWings.Contains(w))
            .GroupBy(i => i.Use!.Values[0]).Select(g => g.First()).Take(3).ToList();
        int WingOf(ItemRecord item) => int.Parse(item.Use!.Values[0]);
        var first = Item(wingItems[0].Id, 1, 8100);
        store.Items.Add(first);
        store.StaffGranted.Add(first.InstanceId);
        var granted = await session.InventoryChangesAsync(default);
        check(session.RegisteredWings.Contains(WingOf(wingItems[0])) && store.Wings.Contains(WingOf(wingItems[0])) && store.Items.All(i => i.WireId != 8100)
              && granted.Any(b => Op(b) == 0x5672) && granted.Any(b => b.SequenceEqual(GamePackets.WingChangeNotice(session.PlayerKey, WingOf(wingItems[0]), false)))
              && store.Settings.TryGetValue("wing", out var worn) && worn[0].GetInt32() == WingOf(wingItems[0])
              && granted.Any(b => Op(b) == 0x561b && b.Length > 8 && b[^2] == 14),
              $"online staff grant of wing item {wingItems[0].Id}: wing {WingOf(wingItems[0])} learned and stored (0x5672), the item leaves the bag "
              + "(decrease 14 item use), worn because no wing was chosen (0x5676, setting \"wing\")");
        var second = Item(wingItems[1].Id, 1, 8101);
        store.Items.Add(second);
        store.StaffGranted.Add(second.InstanceId);
        var secondGrant = await session.InventoryChangesAsync(default);
        check(session.RegisteredWings.Contains(WingOf(wingItems[1])) && store.Items.All(i => i.WireId != 8101)
              && store.Settings["wing"][0].GetInt32() == WingOf(wingItems[0]) && !secondGrant.Any(b => Op(b) == 0x5676),
              "a second granted wing is learned but the worn wing stays");
        var looted = Item(wingItems[2].Id, 1, 8102);
        store.Items.Add(looted);
        await session.InventoryChangesAsync(default);
        check(!session.RegisteredWings.Contains(WingOf(wingItems[2])) && store.Items.Any(i => i.WireId == 8102),
              "the same kind of wing item from loot, a quest or a shop (no staff grant) waits in the bag for a manual use (official)");

        // A title (AutoUseWhenAcquire): learned and worn in its empty title slot.
        var titleItem = ItemDB.Embedded.WithEffect("GetTitle").First(i => (i.Race == raceName || i.Race == "All")
            && TitleTables.Embedded.Of(int.Parse(i.Use!.Values[0])) is { Category: >= 1 and <= 3 } t && (t.Race == 0 || t.Race == race));
        var titleId = int.Parse(titleItem.Use!.Values[0]);
        var category = TitleTables.Embedded.Of(titleId)!.Category;
        var title = Item(titleItem.Id, 1, 8200);
        store.Items.Add(title);
        store.StaffGranted.Add(title.InstanceId);
        var titled = await session.InventoryChangesAsync(default);
        check(session.OwnedTitles.Contains(titleId) && session.TitleEquips.GetValueOrDefault(category) == titleId && store.TitleEquips.GetValueOrDefault(category) == titleId
              && titled.Any(b => b.SequenceEqual(GameSession.TitleEquipChange(category, titleId))) && store.Items.All(i => i.WireId != 8200),
              $"staff grant of title item {titleItem.Id}: title {titleId} learned and worn in its empty slot {category} (0x5681), the item gone");

        // A skin set: every skin learned, each worn in its empty skin slot; a later skin of a worn slot does not replace it.
        var setItem = ItemDB.Embedded.WithEffect("GetSkinSet").First(i => (i.Race == raceName || i.Race == "All") && SkinTables.Embedded.Set(i.Use!.Values[0]).Count >= 3
            && SkinTables.Embedded.Set(i.Use!.Values[0]).All(s => CollectionItems.SkinEquipSlots(SkinTables.Embedded.Category(s)).Count > 0));
        var setSkins = SkinTables.Embedded.Set(setItem.Use!.Values[0]);
        var set = Item(setItem.Id, 1, 8300);
        store.Items.Add(set);
        store.StaffGranted.Add(set.InstanceId);
        var dressed = await session.InventoryChangesAsync(default);
        var slots = setSkins.ToDictionary(s => s, s => CollectionItems.SkinEquipSlots(SkinTables.Embedded.Category(s))[0]);
        check(setSkins.All(session.OwnedSkins.ContainsKey) && slots.All(p => session.SkinEquips.GetValueOrDefault(p.Value) == p.Key && store.SkinEquips.GetValueOrDefault(p.Value) == p.Key)
              && dressed.Any(b => Op(b) == GameSession.SkinUpdateNt) && store.Items.All(i => i.WireId != 8300),
              $"staff grant of skin set {setItem.Id}: its {setSkins.Count} skins learned (0x8d5c) and worn in their empty slots, the item gone");
        var slotTaken = slots.First();
        var otherSkinItem = ItemDB.Embedded.WithEffect("GetSkin").First(i => (i.Race == raceName || i.Race == "All") && SkinTables.Embedded.Of(i.Use!.Values[0]) is var s and > 0
            && s != slotTaken.Key && CollectionItems.SkinEquipSlots(SkinTables.Embedded.Category(s)) is [var only] && only == slotTaken.Value);
        var otherSkin = SkinTables.Embedded.Of(otherSkinItem.Use!.Values[0]);
        var other = Item(otherSkinItem.Id, 1, 8301);
        store.Items.Add(other);
        store.StaffGranted.Add(other.InstanceId);
        await session.InventoryChangesAsync(default);
        check(session.OwnedSkins.ContainsKey(otherSkin) && session.SkinEquips.GetValueOrDefault(slotTaken.Value) == slotTaken.Key && store.Items.All(i => i.WireId != 8301),
              "a granted skin for a slot already worn is learned, the worn skin stays");

        // ---------- Offline: granted while away, applied at the next world entry ----------
        var awayStore = new FakeStore();
        var away = await storedWorldSession("GrantAway", awayStore);
        var awayWing = Item(wingItems[0].Id, 1, 8400);
        var keptWing = Item(wingItems[1].Id, 1, 8401);
        awayStore.Items.AddRange([awayWing, keptWing]);
        awayStore.StaffGranted.Add(awayWing.InstanceId);
        var entry = await away.HandleAsync([0x10, 0x56], now);
        check(away.RegisteredWings.Contains(WingOf(wingItems[0])) && awayStore.Items.All(i => i.WireId != 8400)
              && awayStore.Settings.TryGetValue("wing", out var awayWorn) && awayWorn[0].GetInt32() == WingOf(wingItems[0])
              && entry.Any(b => Op(b) == 0x5611) && entry.Any(b => Op(b) == 0x5672)
              && awayStore.Items.Any(i => i.WireId == 8401) && !away.RegisteredWings.Contains(WingOf(wingItems[1])),
              "offline grant (or a staff-granted wing left in the bag from before, like sorace's 532400022): at the next login the wing is learned, worn, "
              + "the item gone; a wing item that was not a staff grant stays");
        var again = await away.HandleAsync([0x10, 0x56], now + 1000);
        check(!again.Any(b => Op(b) == 0x5672) && awayStore.Items.Count(i => i.WireId is 8400 or 8401) == 1, "applied once: the next inventory list changes nothing");
        // Leave the shared world (the later checks count its players).
        session.ExitSharedWorld();
        away.ExitSharedWorld();
    }
}
