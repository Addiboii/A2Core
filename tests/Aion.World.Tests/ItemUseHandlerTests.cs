using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemUseHandlerTests
{
    const long Now = 1_760_030_000_000;

    [Fact]
    public async Task Unimplemented_use_effect_replies_with_failure_result()
    {
        const int godstoneItem = 513830001;
        Assert.Equal("GetGodstone", RewardBoxes.Embedded.Use(godstoneItem)!.Effect);
        Assert.False(RewardBoxes.Embedded.ImplementsUseEffect("GetGodstone"));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(godstoneItem, 1, 880));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(880).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(store.Items, i => i.TemplateId == godstoneItem && i.Quantity > 0);
    }

    [Fact]
    public async Task Missing_use_data_replies_with_failure_result()
    {
        const int unknownUsable = 999_999_001;
        Assert.Null(RewardBoxes.Embedded.Use(unknownUsable));

        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(unknownUsable, 1, 881));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(881).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(rs, 2));
    }
}
