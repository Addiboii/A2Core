using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Layer doors (EnvObjData EnterInstanceLayer in, MoveMap out). Live 2026-10-30: HQ2101080 "Enter the Storage" put Zakl into
/// 110004 on the spot outside the Guard Storage Door, and the exit door there (Usage MoveMap) did nothing on F.
/// </summary>
public sealed class LayerDoorTests
{
    const long Now = 1_760_004_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    [Fact]
    public void Exit_doors_carry_their_move_map_point()
    {
        var door = WorldObjects.ObjectsOn(110004).Single(o => o.Name == "E_D0_Starter_HQ003_Warehouse_002");
        Assert.Equal("World_D_Starter_HQ003_Exit", door.MoveTo);
        Assert.Contains(WorldObjects.ObjectsOn(111007), o => o.MoveTo == "Tel_BarracksWarehouse_Exit");
    }

    [Fact]
    public async Task Guard_storage_door_enters_inside_and_exit_door_leads_out()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 8, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101080", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ2101080", default, 6);
        var objects = WorldObjects.ObjectsOn(1100);
        var entry = objects.Select((o, i) => (o, i)).Single(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_001");
        var outside = new Vector3d(32244.4, 17603.4, 9297.3);
        session.Teleport(1100, outside, Now + 10);
        await session.HandleAsync(Loaded(), Now + 15);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), Now + 400);
        var entered = await session.HandleAsync(Use(WorldObjects.FirstKey + entry.i), Now + 800);
        // A PCTeleport (reason 2, SeamlessDoorTeleport_heavy_in = 40), not the seamless layer move that left the client outside.
        Assert.Contains(entered, p => p.Length > 14 && p[0] == 0x21 && p[1] == 0x36 && p[^14] == 2 && p[^3] == 40);
        await session.HandleAsync(Loaded(), Now + 810);
        Assert.Equal(110004, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        // Inside, past the door: the layer's respawn point (32900, 17610), not the spot outside.
        Assert.True(session.Position!.Value.X > 32493, $"arrived at {session.Position}");

        var exit = WorldObjects.ObjectsOn(110004).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_002");
        var atDoor = new Vector3d(exit.o.Position[0] + 50, exit.o.Position[1], exit.o.Position[2]);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(atDoor), Now + 1200);
        await session.HandleAsync(Use(WorldObjects.FirstKey + exit.i), Now + 1600);
        await session.HandleAsync(Loaded(), Now + 1610);
        Assert.Equal(1100, session.MapId);
        Assert.Equal(0, session.InstanceKey);
        Assert.InRange(session.Position!.Value.X, 32000, 32200);   // World_D_Starter_HQ003_Exit (32096.6, 17616.2)

        // Live 2026-10-30 14:11:47-49: out at World_D_Starter_HQ003_Exit, the next moves there pulled Zakl straight back
        // into 110004 (the quest step still open). The way out must not re-enter until the player has left the entry area.
        var landed = session.Position!.Value;
        for (var t = 0; t < 6; t++)
        {
            var back = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(new Vector3d(landed.X + t * 20, landed.Y, landed.Z)), Now + 2000 + t * 400);
            Assert.DoesNotContain(back, p => p.Length > 2 && p[0] == 0x21 && p[1] == 0x36);
        }
        // The client still reporting the spot inside the storage (the old seamless exit left it there) must not re-enter either.
        var stale = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(new Vector3d(32900, 17610, 9298.3)), Now + 5000);
        Assert.DoesNotContain(stale, p => p.Length > 2 && p[0] == 0x21 && p[1] == 0x36);
        Assert.Equal(1100, session.MapId);
        Assert.Equal(0, session.InstanceKey);
    }

    [Fact]
    public async Task Exit_door_is_a_door_teleport_out()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 8, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101080", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ2101080", default, 6);
        var entry = WorldObjects.ObjectsOn(1100).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_001");
        var outside = new Vector3d(32244.4, 17603.4, 9297.3);
        session.Teleport(1100, outside, Now + 10);
        await session.HandleAsync(Loaded(), Now + 15);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), Now + 400);
        await session.HandleAsync(Use(WorldObjects.FirstKey + entry.i), Now + 800);
        await session.HandleAsync(Loaded(), Now + 810);
        var exit = WorldObjects.ObjectsOn(110004).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_002");
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(new Vector3d(exit.o.Position[0] + 50, exit.o.Position[1], exit.o.Position[2])), Now + 1200);
        var left = await session.HandleAsync(Use(WorldObjects.FirstKey + exit.i), Now + 1600);
        // PCTeleport reason 2 with SeamlessDoorTeleport_heavy_out (TeleportEffect.dat 41), the counterpart of the way in.
        Assert.Contains(left, p => p.Length > 14 && p[0] == 0x21 && p[1] == 0x36 && p[^14] == 2 && p[^3] == 41);
    }
}
