using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class EquipBagSlotTests
{
    const long Now = 1_793_368_800_000;
    const int Ring = 210130001;

    static byte[] ItemHead(long key, int template, byte container, int slot) =>
        new PacketWriter().I64(key).I32(template).I64(1).U8(container).I32(slot).ToArray();

    [Fact]
    public async Task Equip_swap_changes_only_the_two_moved_items()
    {
        var store = new FakeCharacterStore();
        for (var i = 0; i < 12; i++) store.Items.Add(FakeCharacterStore.BagItem(Ring, 1, 100 + i));
        store.Items.Add(FakeCharacterStore.EquippedItem(Ring, 300, 5));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        // Bag slots 0..11 in wire order; equip the one in slot 3 into equip slot 5.
        var replies = await session.HandleAsync(new PacketWriter().U16(0xe247).I64(103).U8(5).ToArray(), Now + 100);
        Assert.Equal(0, BitConverter.ToUInt16(replies[0], 2));
        var changes = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.Equal(2, changes[2]);
        Assert.True(changes.AsSpan().IndexOf(ItemHead(103, Ring, InventoryItem.Equipped, 5)) > 0);
        Assert.True(changes.AsSpan().IndexOf(ItemHead(300, Ring, InventoryItem.Bag, 3)) > 0);

        // The bag keeps its order on the next change too: a plain unequip adds only the unequipped item.
        replies = await session.HandleAsync(new PacketWriter().U16(0xe249).I64(103).U8(5).I32(0).ToArray(), Now + 200);
        changes = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.Equal(1, changes[2]);
        Assert.True(changes.AsSpan().IndexOf(ItemHead(103, Ring, InventoryItem.Bag, 12)) > 0);
    }
}
