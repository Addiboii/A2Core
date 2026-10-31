using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Anti-cheat bounds on packet handlers (build 25767555): client fields, overflow, floods, malformed bodies.</summary>
public sealed class SecurityPacketTests
{
    const long Now = 1_760_200_000_000;
    const int LifePotion = 530126003;
    const int TradeServer = 83;
    const int KeenStrike = 11020000;

    static ItemState Bag(FakeCharacterStore store, int templateId) =>
        store.Items.Single(i => i.TemplateId == templateId && i.Location == "bag");

    static async Task<GameSession> EnterAsync(FakeCharacterStore store, SharedWorld? world = null)
    {
        world ??= new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(91)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Broker_rejects_overflow_listing_count_and_price()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAsync(store);
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 5, 10));
        await session.InventoryChangesAsync(default);
        var potion = Bag(store, LifePotion);
        var huge = new PacketWriter().U16(GameSession.TradeSellRq).U64((ulong)potion.WireId).U64(long.MaxValue).I32(GameSession.KinaItemId)
            .U64(1).I32(0).I32(0).I32(TradeServer).ToArray();
        var reply = await session.HandleAsync(huge, Now + 1);
        Assert.Equal(GameSession.TradeSellRs, GameSessionTestHelpers.Op(reply[0]));
        Assert.Equal(1, BitConverter.ToUInt16(reply[0], 2));
        Assert.Equal(5, Bag(store, LifePotion).Quantity);
    }

    [Fact]
    public async Task Shop_buy_rejects_count_overflow()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAsync(store);
        session.Teleport(1010, new Vector3d(-127874, -111187, 25393), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 1_000_000, 1));
        await session.InventoryChangesAsync(default);
        var npcKey = session.Targets.Keys.First(k => k != session.PlayerKey);
        var open = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopOpenRq).Varint(npcKey).ToArray(), Now + 2);
        Assert.Equal(GameSession.ShopOpenRs, GameSessionTestHelpers.Op(open[0]));
        var buy = new PacketWriter().U16(GameSession.ShopBuyRq).Varint(1).Varint((uint)npcKey).Varint(1)
            .I32(103015101).I32(int.MaxValue).ToArray();
        var fail = await session.HandleAsync(buy, Now + 3);
        Assert.Equal(GameSession.ShopBuyRs, GameSessionTestHelpers.Op(fail[0]));
        Assert.NotEqual(0, BitConverter.ToUInt16(fail[0], 2));
    }

    [Fact]
    public async Task Exchange_rejects_excessive_kina_offer()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore(Guid.NewGuid());
        var a = await EnterAsync(storeA, world);
        var b = await EnterAsync(storeB, world);
        var spot = new Vector3d(-127874, -111187, 25393);
        a.Teleport(1010, spot, Now);
        b.Teleport(1010, spot, Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, b.PlayerKey), Now + 1);
        var id = world.Social.Exchanges.Values.Single().Id;
        await b.HandleAsync(SocialRequests.EncodeExchangeAnswer(SocialOpcodes.ExchangeAcceptRq, id, a.PlayerKey), Now + 2);
        var bad = await a.HandleAsync(SocialRequests.EncodeExchangeItemModify(
            new ExchangeModifyRequest(id, ExchangeModification.Update, new ExchangeItem(0, 0), (int)(PacketSecurity.MaxExchangeKina + 1))), Now + 3);
        Assert.Equal(SocialOpcodes.ExchangeItemListModifyRs, GameSessionTestHelpers.Op(bad[0]));
        Assert.Equal(1, BitConverter.ToUInt16(bad[0], 2));
    }

    [Fact]
    public async Task Quick_slot_rejects_foreign_item_uid()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAsync(store);
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 1, 77));
        await session.InventoryChangesAsync(default);
        var real = Bag(store, LifePotion).WireId;
        var body = new PacketWriter().U16(GameSession.QuickSlotSetRq).U8(0).U8(5).U8(1).I64(real + 999).I32(LifePotion).I32(0).U8(0).I32(0).U8(0).ToArray();
        var fail = await session.HandleAsync(body, Now + 1);
        Assert.Equal(GameSession.QuickSlotSetRs, GameSessionTestHelpers.Op(fail[0]));
        Assert.Equal(1, BitConverter.ToUInt16(fail[0], 2));
    }

    [Fact]
    public async Task Malformed_trade_packet_does_not_throw()
    {
        var session = await EnterAsync(new FakeCharacterStore());
        var replies = await session.HandleClientPacketSafeAsync(new byte[] { 0x69, 0x3b, 0x01 }, Now + 1, default);
        Assert.Empty(replies);
    }

    [Fact]
    public async Task Packet_rate_limit_drops_flood_without_crashing()
    {
        var session = await EnterAsync(new FakeCharacterStore());
        var move = new PacketWriter().U16(0x3701).U8(0).F32(0).F32(0).F32(0).U16(0).F32(0).ToArray();
        for (var i = 0; i < 200; i++)
            await session.HandleClientPacketSafeAsync(move, Now + i, default);
        Assert.True(session.PacketRateDrops > 0);
    }

    [Fact]
    public async Task Movement_flood_never_silences_the_cast_handshake()
    {
        var session = await EnterAsync(new FakeCharacterStore());
        var move = new PacketWriter().U16(0x3701).U8(0).F32(0).F32(0).F32(0).U16(0).F32(0).ToArray();
        for (var i = 0; i < 200; i++)
            await session.HandleClientPacketSafeAsync(move, Now + i, default);
        Assert.True(session.PacketRateDrops > 0);
        for (var i = 0; i < 300; i++)
            Assert.NotEmpty(await session.HandleClientPacketSafeAsync(new byte[] { 0x3a, 0x38 }, Now + 200 + i, default));
        Assert.NotEmpty(await session.HandleClientPacketSafeAsync(new byte[] { 0x3c, 0x38 }, Now + 600, default));
    }

    [Fact]
    public async Task Item_remove_rejects_negative_count()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAsync(store);
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 3, 20));
        await session.InventoryChangesAsync(default);
        var potion = Bag(store, LifePotion);
        var body = new PacketWriter().U16(GameSession.ItemRemoveRq).Varint(1).I64(potion.WireId).I64(-1).Varint(0).ToArray();
        var fail = await session.HandleAsync(body, Now + 1);
        Assert.Equal(GameSession.ItemRemoveRs, GameSessionTestHelpers.Op(fail[0]));
        Assert.Equal(1, BitConverter.ToUInt16(fail[0], 2));
        Assert.Equal(3, Bag(store, LifePotion).Quantity);
    }

    [Fact]
    public void ParseCast_rejects_non_finite_coordinates()
    {
        var body = new byte[40];
        body[0] = 0;
        body[1] = 0x38;
        body[2] = 0xff;
        body[3] = 0x01;
        BitConverter.TryWriteBytes(body.AsSpan(4), KeenStrike);
        body[8] = 2;
        body[9] = 1;
        BitConverter.TryWriteBytes(body.AsSpan(10), float.NaN);
        Assert.Null(GameSession.ParseCast(body));
    }
}
