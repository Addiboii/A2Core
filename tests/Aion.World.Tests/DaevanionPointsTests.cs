using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionPointsTests
{
    const long Now = 1_760_020_000_000;

    static byte[] Create(int creationId, string name) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    static async Task<GameSession> EnterSpiritmasterAsync(FakeCharacterStore store, int level)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(9))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(Create(6, "PointTest"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = level } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        return session;
    }

    [Fact]
    public async Task Level_16_on_nezekan_tier_earns_four_type_1_points()
    {
        var store = new FakeCharacterStore();
        var session = await EnterSpiritmasterAsync(store, 16);
        Assert.Equal(4, session.DaevanionLevelEarnedForTests(1));
        Assert.Equal(4, session.DaevanionAvailableForTests(1));
    }

    [Fact]
    public async Task Open_node_succeeds_at_level_16_without_item_bonus()
    {
        var store = new FakeCharacterStore();
        var session = await EnterSpiritmasterAsync(store, 16);
        var tables = DaevanionTables.Embedded;
        var boardId = 61;
        var start = tables.StartNodeId(boardId);
        var startNode = tables.NodeOf(start)!;
        var neighborId = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));

        var replies = await session.HandleAsync(PacketWriter.Opcode(GameSession.DaevanionOpenNodeRq).I32(neighborId).ToArray(), Now + 2);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRs);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(3, session.DaevanionAvailableForTests(1));
        var point = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionPointUpdateNt);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(point, 4));
    }
}
