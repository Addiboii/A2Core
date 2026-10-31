using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Map events that name an object by its EnvObjData name, not its spawner. Live 2026-10-30 (Strel, HQ2101090): F on the
/// Prison Compound Door (spawner D_Starter_Layer_2_EnvObj_DoorSmall_1, EnvObjData E_D0_Layer_2_PrisonDoorSmall_001)
/// opened and closed it and "Open the Prison Compound Door" never counted.
/// </summary>
public sealed class MapEventEnvNameTests
{
    const long Now = 1_760_005_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();

    [Fact]
    public void Prison_door_carries_its_envobj_name()
    {
        var door = WorldObjects.ObjectsOn(110005).Single(o => o.Name == "D_Starter_Layer_2_EnvObj_DoorSmall_1");
        Assert.Equal("E_D0_Layer_2_PrisonDoorSmall_001", door.EnvName);
    }

    [Fact]
    public async Task Prison_compound_door_counts_the_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        await session.QuestCommandAsync("QuestAdd", "HQ2101090", default);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 110005, Now + 200);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_Layer_2_MapEvent", Now + 300, attempts: 16);
        Assert.NotNull(run);
        // Step 1 (the corridor trigger and the first Draco warrior) straight on the run.
        var t = Now + 2_000;
        run!.PositionAt(n => n == "D_Starter_Layer_2_Trigger_R1_DracoWay", _ => false, t);
        run.Killed(0, "D_Starter_Layer_2_Single_DracoWar_1", true, false, t);
        await session.TickAsync(t + 500);
        Assert.Equal(2, run!.StepOrder);
        var objects = WorldObjects.ObjectsOn(110005);
        var index = objects.Select((o, i) => (o, i)).Single(x => x.o.Name == "D_Starter_Layer_2_EnvObj_DoorSmall_1").i;
        await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, t + 1_000);
        await session.HandleAsync(Use(WorldObjects.FirstKey + index), t + 2_000);
        for (var tick = 0; tick < 10; tick++) await session.TickAsync(t + 2_000 + objects[index].InteractTime + tick * 500);
        Assert.True(run.StepOrder > 2, $"step {run.StepOrder} goals {string.Join(',', run.Goals)}");
    }
}
