using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Shop open/list/buy packets beyond table parity (NP shop, random refresh, storage delivery).</summary>
public sealed class ShopPacketTests
{
    const long Now = 1_760_100_000_000;
    const int GrocerGoodsId = 103015101;
    const int LifePotion = 530126003;

    static long KinaTotal(FakeCharacterStore store) =>
        store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);

    [Fact]
    public async Task NP_shop_purchase_buys_dat_goods_with_kina()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 40));
        await session.InventoryChangesAsync(default);

        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(GrocerGoodsId).I32(1).ToArray(), Now);
        Assert.Equal(GameSession.NPShopPurchaseRs, GameSessionTestHelpers.Op(buy[0]));
        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Equal(9_750, KinaTotal(store));
        Assert.Contains(store.Items, i => i.TemplateId == LifePotion && i.Location == "bag");
    }

    [Fact]
    public async Task NP_shop_purchase_rejects_unknown_goods_id()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(999_999_999).I32(1).ToArray(), Now);
        Assert.Equal(1, BitConverter.ToUInt16(buy[0], 2));
    }

    [Fact]
    public async Task NP_shop_game_money_limit_lists_purchased_limited_goods()
    {
        var store = new FakeCharacterStore();
        store.Purchases.Add((1, 110019901, ShopTables.Period(0, DateTimeOffset.FromUnixTimeMilliseconds(Now)), 1));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var rq = PacketWriter.Opcode(GameSession.NPShopGameMoneyLimitRq).Varint(1).I32(110019901).ToArray();
        var rs = await session.HandleAsync(rq, Now);
        Assert.Equal(GameSession.NPShopGameMoneyLimitRs, GameSessionTestHelpers.Op(rs[0]));
        Assert.Equal(0, BitConverter.ToUInt16(rs[0], 2));
        Assert.True(rs[0].Length >= 13);
        Assert.Equal(110019901, BitConverter.ToInt32(rs[0], 5));
        Assert.Equal(1, BitConverter.ToInt32(rs[0], 9));
    }

    [Fact]
    public async Task NP_shop_game_money_limit_answers_the_observed_wire_request()
    {
        // OBSERVED client bytes when the BM shop opens: 0x568d, count 10, the currency 159 goods ids.
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var rq = Convert.FromHexString("8D560A9104000094040000B9040000BA040000BB040000E6040000EE04000004870100" + "2D8701004D870100");
        var rs = await session.HandleAsync(rq, Now);
        Assert.Equal((ushort)0x568e, GameSessionTestHelpers.Op(rs[0]));
        Assert.Equal(0, BitConverter.ToUInt16(rs[0], 2));
    }

    [Fact]
    public async Task Trust_limit_info_request_enables_bm_shop_purchase()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var rs = await session.HandleAsync([0x60, 0xE2], Now);
        Assert.Equal(2, rs.Count);
        Assert.Equal((ushort)0xe262, GameSessionTestHelpers.Op(rs[0]));
        Assert.Equal(new byte[] { 0, 0, 1 }, rs[0][2..5]);
        Assert.Equal(Now, BitConverter.ToInt64(rs[0], 5));
        Assert.Equal((ushort)0xe261, GameSessionTestHelpers.Op(rs[1]));
        Assert.Equal(0, BitConverter.ToUInt16(rs[1], 2));
    }

    [Fact]
    public async Task Random_shop_refresh_replies_with_new_notice()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await session.HandleAsync(new PacketWriter().U16(GameSession.RandomShopInfoRq).I32(1).ToArray(), Now);
        var refresh = await session.HandleAsync(new PacketWriter().U16(GameSession.RandomShopRefreshRq).I32(1).ToArray(), Now + 1000);
        Assert.Equal(0, BitConverter.ToUInt16(refresh[0], 2));
        Assert.Contains(refresh, p => GameSessionTestHelpers.Op(p) == GameSession.RandomShopInfoNt);
    }

    [Fact]
    public async Task Shop_buy_delivers_equipment_to_storage_when_bag_full()
    {
        const int goodsId = 103055114;
        const int template = 533460001;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var grocer = session.Targets.Values.First(n => n.NpcId == 2930067);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 2_000_000, 9000));
        for (var i = 0; i < GameSession.ShopBagSlotCap; i++)
            store.Items.Add(FakeCharacterStore.BagItem(610_000_001 + i, 1, 9100 + i));
        await session.InventoryChangesAsync(default);

        await session.HandleAsync(new PacketWriter().U16(GameSession.ShopOpenRq).Varint((uint)grocer.Key).ToArray(), Now);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopBuyRq).Varint(1).Varint((uint)grocer.Key).Varint(1)
            .I32(goodsId).I32(1).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Contains(store.Items, i => i.TemplateId == template && i.Location == "storage");
    }
}
