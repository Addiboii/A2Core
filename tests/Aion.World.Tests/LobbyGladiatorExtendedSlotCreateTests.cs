using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbyGladiatorExtendedSlotCreateTests
{
    const long Now = 7_000_000;
    const byte GladiatorSlot = 5;

    static void SeedFullLobby(FakeCharacterStore store)
    {
        store.ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots;
        for (var slot = 0; slot < GameSession.DefaultLobbySlots; slot++)
        {
            var wire = 200 + slot;
            store.States.Add(new CharacterState(
                new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, $"Hero{wire}", 1),
                1, 1, 1, [], new CharacterProgress(10, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })),
                1, WireId: wire, LobbySlot: slot, RegisteredUnixMs: 1_000 + slot));
        }
    }

    static byte[] GladiatorCreate(string name, byte[]? tail = null)
    {
        var packet = new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(GladiatorSlot).U8(0).ToArray();
        return tail is null ? packet : packet.Concat(tail).ToArray();
    }

    static int CreateResult(IReadOnlyList<byte[]> replies) => BitConverter.ToInt32(replies[0], 2);

    [Fact]
    public async Task Gladiator_in_extended_slot_succeeds_after_duplicate_name_retry_with_padding()
    {
        var store = new FakeCharacterStore();
        SeedFullLobby(store);
        store.States.Add(new CharacterState(
            new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, "TakenName", 1),
            1, 1, 1, [], new CharacterProgress(1, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                System.Text.Json.JsonSerializer.SerializeToElement(new { })),
            1, WireId: 999, LobbySlot: null));

        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);

        var duplicate = await session.HandleAsync(GladiatorCreate("TakenName"), Now + 1);
        Assert.Equal(GamePackets.CreateDuplicateName, CreateResult(duplicate));
        Assert.Equal(4, store.States.Count(s => s.LobbySlot is not null));

        var retryPadding = await session.HandleAsync(GladiatorCreate("NewGlad", [0, 0]), Now + 2);
        Assert.Equal(0, CreateResult(retryPadding));
        Assert.Equal(6, store.States.Count);
        var created = store.States.Single(s => s.Summary.Name == "NewGlad");
        Assert.Equal(1, created.CreationId);
        Assert.Equal(GladiatorSlot, created.LobbySlot);
    }

    [Fact]
    public async Task Extended_slot_capacity_is_refreshed_on_create_without_prior_login_notify()
    {
        var store = new FakeCharacterStore();
        store.ExtendedCharacterSlots = 0;
        for (var slot = 0; slot < GameSession.DefaultLobbySlots; slot++)
        {
            var wire = 300 + slot;
            store.States.Add(new CharacterState(
                new CharacterSummary(Guid.NewGuid(), store.AccountId, store.WorldId, $"Hero{wire}", 1),
                1, 1, 1, [], new CharacterProgress(10, 0, 1000, StoredCharacters.PositionJson(Vector3d.Zero),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })),
                1, WireId: wire, LobbySlot: slot, RegisteredUnixMs: 1_000 + slot));
        }
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(10)) { Store = store };

        var locked = await session.HandleAsync(GladiatorCreate("SlotFive"), Now);
        Assert.Equal(GamePackets.CreateInvalidSlot, CreateResult(locked));

        store.ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots;
        var ok = await session.HandleAsync(GladiatorCreate("SlotFive"), Now + 1);
        Assert.Equal(0, CreateResult(ok));
        Assert.Equal(GladiatorSlot, store.States.Single(s => s.Summary.Name == "SlotFive").LobbySlot);
    }
}
