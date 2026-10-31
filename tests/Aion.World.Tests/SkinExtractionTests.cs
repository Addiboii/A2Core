using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkinExtractionTests
{
    const long Now = 1_760_000_000_456;
    const int Template = SkinExtractionTables.TestTemplateId;
    const int Material = SkinExtractionTables.TestMaterialId;
    const int Skin = SkinExtractionTables.TestSkinId;

    static byte[] Request(params long[] uids)
    {
        var w = PacketWriter.Opcode(GameSession.SkinExtractionRq).Varint(uids.Length);
        foreach (var uid in uids) w.I64(uid);
        return w.ToArray();
    }

    static (int Result, int Uids, bool Success) ReadReply(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var result = r.I32();
        var count = (int)r.Varint();
        for (var i = 0; i < count; i++) r.I64();
        var outcomes = (int)r.Varint();
        var ok = outcomes > 0 && r.U8() != 0;
        return (result, count, ok);
    }

    static async Task<(GameSession Session, FakeCharacterStore Store, long Target)> SessionWithItemAsync()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(Material, 5, 9100));
        store.Items.Add(FakeCharacterStore.BagItem(Template, 1, 9200));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9300));
        await session.InventoryChangesAsync(default);
        return (session, store, 9200);
    }

    [Fact]
    public void Live_opcode_is_catalogue_SkinExtraction_plus_53c5()
    {
        Assert.Equal(0x8d60, GameSession.SkinExtractionRq);
        Assert.Equal(0x399B + 0x53C5, GameSession.SkinExtractionRq);
    }

    [Fact]
    public void Request_parses_item_uids()
    {
        var plain = Request(9200, 9201);
        Assert.Equal([9200L, 9201L], GameSession.ParseSkinExtraction(plain));
    }

    [Fact]
    public async Task Success_spends_costs_grants_skin_and_replies()
    {
        var (session, store, target) = await SessionWithItemAsync();
        session.SkinExtractionRoll = () => 0;
        var replies = await session.HandleAsync(Request(target), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SkinExtractionRs);
        Assert.Equal((0, 1, true), ReadReply(rs));
        Assert.Equal(1, session.OwnedSkins.GetValueOrDefault(Skin));
        Assert.Equal(4, store.Items.Single(i => i.TemplateId == Material).Quantity);
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, GameSession.SkinUpdateNt));
    }

    [Fact]
    public async Task Refusal_when_skin_already_owned_uses_client_result_code()
    {
        var (session, _, target) = await SessionWithItemAsync();
        await session.AcquireSkinsAsync([Skin], default);
        var replies = await session.HandleAsync(Request(target), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SkinExtractionRs);
        Assert.Equal(GameSession.SkinExtractionAlreadyExist, ReadReply(rs).Result);
    }

    [Fact]
    public async Task Second_attempt_refuses_when_extraction_count_is_spent()
    {
        var (session, _, target) = await SessionWithItemAsync();
        session.SkinExtractionRoll = () => 0;
        await session.HandleAsync(Request(target), Now);
        var replies = await session.HandleAsync(Request(target), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SkinExtractionRs);
        Assert.Equal(GameSession.SkinExtractionNotEnoughCount, ReadReply(rs).Result);
    }

    [Fact]
    public async Task Invalid_item_refuses_with_InvalidData()
    {
        var (session, _, _) = await SessionWithItemAsync();
        var replies = await session.HandleAsync(Request(1), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SkinExtractionRs);
        Assert.Equal(GameSession.SkinExtractionInvalidData, ReadReply(rs).Result);
    }
}
