using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>HQ1101050 "Drakana Heist": Nania's walk-in layer World_L_Starter_HQ004 (map 100013) played without dev fallbacks.</summary>
public sealed class Hq1101050DrakanaHeistTests(ITestOutputHelper output)
{
    const long Now = 1_760_090_100_000;
    const string Event = "World_L_Starter_HQ004_MapEvent";

    /// <summary>HQ1101040 turned in; HQ1101050 accepted from Nania (N_L0_Nania_04) by the client's accept, not a dev command.</summary>
    static async Task<GameSession> AcceptedAsync(FakeCharacterStore store)
    {
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 8, trainingDummy: false);
        Assert.Null(session.QuestAcceptBlockReason("HQ1101050"));
        var nania = session.Targets!.Values.Single(n => n.NpcId == 2931088);
        await MapEventScenarioHelpers.SyncPositionAsync(session, nania.Position with { X = nania.Position.X + 100 }, Now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x8d0a).I32(1101050).I32(nania.Key).ToArray(), Now + 8);
        Assert.Equal(1, session.Quests!.Get("HQ1101050")!.Step);
        return session;
    }

    [Fact]
    public async Task Quest_list_at_world_entry_carries_the_turned_in_HQ1101040_and_the_open_HQ1101050()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Incomplete, 1, new int[6]));
        var data = GameSessionTestHelpers.LiveGameData;
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 8, WorldId = store.WorldId, LevelCap = data.MaxLevel }, new Random(21))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var entry = new List<byte[]>();
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now));
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10));

        var list = Assert.Single(entry, p => GameSessionTestHelpers.Op(p) == 0x8d09);
        static bool Carries(byte[] packet, int questId, byte status) =>
            packet.AsSpan().IndexOf([.. BitConverter.GetBytes(questId), status]) >= 0;
        Assert.True(Carries(list, 1101040, (byte)QuestLog.Rewarded), "0x8d09 lacks HQ1101040 as rewarded (the client would not offer HQ1101050)");
        Assert.True(Carries(list, 1101050, (byte)QuestLog.Incomplete), "0x8d09 lacks the open HQ1101050");
    }

    static async Task WalkIntoLayerAsync(GameSession session, long now)
    {
        var entry = MapEventTables.Embedded.EntriesOn(1000).Single(e => e.Dungeon == MapEventTables.Embedded.DungeonIdOfMap(100013));
        var inside = new Vector3d(entry.Center[0], entry.Center[1], entry.Center[2]);
        var outside = inside with { X = inside.X - 8200 };
        session.Teleport(1000, outside, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        var t = now + 100;
        for (var step = 0; step <= 41 && session.MapId == 1000; step++, t += 500)
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(outside with { X = outside.X + step * 200 }), t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 10);
    }

    [Fact]
    public async Task Walking_into_the_depot_opens_the_layer_and_starts_the_event()
    {
        var session = await AcceptedAsync(new FakeCharacterStore());
        await WalkIntoLayerAsync(session, Now + 10);
        Assert.Equal(100013, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, Event, Now + 600, attempts: 24);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task Event_plays_to_DungeonClear_and_Kalid_rewards_and_offers_HQ1101070()
    {
        var store = new FakeCharacterStore();
        var session = await AcceptedAsync(store);
        session.NoCool = true;
        await WalkIntoLayerAsync(session, Now + 10);
        Assert.Equal(100013, session.MapId);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, Event, Now + 600, attempts: 24);
        Assert.NotNull(run);

        var finished = await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run!, Now + 1000, maxSeconds: 400);
        foreach (var note in session.MapEventNotes) output.WriteLine(note);
        Assert.True(finished, $"stuck at step {run!.StepOrder} status={run.Status} goals={string.Join(',', run.Goals)} open={string.Join(',', run.Open.Select(o => o.Objective.Type + ":" + o.Objective.Value(0)))}");

        // The closing Delay + MapExit; the harness player may die to the last bombs and revive inside the layer first.
        for (var i = 0; i < 10; i++) await session.TickAsync(Now + 2_000_000 + i * 1000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2_012_000);
        for (var i = 0; i < 6; i++) await session.TickAsync(Now + 2_013_000 + i * 1000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 2_020_000);

        var q = session.Quests!.Get("HQ1101050")!;
        Assert.Equal(QuestLog.Complete, q.Status);
        Assert.Equal(1000, session.MapId);
        Assert.Equal(0, session.InstanceKey);

        var kalid = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2931030);
        Assert.NotNull(kalid);
        await MapEventScenarioHelpers.SyncPositionAsync(session, kalid!.Position with { X = kalid.Position.X + 100 }, Now + 2_030_000);
        long Count(int item) => store.Items.Where(i => i.TemplateId == item && i.Location == "bag").Sum(i => i.Quantity);
        var (kina, potions, gear) = (Count(GameSession.KinaItemId), Count(930100023), Count(115060026));
        await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)q.Uid).I32(-1).ToArray(), Now + 2_031_000);
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("HQ1101050")!.Status);
        Assert.Equal(kina + 2000, Count(GameSession.KinaItemId));
        Assert.Equal(potions + 5, Count(930100023));
        Assert.Equal(gear + 1, Count(115060026));
        Assert.True(session.CanAcceptQuest("HQ1101070"));
    }
}
