using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1102060 "Visions in Smoke" step 2 (EnterSubZone DreamCrossroad_Subzone): quest teleport, dungeon entry, map event, objective credit.
/// Live 2026-10-30: teleport fell through to field map 1010; QuestStepNeedsDungeonMap ignored EnterSubZone, so DreamCrossroad_MapEvent never started (empty instance).
/// </summary>
public sealed class DreamCrossroadQuestTests
{
    const long Now = 1_760_004_100_000;
    const int AsharNpcId = 2970018;
    static readonly Vector3d LobbyTriggerAt = new(0, 635, 77);

    static byte[] QuestTeleportRequest(long uid) => new PacketWriter().U16(0x8d16).I32((int)uid).ToArray();
    static byte[] Loaded() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    static async Task AdvanceToStep2Async(GameSession session)
    {
        await session.QuestCommandAsync("QuestAdd", "HQ1102060", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102060", default);
        var record = session.Quests!.Get("HQ1102060")!;
        Assert.Equal(2, record.Step);
    }

    static async Task<GameSession> InDreamCrossroadWaitingForAsharAsync(FakeCharacterStore store, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 13);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        await session.QuestCommandAsync("QuestComplete", "AQ1602000", default);
        Assert.Equal(QuestLog.Complete, session.Quests!.Get("AQ1602000")!.Status);
        Assert.NotEmpty(session.EnterDungeonById(200001, now + 100));
        await session.HandleAsync(Loaded(), now + 200);
        await session.TickAsync(now + 250);
        Assert.Equal(200001, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        return session;
    }

    static async Task<MapEventRun> LobbyMapEventAsync(GameSession session, long now)
    {
        var run = session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "DreamCrossroad_MapEvent", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(run);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(LobbyTriggerAt), now + 300);
        await session.TickAsync(now + 350);
        return run!;
    }

    [Fact]
    public async Task Quest_teleport_enters_DreamCrossroad_and_credits_EnterSubZone()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 13);
        await AdvanceToStep2Async(session);
        var record = session.Quests!.Get("HQ1102060")!;

        var replies = await session.HandleAsync(QuestTeleportRequest(record.Uid), Now + 100);
        Assert.Equal(0, BitConverter.ToUInt16(replies.First(p => GameSessionTestHelpers.Op(p) == 0x8d17), 2));
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, 0x3621));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);
        await session.TickAsync(Now + 250);

        Assert.Equal(200001, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.NotNull(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "DreamCrossroad_MapEvent", StringComparison.OrdinalIgnoreCase)));

        record = session.Quests!.Get("HQ1102060")!;
        Assert.True(record.Status >= QuestLog.Complete, "EnterSubZone at the dungeon entrance completes step 2");
    }

    [Fact]
    public async Task DreamCrossroad_map_event_starts_when_only_EnterSubZone_step_is_open()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 50_000, race: 1, level: 13);
        await AdvanceToStep2Async(session);

        var moved = session.EnterDungeonById(200001, Now + 50_200);
        Assert.NotEmpty(moved);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50_300);
        await session.TickAsync(Now + 50_350);

        Assert.NotNull(session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, "DreamCrossroad_MapEvent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Map_event_clear_holds_instance_until_Ashar_spawn_when_quest_still_needs_the_map()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadWaitingForAsharAsync(store, Now + 60_000);
        var run = await LobbyMapEventAsync(session, Now + 60_000);
        ((IMapEventHost)session).DungeonClear(run);
        _ = await session.TickAsync(Now + 60_500);
        Assert.Equal(200001, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);
        Assert.DoesNotContain(session.Targets!.Values, t => t.NpcId == AsharNpcId && !t.Dead);
        await session.TickAsync(Now + 62_600);
        Assert.Contains(session.Targets!.Values, t => t.NpcId == AsharNpcId && !t.Dead);
        Assert.Equal(200001, session.MapId);
    }

    [Fact]
    public async Task Rewarding_AQ1602000_in_Dream_Crossroad_leaves_the_instance()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadWaitingForAsharAsync(store, Now + 70_000);
        var record = session.Quests!.Get("AQ1602000")!;
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), Now + 70_300);
        Assert.Contains(reward, p => GameSessionTestHelpers.Op(p) == 0x8d0f);
        if (GameSessionTestHelpers.HasOpcode(reward, 0x3621))
            await session.HandleAsync(Loaded(), Now + 70_350);
        Assert.Equal(0, session.InstanceKey);
        Assert.NotEqual(200001, session.MapId);
    }

    [Fact]
    public async Task Relog_on_Dream_Crossroad_without_quest_need_uses_return_map()
    {
        var store = new FakeCharacterStore();
        var session = await InDreamCrossroadWaitingForAsharAsync(store, Now + 80_000);
        const int fieldMap = 1010;
        var entryAt = new Vector3d(-127874, -111187, 25393);
        session.Teleport(fieldMap, entryAt, Now + 80_100);
        await session.HandleAsync(Loaded(), Now + 80_110);
        session.EnterDungeonById(200001, Now + 80_200);
        await session.HandleAsync(Loaded(), Now + 80_210);
        await session.CheckpointAsync(Now + 80_300, force: true);
        Assert.Equal(fieldMap, store.States[0].Progress.ReturnMap);
        var idx = store.Quests.FindIndex(q => q.Code == "AQ1602000");
        store.Quests[idx] = store.Quests[idx] with { Status = QuestLog.Rewarded };
        var stateIdx = 0;
        store.States[stateIdx] = store.States[stateIdx] with
        {
            Progress = store.States[stateIdx].Progress with { MapId = 200001 }
        };
        var wireId = (long)store.States[0].WireId;
        await session.FlushDeferredPersistenceAsync(Now + 80_400, default);
        await session.HandleAsync(new PacketWriter().U16(0x3625).ToArray(), Now + 80_500);

        var data = GameSessionTestHelpers.LiveGameData;
        var shared = new SharedWorld(data);
        session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 13, WorldId = store.WorldId, TrainingDummy = false, LevelCap = data.MaxLevel },
            new Random(31))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now + 80_600);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 80_610);
        await session.HandleAsync(Loaded(), Now + 80_620);
        Assert.NotEqual(200001, session.MapId);
        Assert.Equal(0, session.InstanceKey);
        Assert.Equal(fieldMap, session.MapId);
    }
}
