using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionWireTests
{
    static readonly byte[] PointUpdateZeroUsed =
    [
        0x2e, 0xe2, 0x05,
        0x01, 0x00, 0x00,
        0x02, 0x00, 0x00,
        0x03, 0x00, 0x00,
        0x04, 0x00, 0x00,
        0x05, 0x00, 0x00,
    ];

    [Fact]
    public void PointUpdate_nt_is_eighteen_bytes_with_u16_used_values()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions(), Random.Shared);
        session.ReloadDaevanionForTests(new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int>()));
        Assert.Equal(PointUpdateZeroUsed, session.DaevanionPointUpdateWire());

        session.ReloadDaevanionForTests(new DaevanionProgress([], new Dictionary<int, int> { [1] = 101 }, new Dictionary<int, int>()));
        var spent = session.DaevanionPointUpdateWire();
        Assert.Equal(18, spent.Length);
        Assert.Equal((ushort)101, BitConverter.ToUInt16(spent, 4));
    }

    [Fact]
    public async Task Open_node_matches_official_order_and_skips_open_node_nt()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 12, WorldId = 1204 }, new Random(3))
        {
            Store = store,
        };
        const long now = 1_760_001_000_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 12 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 5 });
        session.ReloadDaevanionForTests(store.Daevanion);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        var startNode = tables.NodeOf(start)!;
        var neighborId = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));

        var replies = await session.HandleAsync(PacketWriter.Opcode(GameSession.DaevanionOpenNodeRq).I32(neighborId).ToArray(), now + 1);

        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeNt);
        var order = replies.Select(GameSessionTestHelpers.Op).Where(op => op is GameSession.DaevanionPointUpdateNt or 0x364a or 0x561d
            or GameSession.DaevanionOpenNodeRs or GameSession.AchievementCountUpdateNt).ToList();
        Assert.Equal(
        [
            GameSession.DaevanionPointUpdateNt,
            0x364a,
            0x561d,
            GameSession.DaevanionOpenNodeRs,
            GameSession.AchievementCountUpdateNt,
        ], order);

        var point = replies[0];
        Assert.Equal(PointUpdateZeroUsed[..2], point[..2]);
        Assert.Equal((byte)5, point[2]);
        Assert.Equal((byte)1, point[3]);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(point, 4));

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRs);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(neighborId, BitConverter.ToInt32(rs, 4));
    }
}
