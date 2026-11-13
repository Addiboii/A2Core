using System.Xml.Linq;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Hidden cubes and Spacetime Rifts (fix/rifts-cubes): the client data (pools, rifts, schedule, exits, rewards), the official cube spawn
/// bytes, the pool selection and respawn, the rift schedule and position, the world choice (OURS: busy spreads, quiet consolidates),
/// a Light player through a rift into the chosen Dark world's copy and back home when the visit ends, the refusals, a cube opened
/// with a key in the official order.
/// </summary>
static class RiftCubeChecks
{
    const string AccountSession = "1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b";
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int KeyOf(byte[] b) => (int)new PacketReader(b) { Offset = 2 }.Varint();
    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint((uint)key).Zeros(8).ToArray();
    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }
    static byte[] GameLogin => Strings(0x3613, AccountSession, "", "t", "").Concat(new byte[9]).ToArray();
    static byte[] Create(string name, int creation) =>
        new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(0).U8(0).ToArray();
    static long Kst(int day, int hour, int minute = 0) => new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();

    public static async Task Run(GameData data, Action<bool, string> check)
    {
        Data(check);
        Pools(check);
        Schedule(check);
        Choice(check);
        await Sessions(data, check);
    }

    static void Data(Action<bool, string> check)
    {
        var rifts = WorldPools.Rifts.Values.ToList();
        check(rifts.Count == 10 && rifts.Count(r => r.Map == 1110) == 3 && rifts.Count(r => r.Map == 1010) == 3 && rifts.Count(r => r.Map == 1111) == 2
              && rifts.Count(r => r.Map == 1011) == 2,
              "PeriodSpawn rifts: Altgard 3, Verteron 3, Morheim 2, Eltnen 2");
        check(rifts.All(r => r is { LifeSeconds: 600, Limit: 400, MinLevel: 45, MaxLevel: 100, InteractMs: 2500, Effect: 30, Costs.Count: 0 }
                             && r.Exits.Count == 5 && r.Schedule is { Type: "Cycle", TimeSeconds: 7200, CycleMinutes: 180 } && r.Schedule.Days.All(d => d)
                             && WorldPools.FieldRace(r.Map) != WorldPools.FieldRace(r.ArrivalMap)
                             && WorldPools.RiftPoints(r.Map, r.Spawner).Count == 5),
              "every rift: RiftData 600 s, 400 uses, level 45-100, no cost, 2.5 s use, TeleportEffect 30, 5 positions, 5 exits on the other faction's map, every 3 h from 02:00 KST");
        check(WorldPools.RiftOf("EnvObj_D1_RiftPortal_1") is { Map: 1110, Env: 1100006, Rift: "Altgard_RiftPortal_01", ArrivalMap: 1010, Teleport: "RiftPortal_D1_to_L1_Teleport_1" } a
              && Math.Abs(a.Exits[0].X - 12478.7) < 0.2 && WorldPools.RiftOf("E_D2_RiftPortal_002_1")?.Teleport == "RiftPortal_D2_to_L2_Teleport_2",
              "Altgard rift 1 leads to Verteron at RiftPortal_D1_to_L1_Teleport_1's points; Morheim rift 2 to Teleport_2");
        int Positions(int map) => WorldPools.PoolsOnMap(map).Values.Sum(p => p.Positions.Count);
        check(WorldPools.PoolsOnMap(1010).Count == 95 && WorldPools.PoolsOnMap(1011).Count == 26 && WorldPools.PoolsOnMap(1110).Count == 59 && WorldPools.PoolsOnMap(1111).Count == 26
              && Positions(1010) + Positions(1011) + Positions(1110) + Positions(1111) == 3722
              && WorldPools.PoolsOnMap(1110).Values.All(p => p.Slots.Count == 2 && p.Positions.Count >= p.Slots.Count),
              "cube pools: 206 MapData records (95 / 26 / 59 / 26), 3722 positions, two cubes per record");
        // The official cube spawn (rec 20261106-105448 #26049): E_D1_TreasureBoxRare_002, just respawned (state 06, created 0.35 s before).
        var official = Convert.FromHexString("3436ca8b030400cfda130037ee21c80185c2c600c0c1460000000000308442010106e85c81b0a1010000000000000000000075fd1a33070000");
        float F(int at) => BitConverter.ToSingle(official, at);
        var recorded = new WorldObject(1301199, "EnvObj_D1_Sanctum_Watchpost_HiddenBox_01_A", 857406837, [F(11), F(15), F(19)], F(27), 4000, 60, 7);
        check(GameSession.PoolSpawn(50634, recorded, 0x01a1b0815ce8, true).SequenceEqual(official),
              "a fresh cube's 0x3634 is the official bytes (#26049: state 01 01 06, create time, unique key, relationship 7)");
        // The three official cube spawns (2026-11-06) each stood on a position of its record.
        (double X, double Y, int UniqueKey)[] seen = [(-165817, -24899, 857406837), (-103055, -57334, -967764153), (65686, -65066, -348381964)];
        var altgard = WorldObjects.ObjectsOn(1110);
        check(seen.All(s => altgard.Any(o => o.Pool == s.UniqueKey && Math.Abs(o.Position[0] - s.X) < 2 && Math.Abs(o.Position[1] - s.Y) < 2 && o.Env == 1301199)),
              "the 3 recorded E_D1_TreasureBoxRare_002 spawns are positions of their pool records (Sanctum_Watchpost 01_A, Dredgion_Crash_Site 01_C, Purificatoryforest 01_B)");
        var cube = WorldPools.CubeOf(1301199);
        var d1 = WorldPools.CubeOf(1300081);
        check(cube is { Faction: 2, KeyOnly: true, Key: "reward_manual_TreasureBox_D_Key", KeyCount: 1 } && cube.Keys.Contains(630900003)
              && d1 is { KeyOnly: false, OptionalKey: true, Normal: "reward_manual_TreasureBox_D_Normal" }
              && WorldPools.CubeOf(1201502) is { RewardData: false, Normal: "reward_manual_TreasureBox_L_Normal" }
              && LootTables.Embedded.HasTable("reward_manual_TreasureBox_D_Key") && !LootTables.Embedded.HasTable("reward_manual_onlyKey_TreasureBox_D_Normal"),
              "cube rewards: E_D1_TreasureBoxRare_002 opens only with a key (630900003 among them) for reward_manual_TreasureBox_D_Key; "
              + "the optional-key cubes give RewardName; L2/D2 cubes fall back to their faction's tables (CANDIDATE)");
        var rng = new Random(3);
        var rolls = Enumerable.Range(0, 20).Select(_ => LootTables.Embedded.RollTables(["reward_manual_TreasureBox_D_Key"], 2, rng)).ToList();
        KeyRewardAlways = rolls.Select(r => r.Where(i => i.Count >= 10).Select(i => i.ItemId).ToHashSet())
            .Aggregate((x, y) => { x.IntersectWith(y); return x; });
        check(rolls.All(r => r.Count > 1) && KeyRewardAlways.Count >= 1,
              "the key reward table rolls (RewardItem: 10 Refund_A_s_02a_b every time, plus its race's packages)");
        check(WorldObjects.ObjectsOn(1110).Count == altgard.Count && altgard.Count(o => o.Rift is not null) == 15
              && WorldObjects.ObjectsOn(1110).Take(altgard.Count - 1386).All(o => o.Pool is null && o.Rift is null),
              "the pools and rifts come after world-objects.json's own objects (their keys unchanged)");
        var rules = RiftRules.Parse(XElement.Parse("<rifts mode='spread' busy_percent='40' visit_minutes='30' announce='false'/>"), XElement.Parse("<hidden_cubes respawn_minutes='5'/>"));
        check(rules is { Mode: "spread", BusyPercent: 40, VisitMinutes: 30, Announce: false, CubeRespawnMinutes: 5 }
              && ServerRules.Embedded.Rifts is { Mode: "auto", BusyPercent: 60, VisitMinutes: 60, Announce: true, CubeRespawnMinutes: 15 },
              "server-rules.xml rifts/hidden_cubes: auto, busy at 60 %, 60 min visits (OFFICIAL ticket time), notice on, cubes back after 15 min");
        var refused = false;
        try { RiftRules.Parse(XElement.Parse("<rifts mode='random'/>"), null); } catch (InvalidDataException) { refused = true; }
        check(refused, "an unknown rift mode stops the host at start-up");
    }

    /// <summary>The items the key table gives every time (Refund_A_s_02a_b x10).</summary>
    static HashSet<int> KeyRewardAlways = [];

    static void Pools(Action<bool, string> check)
    {
        const long t0 = 1_800_000_000_000;
        var state = new PoolState(1110);
        var pool = WorldPools.PoolsOnMap(1110).Values.First(p => p.Positions.Count == 30);
        var rng = new Random(11);
        var first = state.Positions(pool.Key, rng, t0);
        int IndexAt(int position) => pool.Positions[position][pool.Slots[0]];
        check(first.Count == 2 && first.All(p => p >= 0) && first[0] != first[1]
              && state.Standing(IndexAt(first[0]), rng, t0) is { Since: 0 } && state.Standing(IndexAt(first[1]), rng, t0) is not null
              && Enumerable.Range(0, 30).Count(p => state.Standing(IndexAt(p), rng, t0) is not null) == 2,
              "a pool keeps two cubes up at two different random positions of its 30 (standing since before: no fresh spawn)");
        var taken = IndexAt(first[0]);
        check(state.Take(taken, rng, t0 + 1000, 900_000) && !state.Take(taken, rng, t0 + 1001, 900_000) && state.Standing(taken, rng, t0 + 2000) is null
              && state.Positions(pool.Key, rng, t0 + 2000) is [-1, var other] && other == first[1],
              "an opened cube is gone for everyone (a second opener is refused), the other cube stays");
        var waiting = state.Positions(pool.Key, rng, t0 + 1000 + 899_999)[0];
        var back = state.Positions(pool.Key, rng, t0 + 1000 + 900_000);
        check(waiting == -1 && back[0] >= 0 && back[0] != back[1]
              && state.Standing(IndexAt(back[0]), rng, t0 + 1000 + 900_500) is { Since: t0 + 1000 + 900_000 },
              "it comes back after the respawn time at a free position of its pool, as a fresh spawn");
        var spread = new HashSet<int>();
        for (var seed = 0; seed < 60; seed++) spread.UnionWith(new PoolState(1110).Positions(pool.Key, new Random(seed), t0));
        check(spread.Count >= 20, $"the positions are random over the pool ({spread.Count} of 30 seen in 60 copies)");
        var l2 = WorldPools.PoolsOnMap(1011).Values.First(p => p.Slots.Distinct().Count() == 2 && p.Positions.Count > 2);
        var l2state = new PoolState(1011);
        var l2at = l2state.Positions(l2.Key, rng, t0);
        check(l2.Positions[l2at[0]].ContainsKey(l2.Slots[0]) && l2.Positions[l2at[1]].ContainsKey(l2.Slots[1])
              && l2state.Standing(l2.Positions[l2at[0]][l2.Slots[0]], rng, t0) is { Slot: 0 } && l2state.Standing(l2.Positions[l2at[1]][l2.Slots[1]], rng, t0) is { Slot: 1 }
              && l2state.Standing(l2.Positions[l2at[0]][l2.Slots[1]], rng, t0) is null,
              "an Eltnen pool of two kinds (plain cube + key cube) keeps one of each, each shown as its own kind");
        check(state.UseRift("X", 5, 2) && state.UseRift("X", 5, 2) && !state.UseRift("X", 5, 2) && state.UseRift("X", 6, 2) && state.RiftUses("X", 5) == 2,
              "a rift opening takes InteractCount uses, the next opening starts again");
    }

    static void Schedule(Action<bool, string> check)
    {
        var rift = WorldPools.RiftOf("envobj_Verteron_RiftPortal_01")!;
        check(WorldPools.OpenedAt(rift, Kst(13, 2, 5)) == Kst(13, 2) && WorldPools.OpenedAt(rift, Kst(13, 2, 10)) is null
              && WorldPools.OpenedAt(rift, Kst(13, 4, 0)) is null && WorldPools.NextOpening(rift, Kst(13, 2, 10)) == Kst(13, 5)
              && WorldPools.OpenedAt(rift, Kst(13, 23, 9)) == Kst(13, 23) && WorldPools.NextOpening(rift, Kst(13, 23, 30)) == Kst(14, 2),
              "rift times: 02:00, 05:00 ... 23:00 KST, open 10 minutes (02:05 open, 02:10 closed, next 05:00; after 23:00 the next is 02:00)");
        var points = WorldPools.RiftPoints(1010, rift.Spawner);
        var objects = WorldObjects.ObjectsOn(1010);
        var shown = points.Where(i => WorldPools.RiftShown(1010, i, objects[i], Kst(13, 2, 5), out _)).ToList();
        var picks = Enumerable.Range(0, 40).Select(n => WorldPools.PointOf(rift, Kst(13, 2) + n * 10_800_000L, 5)).ToHashSet();
        check(shown.Count == 1 && shown[0] == points[WorldPools.PointOf(rift, Kst(13, 2), 5)] && points.All(i => !WorldPools.RiftShown(1010, i, objects[i], Kst(13, 3), out _))
              && picks.Count >= 4,
              "an opening shows the rift at one of its 5 positions (the same for every copy), none while closed; the position changes between openings");
    }

    static void Choice(Action<bool, string> check)
    {
        var worlds = WorldCatalog.Default();   // capacity 500: busy at 300 by default
        var auto = RiftRules.Default;
        int Pick(int home, int a, int b, RiftRules? rules = null, int turn = 0)
        {
            var light = worlds.Of(home)!.Race == 2;
            return RiftWorlds.Pick(worlds, home, id => light ? id == 1361 ? a : id == 1362 ? b : 0 : id == 2361 ? a : id == 2362 ? b : 0, rules ?? auto, turn);
        }
        check(Pick(1361, 3, 10) == 2362 && Pick(1362, 10, 3) == 2361 && Pick(2361, 1, 4) == 1362,
              "quiet: the rift sends the player to the other faction's populated world (OURS)");
        check(Pick(1361, 0, 0) == 2361 && Pick(1362, 0, 0) == 2362 && Pick(1361, 5, 5) == 2361 && Pick(2362, 5, 5) == 1362,
              "quiet and nobody there, or a tie: the match world of the player's pair");
        check(Pick(1361, 350, 100) == 2362 && Pick(1361, 100, 300) == 2361 && Pick(2361, 400, 250) == 1362,
              "busy (a world at 60 % of 500): the rift spreads its users to the emptier world");
        check(Pick(1361, 320, 320, turn: 0) == 2361 && Pick(1361, 320, 320, turn: 1) == 2362 && Pick(1361, 320, 320, turn: 2) == 2361,
              "busy and level: the rift alternates between both worlds");
        check(Pick(1361, 3, 10, auto with { Mode = RiftWorlds.Spread }) == 2361 && Pick(1361, 350, 100, auto with { Mode = RiftWorlds.Populated }) == 2361
              && Pick(1362, 350, 100, auto with { Mode = RiftWorlds.Pair }) == 2362 && Pick(1361, 100, 160, auto with { BusyPercent = 30 }) == 2361,
              "the server-rules modes: spread, populated, pair; busy_percent moves the threshold");
        check(RiftWorlds.Pick(new WorldCatalog([new(1316, 1, "L", 0), new(2316, 2, "D", 0)]), 1316, _ => 0, auto) == 2316,
              "with one world per faction the rift leads to it");
    }

    static async Task Sessions(GameData data, Action<bool, string> check)
    {
        var rift = WorldPools.RiftOf("envobj_Verteron_RiftPortal_01")!;
        var open = Kst(13, 2) + 1000;
        var now = open;
        var worlds = WorldCatalog.Default();
        var shared = new SharedWorld(data) { Clock = () => now };
        var darkCreation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1)?.Race == 2);
        async Task<(GameSession Session, FakeStore Store)> Enter(string name, int server, int creation)
        {
            var store = new FakeStore();
            var s = new GameSession(data, new GameSessionOptions { World = shared, Worlds = worlds, WorldServer = _ => server }, new Random(5)) { Store = store };
            await s.HandleAsync(GameLogin, now);
            await s.HandleAsync(Create(name, creation), now);
            await s.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[^1].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
            return (s, store);
        }
        async Task<List<byte[]>> MoveTo(GameSession s, int map, Vector3d at, long t)
        {
            var shown = s.Teleport(map, at, t);
            shown.AddRange(await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t));
            shown.AddRange(await s.TickAsync(t + 50));
            return shown;
        }
        var (light, lightStore) = await Enter("Riftlight", 1361, 1);
        var (dark, _) = await Enter("Riftdark", 2362, darkCreation);
        check(light.ServerId == 1361 && light.RaceForChecks == 1 && dark.ServerId == 2362 && dark.RaceForChecks == 2, "a Light player on 1361 and a Dark player on 2362");
        light.RiftRulesForChecks = RiftRules.Default with { Announce = true };

        // The open rift on Verteron, its notice and its spawn.
        var objects = WorldObjects.ObjectsOn(1010);
        var points = WorldPools.RiftPoints(1010, rift.Spawner);
        var index = points[WorldPools.PointOf(rift, Kst(13, 2), points.Count)];
        var portal = objects[index];
        var key = WorldObjects.FirstKey + index;
        var near = portal.At with { X = portal.At.X + 60 };
        var shownRift = await MoveTo(light, 1010, near, now);
        var spawn = shownRift.FirstOrDefault(b => Op(b) == 0x3634 && KeyOf(b) == key);
        check(spawn is not null && spawn.SequenceEqual(GameSession.PoolSpawn(key, portal, Kst(13, 2), true)) && light.WorldPlayer?.Map?.Layer == 0,
              "the rift just opened stands at its opening's position on Verteron (0x3634 created 02:00 KST, state 06 fresh; CANDIDATE form)");
        var notice = shownRift.Count(b => b.SequenceEqual(GameSession.Notice(GameSession.RiftSpawnText)));
        var noticeAgain = (await light.TickAsync(now + 1500)).Count(b => Op(b) == GameSession.NoticeNt);
        check(notice == 1 && noticeAgain == 0, "\"A Spacetime Rift has appeared.\" once per opening (MSG_RIFT_SPAWN text, Notice_NT; CANDIDATE form)");

        // Refusals: level, the other faction, closed.
        light.SetLevelForChecks(30);
        var low = await light.HandleAsync(Use(key), now + 2000);
        check(low.Count == 1 && low[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_InvalidRiftLevel, portal.Env)),
              "under level 45 the rift is refused (EnvObj_Fail_InvalidRiftLevel)");
        dark.SetLevelForChecks(50);
        await MoveTo(dark, 1010, near, now + 2000);
        var wrongSide = await dark.HandleAsync(Use(key), now + 2100);
        check(wrongSide.Count == 1 && wrongSide[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_Relationship, portal.Env)),
              "an Asmodian cannot use the Elyos rift (MSG_RIFT_USE_FAIL_NOT_PROPER_TRIBE; result CANDIDATE)");
        // Back home for the Dark player: the population of 2362 is 1, 2361 is 0.
        await MoveTo(dark, 1110, new Vector3d(0, 0, 0), now + 2200);

        // Through the rift (quiet: the populated Dark world 2362, whose copy of Altgard is layer 1).
        light.SetLevelForChecks(50);
        var started = await light.HandleAsync(Use(key), now + 3000);
        var through = await light.TickAsync(now + 3000 + rift.InteractMs + 10);
        var move = through.FirstOrDefault(b => Op(b) == 0x3621);
        var ops = through.Select(Op).ToList();
        check(started.Any(b => Op(b) == 0x3638) && ops.IndexOf(0x363a) >= 0 && ops.IndexOf(0x363a) < ops.IndexOf(0x8d6d) && ops.IndexOf(0x8d6d) < ops.IndexOf(0x3621)
              && move is not null && BitConverter.ToInt32(move, 6) == 1110 && move[45] == 30,
              "after 2.5 s the rift moves the player to Altgard (0x3621 map 1110, TeleportEffect 30)");
        var exit = rift.Exits[WorldPools.PointOf(rift, Kst(13, 2), 5)];
        check(move is not null && Math.Abs(BitConverter.ToSingle(move, 18) - exit.X) < 1 && light.RiftVisitWorld == 2362
              && light.RiftVisit is { Map: 1010 } visit && visit.Until == now + 3000 + rift.InteractMs + 10 + 3_600_000,
              "the arrival is an exit point of RiftPortal_L1_to_D1_Teleport_1; the visit is to world 2362 (quiet: the populated Dark world, OURS) for 60 min");
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 6000);
        check(light.WorldPlayer?.Map is { MapId: 1110, Layer: 1 }, "the visitor joins world 2362's copy of Altgard (layer 1), not its own pair's (layer 0)");
        check(dark.WorldPlayer?.Map is { MapId: 1110, Layer: 1 } m && m == light.WorldPlayer!.Map, "and meets the Asmodian of 2362 there");
        var stay = await light.TickAsync(now + 6000 + 3_500_000);
        check(!stay.Any(b => Op(b) == 0x3621) && light.RiftVisitWorld == 2362, "the visit holds within its time");
        var home = await light.TickAsync(now + 3000 + rift.InteractMs + 10 + 3_600_000 + 1000);
        var back = home.FirstOrDefault(b => Op(b) == 0x3621);
        check(back is not null && BitConverter.ToInt32(back, 6) == 1010 && Math.Abs(BitConverter.ToSingle(back, 18) - near.X) < 1 && light.RiftVisitWorld is null && light.RiftVisit is null,
              "when the visit ends the player goes back to the rift they came through (map 1010)");
        await light.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 3_700_000);
        check(light.WorldPlayer?.Map is { MapId: 1010, Layer: 0 }, "back in their own world's copy (1361: layer 0)");

        // Busy: a world over the threshold spreads (pure rule, the session uses it the same way).
        light.RiftRulesForChecks = RiftRules.Default with { BusyPercent = 1 };   // 1 % of 500: 2362's 1 player is "busy"
        check(RiftWorlds.Pick(worlds, 1361, id => id == 2362 ? 5 : 0, light.RiftRulesForChecks) == 2361, "busy: the emptier Dark world");

        // A hidden cube with a key (Verteron E_HiddenCube_01_V04_001 opens only with one).
        var t = now + 4_000_000;
        var pool = WorldPools.PoolsOnMap(1010).Values.First(p => p.Positions.Count >= 10);
        await MoveTo(light, 1010, near, t);
        var cubes = light.PoolsForChecks;
        var at = cubes.Positions(pool.Key, new Random(1), t)[0];
        var cubeIndex = pool.Positions[at][pool.Slots[0]];
        var cube = WorldObjects.ObjectsOn(1010)[cubeIndex];
        var cubeKey = WorldObjects.FirstKey + cubeIndex;
        var seen = await MoveTo(light, 1010, cube.At with { X = cube.At.X + 40 }, t + 100);
        check(seen.FirstOrDefault(b => Op(b) == 0x3634 && KeyOf(b) == cubeKey) is { } cubeSpawn && cubeSpawn[^24] == 1 && cubeSpawn[^25] == 1,
              "the standing cube comes into view (0x3634, state 01: it stood before)");
        var noKey = await light.HandleAsync(Use(cubeKey), t + 200);
        check(noKey.Count == 1 && noKey[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(cubeKey, EResult.EnvObj_Fail_NotEnoughItem, cube.Env)),
              "a key-only cube without a key is refused (EnvObj_Fail_NotEnoughItem; CANDIDATE result)");
        await lightStore.GrantLootAsync(Guid.Empty, [(630900003, 1)], default);
        var opening = await light.HandleAsync(Use(cubeKey), t + 300);
        var opened = await light.TickAsync(t + 300 + cube.InteractTime + 10);
        var cubeOps = opened.Select(Op).ToList();
        var completed = opened.FirstOrDefault(b => Op(b) == 0x363a);
        check(opening.Any(b => Op(b) == 0x3638) && completed is not null
              && completed.SequenceEqual(new Aion.Protocol.Native.Generated.InteractEnvObjCompletedNT { Key = cubeKey, InteractorKey = light.PlayerKey, InteractionCount = 1, ItemId = 630900003 }.ToArray())
              && cubeOps.IndexOf(0x363a) < cubeOps.IndexOf(0x8d6d) && !cubeOps.Contains(0x363b) && cubeOps.Contains(0xe33a) && cubeOps.Contains(0x561b),
              "opened after 4 s: 0x363a {key, opener, 1, key item 630900003}, 0x8d6d, no 0x363b (OBSERVED form), then 0xe33a + 0x561b (CANDIDATE as boxes)");
        check(lightStore.Items.Where(i => i.TemplateId == 630900003).Sum(i => i.Quantity) == 0 && lightStore.Items.Any(i => KeyRewardAlways.Contains(i.TemplateId)),
              "the key is used up and the key reward is stored");
        var die = await light.TickAsync(t + 300 + cube.InteractTime + 10 + 650);
        var gone = await light.TickAsync(t + 300 + cube.InteractTime + 10 + 700);
        check(die.Any(b => b.SequenceEqual(EnvBoxPackets.EnvObjDie(cubeKey))) && gone.Any(b => b.SequenceEqual(GamePackets.EnvObjDespawn(cubeKey, 4, 3))),
              "then 0x8d05 (+650 ms) and 0x3635 {key, 4, 3} (+700 ms), as official");
        var again = await light.HandleAsync(Use(cubeKey), t + 10_000);
        check(again.Count == 1 && again[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(cubeKey, EResult.EnvObj_Fail_NotFoundObjectState, cube.Env))
              && cubes.Positions(pool.Key, new Random(1), t + 10_000)[0] == -1,
              "the opened cube is gone for this copy until its respawn");
        check(cubes.Positions(pool.Key, new Random(1), t + 300 + cube.InteractTime + 10 + 15 * 60_000)[0] >= 0, "after 15 minutes the pool has its second cube again");
        light.ExitSharedWorld();
        dark.ExitSharedWorld();
    }
}
