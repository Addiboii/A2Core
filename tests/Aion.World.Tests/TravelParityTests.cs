using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Movement, flight, death, teleports, mounts (build pin 25767555).</summary>
public sealed class TravelParityTests
{
    const long Now = 1_760_300_000_000;

    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now, int mapId = 1010)
    {
        var shared = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = shared, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        store.States[0] = store.States[0] with { Ascension = 3 };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        if (mapId != GamePackets.SpawnMapId)
        {
            session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        }
        return session;
    }

    static byte[] FlyUpdate(Vector3d at) =>
        new PacketWriter().U16(0x370b).U8(2).Vec3(at).Zeros(16).U8(0).I64(0).ToArray();

    [Fact]
    public async Task Speed_check_rejects_teleport_hack_but_accepts_run_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var at = session.Position!.Value;
        var reject = await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at with { X = at.X + 20_000 }).ToArray(), Now + 500);
        Assert.Single(reject);
        Assert.Equal(0x373f, GameSessionTestHelpers.Op(reject[0]));
        Assert.Equal(1, session.SpeedRejects);
        Assert.Equal(at, session.Position);
        await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at with { X = at.X + 400 }).ToArray(), Now + 900);
        Assert.Equal(1, session.SpeedRejects);
        Assert.Equal(at with { X = (float)(at.X + 400) }, session.Position);
    }

    [Fact]
    public async Task Glide_and_flight_start_reply_when_winged()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        var glide = await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now);
        Assert.Contains(glide, p => p.SequenceEqual(GamePackets.GlideStartReply(session.PlayerKey)));
        var fly = await session.HandleAsync(new PacketWriter().U16(0x370a).U8(0).ToArray(), Now);
        Assert.Contains(fly, p => p.SequenceEqual(GamePackets.FlyStartReply(session.PlayerKey)));
    }

    [Fact]
    public async Task Glide_reports_skip_speed_anti_cheat()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        var at = session.Position!.Value;
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now);
        var rejectsBefore = session.SpeedRejects;
        await session.HandleAsync(new PacketWriter().U16(0x3707).U8(2).Vec3(at with { X = at.X + 20_000 }).Zeros(16).U8(0).I64(0).ToArray(), Now + 100);
        Assert.Equal(rejectsBefore, session.SpeedRejects);
    }

    [Fact]
    public async Task Glide_buoyancy_and_air_path_requests_get_replies()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        var key = session.PlayerKey;
        const long springboard = 0x000000001A1891C97C2;
        // Live capture (11 bytes): mode 0x04, springboard 0x1891c97c2.
        var buoyancyOn = await session.HandleAsync(Convert.FromHexString("083704C2971C89A1010000"), Now);
        var onNt = PlayerPackets.GlideBuoyancyOn(key, 4, springboard, deadReckoning: true);
        Assert.Single(buoyancyOn);
        Assert.Equal(0x3726, GameSessionTestHelpers.Op(buoyancyOn[0]));
        Assert.True(buoyancyOn[0].AsSpan(2).SequenceEqual(onNt.AsSpan(2)));

        var buoyancyOff = await session.HandleAsync(Convert.FromHexString("093700E3A221C89A101000"), Now + 1);
        var offNt = PlayerPackets.GlideBuoyancyOff(key, deadReckoning: false);
        Assert.Single(buoyancyOff);
        Assert.Equal(0x3727, GameSessionTestHelpers.Op(buoyancyOff[0]));
        Assert.True(buoyancyOff[0].AsSpan(2).SequenceEqual(offNt.AsSpan(2)));

        var start = session.Position!.Value;
        var target = start with { Z = start.Z + 500 };
        var airPathRq = new PacketWriter().U16(0x3719).Bool(false).Vec3(start).Vec3(target).ToArray();
        Assert.Equal(27, airPathRq.Length);
        var airPath = await session.HandleAsync(airPathRq, Now + 2);
        Assert.Single(airPath);
        Assert.Equal(0x3745, GameSessionTestHelpers.Op(airPath[0]));
        var body = airPath[0];
        Assert.Equal(0, BitConverter.ToUInt16(body, 2));   // result u16, as the official reply
        Assert.Equal(1, body[4]);                           // ticket
        var path = GameSession.AirPath(start, target);
        Assert.True(body.SequenceEqual(GamePackets.RequestAirPathReply(false, start, target, path: path)));
        Assert.Equal(target, path[0].Point);                // official order: target first, start last
        Assert.Equal(start, path[^1].Point);
        Assert.All(path, p => Assert.Equal(8, p.Terrain));
    }

    [Fact]
    public void Flight_gauge_constants_match_oct_pin_observation()
    {
        Assert.Equal(250_000, GameSession.FpMax);
        Assert.Equal(1000, GameSession.FlyDrainPerTick);
        Assert.Equal(750, GameSession.GlideDrainPerTick);
        Assert.Equal(1500, GameSession.FlySprintDrainPerTick);
    }

    static int? StatWireValue(byte[] statsUpdate, int wire)
    {
        if (GameSessionTestHelpers.Op(statsUpdate) != 0x364a || statsUpdate.Length < 6) return null;
        var i = 2;
        while (i < statsUpdate.Length && (statsUpdate[i] & 0x80) != 0) i++;
        i++; // skip varint key
        if (i >= statsUpdate.Length) return null;
        var count = statsUpdate[i++];
        for (var n = 0; n < count && i + 6 <= statsUpdate.Length; n++)
        {
            var type = BitConverter.ToUInt16(statsUpdate, i);
            var value = BitConverter.ToInt32(statsUpdate, i + 2);
            if (type == wire) return value;
            i += 6;
        }
        return null;
    }

    static bool CharPointPacket(byte[] packet, CharPoint type, int value)
    {
        if (GameSessionTestHelpers.Op(packet) != 0x8d00) return false;
        var needle = new PacketWriter().U8((byte)type).I32(value).ToArray();
        return packet.AsSpan().IndexOf(needle) >= 0;
    }

    [Fact]
    public async Task Winged_world_entry_sends_fp_max_on_stat_sheet_and_char_points()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        var at = session.Position!.Value;
        session.Teleport(1010, at, Now + 1);
        var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        var stats = load.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x364a);
        Assert.NotNull(stats);
        Assert.Equal(GameSession.FpMax, StatWireValue(stats!, 298));
        Assert.Equal(GameSession.FpMax, StatWireValue(stats!, 247));
        Assert.Contains(load, p => CharPointPacket(p, CharPoint.MaxFp, GameSession.FpMax) && CharPointPacket(p, CharPoint.Fp, GameSession.FpMax));
    }

    [Fact]
    public async Task Glide_drain_sends_fp_and_max_on_char_points_and_fly_cost_stat()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        var fpBefore = session.Resource.Fp;
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 2);
        var tick = await session.TickAsync(Now + 2 + GameSession.SprintTickMs);
        Assert.Equal(fpBefore - GameSession.GlideDrainPerTick, session.Resource.Fp);
        var fpPkt = tick.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x8d00 && CharPointPacket(p, CharPoint.Fp, session.Resource.Fp));
        Assert.NotNull(fpPkt);
        Assert.True(CharPointPacket(fpPkt!, CharPoint.MaxFp, GameSession.FpMax));
        var statPkt = tick.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x364a);
        Assert.NotNull(statPkt);
        Assert.Equal(session.Resource.Fp, StatWireValue(statPkt!, 247));
        Assert.Equal(GameSession.FpMax, StatWireValue(statPkt!, 298));
    }

    [Fact]
    public async Task Glide_without_prior_sprint_drains_one_tick_not_full_pool()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 2);
        await session.TickAsync(Now + 2 + GameSession.SprintTickMs);
        Assert.Equal(GameSession.FpMax - GameSession.GlideDrainPerTick, session.Resource.Fp);
    }

    static async Task<List<byte[]>> DrainFlightFpAsync(GameSession session, long startMs)
    {
        long t = startMs;
        List<byte[]> last = [];
        while (session.Resource.Fp > 0)
        {
            t += 25_000;
            last = await session.TickAsync(t);
        }
        return last;
    }

    [Fact]
    public async Task Empty_fp_forces_gravity_fall_and_blocks_glide_restart()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 2);
        var exhausted = await DrainFlightFpAsync(session, Now + 2);
        Assert.Equal(0, session.Resource.Fp);
        var at = session.Position!.Value;
        var expectedFall = PlayerPackets.GravityStart(session.PlayerKey, at, 0, Vector3d.Zero, gravityMode: 2, jumpUp: false);
        Assert.Contains(exhausted, p => GameSessionTestHelpers.Op(p) == 0x371e && p.AsSpan(2).SequenceEqual(expectedFall.AsSpan(2)));
        var retry = await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 100_000);
        Assert.Single(retry);
        Assert.True(retry[0].SequenceEqual(GamePackets.GlideStartReply(session.PlayerKey, 1)));
    }

    [Fact]
    public async Task Flight_fp_regen_allows_glide_again()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 2);
        await DrainFlightFpAsync(session, Now + 2);
        const long regenAt = Now + 200_000;
        var at = session.Position!.Value;
        await session.HandleAsync(new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(at).F32(0).F32(0).ToArray(), regenAt);
        await session.TickAsync(regenAt + 5_000);
        Assert.True(session.Resource.Fp > 0);
        var glide = await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), regenAt + 5_100);
        Assert.Contains(glide, p => p.SequenceEqual(GamePackets.GlideStartReply(session.PlayerKey)));
    }

    [Fact]
    public async Task Return_scroll_teleports_to_race_home_without_kibelisk_bind()
    {
        const long t = Now + 100_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t, mapId: 1010);
        store.Items.Add(FakeCharacterStore.BagItem(530560001, 1, 80));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(80).ToArray(), t);
        var done = await session.TickAsync(t + 3_000);
        var load = done.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x3621);
        Assert.NotNull(load);
        Assert.Equal(1000, BitConverter.ToInt32(load, 6));
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 530560001 && i.Quantity > 0);
    }

    [Fact]
    public async Task Return_scroll_teleports_to_latest_kibelisk_bind()
    {
        const long t = Now + 150_000;
        var bindOnField = Kibelisks.OnMap(1010).First();
        Assert.True(Kibelisks.TryPlacement(bindOnField.ArtifactId, out var bindMap, out var bindAt));
        Assert.Equal(1010, bindMap);
        var store = new FakeCharacterStore();
        store.TeleportArtifacts.Add(Kibelisks.OnMap(1000).First().ArtifactId);
        store.TeleportArtifacts.Add(bindOnField.ArtifactId);
        var session = await EnterAscendedAsync(store, t, mapId: 1010);
        store.Items.Add(FakeCharacterStore.BagItem(530560001, 1, 85));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(85).ToArray(), t);
        var done = await session.TickAsync(t + 3_000);
        var load = done.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x3621);
        Assert.NotNull(load);
        Assert.Equal(1010, BitConverter.ToInt32(load, 6));
        Assert.Equal(bindAt, session.Position);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 530560001 && i.Quantity > 0);
    }

    [Fact]
    public async Task Return_scroll_cast_cancels_when_player_moves()
    {
        const long t = Now + 200_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t);
        store.Items.Add(FakeCharacterStore.BagItem(530560001, 1, 81));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(81).ToArray(), t);
        var at = session.Position!.Value;
        var cancel = await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(at with { X = at.X + 500 }).ToArray(), t + 500);
        Assert.Contains(cancel, p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseInterruptedNt);
        var after = await session.TickAsync(t + 5_000);
        Assert.DoesNotContain(after, p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseCompletedNt);
        Assert.Contains(store.Items, i => i.TemplateId == 530560001 && i.Quantity == 1);
    }

    [Fact]
    public async Task Death_respawn_at_bind_and_spiritstone_in_place()
    {
        const long t = Now + 300_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t);
        store.Items.Add(FakeCharacterStore.BagItem(502760001, 2, 82));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(82).ToArray(), t);
        Assert.True(session.CanRebirth);
        var fell = session.Position!.Value;
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        Assert.True(session.Dead);
        var rebirth = await session.HandleAsync(new PacketWriter().U16(RidePackets.UseRebirthRq).U8(1).ToArray(), t + 2000);
        Assert.Contains(rebirth, p => p.SequenceEqual(RidePackets.UseRebirthReply(0, true)));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 2100);
        Assert.False(session.Dead);
        Assert.Equal(fell, session.Position);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 3000, killerKey: 2001);
        var bind = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 4000);
        Assert.Contains(bind, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        Assert.Contains(bind, p => GameSessionTestHelpers.Op(p) == 0x3621);
        var fellAgain = session.Position!.Value;
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 5000, killerKey: 2001);
        var stone = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnItem).I64(82).ToArray(), t + 6000);
        Assert.Contains(stone, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 6100);
        Assert.Equal(fellAgain, session.Position);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 502760001 && i.Quantity > 0);
    }

    [Fact]
    public async Task Kibelisk_bind_engraves_once_and_lists_at_entry()
    {
        const long t = Now + 400_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, t);
        session.Teleport(1000, GameSessionTestHelpers.MapStartOrDefault(1000), t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        var placed = Kibelisks.OnMap(1000).ToList();
        Assert.NotEmpty(placed);
        var wireKey = Kibelisks.FirstKey;
        var bind = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).I64(0).I32(placed[0].EnvObj).ToArray(), t);
        Assert.Contains(bind, p => p.SequenceEqual(GamePackets.EngravedTeleportArtifactAdd(placed[0].ArtifactId)));
        Assert.Contains(placed[0].ArtifactId, store.TeleportArtifacts);
        var again = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).I64(0).I32(placed[0].EnvObj).ToArray(), t + 100);
        Assert.Single(store.TeleportArtifacts);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x9233);
    }

    [Fact]
    public async Task World_map_teleport_to_engraved_kibelisk_uses_a_scroll_and_crosses_maps()
    {
        const long t = Now + 450_000;
        var store = new FakeCharacterStore();
        store.TeleportArtifacts.Add(1051035);
        var session = await EnterAscendedAsync(store, t, mapId: 1010);
        store.Items.Add(FakeCharacterStore.BagItem(530560001, 2, 86));
        await GameSessionTestHelpers.LoadInventoryAsync(session, t);
        var replies = await session.HandleAsync(Convert.FromHexString("36929B091000"), t);
        Assert.True(replies[0].SequenceEqual(GamePackets.UseTeleportArtifactReply(1051035)));
        var load = replies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == 0x3621);
        Assert.NotNull(load);
        Assert.Equal(1000, BitConverter.ToInt32(load, 6));
        var arrival = GameData.Embedded.MapPoints(1000)["TeleportArtifact_Poeta_Region_03_1"];
        Assert.Equal(new Vector3d(arrival[0], arrival[1], arrival[2]), session.Position);
        Assert.Contains(store.Items, i => i.TemplateId == 530560001 && i.Quantity == 1);
    }

    [Fact]
    public async Task World_map_teleport_to_unengraved_kibelisk_is_refused()
    {
        const long t = Now + 460_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t, mapId: 1010);
        var at = session.Position;
        var replies = await session.HandleAsync(new PacketWriter().U16(0x9236).I32(1051021).ToArray(), t);
        Assert.Single(replies);
        Assert.Equal(0x9237, GameSessionTestHelpers.Op(replies[0]));
        Assert.NotEqual(0, BitConverter.ToUInt16(replies[0], 2));
        Assert.Equal(at, session.Position);
    }

    [Fact]
    public async Task Subzone_entry_uncovers_fog_group()
    {
        const long t = Now + 500_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, t);
        var subzoneAt = new Vector3d(6297.9, -44596.4, 20000);
        var group = Subzones.GroupAt(1000, subzoneAt);
        Assert.Equal(65, group);
        session.Teleport(1000, subzoneAt, t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        await session.TickAsync(t + 1_000);
        Assert.Contains(65, session.Fog);
    }

    [Fact]
    public async Task Allowed_speed_uses_run_constants_with_tolerance()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now + 600_000);
        var expected = GameSession.RunSpeed * GameSession.SpeedTolerance;
        Assert.InRange(session.AllowedSpeed(), expected * 0.99, expected * 1.01);
    }

    [Fact]
    public async Task Fall_from_height_applies_damage_on_landing()
    {
        var session = await EnterAscendedAsync(new FakeCharacterStore(), Now + 800_000);
        var hpBefore = session.Resource.Hp;
        var high = session.Position!.Value with { Z = session.Position.Value.Z + 2000 };
        await session.HandleAsync(new PacketWriter().U16(0x3702).U8(0).Vec3(high).Zeros(12).F32(0).Vec3(Vector3d.Zero).ToArray(), Now + 800_100);
        var low = high with { Z = high.Z - 2000 };
        var landed = await session.HandleAsync(new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(low).F32(0).F32(0).ToArray(), Now + 800_200);
        Assert.True(session.Resource.Hp < hpBefore);
        Assert.Contains(landed, p => GameSessionTestHelpers.Op(p) == 0x8d00);
    }

    [Fact]
    public void Fall_damage_constants_are_documented_ours()
    {
        Assert.Equal(600, GameSession.FallDamageMinDropCm);
        Assert.True(GameSession.FallDamageHpPerCm > 0);
    }

    [Fact]
    public async Task Map_teleport_resets_speed_check_without_false_reject()
    {
        const long t = Now + 700_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t);
        var far = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, far, t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        var rejectsBefore = session.SpeedRejects;
        await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(far with { X = far.X + 2000 }).ToArray(), t + 100);
        Assert.Equal(rejectsBefore, session.SpeedRejects);
    }
}
