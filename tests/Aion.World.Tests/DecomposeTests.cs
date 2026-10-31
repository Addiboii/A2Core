using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DecomposeTests
{
    const long Now = 1_760_100_100_000;

    [Fact]
    public void Live_opcode_is_catalogue_ItemDecompose_plus_1ebe()
    {
        Assert.Equal(0x5640, GameSession.ItemDecomposeRq);
        Assert.Equal(0x3782 + 0x1ebe, GameSession.ItemDecomposeRq);
        Assert.Equal(0x5641, GameSession.ItemDecomposeRs);
        Assert.Equal(0x3783 + 0x1ebe, GameSession.ItemDecomposeRs);
    }

    [Fact]
    public async Task Malformed_request_still_gets_ItemDecompose_reply()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).ToArray(), Now);
        Assert.Equal(GameSession.ItemDecomposeRs, GameSessionTestHelpers.Op(reply[0]));
        Assert.Equal(1, BitConverter.ToInt32(reply[0], 2));
    }

    [Fact]
    public async Task Twelve_byte_single_uid_request_is_handled()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(3));
        store.Items.Add(FakeCharacterStore.BagItem(110160001, 1, 940, 9));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 5000, 941));
        await session.InventoryChangesAsync(default);

        var request = new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(940).ToArray();
        Assert.Equal(12, request.Length);
        var replies = await session.HandleAsync(request, Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemDecomposeRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(1, BitConverter.ToInt32(rs, 7));
        var nt = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        var types = ModificationTypes(nt);
        Assert.Contains(types, t => t == 2);
        Assert.Contains(types, t => t == 0);
        Assert.True(nt.AsSpan().IndexOf(BitConverter.GetBytes(940L)) > 0);
        Assert.DoesNotContain(store.Items, i => i.WireId == 940);
    }

    [Fact]
    public async Task Batch_decompose_sends_0x561b_removals_and_reward_adds()
    {
        const int template = 110160001;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(11));
        store.Items.Add(FakeCharacterStore.BagItem(template, 1, 0xBC));
        store.Items.Add(FakeCharacterStore.BagItem(template, 1, 0xBD));
        store.Items.Add(FakeCharacterStore.BagItem(template, 1, 0xBF));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 50_000, 0xC0));
        await session.InventoryChangesAsync(default);

        var request = new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(3).I64(0xBC).I64(0xBD).I64(0xBF).ToArray();
        var replies = await session.HandleAsync(request, Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemDecomposeRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(3, BitConverter.ToInt32(rs, 7));

        var nt = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        var types = ModificationTypes(nt);
        Assert.Equal(3, types.Count(t => t == 2));
        Assert.True(types.Count(t => t == 0) >= 1);
        Assert.True(types.Any(t => t == 1), "kina stack update");
        Assert.DoesNotContain(store.Items, i => i.WireId is 0xBC or 0xBD or 0xBF);
        Assert.Contains(store.Items, i => i.TemplateId != template && i.TemplateId != GameSession.KinaItemId);
    }

    static List<byte> ModificationTypes(byte[] nt)
    {
        var r = new PacketReader(nt) { Offset = 2 };
        var count = (int)r.Varint();
        var types = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            types.Add(r.U8());
            SkipCommonItem(r);
            r.U8();
            r.U8();
            r.U8();
        }
        return types;
    }

    static void SkipCommonItem(PacketReader r)
    {
        var hasDetail = r.U8();
        r.I64();
        r.I32();
        r.I64();
        r.U8();
        r.I32();
        r.Bool();
        r.String();
        r.I64();
        r.Bool();
        if (hasDetail == 0) return;
        r.U8();
        r.I32();
        r.I32();
        r.U8();
        r.I32();
        r.I32();
        r.Bool();
        r.U8();
        r.Varint();
        r.Offset += 7;
        r.I64();
        r.U8();
        var godstones = (int)r.Varint();
        for (var g = 0; g < godstones; g++) r.I32();
        r.Offset += 7;
        r.U8();
    }

    [Fact]
    public void SkinOf_falls_back_to_skin_extraction_table()
    {
        Assert.Equal(SkinExtractionTables.TestSkinId, DecomposeTables.Embedded.SkinOf(SkinExtractionTables.TestTemplateId));
    }

    [Fact]
    public void SkinOf_uses_item_decompose_skins_table()
    {
        Assert.Equal(10078104, DecomposeTables.Embedded.SkinOf(110430048));
        Assert.Equal(30019101, DecomposeTables.Embedded.SkinOf(210140035));
    }

    [Fact]
    public async Task Decompose_grants_dismantle_skin_and_SkinUpdate()
    {
        const int template = 110430048;
        const int skinId = 10078104;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, rng: new Random(5));
        store.Items.Add(FakeCharacterStore.BagItem(template, 1, 960));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 20_000, 961));
        await session.InventoryChangesAsync(default);

        var replies = await session.HandleAsync(
            new PacketWriter().U16(GameSession.ItemDecomposeRq).U8(0).Varint(1).I64(960).ToArray(), Now);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemDecomposeRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, GameSession.SkinUpdateNt));
        Assert.Equal(1, session.OwnedSkins.GetValueOrDefault(skinId));
        var reader = new PacketReader(rs) { Offset = 2 };
        reader.I32();
        reader.Bool();
        reader.I32();
        reader.I32();
        var rewardCount = (int)reader.Varint();
        for (var i = 0; i < rewardCount; i++)
        {
            reader.I32();
            reader.I32();
        }
        var skinCount = (int)reader.Varint();
        Assert.Equal(1, skinCount);
        Assert.Equal(skinId, reader.I32());
    }

    [Fact]
    public void Equippable_items_json_entry_gets_equip_detail_on_wire()
    {
        const int template = 810550001;
        var row = FakeCharacterStore.BagItem(template, 1, 44);
        var wire = GameSession.Layout([row], data: GameData.Embedded).Single();
        Assert.NotNull(wire.Detail);
    }
}
