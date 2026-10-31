using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 0x922e / 0x922d carry the step as its place within the stage. Live 2026-10-30: HQ003's stage 2 (steps 3, 4, 5) sent as
/// step 3 showed the stage's third step ("Check the Prison Compound Map") while the server waited on the trigger of step 3.
/// </summary>
public sealed class MapEventWireStepTests
{
    const long Now = 1_760_004_000_000;

    [Fact]
    public async Task Second_stage_steps_go_out_by_their_place_in_the_stage()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), Now, race: 2, level: 8, trainingDummy: false);
        var definition = MapEventTables.Embedded.Event("World_D_Starter_HQ003_MapEvent")!;
        var run = new MapEventRun(definition, 1, (IMapEventHost)session, Now);
        run.Start(Now);
        Assert.Equal((1, 1, 1), (run.StageOrder, run.StepOrder, run.WireStep));
        run.PositionAt(name => name == "World_D_Starter_HQ003_TriggerActor_03", _ => false, Now + 1000);
        Assert.Equal((1, 2, 2), (run.StageOrder, run.StepOrder, run.WireStep));
        run.Killed(2590125, "M_D_Starter_HQ003_DracoWar_01", true, true, Now + 2000);
        run.Killed(2590126, "M_D_Starter_HQ003_DracoArc_01", true, false, Now + 2001);
        for (var t = 1; t <= 5; t++) run.Tick(Now + 2000 + t * 1000);
        Assert.Equal((2, 3, 1), (run.StageOrder, run.StepOrder, run.WireStep));
    }
}