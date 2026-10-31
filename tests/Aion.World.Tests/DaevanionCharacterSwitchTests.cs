using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class DaevanionCharacterSwitchTests
{
    const long Now = 1_760_010_000_000;

    static byte[] Create(int creationId, string name) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    static async Task<List<byte[]>> SelectAndEnterAsync(GameSession session, int wireId, long now)
    {
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        return await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 1);
    }

    static List<int> BoardIds(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        var count = reader.Varint();
        var ids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(reader.I32());
            var nodes = reader.Varint();
            for (var j = 0; j < nodes; j++) reader.I32();
        }
        return ids;
    }

    [Fact]
    public async Task World_entry_board_info_follows_selected_creation_not_previous_character()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(7))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(Create(6, "SpiritFirst"), Now);
        await session.HandleAsync(Create(5, "ArcherSecond"), Now);

        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 16 } };
        store.States[1] = store.States[1] with { Progress = store.States[1].Progress with { Level = 11 } };

        var spiritWire = store.States[0].WireId;
        var archerWire = store.States[1].WireId;

        var spiritEntry = await SelectAndEnterAsync(session, spiritWire, Now + 10);
        var spiritBoard = BoardIds(spiritEntry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt));
        Assert.Equal([61], spiritBoard);
        Assert.DoesNotContain(51, spiritBoard);

        var unlock = spiritEntry.Single(p => GameSessionTestHelpers.Op(p) == 0xe257);
        var unlockTypes = new PacketReader(unlock) { Offset = 2 }.Bytes();
        Assert.Contains(GameSession.DaevanionContentsUnlock, unlockTypes);

        var points = spiritEntry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionPointUpdateNt);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(points, 4));

        var archerEntry = await SelectAndEnterAsync(session, archerWire, Now + 20);
        var archerBoard = BoardIds(archerEntry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt));
        Assert.Empty(archerBoard);

        var spiritAgain = await SelectAndEnterAsync(session, spiritWire, Now + 30);
        var againBoard = BoardIds(spiritAgain.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt));
        Assert.Equal(spiritBoard, againBoard);
    }

    [Fact]
    public async Task World_entry_lists_every_level_open_class_board()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { WorldId = store.WorldId }, new Random(9))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(Create(6, "SpiritBoards"), Now);
        store.States[0] = store.States[0] with { Progress = store.States[0].Progress with { Level = 20 } };
        var entry = await SelectAndEnterAsync(session, store.States[0].WireId, Now + 5);
        var boards = BoardIds(entry.Single(p => GameSessionTestHelpers.Op(p) == GameSession.DaevanionBoardInfoNt));
        Assert.Equal([61, 62], boards);
    }
}
