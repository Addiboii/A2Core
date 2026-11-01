using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Quest dungeon 210002 (Dungeon_L_0002) entered from Verteron 1010: relog restores instanceReturn from the DB,
/// and clearing the dungeon returns to the field entry (not race home 1000).
/// </summary>
public sealed class QuestDungeonRelogReturnTests
{
    const long Now = 1_760_200_000_000;
    const int DungeonId = 210002;
    const int FieldMap = 1010;
    const string MapEvent = "Dungeon_L_0002_MapEvent";

    static Vector3d EtheriaPortalAt =>
        WorldObjects.ObjectsOn(FieldMap).Single(o => o.Name == "From_EllunRiverMarsh_To_EtheriaRift").At;

    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    static async Task<GameSession> ReselectAsync(FakeCharacterStore store, long now, long wireId)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 13, WorldId = store.WorldId, TrainingDummy = false, LevelCap = data.MaxLevel },
            new Random(31))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now + 10);
        await session.HandleAsync(Loaded(), now + 20);
        return session;
    }

    [Fact]
    public async Task Relog_inside_210002_then_clear_returns_to_1010_entry()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 13, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1102100", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102100", default, 3);

        var entryAt = EtheriaPortalAt;
        session.Teleport(FieldMap, entryAt, Now + 100);
        await session.HandleAsync(Loaded(), Now + 110);
        Assert.NotEmpty(session.EnterDungeonById(DungeonId, Now + 200));
        await session.HandleAsync(Loaded(), Now + 210);
        Assert.Equal(DungeonId, session.MapId);

        await session.CheckpointAsync(Now + 300, force: true);
        Assert.Equal(FieldMap, store.States[0].Progress.ReturnMap);
        Assert.Equal(entryAt.X, store.States[0].Progress.ReturnX);

        var wireId = (long)store.States[0].WireId;
        await session.FlushDeferredPersistenceAsync(Now + 400, default);
        await session.HandleAsync(new PacketWriter().U16(0x3625).ToArray(), Now + 500);

        session = await ReselectAsync(store, Now + 600, wireId);
        Assert.Equal(DungeonId, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);

        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, MapEvent, Now + 700);
        Assert.NotNull(run);
        ((IMapEventHost)session).DungeonClear(run!);
        var holdMs = (MapEventTables.Embedded.Dungeon(DungeonId)?.Hold ?? 0) * 1000L;
        await session.TickAsync(Now + 700 + holdMs + GameSession.InstanceExitDelayMs + 500);
        await session.HandleAsync(Loaded(), Now + 800_000);

        Assert.Equal(FieldMap, session.MapId);
        Assert.Equal(0, session.InstanceKey);
        var pos = session.Position!.Value;
        Assert.True(Math.Abs(pos.X - entryAt.X) < 500 && Math.Abs(pos.Y - entryAt.Y) < 500,
            $"expected near portal ({entryAt.X},{entryAt.Y}), got ({pos.X},{pos.Y})");
    }
}
