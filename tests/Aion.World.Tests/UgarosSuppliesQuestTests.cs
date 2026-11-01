using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1102070 step 7 / EQ1820001: five supply boxes (E_L1_Garrison_H1_Box_01) inside dungeon 153001 must count on the map event
/// and complete the exploration ClearMapEvent without looping the instance gate.
/// </summary>
public sealed class UgarosSuppliesQuestTests
{
    const long Now = 1_760_100_100_000;
    const int DungeonId = 153001;
    const string MapEvent = "World_L1_GarrisonTerritory_H1_MapEvent";

    static async Task<GameSession> EnterUgarosAsync(FakeCharacterStore store, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 13, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1102070", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102070", default, 7);
        await session.QuestCommandAsync("QuestAdd", "EQ1820001", default);
        var moved = await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, DungeonId, now + 100);
        Assert.NotEmpty(moved);
        Assert.Equal(DungeonId, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, MapEvent, now + 200);
        Assert.NotNull(run);
        return session;
    }

    static int SupplyBoxIndex(int mapId = DungeonId) =>
        WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i))
            .First(x => string.Equals(x.o.EnvName, "E_L1_Garrison_H1_Box_01", StringComparison.OrdinalIgnoreCase)).i;

    static async Task UseSupplyBoxAsync(GameSession session, int index, long now)
    {
        var o = WorldObjects.ObjectsOn(session.MapId ?? DungeonId)[index];
        await MapEventScenarioHelpers.SyncPositionAsync(session, o.At, now);
        var key = WorldObjects.FirstKey + index;
        _ = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), now + 10);
        await session.TickAsync(now + o.InteractTime + 80);
    }

    [Fact]
    public void Supply_boxes_use_env_name_not_spawner_name()
    {
        Assert.True(SupplyBoxIndex() >= 0);
        var o = WorldObjects.ObjectsOn(DungeonId)[SupplyBoxIndex()];
        Assert.Equal("E_L1_H1_TargetBox", o.Name);
        Assert.Equal("E_L1_Garrison_H1_Box_01", o.EnvName);
        Assert.True(MapEventRun.ReferencesUseEnvObj(MapEventTables.Embedded.Event(MapEvent)!, o.EnvName!));
        Assert.False(MapEventRun.ReferencesUseEnvObj(MapEventTables.Embedded.Event(MapEvent)!, o.Name));
    }

    [Fact]
    public async Task Using_supply_boxes_advances_map_event_UseEnvObj_goal()
    {
        var store = new FakeCharacterStore();
        var session = await EnterUgarosAsync(store, Now);
        var run = session.MapEventRuns.Single(r => r.Name == MapEvent);
        Assert.Equal(0, run.Goals[8]); // objective id 9 is index 8 in step 1

        var index = SupplyBoxIndex();
        Assert.True(index >= 0);
        await UseSupplyBoxAsync(session, index, Now + 500);
        run = session.MapEventRuns.Single(r => r.Name == MapEvent);
        var supplyIdx = run.Step!.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj" && o.Names.Contains("E_L1_Garrison_H1_Box_01"));
        Assert.True(supplyIdx >= 0 && run.Goals[supplyIdx] >= 1, $"expected supply UseEnvObj progress, goals=[{string.Join(',', run.Goals)}]");
    }

    [Fact]
    public async Task Five_supply_boxes_complete_exploration_and_hero_ClearQuest()
    {
        var store = new FakeCharacterStore();
        var session = await EnterUgarosAsync(store, Now + 10_000);
        var objects = WorldObjects.ObjectsOn(DungeonId);
        var boxIndices = objects.Select((o, i) => (o, i))
            .Where(x => string.Equals(x.o.EnvName, "E_L1_Garrison_H1_Box_01", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .Take(5)
            .ToList();
        Assert.Equal(5, boxIndices.Count);

        var t = Now + 10_500;
        foreach (var bi in boxIndices)
        {
            await UseSupplyBoxAsync(session, bi, t);
            t += 5000;
        }

        var run = session.MapEventRuns.Single(r => r.Name == MapEvent);
        Assert.True(run.StepOrder > 1, $"five supplies should finish map event step 1 (step={run.StepOrder}, goals=[{string.Join(',', run.Goals)}])");

        ((IMapEventHost)session).DungeonClear(run);
        await session.TickAsync(t + 5000);

        Assert.Equal(QuestLog.Complete, session.Quests!.Get("EQ1820001")!.Status);
        Assert.True(session.Quests!.Get("HQ1102070")!.Step >= 8,
            $"HQ should pass ClearQuest step 7 after EQ completes (step={session.Quests!.Get("HQ1102070")!.Step})");
    }

    [Fact]
    public async Task World_entry_recheck_advances_HQ_when_exploration_already_complete()
    {
        var store = new FakeCharacterStore();
        var data = GameSessionTestHelpers.LiveGameData;
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 13, WorldId = store.WorldId, TrainingDummy = false, LevelCap = data.MaxLevel },
            new Random(21))
        { Store = store };
        var book = QuestBook.Embedded;
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now + 50_000);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now + 50_010);
        store.Quests.Add(QuestLog.New("HQ1102070", book.Find("HQ1102070")!) with { Uid = 501, Step = 7 });
        store.Quests.Add(QuestLog.New("EQ1820001", book.Find("EQ1820001")!) with { Uid = 502, Status = QuestLog.Complete, Step = 1, Goals = [1, 0, 0, 0, 0, 0] });
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 50_020);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50_030);
        Assert.True(session.Quests!.Get("HQ1102070")!.Step >= 8,
            $"login Recheck should heal HQ stuck on step 7 (step={session.Quests!.Get("HQ1102070")!.Step})");
    }

    [Fact]
    public async Task Garrison_title_reward_item_grants_title_on_use()
    {
        const int TitleItem = 532560034;
        const int TitleId = 12020001;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 60_000, level: 13);
        var wire = store.Items.Count == 0 ? 8000L : store.Items.Max(i => i.WireId) + 1;
        store.Items.Add(FakeCharacterStore.BagItem(TitleItem, 1, wire));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60_100);
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(wire).ToArray(), Now + 60_200);
        Assert.True(GameSessionTestHelpers.HasOpcode(replies, RidePackets.ItemUseRs));
        Assert.Equal(0, BitConverter.ToUInt16(replies.First(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs), 2));
        Assert.Contains(TitleId, session.OwnedTitles);
    }

    [Fact]
    public async Task Hero_without_exploration_quest_enters_door_and_counts_supplies()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 20_000, race: 1, level: 13, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1102070", default);
        await session.QuestCommandAsync("QuestSetStep", "HQ1102070", default, 7);
        Assert.Null(session.Quests!.Get("EQ1820001"));

        var door = WorldObjects.ObjectsOn(1010).Select((o, i) => (o, i)).Single(x => x.o.Name == "E_L1_UgarosInsDoor_01");
        session.Teleport(1010, door.o.At with { X = door.o.At.X + 40 }, Now + 20_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_110);
        var key = WorldObjects.FirstKey + door.i;
        _ = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), Now + 20_200);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_210);
        Assert.Equal(DungeonId, session.MapId);

        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, MapEvent, Now + 20_300);
        Assert.NotNull(run);
        var index = SupplyBoxIndex();
        await UseSupplyBoxAsync(session, index, Now + 20_500);
        run = session.MapEventRuns.Single(r => r.Name == MapEvent);
        var supplyIdx = run.Step!.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj");
        Assert.True(run.Goals[supplyIdx] >= 1, "supply UseEnvObj should count when only hero ClearQuest gate is open");
    }

    [Fact]
    public async Task Leaving_instance_does_not_reset_exploration_quest_progress()
    {
        var store = new FakeCharacterStore();
        var session = await EnterUgarosAsync(store, Now + 30_000);
        await UseSupplyBoxAsync(session, SupplyBoxIndex(), Now + 30_500);
        var eqBefore = session.Quests!.Get("EQ1820001")!;
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), Now + 35_000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 35_010);
        Assert.Equal(0, session.InstanceKey);
        var eqAfter = session.Quests!.Get("EQ1820001")!;
        Assert.Equal(eqBefore.Status, eqAfter.Status);
        Assert.Equal(eqBefore.Step, eqAfter.Step);
        Assert.Equal(eqBefore.Goals, eqAfter.Goals);
        Assert.Equal(7, session.Quests!.Get("HQ1102070")!.Step);
    }
}