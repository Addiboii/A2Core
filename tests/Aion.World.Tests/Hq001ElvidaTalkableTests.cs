using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class Hq001ElvidaTalkableTests
{
    const long Now = 1_760_006_000_000;

    [Fact]
    public async Task Step10_prepares_Elvida_and_proximity_fallback_finishes_event()
    {
        var store = new FakeCharacterStore();
        var data = GameData.Embedded;
        var shared = new SharedWorld(data);
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 3, WorldId = store.WorldId, TrainingDummy = true, MapEventAskNpcProximityFallback = true },
            new Random(31))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ProxElv").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now + 5);
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

        var played = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 300, skipAskNpcAtSteps: new HashSet<int> { 10 });
        Assert.False(played);
        Assert.Equal(10, run!.StepOrder);
        Assert.Contains(run.Open, x => x.Objective.Type == "AskNpc");

        var elvida = session.Targets!.Values.First(n => n.NpcId == 2950867);
        Assert.Equal(4, elvida.Relationship);
        Assert.Contains(session.MapEventNotes, n => n.Contains("talk-ready") && n.Contains("Elvida"));
        Assert.Contains(session.MapEventNotes, n => n.Contains("wire AskNpc") && n.Contains("0x922e"));

        await MapEventScenarioHelpers.SyncPositionAsync(session, elvida.Position, Now + 400_000);
        for (var i = 0; i < 6; i++)
            await session.TickAsync(Now + 401_000 + i * 600);

        Assert.Equal(MapEventRun.Complete, run.Status);
    }

    [Fact]
    public async Task Step10_lock_on_interact_fallback_credits_AskNpc_when_enabled()
    {
        var store = new FakeCharacterStore();
        var data = GameData.Embedded;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions
            {
                World = shared,
                CharacterLevel = 3,
                WorldId = store.WorldId,
                TrainingDummy = true,
                MapEventAskNpcProximityFallback = true,
            },
            new Random(32))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now + 50_000);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("LockElv").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now + 50_010);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 50_020);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50_030);

        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 50_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 50_120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 50_200);
        Assert.NotNull(run);
        _ = await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run!, Now + 50_300, skipAskNpcAtSteps: new HashSet<int> { 10 });
        Assert.Equal(10, run!.StepOrder);

        var elvida = session.Targets!.Values.First(n => n.NpcId == 2950867);
        await session.HandleAsync(new PacketWriter().U16(GameSession.LockOnTargetRq).U8(0).Varint((uint)elvida.Key).ToArray(), Now + 450_000);
        for (var i = 0; i < 4; i++)
            await session.TickAsync(Now + 451_000 + i * 500);
        Assert.Equal(MapEventRun.Complete, run.Status);
    }
}
