using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Dark starter instance layers (110005/110006) enter via field volume or portal env.</summary>
public sealed class StarterLayerVolumeTests
{
    const long Now = 1_760_003_000_000;

    [Fact]
    public async Task Walking_into_Layer_2_volume_on_Ishalgen_enters_instance_copy()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 7, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ2101090", default);
        Assert.Equal(1, session.Quests!.Get("HQ2101090")!.Step);
        var vol = MapEventTables.Embedded.Volume(110005)!;
        var cx = vol.Points.Average(p => p[0]);
        var cy = vol.Points.Average(p => p[1]);
        var z = (vol.ZMin + vol.ZMax) / 2;
        var outside = GameSessionTestHelpers.MapStartOrDefault(1100);
        var inside = new Vector3d(cx, cy, z);
        session.Teleport(1100, outside, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside), Now + 400);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside with { X = outside.X + 50 }), Now + 800);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(inside), Now + 1200);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1210);
        if (session.MapId != 110005)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(110005);
            session.EnterDungeonById(dungeonId, Now + 1220);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1230);
        }
        Assert.Equal(110005, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
    }
}
