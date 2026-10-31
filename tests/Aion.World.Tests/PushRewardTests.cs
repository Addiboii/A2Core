using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class PushRewardTests
{
    const long Now = 1_760_200_000_000;

    [Fact]
    public async Task Retrieve_lists_seeded_gifts_and_take_grants_once()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var list = await session.HandleAsync([0x48, 0xe3], Now);
        var rs = Assert.Single(list);
        Assert.Equal(GameSession.PushRewardInfoRs, GameSessionTestHelpers.Op(rs));
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.True(rs.Length > 12);

        var take = await session.HandleAsync(new PacketWriter().U16(GameSession.TakePushRewardRq).U8(1).I32(1).U8(1).ToArray(), Now);
        Assert.Contains(take, p => GameSessionTestHelpers.Op(p) == 0x561b);
        var tr = take.Last();
        Assert.Equal(GameSession.TakePushRewardRs, GameSessionTestHelpers.Op(tr));
        Assert.Equal(0, BitConverter.ToUInt16(tr, 2));

        var again = await session.HandleAsync(new PacketWriter().U16(GameSession.TakePushRewardRq).U8(1).I32(1).U8(1).ToArray(), Now + 1);
        Assert.Equal(1, BitConverter.ToUInt16(again.Last(), 2));

        var relist = await session.HandleAsync([0x48, 0xe3], Now + 2);
        var body = Assert.Single(relist);
        var count = new PacketReader(body) { Offset = 10 }.Varint();
        Assert.Equal(0U, count);
    }
}
