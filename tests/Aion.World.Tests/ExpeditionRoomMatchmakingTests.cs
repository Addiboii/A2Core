using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ExpeditionRoomMatchmakingTests
{
    const long Now = 1_760_500_000_000;

    [Fact]
    public async Task Public_room_lists_and_accepts_free_join()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var joinStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(joinStore);
        var lead = await EnterExpeditionHero(world, leadStore, "Lead", 3);
        var joiner = await EnterExpeditionHero(world, joinStore, "Joiner", 4);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(joiner, Now);

        var create = await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).U8(0).String("open").Bool(true).U8(PartyOptions.JoinFree).Varint(0).Bool(false).Varint(0).ToArray(), Now + 1);
        Assert.True(GameSessionTestHelpers.HasOpcode(create, GameSession.DungeonRoomCreateRs));
        var roomKey = world.Social.DungeonRooms.Keys.Single();

        var list = await joiner.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomListRq).I32(600002).U8(0).ToArray(), Now + 2);
        Assert.True(GameSessionTestHelpers.HasOpcode(list, GameSession.DungeonRoomListRs));

        var joined = await joiner.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 3);
        Assert.True(GameSessionTestHelpers.HasOpcode(joined, GameSession.DungeonRoomJoinRs));
        Assert.Equal(roomKey, world.Social.DungeonRoomOf(joiner.WorldPlayer!)!.RoomKey);
        Assert.Equal(2, world.Social.DungeonRooms[roomKey].Members.Count);
    }

    [Fact]
    public async Task Room_start_puts_both_players_in_same_instance_and_consumes_tickets()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await EnterExpeditionHero(world, leadStore, "Cap", 5);
        var mate = await EnterExpeditionHero(world, mateStore, "Mate", 6);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);

        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now + 1);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 2);

        var enter = await lead.HandleAsync(new PacketWriter().U16(GameSession.PartyDungeonEnterRq).ToArray(), Now + 50);
        Assert.True(GameSessionTestHelpers.HasOpcode(enter, GameSession.PartyDungeonEnterRs));
        Assert.NotEqual(0, lead.InstanceKey);
        Assert.Equal(lead.InstanceKey, mate.InstanceKey);
        await lead.TickAsync(Now + 60_000);
        await mate.TickAsync(Now + 60_000);
        Assert.Contains(leadStore.ExpeditionTickets, t => t.TicketId == 4 && t.Count == 1);
        Assert.Contains(mateStore.ExpeditionTickets, t => t.TicketId == 4 && t.Count == 1);
        Assert.DoesNotContain(world.Social.DungeonRooms, kv => kv.Key == roomKey);
    }

    [Fact]
    public async Task Leader_can_kick_member_from_waiting_room()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await EnterExpeditionHero(world, leadStore, "KickLead", 7);
        var mate = await EnterExpeditionHero(world, mateStore, "Kicked", 8);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 1);

        var kick = await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomKickMemberRq).I64(mateStore.States[^1].WireId).ToArray(), Now + 2);
        Assert.True(GameSessionTestHelpers.HasOpcode(kick, GameSession.DungeonRoomKickMemberRs));
        Assert.Null(world.Social.DungeonRoomOf(mate.WorldPlayer!));
        Assert.Single(world.Social.DungeonRooms[roomKey].Members);
    }

    [Fact]
    public async Task Instant_join_opens_or_fills_a_public_room()
    {
        var world = new SharedWorld(GameData.Embedded);
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var player = await EnterExpeditionHero(world, store, "Auto", 9);
        await GameSessionTestHelpers.LoadInventoryAsync(player, Now);

        var joined = await player.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomInstantJoinRq).I32(600002).Varint(0).U8(0).U8(0).ToArray(), Now + 1);
        Assert.True(GameSessionTestHelpers.HasOpcode(joined, GameSession.DungeonRoomInstantJoinRs));
        Assert.Equal(0, BitConverter.ToInt32(joined.First(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonRoomInstantJoinRs), 2));
        Assert.NotNull(world.Social.DungeonRoomOf(player.WorldPlayer!));
    }

    [Fact]
    public async Task Member_ready_broadcasts_to_room()
    {
        var world = new SharedWorld(GameData.Embedded);
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await EnterExpeditionHero(world, store, "Ready", 10);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now);

        var ready = await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomReadyRq).U8(1).ToArray(), Now + 1);
        Assert.True(GameSessionTestHelpers.HasOpcode(ready, GameSession.DungeonRoomReadyRs));
        Assert.True(GameSessionTestHelpers.HasOpcode(ready, GameSession.DungeonRoomReadyNt));
        Assert.Contains(world.Social.DungeonRoomOf(session.WorldPlayer!)!.Ready, k => k == session.WorldPlayer!.Key);
    }

    [Fact]
    public async Task Leader_leave_transfers_leadership()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await EnterExpeditionHero(world, leadStore, "OldLead", 11);
        var mate = await EnterExpeditionHero(world, mateStore, "NewLead", 12);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 1);

        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomLeaveRq).ToArray(), Now + 2);
        Assert.Equal(mate.WorldPlayer, world.Social.DungeonRooms[roomKey].Leader);
    }

    [Fact]
    public async Task Disconnect_removes_member_from_waiting_room()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await EnterExpeditionHero(world, leadStore, "Stay", 13);
        var mate = await EnterExpeditionHero(world, mateStore, "Drop", 14);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 1);

        typeof(GameSession).GetMethod("LeaveSocial", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mate, [Now + 2]);
        Assert.Null(world.Social.DungeonRoomOf(mate.WorldPlayer!));
        Assert.Single(world.Social.DungeonRooms[roomKey].Members);
    }

    [Fact]
    public async Task Leader_can_edit_room_settings()
    {
        var world = new SharedWorld(GameData.Embedded);
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var lead = await EnterExpeditionHero(world, store, "Editor", 15);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).ToArray(), Now);

        var edit = await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomEditSettingRq).String("renamed").Bool(false).U8(PartyOptions.JoinApproval).Varint(0).Bool(false).Varint(0).ToArray(), Now + 1);
        Assert.True(GameSessionTestHelpers.HasOpcode(edit, GameSession.DungeonRoomEditSettingRs));
        var room = world.Social.DungeonRoomOf(lead.WorldPlayer!)!;
        Assert.Equal("renamed", room.Desc);
        Assert.False(room.Public);
        Assert.Equal(PartyOptions.JoinApproval, room.JoinType);
    }

    [Fact]
    public void Resolve_dungeon_level_picks_tier_from_expedition_catalog()
    {
        var tables = ExpeditionTables.Embedded;
        Assert.Equal(600002, tables.ResolveDungeonId(600002, 0));
        Assert.Equal(600004, tables.ResolveDungeonId(600001, 1));
    }

    internal static async Task<GameSession> EnterExpeditionHero(SharedWorld world, FakeCharacterStore store, string name, uint creation)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, WorldId = store.WorldId, CharacterLevel = 55, TrainingDummy = true },
            new Random((int)creation))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[^1].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }
}
