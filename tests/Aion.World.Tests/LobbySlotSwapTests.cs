using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbySlotSwapTests
{
    const long Now = 5_000_000;

    static byte[] SwapRequest(params (long wire, int slot)[] rows)
    {
        var w = PacketWriter.Opcode(GameSession.CharSlotSwapRq).Varint(rows.Length);
        foreach (var (wire, slot) in rows) w.I64(wire).U8(slot);
        return w.ToArray();
    }

    [Fact]
    public async Task Swap_in_lobby_replies_with_official_shaped_rs_only()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var first = store.States[0];
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("AltHero").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(0).U8(0).ToArray(), Now + 1);
        var second = store.States[1];
        Assert.Equal(1, first.LobbySlot);
        Assert.Equal(0, second.LobbySlot);

        var replies = await session.HandleAsync(SwapRequest((first.WireId, 0), (second.WireId, 1)), Now + 2);
        Assert.Single(replies);
        var expected = GamePackets.CharSlotSwap(0, [(first.WireId, 0), (second.WireId, 1)]);
        Assert.Equal(23, expected.Length);
        Assert.True(replies[0].AsSpan().SequenceEqual(expected));
        Assert.Equal(0, store.States.First(s => s.WireId == first.WireId).LobbySlot);
        Assert.Equal(1, store.States.First(s => s.WireId == second.WireId).LobbySlot);
    }

    [Fact]
    public async Task Swap_four_character_layout_persists_without_followup_packets()
    {
        // Live RQ: 5→3, 8→1, 6→2, 7→4 (varint count 04, then i64 dbid + u8 slot each).
        var body = Convert.FromHexString(
            "36E304050000000000000003080000000000000001060000000000000002070000000000000004");
        var store = new FakeCharacterStore { ExtendedCharacterSlots = 1 };
        foreach (var (wire, slot) in new[] { (5, 0), (6, 1), (7, 2), (8, 3) })
        {
            store.States.Add(new CharacterState(
                new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, $"Hero{wire}", 1),
                1, 1, 1, [], new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })),
                1, WireId: wire, LobbySlot: slot));
        }
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var replies = await session.HandleAsync(body, Now + 1);
        Assert.Single(replies);
        var expected = GamePackets.CharSlotSwap(0, [(5, 3), (8, 1), (6, 2), (7, 4)]);
        Assert.Equal(41, expected.Length);
        Assert.Equal(39, body.Length);
        Assert.True(replies[0].AsSpan().SequenceEqual(expected));
        Assert.Equal(GameSession.CharSlotSwapRs, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(3, store.States.First(s => s.WireId == 5).LobbySlot);
        Assert.Equal(1, store.States.First(s => s.WireId == 8).LobbySlot);
        Assert.Equal(2, store.States.First(s => s.WireId == 6).LobbySlot);
        Assert.Equal(4, store.States.First(s => s.WireId == 7).LobbySlot);
    }

    [Fact]
    public async Task Swap_accepts_shifted_dbid_like_selection_key()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var hero = store.States[0];
        var shifted = (long)hero.WireId << 16;
        var replies = await session.HandleAsync(SwapRequest((shifted, 2)), Now + 1);
        Assert.Single(replies);
        Assert.Equal(0, BitConverter.ToUInt16(replies[0], 2));
        Assert.Equal(2, store.States[0].LobbySlot);
        Assert.Equal([(shifted, 2)], ParseSlotSwapRs(replies[0]));
    }

    [Fact]
    public async Task Swap_with_unknown_dbid_fails_without_clearing_slots()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var hero = store.States[0];
        var slot = hero.LobbySlot;
        var replies = await session.HandleAsync(SwapRequest((999, 0)), Now + 1);
        Assert.Single(replies);
        Assert.Equal(GameSession.CharSlotSwapFailed, BitConverter.ToUInt16(replies[0], 2));
        Assert.Equal(slot, store.States[0].LobbySlot);
    }

    [Fact]
    public void Lobby_slot_layout_fills_null_and_duplicate_slots_deterministically()
    {
        var account = Guid.NewGuid();
        var states = new[]
        {
            new CharacterState(new CharacterSummary(Guid.NewGuid(), account, 1, "A", 1), 1, 1, 1, [],
                new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })), 1, WireId: 10, LobbySlot: 2, RegisteredUnixMs: 100),
            new CharacterState(new CharacterSummary(Guid.NewGuid(), account, 1, "B", 1), 1, 1, 1, [],
                new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })), 1, WireId: 11, LobbySlot: null, RegisteredUnixMs: 200),
            new CharacterState(new CharacterSummary(Guid.NewGuid(), account, 1, "C", 1), 1, 1, 1, [],
                new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })), 1, WireId: 12, LobbySlot: 2, RegisteredUnixMs: 300),
        };
        var layout = GameSession.LobbySlotLayout(states, 8);
        Assert.Equal(3, layout.Count);
        Assert.Equal([0, 1, 2], layout.Select(p => p.SlotNo).Order().ToArray());
    }

    [Fact]
    public async Task CharCustomizingList_in_lobby_returns_slot_appearance_rows()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var hero = store.States[0];
        var replies = await session.HandleAsync(PacketWriter.Opcode(GameSession.CharCustomizingListRq).ToArray(), Now + 1);
        Assert.Single(replies);
        Assert.Equal(GameSession.CharCustomizingListRs, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(0, BitConverter.ToInt32(replies[0], 2));
        var reader = new PacketReader(replies[0]) { Offset = 6 };
        Assert.Equal(1, (int)reader.Varint());
        Assert.Equal(hero.LobbySlot, reader.U8());
        _ = reader.Bytes();
        _ = reader.U8();
        _ = reader.I32();
    }

    static List<(long Wire, int Slot)> ParseSlotSwapRs(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.U16();
        var count = reader.U8();
        var list = new List<(long, int)>(count);
        for (var i = 0; i < count; i++) list.Add((reader.I64(), reader.U8()));
        return list;
    }

    [Fact]
    public void Char_slot_swap_rs_matches_official_wire_layout()
    {
        Assert.Equal(0xe336, GameSession.CharSlotSwapRq);
        Assert.Equal(0xe337, GameSession.CharSlotSwapRs);
        var two = GamePackets.CharSlotSwap(0, [(5, 3), (8, 1)]);
        Assert.Equal(Convert.FromHexString("37E3000002050000000000000003080000000000000001"), two);
        Assert.Equal(23, two.Length);
        var four = GamePackets.CharSlotSwap(0, [(5, 3), (8, 1), (6, 2), (7, 4)]);
        Assert.Equal(41, four.Length);
        Assert.Equal(39, SwapRequest((1, 0), (2, 1), (3, 2), (4, 3)).Length);
    }

    [Fact]
    public async Task Swap_while_in_world_is_rejected()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var hero = store.States[0];
        var replies = await session.HandleAsync(SwapRequest((hero.WireId, 2)), Now + 1);
        Assert.Single(replies);
        Assert.Equal(GameSession.CharSlotSwapFailed, BitConverter.ToUInt16(replies[0], 2));
    }

    [Fact]
    public async Task Swap_to_out_of_range_slot_is_rejected()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var hero = store.States[0];
        var replies = await session.HandleAsync(SwapRequest((hero.WireId, GameSession.BaseCharacterSlots)), Now + 1);
        Assert.Single(replies);
        Assert.Equal(GameSession.CharSlotSwapFailed, BitConverter.ToUInt16(replies[0], 2));
    }
}
