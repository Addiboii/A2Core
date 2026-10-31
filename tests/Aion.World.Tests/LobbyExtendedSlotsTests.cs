using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbyExtendedSlotsTests
{
    const long Now = 5_000_000;

    static byte[] SwapRequest(long wire, int slot) =>
        PacketWriter.Opcode(GameSession.CharSlotSwapRq).Varint(1).I64(wire).U8(slot).ToArray();

    [Fact]
    public async Task Game_login_appends_extended_slot_notify()
    {
        var store = new FakeCharacterStore { ExtendedCharacterSlots = 2 };
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(3)) { Store = store };
        var replies = await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        Assert.Equal(2, replies.Count);
        Assert.Equal(0xffff, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(0x3A16, GameSessionTestHelpers.Op(replies[1]));
        Assert.Equal(2, replies[1][2]);
    }

    [Fact]
    public async Task Locked_slot_four_rejected_until_extensions_unlocked()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var hero = store.States[0];

        var locked = await session.HandleAsync(SwapRequest(hero.WireId, GameSession.DefaultLobbySlots), Now + 1);
        Assert.Single(locked);
        Assert.Equal(GameSession.CharSlotSwapFailed, BitConverter.ToUInt16(locked[0], 2));

        store.ExtendedCharacterSlots = GameSession.MaxExtendedLobbySlots;
        var unlockedSession = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(11)) { Store = store };
        await unlockedSession.HandleAsync(GameSessionTestHelpers.Login(), Now + 2);
        var unlocked = await unlockedSession.HandleAsync(SwapRequest(hero.WireId, GameSession.DefaultLobbySlots), Now + 3);
        Assert.Single(unlocked);
        Assert.Equal(0xe337, GameSessionTestHelpers.Op(unlocked[0]));
        Assert.Equal(0, BitConverter.ToUInt16(unlocked[0], 2));
        Assert.Equal(GameSession.DefaultLobbySlots, store.States[0].LobbySlot);
    }
}
