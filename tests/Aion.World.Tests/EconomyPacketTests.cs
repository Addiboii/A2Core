using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Player economy E2E on SharedWorld (build 25767555): trade, mail, broker, shop, warehouse.</summary>
public sealed class EconomyPacketTests
{
    const long Now = 1_760_100_100_000;
    const int LifePotion = 530126003;
    const int TradeServer = 83;

    static async Task<(GameSession A, GameSession B, SharedWorld World, FakeCharacterStore StoreA, FakeCharacterStore StoreB)> TwoPlayersAsync()
    {
        FakeCharacterStore.BrokerListings.Clear();
        FakeCharacterStore.Inboxes.Clear();
        FakeCharacterStore.EconomyTimeMs = Now;
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore(Guid.NewGuid());
        var a = await Enter(world, storeA, "EcoA", 11);
        var b = await Enter(world, storeB, "EcoB", 12);
        var spot = new Vector3d(-127874, -111187, 25393);
        a.Teleport(1010, spot, Now);
        b.Teleport(1010, spot, Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (a, b, world, storeA, storeB);
    }

    static async Task<GameSession> Enter(SharedWorld world, FakeCharacterStore store, string name, int seed)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([1]).U8(0).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static long Kina(FakeCharacterStore s) => FakeCharacterStore.KinaIn(s);

    [Fact]
    public async Task Trade_lock_confirm_moves_items_and_kina_without_duplication()
    {
        var (a, b, world, storeA, storeB) = await TwoPlayersAsync();
        storeA.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 40));
        storeA.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 41));
        storeB.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 100, 50));
        await a.InventoryChangesAsync(default);
        await b.InventoryChangesAsync(default);
        var targetKey = b.PlayerKey;
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, targetKey), Now + 1);
        var exchangeId = world.Social.Exchanges.Values.Single().Id;
        await b.HandleAsync(SocialRequests.EncodeExchangeAnswer(SocialOpcodes.ExchangeAcceptRq, exchangeId, a.PlayerKey), Now + 2);
        var potionKey = storeA.Items.Single(i => i.TemplateId == LifePotion).WireId;
        await a.HandleAsync(SocialRequests.EncodeExchangeItemModify(new ExchangeModifyRequest(exchangeId, ExchangeModification.Add, new ExchangeItem(potionKey, 1), 0)), Now + 3);
        await a.HandleAsync(SocialRequests.EncodeExchangeItemModify(new ExchangeModifyRequest(exchangeId, ExchangeModification.Update, new ExchangeItem(0, 0), 250)), Now + 4);
        await b.HandleAsync(SocialRequests.EncodeExchangeItemModify(new ExchangeModifyRequest(exchangeId, ExchangeModification.Update, new ExchangeItem(0, 0), 100)), Now + 5);
        await a.HandleAsync(SocialRequests.EncodeExchangeLock(exchangeId, true), Now + 6);
        await b.HandleAsync(SocialRequests.EncodeExchangeLock(exchangeId, true), Now + 7);
        await a.HandleAsync(SocialRequests.EncodeDbId(SocialOpcodes.ExchangeConfirmRq, exchangeId), Now + 8);
        var confirmB = await b.HandleAsync(SocialRequests.EncodeDbId(SocialOpcodes.ExchangeConfirmRq, exchangeId), Now + 9);
        Assert.Contains(confirmB, p => GameSessionTestHelpers.Op(p) == SocialOpcodes.ExchangeCompletedNt);
        Assert.Single(storeB.Items, i => i.TemplateId == LifePotion);
        Assert.Equal(4850, Kina(storeA));
        Assert.Equal(250, Kina(storeB));
        Assert.Empty(world.Social.Exchanges);
    }

    [Fact]
    public async Task Trade_disconnect_before_confirm_does_not_move_items()
    {
        var (a, b, world, storeA, _) = await TwoPlayersAsync();
        storeA.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 40));
        await a.InventoryChangesAsync(default);
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, b.PlayerKey), Now + 1);
        var exchangeId = world.Social.Exchanges.Values.Single().Id;
        await b.HandleAsync(SocialRequests.EncodeExchangeAnswer(SocialOpcodes.ExchangeAcceptRq, exchangeId, a.PlayerKey), Now + 2);
        var key = storeA.Items.Single(i => i.TemplateId == LifePotion).WireId;
        await a.HandleAsync(SocialRequests.EncodeExchangeItemModify(new ExchangeModifyRequest(exchangeId, ExchangeModification.Add, new ExchangeItem(key, 1), 0)), Now + 3);
        await a.HandleAsync(new PacketWriter().U16(0x3625).ToArray(), Now + 4);
        Assert.Empty(world.Social.Exchanges);
        Assert.Single(storeA.Items, i => i.TemplateId == LifePotion);
    }

    [Fact]
    public async Task Mail_send_take_and_cod()
    {
        var (a, b, world, storeA, storeB) = await TwoPlayersAsync();
        var to = storeB.States[0].Summary.CharacterId;
        storeA.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 60));
        storeA.Items.Add(FakeCharacterStore.BagItem(LifePotion, 2, 61));
        storeB.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 500, 70));
        var post = await storeA.SendPlayerPostAsync(storeA.States[0].Summary.CharacterId, to, TradeTables.Embedded.CodPostDataId, "fee|body",
            [(LifePotion, 1)], codKina: 50, mailFeeKina: TradeTables.Embedded.PlayerMailFeeKina, default);
        Assert.NotNull(post);
        Assert.Equal(4900, Kina(storeA));
        b.Store = storeB;
        var load = await b.HandleAsync(new PacketWriter().U16(GameSession.PostLoadRq).U8(2).U8(0).I64(0).I64(0).ToArray(), Now + 10);
        Assert.Equal(GameSession.PostLoadRs, GameSessionTestHelpers.Op(load[0]));
        var take = await b.HandleAsync(new PacketWriter().U16(GameSession.PostTakeRq).U8(1).I64(post!.Id).ToArray(), Now + 11);
        Assert.Equal(0, BitConverter.ToUInt16(take[0], 2));
        Assert.Equal(450, Kina(storeB));
        Assert.Single(storeB.Items, i => i.TemplateId == LifePotion);
    }

    [Fact]
    public async Task Broker_list_buy_offline_payout_and_cancel()
    {
        var (seller, buyer, _, storeS, storeB) = await TwoPlayersAsync();
        storeS.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 80));
        storeS.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 81));
        storeB.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 90));
        await seller.InventoryChangesAsync(default);
        var wire = storeS.Items.Single(i => i.TemplateId == LifePotion).WireId;
        var list = await seller.HandleAsync(new PacketWriter().U16(GameSession.TradeSellRq).I64(wire).I64(1).I32(GameSession.KinaItemId).I64(200)
            .I32(500).I32(10).I32(TradeServer).ToArray(), Now + 20);
        Assert.Equal(0, BitConverter.ToUInt16(list[0], 2));
        var saleId = storeS.TradeListings.Single().SaleId;
        Assert.DoesNotContain(storeS.Items, i => i.Location == "bag" && i.TemplateId == LifePotion);
        var buy = await buyer.HandleAsync(new PacketWriter().U16(GameSession.TradePurchaseRq).String(saleId.ToString()).I32(LifePotion).I64(1).I32(TradeServer).ToArray(), Now + 21);
        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Single(storeB.Items, i => i.TemplateId == LifePotion && i.Location == "bag");
        Assert.Equal(4800, Kina(storeB));
        var payout = await seller.HandleAsync(new PacketWriter().U16(GameSession.TradeDeliveryCompleteRq).String(storeS.TradeDeliveries.Single().DeliveryId.ToString())
            .I32(LifePotion).I32(TradeServer).ToArray(), Now + 22);
        Assert.Equal(0, BitConverter.ToUInt16(payout[0], 2));
        Assert.True(Kina(storeS) > 9900);
        storeS.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 82));
        await seller.InventoryChangesAsync(default);
        var wire2 = storeS.Items.Single(i => i.WireId == 82).WireId;
        await seller.HandleAsync(new PacketWriter().U16(GameSession.TradeSellRq).I64(wire2).I64(1).I32(GameSession.KinaItemId).I64(100).I32(500).I32(10).I32(TradeServer).ToArray(), Now + 30);
        var sale2 = storeS.TradeListings.Last().SaleId;
        var cancel = await seller.HandleAsync(new PacketWriter().U16(GameSession.TradeCancelSaleRq).String(sale2.ToString()).I32(LifePotion).I64(1).I32(TradeServer).ToArray(), Now + 31);
        Assert.Equal(0, BitConverter.ToUInt16(cancel[0], 2));
        Assert.Single(storeS.Items, i => i.TemplateId == LifePotion && i.Location == "bag");
    }

    [Fact]
    public async Task Mail_expired_returns_attachments_and_cod_to_sender()
    {
        var (a, b, _, storeA, storeB) = await TwoPlayersAsync();
        var from = storeA.States[0].Summary.CharacterId;
        var to = storeB.States[0].Summary.CharacterId;
        storeA.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 70));
        storeA.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 1000, 71));
        var post = await storeA.SendPlayerPostAsync(from, to, TradeTables.Embedded.CodPostDataId, "sale|body", [(LifePotion, 1)], codKina: 75,
            mailFeeKina: TradeTables.Embedded.PlayerMailFeeKina, default);
        Assert.NotNull(post);
        Assert.DoesNotContain(storeA.Items, i => i.TemplateId == LifePotion);
        var inbox = FakeCharacterStore.Inboxes[to];
        inbox[0] = inbox[0] with { Expire = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var swept = await storeA.ReturnExpiredPostsAsync(default);
        Assert.Equal(1, swept);
        Assert.Empty(FakeCharacterStore.Inboxes[to]);
        Assert.Single(FakeCharacterStore.Inboxes[from], p => p.Attachments.Any(a => a.TemplateId == LifePotion));
        Assert.Equal(975, FakeCharacterStore.KinaIn(storeA));
    }

    [Fact]
    public async Task Personal_and_account_warehouse_round_trip()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 55));
        await session.InventoryChangesAsync(default);
        var key = store.Items.Single(i => i.Location == "bag").WireId;
        var toStorage = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Storage).I64(key).I32(1).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(toStorage[0], 2));
        var toAccount = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.AccountStorage).I64(key).I32(0).ToArray(), Now + 1);
        Assert.Equal(0, BitConverter.ToUInt16(toAccount[0], 2));
        Assert.Equal("account_storage", store.Items.Single(i => i.WireId == key).Location);
        var back = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemMoveRq).I32(0).U8(InventoryItem.Bag).I64(key).I32(3).ToArray(), Now + 2);
        Assert.Equal(0, BitConverter.ToUInt16(back[0], 2));
        Assert.Equal("bag", store.Items.Single(i => i.WireId == key).Location);
    }
}
