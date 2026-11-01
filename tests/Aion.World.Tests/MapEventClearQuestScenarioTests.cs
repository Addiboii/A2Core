using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Hero ClearMapEvent objectives complete after the map event script runs to <see cref="IMapEventHost.DungeonClear"/>
/// (0x922e join → combat/dialog steps → 0x8d10 quest progress in the same batch as the finishing talk when live).
/// </summary>
public sealed class MapEventClearQuestScenarioTests
{
    const long Now = 1_760_000_200_000;

    static async Task AdvanceToStepAsync(GameSession session, string code, int step)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 12 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete, Step: var s } && s < step; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", code, default);
        Assert.True(session.Quests!.Get(code)!.Step >= step, $"expected {code} step>={step}");
    }

    [Fact]
    public async Task HQ1101012_ClearMapEvent_after_full_World_L_Starter_HQ001_script()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now);
        await AdvanceToStepAsync(session, "HQ1101012", 2);

        var entered = await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2930710, Now + 200);
        Assert.Equal(100010, session.MapId);
        var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 260);
        var all = entered.Concat(load).ToList();
        Assert.True(GameSessionTestHelpers.HasOpcode(all, 0x922e) || session.MapEventRuns.Count > 0,
            "Robin layer should join map event (0x922e)");
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_HQ001_MapEvent", Now + 270);
        Assert.NotNull(run);

        var scriptPackets = new List<byte[]>();
        var finished = await MapEventScenarioHelpers.PlayLightHq001ScriptAsync(session, run!, Now + 300, scriptPackets);
        Assert.True(finished && run!.Status == MapEventRun.Complete, "HQ001 script should reach DungeonClear naturally");
        Assert.True(GameSessionTestHelpers.HasOpcode(scriptPackets, 0x8d10),
            "ClearMapEvent quest progress (0x8d10) should be in the same response batch as the finishing map-event talk");

        var record = session.Quests!.Get("HQ1101012")!;
        Assert.True(record.Status >= QuestLog.Complete || record.Step > 2,
            $"ClearMapEvent on HQ001 layer should finish step 2 (step={record.Step}, status={record.Status})");
    }

    [Fact]
    public async Task HQ1102070_ClearMapEvent_after_World_L_Barracks_03_MapEvent_script()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await AdvanceToStepAsync(session, "HQ1102070", 3);

        var packets = new List<byte[]>();
        session.Teleport(101001, GameSessionTestHelpers.MapStartOrDefault(101001), Now + 400);
        packets.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 450));
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Barracks_03_MapEvent", Now + 460);
        Assert.NotNull(run);
        Assert.True(await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run!, Now + 500, maxSeconds: 60, capture: packets));
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x922e), "barracks layer should join map event");
        Assert.True(GameSessionTestHelpers.HasOpcode(packets, 0x8d10), "Barracks clear should emit quest progress");

        var record = session.Quests!.Get("HQ1102070")!;
        Assert.True(record.Step >= 4 || record.Status >= QuestLog.Complete,
            "Barracks map event clear should advance Illusion Cavaliers past step 3");
    }

    [Fact]
    public async Task HQ2101030_ClearMapEvent_after_World_D_Starter_HQ001_script()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        await AdvanceToStepAsync(session, "HQ2101030", 3);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 480);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 490);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 500);
        Assert.Equal(110002, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 570);
        Assert.NotNull(run);

        var scriptPackets = new List<byte[]>();
        var finished = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 600, scriptPackets);
        Assert.True(finished && run!.Status == MapEventRun.Complete,
            $"Dark HQ001 script should finish (finished={finished}, status={run.Status}, step={run.StepOrder}, stage={run.StageIndex}, goals=[{string.Join(',', run.Goals)}], notes={string.Join(" | ", session.MapEventNotes.Take(3))})");
        Assert.True(GameSessionTestHelpers.HasOpcode(scriptPackets, 0x8d10), "Dark HQ001 clear should emit 0x8d10 in script batch");

        var record = session.Quests!.Get("HQ2101030")!;
        Assert.True(record.Step >= 4 || record.Status >= QuestLog.Complete,
            $"Dark Shadow Forest ClearMapEvent should advance past step 3 (step={record.Step}, status={record.Status})");
    }

    [Fact]
    public async Task HQ2101030_ClearMapEvent_when_Elvida_talk_uses_live_0xe240()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now + 900_000);
        await AdvanceToStepAsync(session, "HQ2101030", 3);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 900_480);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 900_490);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 900_500);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 900_570);
        Assert.NotNull(run);

        var scriptPackets = new List<byte[]>();
        var finished = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 900_600, scriptPackets, liveAskNpc: true);
        Assert.True(finished && run!.Status == MapEventRun.Complete,
            $"Dark HQ001 should finish when Elvida step uses 0xe240 (status={run.Status}, step={run.StepOrder}, notes={string.Join(" | ", session.MapEventNotes.TakeLast(5))})");
        Assert.Contains(session.MapEventNotes, n => n.Contains("step=10") && n.Contains("AskNpc:N_D_Starter_HQ001_Elvida_01"));
        Assert.True(GameSessionTestHelpers.HasOpcode(scriptPackets, 0x8d10), "ClearMapEvent should emit 0x8d10 when dungeon clears via Elvida talk");

        var record = session.Quests!.Get("HQ2101030")!;
        Assert.True(record.Step >= 4 || record.Status >= QuestLog.Complete,
            $"quest should advance to Maard AskNpc after clear (step={record.Step}, status={record.Status})");
    }
}
