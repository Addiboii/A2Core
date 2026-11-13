using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Map markers of instance boxes (fix/map-dots): a bGetRewardOnce object's 0x3634 carries its spawn's UseEnvObjSpawnerName
/// achievement (the trace form, mask 0x0c), every other placed object keeps the placed form (official bytes of a recovery orb box,
/// rec 20261106-105448 #40091); MapEventJoin_NT lists the first step's objectives as zeros (official join rec 20261107-014745 #974);
/// the stronghold GarrisonTerritory_L1_H1 (153001) entered in a stored session shows both.
/// </summary>
static class MapDotsChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int KeyOf(byte[] b) => (int)new PacketReader(b) { Offset = 2 }.Varint();
    static Vector3d F3(string hex)
    {
        var b = Convert.FromHexString(hex);
        return new Vector3d(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
    }

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Official bytes ----------
        check(GamePackets.EnvObjSpawn(39301, 1400607, F3("0000a0c100a02a4500007ec3"), 180, 0x01a1b08295cb, unchecked((int)0xdaade1a6), 7)
                  .SequenceEqual(Convert.FromHexString("343685b30204001f5f15000000a0c100a02a4500007ec30000000000003443010101cb9582b0a10100000000000000000000a6e1adda070000")),
              "a recovery orb box's 0x3634 (E_All_RecoveryOrbBoxRandom_01, not bGetRewardOnce) as official (#40091: mask 4, no achievement)");
        check(GamePackets.MapEventJoin(0x06be82, "World_L_Starter_InstanceLayer_2_MapEvent", 2, 1, 1, [0, 0], 0x01a1b3b10196, new int[8])
                  .SequenceEqual(Convert.FromHexString("2e9282be060028576f726c645f4c5f537461727465725f496e7374616e63654c617965725f325f4d61704576656e740201010200009601b1b3a10100000800000000000000000000000000000000")),
              "0x922e at an event's start lists the first step's objectives as zeros (rec 20261107-014745 #974: InstanceLayer_2, 2 objectives)");
        // A bGetRewardOnce quest object that is not an Empyrean Trace (world-objects.json has no achievement for it): the official
        // spawn carries its spawn's UseEnvObjSpawnerName achievement 100050001 (rec 20261106-115118 #31926); placed data rounds the
        // position and yaw, so the official floats are put back for the byte comparison.
        var fragment = WorldObjects.ObjectsOn(1110).Single(o => o.Name == "E_D1_HQ_GodFragment_02_001");
        var official = Convert.FromHexString("3436b8d0010c008eda1300b49a9ec7710107c84dd30c470000000000d03643010101dc7767a5a10100000000000000000000a378066e070051a4f60500");
        var fragmentAt = Convert.FromHexString("b49a9ec7710107c84dd30c47");
        check(fragment.Achievement == 0 && GameSession.SpawnAchievement(fragment) == 100050001
              && GameSession.PlacedSpawnForChecks(26680, fragment with
                 {
                     Position = [BitConverter.ToSingle(fragmentAt, 0), BitConverter.ToSingle(fragmentAt, 4), BitConverter.ToSingle(fragmentAt, 8)], Yaw = 182.8125,
                 }, 0x01a1a56777dc).SequenceEqual(official),
              "a bGetRewardOnce quest object's 0x3634 as official (E_D1_HQ_GodFragment_02_001 #31926: mask 0x0c, its spawn achievement 100050001)");

        // ---------- Which placed objects carry a spawn achievement ----------
        var h1 = WorldObjects.ObjectsOn(153001);
        int Achievement(string name) => h1.Where(o => o.Name == name).Select(GameSession.SpawnAchievement).Distinct().SingleOrDefault(-1);
        check(Achievement("E_L1_H1_TargetBox") == 110010050 && Achievement("Envobj_L1_H1_BalaurBox_1") == 110010000 && Achievement("Envobj_L1_H1_BalaurBox_3") == 110010002
              && Achievement("Envobj_L1_H1_RewardBox") == 110010056,
              "stronghold H1: Supply Chests 110010050, Stronghold Chests 110010000-110010002, reward chest 110010056 (their spawns' UseEnvObjSpawnerName achievements)");
        check(Achievement("M_L1_CT_H1_INS_OrbBox_Multi_01") == 0 && Achievement("EnvObj_L1_CT_H1_INS_Insk_1") == 0,
              "stronghold H1: the orb chests and the Kisk (not bGetRewardOnce) carry none");
        var all = Enumerable.Range(0, 1_000_000).Select(m => WorldObjects.ObjectsOn(m)).Where(l => l.Count > 0).SelectMany(l => l).ToList();
        var marked = all.Where(o => GameSession.SpawnAchievement(o) != 0).ToList();
        check(marked.All(o => EnvUsages.Of(o.Env)?.RewardOnce == true) && marked.Count(o => o.Achievement == 0) > 0
              && all.Where(o => o.Achievement == 0 && EnvUsages.Of(o.Env)?.RewardOnce != true).All(o => GameSession.SpawnAchievement(o) == 0),
              $"only bGetRewardOnce objects carry one ({marked.Count} placed, {marked.Count(o => o.Achievement == 0)} of them boxes besides the traces)");
        var common = all.FirstOrDefault(o => o.Env == 1400711);
        check(common is not null && GameSession.SpawnAchievement(common) != 0 && AchievementTables.Embedded.Of(GameSession.SpawnAchievement(common)) is { Type: "UseEnvObjSpawnerName" } entry
              && entry.Has(common.Name),
              "a dungeon reward box (E_CommonRewardBox_001) carries its spawn's achievement");
        var box = h1.First(o => o.Name == "E_L1_H1_TargetBox");
        var orb = h1.First(o => o.Name == "M_L1_CT_H1_INS_OrbBox_Multi_01");
        check(GameSession.PlacedSpawnForChecks(910000, box, 0x01a1cad7a26c)
                  .SequenceEqual(MonolithPackets.TraceSpawn(910000, box.Env, box.At, box.Yaw, 0x01a1cad7a26c, box.UniqueKey, 7, 110010050))
              && GameSession.PlacedSpawnForChecks(910013, orb, 0x01a1cad7a26c)
                  .SequenceEqual(GamePackets.EnvObjSpawn(910013, orb.Env, orb.At, orb.Yaw, 0x01a1cad7a26c, orb.UniqueKey, 7)),
              "a Supply Chest spawns in the trace form (mask 0x0c, EnvObj_Neutral, achievement after the phase); an orb chest in the placed form");

        // ---------- Entering the stronghold in a stored session ----------
        var store = new FakeStore();
        var player = await storedWorldSession("MapDotter", store);
        var dungeon = MapEventTables.Embedded.DungeonIdOfMap(153001);
        player.Teleport(1010, box.At with { X = box.At.X + 50 }, now);
        await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var moved = player.EnterDungeonForChecks(dungeon, now + 100, byTrigger: false);
        var loaded = await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 200);
        loaded.AddRange(await player.TickAsync(now + 300));
        check(dungeon != 0 && moved.Any(b => Op(b) == 0x3621) && player.InstanceKey != 0, "the stronghold H1 (map 153001) is entered (0x3621, a private copy)");
        var join = loaded.FirstOrDefault(b => Op(b) == 0x922e);
        const string eventName = "World_L1_GarrisonTerritory_H1_MapEvent";
        check(join is not null && join.Length > 58 && join[6] == eventName.Length && join.AsSpan(45, 13).SequenceEqual(new byte[] { 2, 1, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
              "its 0x922e lists step 1's nine objectives (8 box triggers, UseEnvObj E_L1_Garrison_H1_Box_01 x5) as zeros, not an empty list");
        var index = Enumerable.Range(0, h1.Count).OrderBy(i => h1[i].At.DistanceSquared(box.At)).First(i => h1[i].Name == "E_L1_H1_TargetBox");
        var spawn = loaded.Concat(moved).FirstOrDefault(b => Op(b) == 0x3634 && KeyOf(b) == WorldObjects.FirstKey + index);
        check(spawn is not null && spawn[5] == 0x0c && spawn[^5..^1].SequenceEqual(BitConverter.GetBytes(110010050)),
              "the Supply Chest comes into view with its spawn achievement (0x3634 mask 0x0c, 110010050): the client can draw its map marker");
        player.ExitSharedWorld();
    }
}
