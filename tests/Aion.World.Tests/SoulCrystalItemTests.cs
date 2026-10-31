using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SoulCrystalItemTests
{
    const long Now = 1_793_368_800_000;
    const int AllCrystalItem = 531900009;   // GetVehicleSoulCrystal [SoulCrystalAll, 1]

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Crystal_item_use_adds_an_all_crystal_and_uses_up_the_item(long quantity)
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(AllCrystalItem, quantity, 0x8d));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(0x8d).ToArray(), Now + 100);

        Assert.Equal(Convert.FromHexString("135600008D000000000000006926B41F"), replies[0]);
        Assert.Contains(replies, p => p.AsSpan().SequenceEqual(Convert.FromHexString("11900106010000000" + "2")));
        Assert.Equal(1, store.Crystals[GameSession.AllCrystals]);
        Assert.Equal(1, session.Crystals[GameSession.AllCrystals]);
        var changes = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.Equal(1, changes[2]);
        Assert.Equal(quantity == 1 ? 2 : 1, changes[3]);   // EItemModificationType: removed, or the stack changed
        if (quantity > 1) Assert.True(changes.AsSpan().IndexOf(new PacketWriter().I64(0x8d).I32(AllCrystalItem).I64(quantity - 1).ToArray()) > 0);
    }
}
