using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbyCreateCancelTests
{
    const long Now = 6_000_000;

    static void SeedFourCharacters(FakeCharacterStore store, int race = 1, int creationId = 1)
    {
        store.ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots;
        for (var slot = 0; slot < 4; slot++)
        {
            var wire = 100 + slot;
            store.States.Add(new CharacterState(
                new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, $"Hero{wire}", 1),
                creationId, race, 1, [], new CharacterProgress(10, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })),
                1, WireId: wire, LobbySlot: slot, RegisteredUnixMs: 1_000 + slot));
        }
    }

    static int LobbyCharacterCount(byte[] lobbyRs)
    {
        var reader = new PacketReader(lobbyRs) { Offset = 4 };
        return checked((int)reader.Varint());
    }

    [Fact]
    public async Task Cancel_character_creation_with_lobby_rq_slot_byte_refreshes_list()
    {
        var store = new FakeCharacterStore();
        SeedFourCharacters(store);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(7)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var cancelSlot = GameSession.DefaultLobbySlots + 1;
        var replies = await session.HandleAsync(PacketWriter.Opcode(0x3625).U8(cancelSlot).ToArray(), Now + 1);

        Assert.Equal(2, replies.Count);
        Assert.Equal(0x3626, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(0x3A16, GameSessionTestHelpers.Op(replies[1]));
        Assert.Equal(GameSession.MaxExtendedLobbySlots, replies[1][2]);
        Assert.Equal(4, LobbyCharacterCount(replies[0]));
        foreach (var wire in new[] { 100, 101, 102, 103 })
            Assert.Contains(System.Text.Encoding.UTF8.GetBytes($"Hero{wire}"), replies[0]);
    }

    static int DarkCreationId() =>
        Enumerable.Range(1, 64).First(c => GameData.Embedded.Profile(c, 1) is { Race: 2 });

    static int CreateRefusalCode(IReadOnlyList<byte[]> replies) => BitConverter.ToInt32(replies[0], 2);

    [Fact]
    public async Task Asmodian_gladiator_succeeds_with_light_creation_id_on_asmodian_account()
    {
        var store = new FakeCharacterStore();
        SeedFourCharacters(store, race: 2, creationId: 9);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(13)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var slot = (byte)4;
        var created = await session.HandleAsync(
            new PacketWriter().U16(0x361b).String("DarkGlad").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(slot).U8(0).ToArray(),
            Now + 1);

        Assert.Equal(0, BitConverter.ToInt32(created[0], 2));
        Assert.Equal(5, store.States.Count);
        var glad = store.States.Single(s => s.Summary.Name == "DarkGlad");
        Assert.Equal(9, glad.CreationId);
        Assert.Equal(2, glad.Race);
        Assert.Equal(slot, glad.LobbySlot);
    }

    [Fact]
    public async Task Refused_other_race_create_refreshes_lobby_list()
    {
        var store = new FakeCharacterStore();
        SeedFourCharacters(store);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(8)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var darkSlot = (byte)4;
        var refused = await session.HandleAsync(
            new PacketWriter().U16(0x361b).String("DarkNew").U8(1).U32((uint)DarkCreationId()).Bytes([1]).U8(0).U8(darkSlot).U8(0).ToArray(),
            Now + 1);

        Assert.Equal(3, refused.Count);
        Assert.Equal(0x361c, GameSessionTestHelpers.Op(refused[0]));
        Assert.Equal(GamePackets.CreateInvalidServerRace, CreateRefusalCode(refused));
        Assert.Equal(0x3626, GameSessionTestHelpers.Op(refused[1]));
        Assert.Equal(0x3A16, GameSessionTestHelpers.Op(refused[2]));
        Assert.Equal(4, LobbyCharacterCount(refused[1]));
    }

    [Fact]
    public async Task After_refused_create_cancel_lobby_rq_still_lists_characters()
    {
        var store = new FakeCharacterStore();
        SeedFourCharacters(store);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        await session.HandleAsync(
            new PacketWriter().U16(0x361b).String("DarkNew").U8(1).U32((uint)DarkCreationId()).Bytes([1]).U8(0).U8(4).U8(0).ToArray(),
            Now + 1);

        var cancel = await session.HandleAsync(PacketWriter.Opcode(0x3625).U8(7).ToArray(), Now + 2);
        Assert.Equal(2, cancel.Count);
        Assert.Equal(4, LobbyCharacterCount(cancel[0]));
    }

    [Fact]
    public async Task PlaytestDebug_logs_character_create_refusal()
    {
        var store = new FakeCharacterStore();
        SeedFourCharacters(store);
        var capture = new CapturePlaytestSink();
        PlaytestDebug.Sink = capture;
        try
        {
            var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(12)) { Store = store };
            await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
            var darkClass = DarkCreationId();
            await session.HandleAsync(
                new PacketWriter().U16(0x361b).String("X").U8(1).U32((uint)darkClass).Bytes([1]).U8(0).U8(4).U8(0).ToArray(),
                Now + 1);
            var entry = Assert.Single(capture.ActionLog.ToList(), a =>
                a.Kind == "character_create_refused" && Equals(a.Fields["creationId"], darkClass));
            Assert.Equal(4, entry.Fields["slot"]);
            Assert.Equal(1, entry.Fields["nameLength"]);
            Assert.Equal(2, entry.Fields["race"]);
            Assert.Equal(GamePackets.CreateInvalidServerRace, entry.Fields["result"]);
        }
        finally
        {
            PlaytestDebug.Sink = null;
        }
    }

    sealed class CapturePlaytestSink : IPlaytestDebugSink
    {
        public bool Packets => false;
        public bool Actions => true;
        public bool Entities => false;
        public List<(string Kind, IReadOnlyDictionary<string, object?> Fields)> ActionLog { get; } = [];
        public void Packet(in PlaytestPacketRecord record) { }
        public void Action(string kind, IReadOnlyDictionary<string, object?> fields) => ActionLog.Add((kind, fields));
        public void EntitySpawn(int entityKey, int datId, string? name) { }
    }
}
