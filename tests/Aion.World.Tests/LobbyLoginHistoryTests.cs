using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LobbyLoginHistoryTests
{
    const long Now = 4_000_000;

    [Fact]
    public async Task FlushDeferredPersistenceAsync_while_in_world_records_logout()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        Assert.True(session.InWorld);
        await session.FlushDeferredPersistenceAsync(Now + 1000, default);
        Assert.Equal(1, store.LogoutRecordCount);
    }

    [Fact]
    public async Task Logout_request_records_logout_once_via_flush()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await session.HandleAsync(PacketWriter.Opcode(0x3630).ToArray(), Now + 2000);
        Assert.Equal(1, store.LogoutRecordCount);
    }

    [Fact]
    public async Task Return_to_lobby_records_logout_via_flush()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await session.HandleAsync(PacketWriter.Opcode(0x3625).ToArray(), Now + 3000);
        Assert.Equal(1, store.LogoutRecordCount);
        Assert.False(session.InWorld);
    }
}
