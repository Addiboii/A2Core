using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Instance layer map events must not join on map load unless the owning quest step (ClearMapEvent / IncompleteQuestStep) is active.
/// </summary>
public sealed class MapEventQuestStartGateTests
{
    const long Now = 1_760_003_300_000;

    static async Task AdvanceToStepAsync(GameSession session, string code, int step)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 12 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete, Step: var s } && s < step; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", code, default);
        Assert.True(session.Quests!.Get(code)!.Step >= step, $"expected {code} step>={step}");
    }

    static async Task LoadMapAsync(GameSession session, long now)
    {
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.TickAsync(now + 30);
    }

    static bool HasEventStartNote(GameSession session, string eventName) =>
        session.MapEventNotes.Any(n => n.Contains("event start", StringComparison.OrdinalIgnoreCase)
            && n.Contains(eventName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Starter_layer_events_export_IncompleteQuestStep_start_conditions()
    {
        var l2 = MapEventTables.Embedded.Event("World_D_Starter_Layer_2_MapEvent")!;
        var l3 = MapEventTables.Embedded.Event("World_D_Starter_Layer_3_MapEvent")!;
        Assert.Contains(l2.Start.SelectMany(g => g), c => c.Type == "IncompleteQuestStep" && c.Value(0) == "210109001");
        Assert.Contains(l3.Start.SelectMany(g => g), c => c.Type == "IncompleteQuestStep" && c.Value(0) == "210110001");
        Assert.True(MapEventTables.Embedded.Points(110006).ContainsKey("D_Starter_Layer_3_StartPoint"));
    }

    [Fact]
    public async Task Layer_3_does_not_start_on_load_when_only_HQ2101030_Shadow_Forest_step_is_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        await AdvanceToStepAsync(session, "HQ2101030", 3);

        var at = GameSessionTestHelpers.MapStartOrDefault(110006);
        session.Teleport(110006, at, Now + 200);
        await LoadMapAsync(session, Now + 260);

        Assert.Null(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "World_D_Starter_Layer_3_MapEvent", StringComparison.OrdinalIgnoreCase)));
        Assert.False(HasEventStartNote(session, "World_D_Starter_Layer_3_MapEvent"));
    }

    [Fact]
    public async Task Layer_3_starts_on_load_when_HQ2101100_ClearMapEvent_step_is_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now + 10_000);
        await AdvanceToStepAsync(session, "HQ2101100", 1);

        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 110006, Now + 10_200);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_Layer_3_MapEvent", Now + 10_300, attempts: 16);
        Assert.NotNull(run);
        Assert.DoesNotContain(session.MapEventNotes, n => n.Contains("PCTeleport D_Starter_Layer_3_StartPoint: no such point"));
    }

    [Fact]
    public async Task Layer_2_does_not_start_without_HQ2101090_step_1()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now + 20_000);
        await AdvanceToStepAsync(session, "HQ2101030", 3);

        session.Teleport(110005, GameSessionTestHelpers.MapStartOrDefault(110005), Now + 20_200);
        await LoadMapAsync(session, Now + 20_260);

        Assert.Null(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "World_D_Starter_Layer_2_MapEvent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Layer_2_starts_on_load_when_HQ2101090_ClearMapEvent_step_is_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now + 30_000);
        await AdvanceToStepAsync(session, "HQ2101090", 1);

        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 110005, Now + 30_200);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_Layer_2_MapEvent", Now + 30_300, attempts: 16);
        Assert.NotNull(run);
    }

}
