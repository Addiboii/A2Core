using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionLiveOpcodeTests
{
    static byte[] LiveOpenRoute(int boardId, params int[] nodeIds)
    {
        var w = PacketWriter.Opcode(GameSession.DaevanionOpenNodeRqLive).I32(boardId).Varint((uint)nodeIds.Length);
        foreach (var id in nodeIds) w.I32(id);
        return w.ToArray();
    }

    [Fact]
    public async Task Live_e502_twenty_seven_byte_route_opens_neighbor_and_replies_e503()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 17, WorldId = 1204 }, new Random(3))
        {
            Store = store,
        };
        const long now = 1_760_010_000_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 17 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 5 });
        session.ReloadDaevanionForTests(store.Daevanion);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        session.ReloadDaevanionForTests(new DaevanionProgress([start], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 5 }));
        var startNode = tables.NodeOf(start)!;
        var neighborId = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));

        var plain = LiveOpenRoute(boardId, start, neighborId, 0, 0, 0);
        Assert.Equal(27, plain.Length);

        var replies = await session.HandleAsync(plain, now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRsLive);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(neighborId, BitConverter.ToInt32(rs, 4));
        Assert.Contains(neighborId, store.Daevanion!.NodeIds);
        Assert.Equal(1, store.Daevanion.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task Live_e502_rejects_adjacency_violation_with_result_nonzero()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 17, WorldId = 1204 }, new Random(4))
        {
            Store = store,
        };
        const long now = 1_760_010_100_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 17 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        session.ReloadDaevanionForTests(new DaevanionProgress([start], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 5 }));
        var far = tables.NodesOfBoard(boardId).First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && !tables.IsNeighbor(n, tables.NodeOf(start)!));

        var replies = await session.HandleAsync(LiveOpenRoute(boardId, far), now + 1);
        var rs = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRsLive);
        Assert.NotEqual((ushort)0, BitConverter.ToUInt16(rs, 2));
    }

    [Fact]
    public async Task Live_e502_ranger_route_on_board_31_spends_seven_points()
    {
        const int rangerCreation = 5;
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 17, WorldId = 1204 }, new Random(8))
        {
            Store = store,
        };
        const long now = 1_760_010_200_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Archer").U8(1).U32((uint)rangerCreation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 17 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        var tables = DaevanionTables.Embedded;
        const int boardId = 31;
        var start = tables.StartNodeId(boardId);
        Assert.Equal(310113, start);
        Assert.Equal(3, tables.ClassSlot(rangerCreation));
        session.ReloadDaevanionForTests(new DaevanionProgress([start], new Dictionary<int, int>(), new Dictionary<int, int> { [1] = 10 }));

        int[] route = [310098, 310099, 310084, 310069, 310068];
        var plain = LiveOpenRoute(boardId, route);
        var replies = await session.HandleAsync(plain, now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRsLive);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(boardId, BitConverter.ToInt32(rs, 6));
        var reader = new PacketReader(rs) { Offset = 10 };
        var openedCount = reader.Varint();
        Assert.Equal((uint)route.Length, openedCount);
        var openedIds = new List<int>();
        for (var i = 0; i < openedCount; i++) openedIds.Add(reader.I32());
        Assert.Equal(route, openedIds);
        Assert.NotNull(store.Daevanion);
        foreach (var id in route) Assert.Contains(id, store.Daevanion!.NodeIds);
        Assert.Equal(7, store.Daevanion!.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task EnsureStartNodes_prunes_legacy_board_61_start_and_opens_ranger_310113()
    {
        const int rangerCreation = 5;
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 16, WorldId = 1204 }, new Random(9))
        {
            Store = store,
        };
        const long now = 1_760_010_300_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("MigrateArcher").U8(1).U32((uint)rangerCreation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 16 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        store.Daevanion = new DaevanionProgress([610113], new Dictionary<int, int> { [1] = 1 }, new Dictionary<int, int>());
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 11);

        Assert.NotNull(store.Daevanion);
        Assert.Contains(310113, store.Daevanion!.NodeIds);
        Assert.DoesNotContain(610113, store.Daevanion.NodeIds);
        Assert.Equal(0, store.Daevanion.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task Live_e502_level_18_ranger_opens_four_nodes_after_three_spent()
    {
        const int rangerCreation = 5;
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 18, WorldId = 1204 }, new Random(10))
        {
            Store = store,
        };
        const long now = 1_760_010_400_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Lvl18Archer").U8(1).U32((uint)rangerCreation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 18 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        const int boardId = 31;
        var tables = DaevanionTables.Embedded;
        var start = tables.StartNodeId(boardId);
        int[] already = [start, 310097, 310082, 310067];
        session.ReloadDaevanionForTests(new DaevanionProgress(already, new Dictionary<int, int> { [1] = 3 }, new Dictionary<int, int>()));

        int[] route = [310068, 310069, 310084, 310099];
        var replies = await session.HandleAsync(LiveOpenRoute(boardId, route), now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionOpenNodeRsLive);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        foreach (var id in route) Assert.Contains(id, store.Daevanion!.NodeIds);
        Assert.Equal(9, store.Daevanion!.UsedPoints.GetValueOrDefault(1));
    }

    [Fact]
    public async Task Opened_nodes_persist_across_world_reload()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 18, WorldId = 1204 }, new Random(11))
        {
            Store = store,
        };
        const long now = 1_760_010_500_000;
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var created = store.States[0];
        store.States[0] = created with { Progress = created.Progress with { Level = 18 } };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);

        var tables = DaevanionTables.Embedded;
        var boardId = tables.BoardsForCreation(1).First();
        var start = tables.StartNodeId(boardId);
        var startNode = tables.NodeOf(start)!;
        var neighborId = tables.NodesOfBoard(boardId)
            .First(id => tables.NodeOf(id) is { } n && n.Cost == 1 && tables.IsNeighbor(n, startNode));

        await session.HandleAsync(LiveOpenRoute(boardId, start, neighborId, 0, 0, 0), now + 1);
        Assert.Contains(neighborId, store.Daevanion!.NodeIds);

        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 101);

        Assert.Contains(neighborId, store.Daevanion!.NodeIds);
        Assert.Equal(1, store.Daevanion.UsedPoints.GetValueOrDefault(1));
    }
}
