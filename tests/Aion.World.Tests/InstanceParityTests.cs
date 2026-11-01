using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Instances and party expeditions end-to-end on <see cref="SharedWorld"/> (build 25767555):
/// entry gates, map-event scripts, timers, tickets, loot UI, leave/rejoin, retry vote.
/// </summary>
public sealed class InstanceParityTests
{
    const long Now = 1_760_600_000_000;
    const int KraoEasy = 600002;

    static async Task<(SharedWorld World, GameSession Lead, GameSession Mate, GameSession Third, FakeCharacterStore LeadStore, FakeCharacterStore MateStore, FakeCharacterStore ThirdStore)>
        ExpeditionPartyOfThreeAsync()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        var thirdStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        FakeCharacterStore.SeedExpeditionItemLevel(thirdStore);
        var lead = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, leadStore, "InstLead", 18);
        var mate = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, mateStore, "InstMate", 19);
        var third = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, thirdStore, "InstThird", 20);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(third, Now);
        await lead.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionLeader, PartyOptions.JoinFree, PartyOptions.SpecialDice, 2)), Now + 1);
        var partyKey = world.Social.Parties.Values.Single().Key;
        await lead.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "InstMate"), Now + 2);
        await mate.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), Now + 3);
        await lead.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "InstThird"), Now + 4);
        await third.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), Now + 5);
        Assert.Equal(3, world.Social.Parties[partyKey].Members.Count);
        return (world, lead, mate, third, leadStore, mateStore, thirdStore);
    }

    [Fact]
    public async Task Three_member_party_room_start_shares_copy_and_krao_map_event()
    {
        var (world, lead, mate, third, _, _, _) = await ExpeditionPartyOfThreeAsync();
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(KraoEasy).U8(0).ToArray(), Now + 10);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 11);
        await third.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 12);
        var enter = await lead.HandleAsync(new PacketWriter().U16(GameSession.PartyDungeonEnterRq).ToArray(), Now + 50);
        Assert.True(GameSessionTestHelpers.HasOpcode(enter, GameSession.PartyDungeonEnterRs));
        Assert.NotEqual(0, lead.InstanceKey);
        Assert.Equal(lead.InstanceKey, mate.InstanceKey);
        Assert.Equal(lead.InstanceKey, third.InstanceKey);
        var load = await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        Assert.Contains(lead.MapEventRuns, r => r.Name == "KraoCave_Easy_MapEvent");
        Assert.True(GameSessionTestHelpers.HasOpcode(load, GameSession.DungeonStateNt));
    }

    [Fact]
    public async Task Party_member_joining_leaders_copy_consumes_expedition_ticket()
    {
        var (world, lead, mate, _, leadStore, mateStore, _) = await ExpeditionPartyOfThreeAsync();
        lead.EnterDungeonById(KraoEasy, Now + 20);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 21);
        await lead.TickAsync(Now + 22_000);
        Assert.Contains(leadStore.ExpeditionTickets, t => t.TicketId == 4 && t.Count == 1);
        mate.EnterDungeonById(KraoEasy, Now + 30);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 31);
        await mate.TickAsync(Now + 32_000);
        Assert.Equal(lead.InstanceKey, mate.InstanceKey);
        Assert.Contains(mateStore.ExpeditionTickets, t => t.TicketId == 4 && t.Count == 1);
    }

    [Fact]
    public async Task Time_limit_fail_ejects_every_member_in_the_copy()
    {
        var (world, lead, mate, _, _, _, _) = await ExpeditionPartyOfThreeAsync();
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(KraoEasy).U8(0).ToArray(), Now);
        var roomKey = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(roomKey).U8(0).ToArray(), Now + 1);
        await lead.HandleAsync(new PacketWriter().U16(GameSession.PartyDungeonEnterRq).ToArray(), Now + 50);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 61);
        Assert.NotNull(lead.Copy);
        lead.Copy!.DeadlineMs = Now + 100;
        var hostFail = await lead.TickAsync(Now + 200);
        Assert.True(GameSessionTestHelpers.HasOpcode(hostFail, GameSession.DungeonCompleteResultNt));
        Assert.True(GameSessionTestHelpers.HasOpcode(hostFail, GameSession.DungeonStateNt));
        var hostExit = await lead.TickAsync(Now + 200 + GameSession.DungeonFailExitMs);
        var mateExit = await mate.TickAsync(Now + 200 + GameSession.DungeonFailExitMs);
        Assert.Equal(0, lead.InstanceKey);
        Assert.Equal(0, mate.InstanceKey);
        Assert.True(GameSessionTestHelpers.HasOpcode(hostExit, 0x3621));
        Assert.True(GameSessionTestHelpers.HasOpcode(mateExit, 0x3621));
    }

    [Fact]
    public async Task Solo_expedition_timer_fail_returns_to_field()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(KraoEasy, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 60);
        session.Copy!.DeadlineMs = Now + 100;
        await session.TickAsync(Now + 200);
        var exit = await session.TickAsync(Now + 200 + GameSession.DungeonFailExitMs);
        Assert.Equal(0, session.InstanceKey);
        Assert.True(GameSessionTestHelpers.HasOpcode(exit, 0x3621));
    }

    [Fact]
    public async Task Leave_instance_packet_returns_to_field_map()
    {
        var (_, lead, _, _, _, _, _) = await ExpeditionPartyOfThreeAsync();
        lead.EnterDungeonById(KraoEasy, Now + 10);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        Assert.Equal(600002, lead.MapId);
        var left = await lead.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), Now + 30);
        Assert.True(GameSessionTestHelpers.HasOpcode(left, 0x4005));
        Assert.Equal(0, BitConverter.ToUInt16(left.First(b => GameSessionTestHelpers.Op(b) == 0x4005), 2));
        Assert.True(GameSessionTestHelpers.HasOpcode(left, 0x3621));
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 40);
        Assert.Equal(0, lead.InstanceKey);
        Assert.NotEqual(600002, lead.MapId);
    }

    [Fact]
    public async Task Host_handover_when_leader_leaves_copy_while_member_stays()
    {
        var (_, lead, mate, _, _, _, _) = await ExpeditionPartyOfThreeAsync();
        lead.EnterDungeonById(KraoEasy, Now + 10);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 11);
        mate.EnterDungeonById(KraoEasy, Now + 12);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 13);
        var run = lead.MapEventRuns.First(r => r.Name == "KraoCave_Easy_MapEvent");
        lead.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), Now + 20);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 21);
        Assert.Empty(lead.MapEventRuns);
        Assert.Contains(mate.MapEventRuns, r => r.Name == run.Name);
        Assert.Same(mate, mate.Copy?.Host);
        Assert.Same(run, mate.MapEventRuns.First(r => r.Name == run.Name));
        Assert.Same(mate, run.Host);
    }

    [Fact]
    public async Task Unanimous_retry_vote_restarts_dungeon_and_consumes_tickets()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, leadStore, "RetryLead", 16);
        var mate = await ExpeditionRoomMatchmakingTests.EnterExpeditionHero(world, mateStore, "RetryMate", 17);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        await lead.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now + 1);
        var partyKey = world.Social.Parties.Values.Single().Key;
        await lead.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "RetryMate"), Now + 2);
        await mate.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), Now + 3);
        foreach (var (s, t) in new[] { (lead, Now + 10), (mate, Now + 11) })
        {
            s.EnterDungeonById(KraoEasy, t);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 1);
            await s.TickAsync(t + 2_000);
        }
        var retry = await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomRetryRq).ToArray(), Now + 100);
        Assert.True(GameSessionTestHelpers.HasOpcode(retry, GameSession.DungeonRoomRetryRs));
        Assert.Equal(0, BitConverter.ToInt32(retry.First(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonRoomRetryRs), 2));
        var accept = await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomRetryResponseRq).U8(0).ToArray(), Now + 101);
        Assert.True(GameSessionTestHelpers.HasOpcode(accept, GameSession.DungeonRoomRetryResponseRs));
        await lead.TickAsync(Now + 102_000);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 102_001);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 102_002);
        Assert.Equal(600002, lead.MapId);
        Assert.Contains(lead.MapEventRuns, r => r.Name == "KraoCave_Easy_MapEvent");
        Assert.Contains(leadStore.ExpeditionTickets, t => t.TicketId == 4);
        Assert.Contains(mateStore.ExpeditionTickets, t => t.TicketId == 4);
    }

    [Fact]
    public async Task Retry_refusal_broadcasts_fail_to_party()
    {
        var (world, lead, mate, third, _, _, _) = await ExpeditionPartyOfThreeAsync();
        foreach (var s in new[] { lead, mate, third })
        {
            s.EnterDungeonById(KraoEasy, Now + 10);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 11);
        }
        await lead.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomRetryRq).ToArray(), Now + 50);
        var refuse = await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomRetryResponseRq).U8(1).ToArray(), Now + 51);
        Assert.True(GameSessionTestHelpers.HasOpcode(refuse, GameSession.DungeonRoomRetryResponseRs));
        Assert.DoesNotContain(world.Social.DungeonRetries, _ => true);
    }

    [Fact]
    public async Task Party_dice_roll_reaches_members_in_the_same_expedition_copy()
    {
        var (world, lead, mate, _, _, _, _) = await ExpeditionPartyOfThreeAsync();
        lead.EnterDungeonById(KraoEasy, Now + 10);
        mate.EnterDungeonById(KraoEasy, Now + 11);
        await lead.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 12);
        await mate.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 13);
        var party = world.Social.Parties.Values.Single();
        var roll = new PartyDiceRoll
        {
            DiceUid = 9001, PartyKey = party.Key, ItemId = 110120003, Count = 1, KillerKey = lead.PlayerKey,
            Center = lead.Position ?? Vector3d.Zero, At = lead.Position ?? Vector3d.Zero, ExpiresAtMs = Now + GameSession.PartyLootRollMs,
        };
        roll.Eligible.Add(lead.PlayerKey);
        roll.Eligible.Add(mate.PlayerKey);
        roll.Responses[lead.PlayerKey] = null;
        roll.Responses[mate.PlayerKey] = null;
        world.Social.DiceRolls[roll.DiceUid] = roll;
        var leadRoll = await lead.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 20);
        Assert.Contains(leadRoll, p => GameSessionTestHelpers.Op(p) == DistributionOpcodes.RollDiceRs);
        var mateRoll = await mate.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 21);
        Assert.Contains(mateRoll, p => GameSessionTestHelpers.Op(p) == DistributionOpcodes.RollDiceRs);
        Assert.DoesNotContain(world.Social.DiceRolls, kv => kv.Key == roll.DiceUid);
    }

    [Fact]
    public async Task Weekly_ticket_period_reset_allows_reentry_after_new_week()
    {
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var period = ShopTables.Period(2, DateTimeOffset.FromUnixTimeMilliseconds(Now));
        store.ExpeditionTickets.Add((4, period, 30));
        var weekLater = Now + 8L * 24 * 60 * 60 * 1000;
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, weekLater, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, weekLater + 10);
        var moved = session.EnterDungeonById(KraoEasy, weekLater + 50);
        Assert.NotEmpty(moved);
    }

    [Fact]
    public void Instance_layer_dat_lists_boss_and_map_event()
    {
        var layer = InstanceTables.Embedded.Of(100002)!;
        Assert.Equal("World_L_Starter_InstanceLayer_2_MapEvent", layer.MapEvent);
        Assert.Contains(2701128, layer.Boss);
    }

    [Fact]
    public async Task Field_load_after_brigade_layer_clears_instance_key_and_shares_world_npcs()
    {
        const long Now = 1_760_000_300_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);

        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        Assert.Equal(100002, session.MapId);
        Assert.NotEqual(0, session.InstanceKey);

        var boss = session.Targets!.Values.First(t => t.NpcId == 2701128);
        boss.Hp = 1;
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        await MapEventScenarioHelpers.CombatCastAsync(session, skillId, boss.Key, boss.Position, Now + 7000);
        await session.TickAsync(Now + 7000 + MapEventScenarioHelpers.SkillHitsLandMs(skillId) + GameSession.InstanceExitDelayMs + 500);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20_000);

        // HQ1101070's CompleteTeleportName (DawnLegionBase_Indoor_Recovery_Start) sends the player to the recovery room on map 1010.
        Assert.Equal(1010, session.MapId);
        Assert.Equal(0, session.InstanceKey);
        Assert.True(session.Targets!.Count > 10, "shared NPCs should be visible after leaving the private layer");
    }

    [Fact]
    public async Task Instance_layer_respawns_boss_while_ClearMapEvent_step_is_open()
    {
        const long Now = 1_760_000_400_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);

        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        var boss = session.Targets!.Values.First(t => t.NpcId == 2701128);
        boss.Hp = 0;
        await session.TickAsync(Now + 5200);

        await session.HandleAsync(new PacketWriter().U16(0x4004).ToArray(), Now + 6000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6100);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 6200);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 6210);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 11_000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 11_100);

        boss = session.Targets!.Values.First(t => t.NpcId == 2701128);
        Assert.False(boss.Dead);
    }
}
