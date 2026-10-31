using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Duels, arena matching, faction PvP, rift flag, kill rewards (build pin 25767555).</summary>
public sealed class PvpParityTests
{
    const long Now = 1_760_400_000_000;

    static async Task<GameSession> EnterSharedAsync(SharedWorld world, FakeCharacterStore store, string name, int race, int level = 45, int seed = 3)
    {
        var data = GameData.Embedded;
        var creation = race == 2
            ? Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 })
            : 1;
        store.Progress = (level, 0);
        var session = new GameSession(data,
            new GameSessionOptions { World = world, CharacterLevel = level, WorldId = store.WorldId, TrainingDummy = true },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        if (race == 2)
            await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now);
        else
            await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([1]).U8(0).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task<(GameSession Light, GameSession Dark, SharedWorld World)> TwoRacesAsync(int level = 45)
    {
        var world = new SharedWorld(GameData.Embedded);
        var light = await EnterSharedAsync(world, new FakeCharacterStore(), "LightPvp", 1, level, 41);
        var dark = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "DarkPvp", 2, level, 42);
        return (light, dark, world);
    }

    static void SameSpot(GameSession a, GameSession b, int mapId, Vector3d at, long now)
    {
        a.Teleport(mapId, at, now);
        b.Teleport(mapId, at with { X = at.X + 50 }, now);
    }

    [Fact]
    public void Embedded_pvp_json_lists_invasion_maps_and_protection()
    {
        var pvp = PvpTables.Embedded;
        Assert.Equal(20, pvp.ProtectBelowLevel);
        Assert.True(pvp.IsInvasionMap(500032));
        Assert.False(pvp.IsInvasionMap(1010));
        Assert.True(pvp.IsAbyssMap(20));
    }

    [Fact]
    public async Task Field_map_blocks_cross_faction_without_rift_flag()
    {
        var (light, dark, _) = await TwoRacesAsync();
        var spot = new Vector3d(-127874, -111187, 25393);
        SameSpot(light, dark, 1010, spot, Now);
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await dark.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        light.NoCool = true;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(light);
        var hit = await light.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, dark.PlayerKey, dark.WorldPlayer!.Position), Now + 100);
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task Rift_flag_and_garrison_subzone_allow_field_pvp()
    {
        var (light, dark, _) = await TwoRacesAsync();
        // RiftEvent_Garrison_L1_D1_Subzone on map 1010 (centroid from subzones.json).
        var riftAt = new Vector3d(-5382, -34656, 12213);
        SameSpot(light, dark, 1010, riftAt, Now);
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await dark.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await light.HandleAsync(new PacketWriter().U16(GameSession.RiftOptionUpdateRq).U8(1).ToArray(), Now + 1);
        await dark.HandleAsync(new PacketWriter().U16(GameSession.RiftOptionUpdateRq).U8(1).ToArray(), Now + 1);
        light.NoCool = true;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(light);
        var castAt = Now + 100;
        await light.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, dark.PlayerKey, dark.WorldPlayer!.Position), castAt);
        var hit = await GameSessionTestHelpers.AfterSkillHitsAsync(light, castAt);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task Low_level_characters_are_protected_from_rift_field_pvp()
    {
        var (light, dark, _) = await TwoRacesAsync(level: 15);
        var riftAt = new Vector3d(-5382, -34656, 12213);
        SameSpot(light, dark, 1010, riftAt, Now);
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await dark.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await light.HandleAsync(new PacketWriter().U16(GameSession.RiftOptionUpdateRq).U8(1).ToArray(), Now + 1);
        await dark.HandleAsync(new PacketWriter().U16(GameSession.RiftOptionUpdateRq).U8(1).ToArray(), Now + 1);
        light.NoCool = true;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(light);
        var hit = await light.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, dark.PlayerKey, dark.WorldPlayer!.Position), Now + 100);
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task Abyss_kill_grants_ap_and_ranking_updates()
    {
        var (light, dark, _) = await TwoRacesAsync();
        SameSpot(light, dark, 20, Vector3d.Zero, Now);
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await dark.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        dark.SetHpForChecks(1);
        light.NoCool = true;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(light);
        var castAt = Now + 200;
        await light.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, dark.PlayerKey, dark.WorldPlayer!.Position), castAt);
        var hit = await GameSessionTestHelpers.AfterSkillHitsAsync(light, castAt);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == GameSession.AbyssPointUpdateNt);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == GameSession.RankingPointUpdateNt);
    }

    [Fact]
    public void Invasion_field_events_are_catalogued_for_open_world_pvp()
    {
        var room = MapEventTables.Embedded.DungeonOfMap(500032);
        Assert.NotNull(room);
        Assert.Equal("FieldEvent", room!.Type);
        Assert.StartsWith("invasion_", room.Event);
        Assert.True(PvpTables.Embedded.IsInvasionMap(500032));
    }

    [Fact]
    public async Task Rift_option_update_replies_and_broadcasts_char_flag()
    {
        var world = new SharedWorld(GameData.Embedded);
        var a = await EnterSharedAsync(world, new FakeCharacterStore(), "FlagA", 1);
        var b = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "FlagB", 1, seed: 4);
        SameSpot(a, b, 1010, new Vector3d(-127874, -111187, 25393), Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var replies = await a.HandleAsync(new PacketWriter().U16(GameSession.RiftOptionUpdateRq).U8(1).ToArray(), Now + 1);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.RiftOptionUpdateRs);
        var seen = await b.TickAsync(Now + 2);
        Assert.Contains(seen, p => GameSessionTestHelpers.Op(p) == GameSession.RiftOptionCharUpdateNt);
    }

    [Fact]
    public async Task Duel_request_accept_and_hit()
    {
        var world = new SharedWorld(GameData.Embedded);
        var a = await EnterSharedAsync(world, new FakeCharacterStore(), "DuelA", 1);
        var b = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "DuelB", 1, seed: 5);
        var spot = new Vector3d(-127874, -111187, 25393);
        SameSpot(a, b, 1010, spot, Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var challenge = await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, b.PlayerKey), Now + 10);
        Assert.Equal(SocialOpcodes.DuelCallOutRs & 0xff, challenge[0][0]);
        var roomKey = world.Social!.Duels.Single().Key;
        await b.HandleAsync(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutAcceptRq, roomKey, a.PlayerKey), Now + 20);
        await a.TickAsync(Now + 20 + Duel.CountdownMs);
        Assert.Equal(DuelState.InBattle, world.Social.Duels[roomKey].State);
        var skill = MapEventScenarioHelpers.BasicAttackSkill(a);
        a.NoCool = true;
        var castAt = Now + 30_000;
        await a.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, b.PlayerKey, b.WorldPlayer!.Position), castAt);
        var hit = await GameSessionTestHelpers.AfterSkillHitsAsync(a, castAt);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0x3804);
    }

    [Fact]
    public async Task Duel_reject_clears_pending_challenge()
    {
        var world = new SharedWorld(GameData.Embedded);
        var a = await EnterSharedAsync(world, new FakeCharacterStore(), "Chall", 1);
        var b = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "Decl", 1, seed: 6);
        SameSpot(a, b, 1010, new Vector3d(-127874, -111187, 25393), Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, b.PlayerKey), Now + 10);
        var roomKey = world.Social!.Duels.Single().Key;
        var reject = await b.HandleAsync(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutRejectRq, roomKey, a.PlayerKey), Now + 20);
        Assert.Contains(reject, p => GameSessionTestHelpers.Op(p) == SocialOpcodes.DuelCallOutRejectRs);
        Assert.Empty(world.Social.Duels);
    }

    [Fact]
    public async Task Duel_forfeit_on_leave_sends_result()
    {
        var world = new SharedWorld(GameData.Embedded);
        var a = await EnterSharedAsync(world, new FakeCharacterStore(), "Stay", 1);
        var b = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "Leave", 1, seed: 7);
        SameSpot(a, b, 1010, new Vector3d(-127874, -111187, 25393), Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, b.PlayerKey), Now + 10);
        var roomKey = world.Social!.Duels.Single().Key;
        await b.HandleAsync(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutAcceptRq, roomKey, a.PlayerKey), Now + 20);
        await a.TickAsync(Now + 20 + Duel.CountdownMs);
        a.ExitSharedWorld();
        Assert.Empty(world.Social.Duels);
    }

    [Fact]
    public async Task Arena_matchmaking_ready_and_cancel()
    {
        var world = new SharedWorld(GameData.Embedded);
        var erin = await EnterSharedAsync(world, new FakeCharacterStore(), "ArenaA", 1, seed: 8);
        var finn = await EnterSharedAsync(world, new FakeCharacterStore(Guid.NewGuid()), "ArenaB", 1, seed: 9);
        var q1 = await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartRq).I32(1).ToArray(), Now);
        Assert.Contains(q1, p => GameSessionTestHelpers.Op(p) == GameSession.MatchingStateChangeNt);
        Assert.Single(world.Social!.ArenaQueue);
        var cancel = await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingCancelRq).ToArray(), Now + 10);
        Assert.Equal(0, cancel[0][2]);
        Assert.Empty(world.Social.ArenaQueue);
        var q2 = await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartRq).I32(1).ToArray(), Now + 20);
        Assert.Equal(0, q2[0][2]);
        var matched = await finn.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartRq).I32(1).ToArray(), Now + 30);
        Assert.Contains(matched, p => GameSessionTestHelpers.Op(p) == GameSession.MatchingStateChangeNt && p[6] == 3);
        Assert.Single(world.Social.Arenas);
        await erin.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartResponseRq).I32(1).U8(1).ToArray(), Now + 40);
        await finn.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartResponseRq).I32(1).U8(0).ToArray(), Now + 50);
        Assert.Empty(world.Social.Arenas);
    }

    [Fact]
    public void Guard_relationship_ids_exist_for_town_npcs()
    {
        Assert.Equal(33, MapEventTables.Relationship("NPC_Light_Guard"));
        Assert.Equal(34, MapEventTables.Relationship("NPC_Dark_Guard"));
    }
}
