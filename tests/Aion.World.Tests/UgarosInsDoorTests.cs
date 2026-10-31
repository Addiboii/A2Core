using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1102070 step 7 / EQ1820001 Ugaros Thieves Campsite (dungeon 153001): the visible door is E_L1_UgarosInsDoor_01 on Map.dat
/// overlay 800002 while the player stays on map 1010; entry must not fail silently when the hero step waits on EQ1820001.
/// </summary>
public sealed class UgarosInsDoorTests
{
    const long Now = 1_760_100_000_000;
    const int DungeonId = 153001;
    static readonly Vector3d DoorAt = new(-52445.1, -74172.8, 11338.4);

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    [Fact]
    public void Overlay_ins_door_is_on_field_map_1010_object_list()
    {
        Assert.Contains(WorldObjects.ObjectsOn(1010), o => o.Name == "E_L1_UgarosInsDoor_01" && o.Portal is [DungeonId]);
    }

    [Fact]
    public async Task Hero_step_waiting_on_exploration_quest_enters_through_ins_door()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 13, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1102070", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102070", default, 7);
        var door = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_L1_UgarosInsDoor_01");
        session.Teleport(1010, DoorAt with { X = DoorAt.X + 40 }, Now + 10);
        await session.HandleAsync(Loaded(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(DoorAt with { X = DoorAt.X + 40 }), Now + 400);
        var used = await session.HandleAsync(Use(WorldObjects.FirstKey + door.i), Now + 800);
        Assert.Contains(used, p => p.Length > 6 && p[0] == 0x21 && p[1] == 0x36 && BitConverter.ToInt32(p, 6) == DungeonId);
        await session.HandleAsync(Loaded(), Now + 810);
        Assert.Equal(DungeonId, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
    }

    [Fact]
    public async Task Exploration_quest_step_reopens_ins_door_after_leaving()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 13, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "EQ1820001", default);
        var door = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_L1_UgarosInsDoor_01");
        session.Teleport(1010, DoorAt with { X = DoorAt.X + 40 }, Now + 10);
        await session.HandleAsync(Loaded(), Now + 20);
        await session.HandleAsync(Use(WorldObjects.FirstKey + door.i), Now + 800);
        await session.HandleAsync(Loaded(), Now + 810);
        Assert.Equal(DungeonId, session.MapId);
        session.Teleport(1010, DoorAt with { X = DoorAt.X + 40 }, Now + 2000);
        await session.HandleAsync(Loaded(), Now + 2010);
        var again = await session.HandleAsync(Use(WorldObjects.FirstKey + door.i), Now + 2400);
        Assert.Contains(again, p => p.Length > 6 && p[0] == 0x21 && p[1] == 0x36 && BitConverter.ToInt32(p, 6) == DungeonId);
    }
}
