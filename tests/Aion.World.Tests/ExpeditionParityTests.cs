using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ExpeditionParityTests
{
    const long Now = 1_760_500_000_000;

    static List<byte[]> InvokeDie(GameSession session, long now) =>
        (List<byte[]>)typeof(GameSession).GetMethod("Die", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [0, now])!;

    [Fact]
    public async Task Seeded_store_equipment_loads_at_world_entry()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var moved = session.EnterDungeonById(600002, Now + 50);
        Assert.NotEmpty(moved);
    }

    [Fact]
    public void Krao_easy_dat_carries_il_gate_and_timer()
    {
        var room = MapEventTables.Embedded.Dungeon(600002)!;
        Assert.Equal("Party", room.Type);
        Assert.Equal(200, room.ItemLevel);
        Assert.Equal(3600, room.Limit);
        Assert.Equal(600, room.Hold);
    }

    [Fact]
    public async Task Low_item_level_refuses_expedition_entry()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45);
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), Now);
        var moved = session.EnterDungeonById(600002, Now + 100);
        Assert.Empty(moved);
        Assert.Equal(0, session.InstanceKey);
    }

    [Fact]
    public async Task Dungeon_state_at_entry_includes_process_timer()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var moved = session.EnterDungeonById(600002, Now + 50);
        Assert.NotEmpty(moved);
        var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200);
        Assert.True(GameSessionTestHelpers.HasOpcode(load, GameSession.DungeonStateNt));
        var state = load.First(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonStateNt);
        Assert.Equal(GameSession.DungeonStateProcess, state[6]);
        Assert.Equal(3_600_000, BitConverter.ToInt64(state, 15) - BitConverter.ToInt64(state, 7));
    }

    [Fact]
    public async Task Clear_sends_party_dungeon_score_packet()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(600002, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        var run = session.MapEventRuns.FirstOrDefault();
        Assert.NotNull(run);
        ((IMapEventHost)session).DungeonClear(run!);
        var tick = await session.TickAsync(Now + 500);
        Assert.True(GameSessionTestHelpers.HasOpcode(tick, GameSession.PartyDungeonClearNt));
    }

    [Fact]
    public async Task Clear_notifies_every_member_in_the_copy()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, leadStore, "ClearLead", 16);
        var mate = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, mateStore, "ClearMate", 17);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now + 1);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 2);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.PartyDungeonEnterRq).ToArray(), Now + 50);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        var run = lead.MapEventRuns.FirstOrDefault();
        Assert.NotNull(run);
        ((IMapEventHost)lead).DungeonClear(run!);
        var mateTick = await mate.TickAsync(Now + 500);
        Assert.True(GameSessionTestHelpers.HasOpcode(mateTick, GameSession.PartyDungeonClearNt));
        Assert.True(GameSessionTestHelpers.HasOpcode(mateTick, GameSession.DungeonCompleteResultNt));
    }

    [Fact]
    public async Task Death_in_copy_updates_dungeon_death_limit()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(600002, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        Assert.NotNull(session.Copy);
        var died = InvokeDie(session, Now + 200);
        Assert.Contains(died, b => GameSessionTestHelpers.Op(b) == GameSession.DungeonDeathLimitNt && BitConverter.ToInt32(b, 2) == 1);
    }

    [Fact]
    public async Task Expedition_ticket_is_consumed_on_first_entry()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var before = session.EnterDungeonById(600002, Now + 50);
        Assert.NotEmpty(before);
        await session.TickAsync(Now + 60_000);
        Assert.Contains(store.ExpeditionTickets, t => t.TicketId == 4 && t.Count == 1);
    }

    [Fact]
    public async Task Weekly_reset_ignores_previous_period_ticket_usage()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var oldPeriod = ShopTables.Period(2, DateTimeOffset.FromUnixTimeMilliseconds(Now));
        store.ExpeditionTickets.Add((4, oldPeriod, 30));
        var weekLater = Now + 8L * 24 * 60 * 60 * 1000;
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, weekLater, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, weekLater + 10);
        var moved = session.EnterDungeonById(600002, weekLater + 50);
        Assert.NotEmpty(moved);
    }

    [Fact]
    public async Task Party_dungeon_ui_create_then_enter_moves_into_instance()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var create = await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now + 20);
        Assert.True(GameSessionTestHelpers.HasOpcode(create, GameSession.DungeonRoomCreateRs));
        Assert.Equal(0, BitConverter.ToInt32(create.First(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonRoomCreateRs), 2));
        var enter = await session.HandleAsync(new PacketWriter().U16(GameSession.PartyDungeonEnterRq).ToArray(), Now + 30);
        Assert.True(GameSessionTestHelpers.HasOpcode(enter, GameSession.PartyDungeonEnterRs));
        Assert.Equal(0, BitConverter.ToInt32(enter.First(b => GameSessionTestHelpers.Op(b) == GameSession.PartyDungeonEnterRs), 2));
        Assert.NotEqual(0, session.InstanceKey);
    }

    [Fact]
    public async Task Exhausted_tickets_block_new_expedition_entry()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var period = ShopTables.Period(2, DateTimeOffset.FromUnixTimeMilliseconds(Now));
        store.ExpeditionTickets.Add((4, period, 30));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var moved = session.EnterDungeonById(600002, Now + 50);
        Assert.Empty(moved);
    }
}
