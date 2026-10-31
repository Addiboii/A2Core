using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Dark Shadow Forest (HQ2101030): map-event kills/ firewood, ClearMapEvent quest credit, field AskNpc dialog.
/// </summary>
public sealed class Hq2101030ShadowForestTests
{
    const long Now = 1_760_004_000_000;

    [Fact]
    public void Munihelle_forest_layer_is_entered_by_Maard_not_by_walking_in()
    {
        var walkIn = MapEventTables.Embedded.WalkInOn(1100).ToArray();
        Assert.DoesNotContain(walkIn, l => l.LayerMap == 110002);
    }

    [Fact]
    public async Task ClearMapEvent_emits_0x8d10_in_same_batch_as_layer_script_finish()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        Assert.Equal(3, session.Quests!.Get("HQ2101030")!.Step);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 120);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 130);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 200);
        Assert.NotNull(run);

        var scriptPackets = new List<byte[]>();
        var finished = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 300, scriptPackets);
        Assert.True(finished && run!.Status == MapEventRun.Complete);

        Assert.True(GameSessionTestHelpers.HasOpcode(scriptPackets, 0x8d10),
            "ClearMapEvent should emit quest progress in the map-event finish batch");

        var record = session.Quests!.Get("HQ2101030")!;
        Assert.True(record.Step >= 4 || record.Status >= QuestLog.Complete,
            $"expected step 4 AskNpc after HQ001 clear (step={record.Step}, status={record.Status}, goals={string.Join(',', record.Goals)})");
    }

    [Fact]
    public async Task Step4_Maard_dialog_opens_via_e240_after_map_event()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 50_000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 3; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        Assert.Equal(4, session.Quests!.Get("HQ2101030")!.Step);

        if (session.MapId != 1100)
        {
            session.Teleport(1100, GameSessionTestHelpers.MapStartOrDefault(1100), Now + 60_000);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60_010);
        }

        var maard = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2951080);
        Assert.NotNull(maard);

        var dialog = await session.HandleAsync(new PacketWriter().U16(0xe240).Varint(maard!.Key).ToArray(), Now + 60_020);
        Assert.True(dialog.Any(p => GameSessionTestHelpers.Op(p) == 0xe241),
            "Maard should answer StartNpcInteraction (0xe241) on step 4");
        Assert.False(GameSessionTestHelpers.HasOpcode(dialog, 0x8d10),
            "AskNpc talk must not move the step while the dialog plays");
        var finish = await session.HandleAsync(new PacketWriter().U16(0xe242).I32(maard.Key).ToArray(), Now + 60_030);
        Assert.Equal(0xe243, GameSessionTestHelpers.Op(finish[0]));
        Assert.True(GameSessionTestHelpers.HasOpcode(finish, 0x8d10),
            "AskNpc talk should emit quest progress when the dialog ends (0xe242)");

        var after = session.Quests!.Get("HQ2101030")!;
        Assert.True(after.Goals[0] >= 1 || after.Status >= QuestLog.Complete,
            $"Maard talk should credit step 4 (goals={string.Join(',', after.Goals)}, status={after.Status})");
    }

    [Fact]
    public async Task Firewood_not_interactable_before_UseEnvObj_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 250_000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 250_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 250_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 250_120);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 250_130);
        _ = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 250_200);

        var mapId = session.MapId ?? 110002;
        var firewoodIndex = WorldObjects.ObjectsOn(mapId).ToList().FindIndex(o =>
            o.Name.StartsWith("E_D0_FireWood_001", StringComparison.OrdinalIgnoreCase));
        Assert.True(firewoodIndex >= 0);
        var key = WorldObjects.FirstKey + firewoodIndex;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now + 250_300);
        Assert.True(use.Any(p => GameSessionTestHelpers.Op(p) == 0x3637),
            "early firewood use should be refused (0x3637 reply) while step 1 is still open");
    }

    [Fact]
    public async Task QuestSetStep_jumps_to_step_and_emits_0x8d10()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 150_000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        var (message, packets) = await session.QuestCommandAsync("QuestSetStep", "HQ2101030", default, 3);
        Assert.Contains("step 3", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, session.Quests!.Get("HQ2101030")!.Step);
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d10), "QuestSetStep should refresh the tracker");
    }

    [Fact]
    public async Task Hq001_map_event_rejoins_after_leaving_instance_with_step3_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 200_000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 200_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 200_120);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200_130);
        var first = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 200_200);
        Assert.NotNull(first);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 200_300);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200_310);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 200_320);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200_330);
        var second = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 200_400);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Step3_stuck_layer_still_opens_field_NPC_dialog_with_varint_e240()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 100_000, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 100_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 100_120);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100_130);
        _ = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 100_200);

        var pelleir = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2950854);
        if (pelleir is null)
        {
            session.Teleport(1100, GameSessionTestHelpers.MapStartOrDefault(1100), Now + 100_300);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100_310);
            pelleir = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2950854);
        }
        Assert.NotNull(pelleir);

        var dialog = await session.HandleAsync(new PacketWriter().U16(0xe240).Varint(pelleir!.Key).ToArray(), Now + 100_400);
        Assert.True(dialog.Any(p => GameSessionTestHelpers.Op(p) == 0xe241),
            "NPC dialog must not be dropped while ClearMapEvent step is open (empty client reply hangs talk mode)");
    }
}
