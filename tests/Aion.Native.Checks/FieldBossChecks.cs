using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Field bosses on the world map (feature/field-boss-map): the client tables (WorldMapFieldNamed / PeriodSpawn), the schedules in KST,
/// WorldMapContentsInfo_RQ 0x8dd1 -> RS 0x8dd2 (CANDIDATE layout from the usmap), hidden period bosses on a new map, and the kills
/// loaded from and saved to the store (game.field_boss_kills, migration 091).
/// </summary>
static class FieldBossChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static long Utc(int day, int hour, int minute = 0) => new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static async Task Run(GameData data, Func<SharedWorld, string, FakeStore, Task<GameSession>> fieldSession, Action<bool, string> check)
    {
        Tables(data, check);
        Schedules(check);
        Wire(check);
        await Sessions(data, fieldSession, check);
    }

    static void Tables(GameData data, Action<bool, string> check)
    {
        var all = FieldBosses.All;
        check(all.Count == 85 && FieldBosses.On(1010).Count == 24 && FieldBosses.On(1110).Count == 24 && FieldBosses.On(1011).Count == 12
              && FieldBosses.On(1111).Count == 12 && FieldBosses.On(20).Count == 8 && FieldBosses.On(22).Count == 5,
            "WorldMapFieldNamed: 85 field bosses (1010: 24, 1110: 24, 1011: 12, 1111: 12, Abyss 20: 8, 22: 5)");
        check(all.Count(e => e.Period != 0) == 12 && all.All(e => e.Period == 0 || FieldBosses.ScheduleOf(e) is not null),
            "12 PeriodSpawn bosses, each with its PeriodSpawn row");
        check(all.All(e => data.Spawns(e.Map).Any(s => string.Equals(s.Name, e.Spawner, StringComparison.OrdinalIgnoreCase))),
            "every field boss spawner is a MapData spawn of its map (world-npcs.json)");
        check(FieldBosses.OfSpawner(1010, "M_L1_FieldBoss_Dragon_01_1") is { Id: 101022, Period: 0 } && FieldBosses.OfSpawner(20, "M_AR1_AbBoss_01_1") is { Id: 2003, Period: 1013 },
            "spawner -> list entry: M_L1_FieldBoss_Dragon_01_1 = 101022 (Basic), M_AR1_AbBoss_01_1 = 2003 (PeriodSpawn 1013)");
    }

    static void Schedules(Action<bool, string> check)
    {
        var day = FieldBosses.ScheduleOf(FieldBosses.OfSpawner(20, "M_AR1_AbBoss_01_1")!)!;
        var cycle = FieldBosses.ScheduleOf(FieldBosses.OfSpawner(20, "M_AR1_BeritraD_01_001")!)!;
        var wednesday = Utc(11, 0, 30);   // 2026-11-11 09:30 KST
        check(day is { Type: "Day", TimeSeconds: 75600 } && FieldBosses.LastSpawn(day, wednesday) == Utc(8, 12) && FieldBosses.NextSpawn(day, wednesday) == Utc(13, 12),
            "Day boss (21:00 KST Sunday + Friday): last Sun 2026-11-08 12:00 UTC, next Fri 2026-11-13 12:00 UTC");
        check(cycle is { Type: "Cycle", CycleMinutes: 180 } && FieldBosses.LastSpawn(cycle, wednesday) == Utc(10, 22) && FieldBosses.NextSpawn(cycle, wednesday) == Utc(11, 1),
            "Cycle boss (every 3 h from 01:00 KST): 07:00 KST before, 10:00 KST next");
        var state = new FieldBossState();
        var abBoss = FieldBosses.OfSpawner(20, "M_AR1_AbBoss_01_1")!;
        var dragon = FieldBosses.OfSpawner(1010, "M_L1_FieldBoss_Dragon_01_1")!;
        check(state.Status(abBoss, wednesday) == (true, 0) && state.Status(dragon, wednesday) == (true, 0), "unkilled: a period boss past its time and a Basic boss stand");
        state.Killed(abBoss, Utc(8, 13), Utc(13, 12));
        state.Killed(dragon, wednesday - 1000, wednesday + 29_000);
        check(state.Status(abBoss, wednesday) == (false, Utc(13, 12)) && state.Status(abBoss, Utc(13, 12)) == (true, 0),
            "a period boss killed after its time waits for the next one (Friday 12:00 UTC), then stands");
        check(state.Status(dragon, wednesday) == (false, wednesday + 29_000) && state.Status(dragon, wednesday + 29_000).Spawned, "a Basic boss is back at its respawn time");
    }

    static void Wire(Action<bool, string> check)
    {
        var at = new Vector3d(14626.877, 141980.125, 30567.959);
        var rs = FieldBosses.ContentsInfo(1010, [(101022, at, true, 0), (101023, at, false, 1794000000000)]);
        var expected = PacketWriter.Opcode(0x8dd2).U16(0).I32(1010).Varint(2).I32(101022).Vec3(at).U8(1).I64(0)
            .I32(101023).Vec3(at).U8(0).I64(1794000000000).Varint(0).Varint(0).Varint(0).ToArray();
        check(rs.SequenceEqual(expected) && GameSession.WorldMapContentsInfoRq == 0x8dd1,
            "WorldMapContentsInfo_RS 0x8dd2: result u16, map, {data id, Vec3, spawned, respawn ms}, empty boss/rift/object lists (usmap; CANDIDATE)");
    }

    static async Task Sessions(GameData data, Func<SharedWorld, string, FakeStore, Task<GameSession>> fieldSession, Action<bool, string> check)
    {
        var now = Utc(11, 0, 30);
        var world = new SharedWorld(data) { Clock = () => now };
        var store = new FakeStore();
        store.FieldBossRows.Add((20, 2003, Utc(8, 13), Utc(13, 12)));
        var hunter = await fieldSession(world, "BossHunter", store);
        check(world.FieldBossKills.Loaded && world.FieldBossKills.KillOf(FieldBosses.OfSpawner(20, "M_AR1_AbBoss_01_1")!) == (Utc(8, 13), Utc(13, 12)),
            "world entry reads the stored field boss kills once for the host");

        var verteron = await hunter.HandleAsync(new PacketWriter().U16(GameSession.WorldMapContentsInfoRq).I32(1010).ToArray(), now);
        var list1010 = GameSession.FieldBossList(data, world.FieldBossKills, 1010, now);
        check(verteron.Count == 1 && Op(verteron[0]) == 0x8dd2 && verteron[0].SequenceEqual(FieldBosses.ContentsInfo(1010, list1010))
              && list1010.Count == 24 && list1010.All(e => e.Spawned) && list1010.Any(e => e.DataId == 101022 && Math.Abs(e.Position.X - 14626.877) < 0.01),
            "map 1010 asked: the 24 field bosses listed, all spawned, the dragon at its MapData point");

        var abyss = GameSession.FieldBossList(data, world.FieldBossKills, 20, now);
        check(abyss.Count == 8 && abyss.Single(e => e.DataId == 2003) is { Spawned: false } killed && killed.RespawnAt == Utc(13, 12)
              && abyss.Where(e => e.DataId != 2003).All(e => e.Spawned),
            "map 20: the stored kill shows 2003 waiting for Friday 12:00 UTC; the others stand");

        var map = world.Map(20);
        var abBoss = map.Targets.Values.Single(t => t.Name == "M_AR1_AbBoss_01_1");
        var cycle = map.Targets.Values.Where(t => t.Name == "M_AR1_BeritraD_01_001").ToList();
        check(abBoss is { Dead: true, CorpseRemoved: true } && map.RespawnAt[abBoss.Key] == Utc(13, 12),
            "a new map hides the killed period boss until its next time");
        check(cycle.Count == 6 && cycle.Count(t => !t.Dead) == 1 && cycle.Where(t => t.Dead).All(t => t.CorpseRemoved && map.RespawnAt[t.Key] == Utc(11, 1)),
            "the 3-hourly boss stands at one of its 6 points; the other points wait for the next time");
        var live = GameSession.FieldBossList(data, world.FieldBossKills, 20, now, (map.Targets, map.RespawnAt));
        check(live.Single(e => e.DataId == 2002).Position.X == cycle.Single(t => !t.Dead).Position.X, "the list shows the standing cycle boss where it stands");

        world.FieldBossKills.Killed(FieldBosses.OfSpawner(1010, "M_L1_FieldBoss_Dragon_01_1")!, now, now + GameSession.RespawnMs);
        await hunter.TickAsync(now + 50);
        check(store.FieldBossRows.Any(r => r is { Map: 1010, Id: 101022 } && r.RespawnAt == now + GameSession.RespawnMs),
            "a field boss kill is saved at the next tick (game.field_boss_kills)");
        hunter.ExitSharedWorld();
    }
}
