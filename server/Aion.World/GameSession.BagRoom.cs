using Aion.Protocol.Native;

namespace Aion.World;

// Bag room (live 2026-11-11, Axel3): the bag holds SlotCapacity(Bag) slots (8 lines of 5 plus the expansions), but nothing checked
// it - four godstones bought at the Black Cloud Merchants with a full bag were laid out at slots 39, 46, 47, 48, past the bag's last
// slot, and the client showed empty spaces. Now: a grant that needs more new stacks than the bag has free slots is refused with the
// path's own result, and Layout never places an item past the bag's last slot: an item without room (granted by a path that does
// not check, or before this) waits unseen until a slot frees.
// Paths and results (CANDIDATE: no official full-bag refusal recorded; each is the client's EResult named for the path):
//   NpcShop / coin shop / random shop, crafting, conversion, decomposing, monolith resonance, mail attachments
//       Item_Common_Fail_NotEnoughSlotCountContainer
//   Black Cloud market NPShopPurchase_NotEnoughInventorySlot, loot DropItem_NotEnoughInventory, quest rewards
//   Quest_NotEnoughInventory, reward boxes Item_RewardBox_NotEnoughSlot, Arcana creation Arcana_Create_NotEnoughInventory,
//   trades Exchange_Fail_NotEnoughSlotCountContainer / Exchange_Fail_PeerNotEnoughSlotCountContainer.
// Not refused (the grant follows something already done; the items wait for a slot): gathering, Empyrean traces and dungeon boxes
// (the object is spent), collection rewards (the items are registered), party dungeon rewards, staff grants.
// Item_Use_Fail_GetArcana_NotEnoughArcanaInventory and kArcanaInventory 14 (an Arcana bag of its own) are not built: cards share the bag.
// Stacks are filled first (ItemRules.StackLimit: a currency has no limit, one slot holds all of it), stacks used up by the same
// request free their slots, and items that need no slot (ItemRules.NeedsBagSlot: self-using items, account currencies) count none.
public sealed partial class GameSession
{
    /// <summary>Checks only: a bag size instead of the character's lines (the checks' long-lived sessions hold many items).</summary>
    internal int? BagSlotsOverride { get; set; }

    /// <summary>The bag's slots now (lines × 5, or the checks' override).</summary>
    int BagSlots() => BagSlotsOverride ?? SlotCapacity(InventoryItem.Bag);

    /// <summary>The bag slots a request frees: the bag stacks it uses up whole.</summary>
    int SlotsFreedBy(IEnumerable<(Guid Item, long Revision, int Count)> costs) =>
        costs.GroupBy(c => c.Item).Count(g => storedItems.Values.FirstOrDefault(i => i.InstanceId == g.Key) is { Location: "bag" } row
            && ItemRules.NeedsBagSlot(row.TemplateId) && g.Sum(c => (long)c.Count) >= row.Quantity);

    /// <summary>Whether the bag has the free slots for these goods (with the slots the same request frees): existing plain stacks are
    /// topped up first (Item.dat MaxStackCount), self-using items and account currencies need none.</summary>
    internal bool BagRoomFor(IEnumerable<(int Item, long Count)> goods, int freed = 0)
    {
        var capacity = BagSlots();
        if (capacity <= 0) return true;
        var bag = storedItems.Values.Where(i => i.Location == "bag" && ItemRules.NeedsBagSlot(i.TemplateId)).ToList();
        var free = capacity - bag.Count + freed;
        foreach (var group in goods.GroupBy(g => Currencies.StoredTemplate(g.Item)))
        {
            var item = group.Key;
            var count = group.Sum(g => g.Count);
            if (count <= 0 || !ItemRules.NeedsBagSlot(item)) continue;
            var max = ItemRules.StackLimit(item);
            if (max == long.MaxValue)
            {
                // A currency: one stack holds any amount (game.grant_item tops the plain bag stack up).
                if (!bag.Any(i => i.TemplateId == item && IsPlainStack(i))) free--;
            }
            else if (max > 1)
            {
                var room = bag.Where(i => i.TemplateId == item && i.Quantity < max && IsPlainStack(i)).Sum(i => max - i.Quantity);
                count -= Math.Min(count, room);
                if (count <= 0) continue;
                free -= (int)Math.Min(int.MaxValue, (count + max - 1) / max);
            }
            else free -= (int)Math.Min(int.MaxValue, count);
            if (free < 0) return false;
        }
        return free >= 0;
    }

    static bool IsPlainStack(Aion.Application.Gameplay.ItemState i) =>
        (i.Upgrades.ValueKind != System.Text.Json.JsonValueKind.Object || !i.Upgrades.EnumerateObject().Any())
        && (i.RolledStats.ValueKind != System.Text.Json.JsonValueKind.Object || !i.RolledStats.EnumerateObject().Any());
}
