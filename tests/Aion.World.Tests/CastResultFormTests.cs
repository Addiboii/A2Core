using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public class CastResultFormTests
{
    [Fact]
    public void LiveRangerCastGetsNineByteAccept()
    {
        var reply = GamePackets.CastResult(14020000);
        Assert.Equal(Convert.FromHexString("01380000A0EDD50000"), reply);
        Assert.Equal(0, GameSession.CastResultCode(reply));
        Assert.Equal(1, GameSession.CastResultCode(GamePackets.CastResult(14020000, 1)));
        // The old flags-first accept still reads as accepted by the server's own readers.
        Assert.Equal(0, GameSession.CastResultCode(GamePackets.CastReply(true, 14020000, 0)));
    }
}
