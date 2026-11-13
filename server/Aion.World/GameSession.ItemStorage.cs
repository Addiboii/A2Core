using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Items that use themselves when gained and period items (USER 2026-11-11: "check all the items if needs or no bag store, all the
// info and implement in our server"; a2core-log/item-storage-audit-2026-11-11.md). The rules are ItemRules.cs.
//
// Self-using items (Item.dat AutoUseWhenAcquire, ItemRules.AutoUseEffects): OBSERVED official, 139 uses in the recordings: the item
// comes into container 4 kAutoUseItem and leaves it at once (decrease 38; an auto-opened box 37), followed by the effect's own
// message (0x9009 a pet, 0x900d soul points, 0x9011 crystals with ESoulCrystalIncreaseReason 2); no ItemUse_NT.
// A stack is used whole (soul crystals: 20 at once, one 0x9011 of 20). Live 2026-11-10/11: staff grants of 75-100 pets (Item.dat
// MaxStackCount 1, so a row each) were used one by one, each use reloading the whole inventory - 95 were left when the character
// was deleted, and 100 granted to a full bag waited past its end where nothing used them. Now the pets of one ride go in one
// transaction (UseRideItemsAsync), stacks are used whole, the list of changes is sent in parts, and self-using items never take a
// bag slot (Layout puts them in container 4; the login list leaves them out and uses them).
// CANDIDATE: the messages of a duplicate title (the item is used up, no title), of the default wing (no WingChange_NT here) and of
// the slot-extension items (one 0x8d51 per container); the order of the effect's message against the 0x561b for titles and wings.
//
// Period items: Item.dat FixedLimitTime (298 items, tools/data/item-limits.json) is sent as the item's end with the fixed flag
// (OBSERVED); MaintainTime (116 items) ends that many minutes after the row was created (CANDIDATE). An ended item in the bag is
// removed at login and once a minute in the world with decrease 29 kPeriodItemExpired (CANDIDATE: never recorded), and its use
// is refused with Item_Common_Fail_ExpiredPeriodItem.
public sealed partial class GameSession
{
    const byte DecreasePeriodItemExpired = 29, DecreaseExtendInventory = 7, DecreaseExtendStorage = 8;   // EDecreaseReason (usmap)
    /// <summary>How often the world checks for ended period items (OURS).</summary>
    public const long ExpiryCheckMs = 60_000;
    long expiryCheckAt;

    /// <summary>An item's use action: the boxes.json summary first, else the item XML (as UseItemAsync).</summary>
    static ItemUse? UseOf(int templateId) => Boxes.Use(templateId)
        ?? (ItemDB.Embedded.Of(templateId) is { Use: { } action } record ? new ItemUse(action.Effect, record.AutoUseWhenAcquired, action.Values) : null);

    /// <summary>Bag items whose period ended are removed (decrease 29 kPeriodItemExpired, CANDIDATE).</summary>
    async Task<List<byte[]>> RemoveExpiredAsync(long now, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || now <= 0) return [];
        var ended = storedItems.Values.Where(i => i.Location == "bag" && i.Quantity is > 0 and <= int.MaxValue
            && ItemRules.Expired(i.TemplateId, i.Acquired, now)).ToList();
        if (ended.Count == 0) return [];
        var removed = new List<ItemState>();
        await Stored(async () =>
        {
            foreach (var row in ended)
                if (await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, (int)row.Quantity, ct)) removed.Add(row);
            return new List<byte[]>();
        });
        if (removed.Count == 0) return [];
        Note($"period items ended: {string.Join(", ", removed.Select(r => $"{r.TemplateId} x{r.Quantity}"))}");
        return await InventoryChangesAsync(ct, 0, DecreasePeriodItemExpired);
    }

    /// <summary>Once a minute in the world: the period items that ended meanwhile.</summary>
    async Task<List<byte[]>> ExpiryTickAsync(long now, CancellationToken ct)
    {
        if (resources is null || dead || now < expiryCheckAt) return [];
        expiryCheckAt = now + ExpiryCheckMs;
        return await RemoveExpiredAsync(now, ct);
    }

    /// <summary>
    /// The pet items of one ride used at once (a staff grant of a hundred: one transaction, one inventory update): the ride is
    /// acquired (0x9009) when not owned, every other item is a soul point (one 0x900d with all of them).
    /// </summary>
    async Task<List<byte[]>> AutoUsePetItemsAsync(INativeCharacterStore store, Guid character, IReadOnlyList<ItemState> rows, CancellationToken ct)
    {
        if (rows.Count == 0 || UseOf(rows[0].TemplateId)?.Values is not [var value, ..] || !int.TryParse(value, out var rideId)
            || Boxes.Ride(rideId) is not { } info) return [];
        var owned = OwnsRide(rideId);
        var soulsBefore = rides.TryGetValue(rideId, out var entry) ? entry.SoulPoints : 0;
        RideRecord? ride = null;
        await Stored(async () => (ride = await store.UseRideItemsAsync(character, rows.Select(r => (r.InstanceId, r.Revision)).ToList(), rideId, ct)) is null ? [] : [[]]);
        if (ride is null) return [];
        Remember(ride);
        var replies = new List<byte[]>();
        if (!owned)
        {
            var (extra, board) = await RideBoardAsync(info, 1, ct);
            replies.Add(RidePackets.Acquired(playerKey, rideId, RideLevel(rideId, ride.Level), (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0));
            replies.AddRange(extra);
        }
        if (ride.SoulPoints > soulsBefore) replies.Add(RidePackets.SoulPoints([(rideId, ride.SoulPoints - soulsBefore)]));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseAutoUsed));
        if (!owned) replies.AddRange(RefreshStats());
        var used = rows.Count(r => !storedItems.ContainsKey(WireKey(r)));
        Note($"pet items used: {rows[0].TemplateId} x{used} -> ride {rideId}");
        replies.AddRange(AchieveUses(rows[0].TemplateId, "GetVehicle", used));
        return replies;
    }

    List<byte[]> AchieveUses(int itemId, string effect, int count) => count < 1 ? []
        : [.. Achieve("UseItem", count, e => e.Items?.Contains(itemId) == true), .. Achieve("UseUsableItemType", count, e => e.Has(effect))];

    /// <summary>
    /// Uses up a whole stack one unit at a time through a one-item store call (each partial use moves the row's revision on by
    /// one, as PostgresGameplayStore.Consume); the units used.
    /// </summary>
    async Task<int> UseUnitsAsync(ItemState row, Func<long, Task<bool>> useOne)
    {
        var used = 0;
        await Stored(async () =>
        {
            for (var i = 0; i < row.Quantity && i < int.MaxValue; i++)
            {
                if (!await useOne(row.Revision + i)) break;
                used++;
            }
            return new List<byte[]>();
        });
        return used;
    }

    /// <summary>
    /// One self-using stack other than skins, Quna and pets (AutoUseAcquiredAsync): used whole for its effect. A wing or title stack
    /// that sat in a bag slot (a staff grant, GameSession.StaffGrants.cs) leaves the bag with <paramref name="bagDecrease"/>.
    /// </summary>
    async Task<List<byte[]>> AutoUseStackAsync(INativeCharacterStore store, Guid character, ItemState row, string effect, CancellationToken ct,
        byte bagDecrease = DecreaseAutoUsed)
    {
        var use = UseOf(row.TemplateId);
        var values = use?.Values ?? [];
        switch (effect)
        {
            case "GetVehicleSoul" when values is [var rideText, var soulText, ..] && int.TryParse(rideText, out var rideId)
                && int.TryParse(soulText, out var souls) && souls > 0 && Boxes.Ride(rideId) is not null:
            {
                RideRecord? ride = null;
                var used = await UseUnitsAsync(row, async revision =>
                {
                    if (await store.AddRideSoulsAsync(character, row.InstanceId, revision, rideId, souls, ct) is not { } after) return false;
                    ride = after;
                    return true;
                });
                if (used == 0 || ride is null) return [];
                Remember(ride);
                return [RidePackets.SoulPoints([(rideId, souls * used)]), .. await InventoryChangesAsync(ct, 0, DecreaseAutoUsed),
                    .. AchieveUses(row.TemplateId, effect, used)];
            }
            case "GetVehicleSoulCrystal" when values is [var name, var amountText, ..] && CrystalTypes.TryGetValue(name, out var type)
                && int.TryParse(amountText, out var amount) && amount > 0:
            {
                int? total = null;
                var used = await UseUnitsAsync(row, async revision =>
                {
                    if (await store.AddRideCrystalsAsync(character, row.InstanceId, revision, type, amount, ct) is not { } after) return false;
                    total = after;
                    return true;
                });
                if (used == 0 || total is not { } now) return [];
                crystals[type] = now;
                // OBSERVED (115817 #150813): a stack of 20 All crystals used whole, one 0x9011 {6: 20} reason 2.
                return [.. await InventoryChangesAsync(ct, 0, DecreaseAutoUsed),
                    RidePackets.CrystalsGained(new Dictionary<int, int> { [type] = amount * used }, RidePackets.ItemReason), .. AchieveUses(row.TemplateId, effect, used)];
            }
            case "GetStigmaShard" or "GetStigmaShardSuperior" when use is not null:
                return await UseStigmaItemsAsync(store, character, row, use, (int)Math.Min(int.MaxValue, row.Quantity), ct) ?? [];
            case "GetTitle" when values is [var titleText, ..] && int.TryParse(titleText, out var titleId)
                && Titles.Of(titleId) is { } title && (title.Race == 0 || title.Race == profileRace):
            {
                // A title already held: the item is used up all the same (CANDIDATE: no duplicate recorded).
                var count = (int)Math.Min(int.MaxValue, row.Quantity);
                var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
                if (used.Count == 0) return [];
                // OBSERVED (105448 #78420): the item leaves container 4 with decrease 38; the title message after it is CANDIDATE.
                return [.. await InventoryChangesAsync(ct, 0, bagDecrease), .. titles.Contains(titleId) ? [] : await AcquireTitleAsync(titleId, ct),
                    .. AchieveUses(row.TemplateId, effect, count)];
            }
            case "GetWing" when values is [var wingText, ..] && int.TryParse(wingText, out var wingId) && wingId > 0:
            {
                var used = await UseUnitsAsync(row, async revision => await store.UseWingItemAsync(character, row.InstanceId, revision, wingId, ct));
                if (used == 0) return [];
                registeredWings.Add(wingId);
                return [.. await InventoryChangesAsync(ct, 0, bagDecrease), GamePackets.WingList(OwnedWings()), .. RefreshStats(),
                    .. AchieveUses(row.TemplateId, effect, used)];
            }
            case "RewardBox":
            {
                // Opened whole as it arrives (OBSERVED 010735 #10948-10953: Add to container 4 reason 49, then Remove decrease 37 with the
                // contents added in the same list); no OpenRewardBox_RS was sent for it.
                var opened = await OpenBoxAsync(WireKey(row), 0, (int)Math.Min(int.MaxValue, row.Quantity), lastNow, ct);
                return opened.Count > 1 ? opened.Skip(1).ToList() : [];
            }
            case "ExtendInventorySlot" or "ExtendStorageSlot" or "ExtendCharacterStorageSlot":
            {
                var container = effect switch
                {
                    "ExtendInventorySlot" => InventoryItem.Bag,
                    "ExtendStorageSlot" => InventoryItem.Storage,
                    _ => InventoryItem.CharacterStorage,
                };
                if (SlotExtendIndex(container) is not { } at) return [];
                var max = container == InventoryItem.Bag ? BagMaxLines : StorageMaxLines;
                var count = (int)Math.Min(row.Quantity, Math.Max(0, max - SlotLines(container)));
                if (count < 1) return [];
                var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
                if (used.Count == 0) return [];
                // One line per item, counted as an expansion by item (ItemDepotList_RS's by-item counts; CANDIDATE).
                slotExtends[at.Item] += count;
                await SaveSettingAsync("slot_extends", slotExtends, ct);
                var notice = PacketWriter.Opcode(SlotExtendNt).U8(container).I64(WireKey(row))
                    .U8((byte)slotExtends[at.Item]).U8((byte)slotExtends[at.Gold]).ToArray();
                return [.. await InventoryChangesAsync(ct, 0, container == InventoryItem.Bag ? DecreaseExtendInventory : DecreaseExtendStorage), notice,
                    .. AchieveUses(row.TemplateId, effect, count)];
            }
            case "AscensionGrade":
                return await UseItemAsync(WireKey(row), lastNow, ct, reply: false);
            default:
                return [];
        }
    }
}
