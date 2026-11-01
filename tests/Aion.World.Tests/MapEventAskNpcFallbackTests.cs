using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventAskNpcFallbackTests
{
    const long Now = 1_760_007_000_000;

    [Fact]
    public void NpcTemplate_gate_companion_Elvida_not_quest_citizens()
    {
        var data = GameData.Embedded;
        Assert.True(data.MapEventAskNpcNeedsProximityFallback(2950867));   // N_D_Starter_HQ001_Elvida, Companion rel 13
        Assert.False(data.MapEventAskNpcNeedsProximityFallback(2951076)); // N_D0_Q_Stellan_01, quest citizen
    }

    [Fact]
    public async Task Elvida_step10_proximity_works_with_default_session_option()
    {
        var store = new FakeCharacterStore();
        var data = GameData.Embedded;
        var shared = new SharedWorld(data);
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 3, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41))
        { Store = store };

        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("DefElv").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now + 5);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 15);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 200);
        Assert.NotNull(run);
        _ = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 300, skipAskNpcAtSteps: new HashSet<int> { 10 });
        Assert.Equal(10, run!.StepOrder);

        var elvida = session.Targets!.Values.First(n => n.NpcId == 2950867);
        await MapEventScenarioHelpers.SyncPositionAsync(session, elvida.Position, Now + 400_000);
        for (var i = 0; i < 6; i++)
            await session.TickAsync(Now + 401_000 + i * 600);

        Assert.Equal(MapEventRun.Complete, run.Status);
    }

    [Fact]
    public async Task Elvida_step10_live_0xe240_completes_when_proximity_fallback_disabled()
    {
        var store = new FakeCharacterStore();
        var data = GameData.Embedded;
        var shared = new SharedWorld(data);
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });
        var session = new GameSession(data,
            new GameSessionOptions
            {
                World = shared,
                CharacterLevel = 3,
                WorldId = store.WorldId,
                TrainingDummy = true,
                MapEventAskNpcProximityFallback = false,
            },
            new Random(42))
        { Store = store };

        await session.HandleAsync(GameSessionTestHelpers.Login(), Now + 60_000);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("LiveElv").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now + 60_010);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 60_020);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60_030);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);

        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 60_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 60_120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 60_200);
        Assert.NotNull(run);

        var scriptPackets = new List<byte[]>();
        var finished = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 60_300, scriptPackets, liveAskNpc: true);
        Assert.True(finished && run!.Status == MapEventRun.Complete,
            $"0xe240 path should finish HQ001 without proximity fallback (status={run.Status}, step={run.StepOrder})");
        Assert.DoesNotContain(session.MapEventNotes, n => n.Contains("AskNpc proximity fallback"));
    }
}
