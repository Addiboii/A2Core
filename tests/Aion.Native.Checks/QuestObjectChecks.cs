using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Quest objects and traps (fix/quest-objects-traps): the Drakana Heist's traps (HQ1101050 "Search inside the Brigade Supply Depot",
/// map 100013) with the official bytes and timings of capture-13700-20261107-013918, the Draco bomb's self-destruct, the reach of
/// immobile Self-area NPC skills, and the placed objects the official server spawns on the audited maps.
/// </summary>
static class QuestObjectChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int KeyOf(byte[] b) => (int)new PacketReader(b) { Offset = 2 }.Varint();
    static byte[] H(string hex) => Convert.FromHexString(hex);
    static Vector3d F3(string hex)
    {
        var b = H(hex);
        return new Vector3d(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
    }
    static byte MaskOf(byte[] b) { var r = new PacketReader(b) { Offset = 2 }; r.Varint(); return r.U8(); }
    static float F(string hex) => BitConverter.ToSingle(H(hex), 0);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Trap table ----------
        var traps = TrapTable.Embedded;
        check(traps.Of(2290018) is { Skill: 1236490, CastMs: 2000, Once: true, Near: false, FirstMs: 100, DamagePercent: 10, Untouchable: true, AttackSpeed: true, GoneMs: 1250 }
              && traps.Of(2290014) is { Skill: 1980140, CastMs: 500, Once: true, Near: true, NearRadius: 300, AttackSpeed: false, MarkAbnormal: 12262701, GoneMs: 4350 }
              && traps.SelfDestructOf(2290013) is { Skill: 1239040, EndMs: 400, GoneMs: 500 } && traps.Of(2290013) is null && traps.Of(2700011) is { Once: false },
              "Drakana Heist traps: floor bombs 2290018 (once, timer), hidden floor traps 2290014 (once, near 300), Draco bombs 2290013 self-destruct; the rockfall stays periodic");
        check(NpcSkillEffects.Of(1236490)?.Area is { Radius: 300 } && NpcSkillEffects.Of(1980140)?.Area is { Radius: 300 }
              && SpawnPoses.Of(100013, "M_L0_Starter_HQ004_Bomb_Muti_up_1", 2290014).Abnormal == 10000001
              && SpawnPoses.Of(100013, "M_L0_Starter_HQ004_Bomb_05_Mulit_1", 2290018).Abnormal == 10000001,
              "the trap skills hit their client areas (circle 300) and the traps appear with abnormal 10000001 as official");

        // ---------- Official bytes (rec 013918) ----------
        check(GamePackets.SkillCastingStart(32909, 1980140, 32909, F("0090ab43"), F3("a877c1454af467c700347946"), 500, null)
                  .SequenceEqual(H("09388d810200ec361e0001028d81020090ab43a877c1454af467c700347946f40301")),
              "the floor trap's cast 0x3809 mask 0 (no attack speed) as official #16634");
        check(GamePackets.SkillCastingStart(22929, 1236490, 22929, 0, F3("038c734485cf59c700608546"), 2000)
                  .SequenceEqual(H("093891b301020ade1200010291b30100000000038c734485cf59c700608546d00f01904e"))
              && GamePackets.SkillStart(1236490, 1, 0, F3("038c734485cf59c700608546"), targetKey: 22929, actorKey: 22929, cooldown: 8000)
                  .SequenceEqual(H("023891b301000ade1200010291b30100000000038c734485cf59c700608546904e01c03e"))
              && GamePackets.SkillEnd(22929, 1236490, 1).SequenceEqual(H("063891b3010ade12000100"))
              && GamePackets.NpcHp(22929, 0).SequenceEqual(H("008d91b3010201000000000000000000"))
              && GamePackets.SelfDeath(22929).SequenceEqual(H("048d91b301000000000000000000000000000000000000"))
              && GamePackets.Death(22929, 3).SequenceEqual(H("423691b3010003")),
              "the floor bomb's cast, blast, end, HP 0, kill notice and corpse removal as official #17912-#18165");
        check(GamePackets.NpcSkillHit(8097, 44934, 1980140, 1, 198014011, 45).SequenceEqual(H("0438a13f060086df02ec361e0001020000023b74cd0b01000000904e2d0100"))
              && GamePackets.NpcEffectHit(8097, 35748, 1980140, 1, 198014012).SequenceEqual(H("0438a13f0000a49702ec361e0001023c74cd0b01000000904e0100")),
              "the floor trap's damage hit and knockdown-row hit on the player as official #16880 / #16785");
        check(PlayerPackets.AbnormalAdd(35748, [new AbnormalWire(2, 12262701, -1, Target.PermanentExpiry, CasterKey: 35748, RangeBase: F3("0050aa45005066c700c47946"))])
                  .SequenceEqual(H("2a38a497020111022d1dbb00ffffffffffffffff8075d52abb030000a4970201000050aa45005066c700c47946"))
              && PlayerPackets.AbnormalRemove(35748, [(2, (byte)19)]).SequenceEqual(H("2c38a4970201000213"))
              && GamePackets.TargetUpdate(32909, 8097).SequenceEqual(H("35388d810200a13f")),
              "the floor trap's standing abnormal 12262701, its removal at death and its aim at the player as official #16018 / #16823 / #16633");
        check(new Aion.Protocol.Native.Generated.AbnormalMoveInstantNT { Key = 8097, CasterKey = 35748, EffectId = 160221011,
                  GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(F3("0068a645003964c700507c46")), MoveType = 4, StartTerrainType = 1, EndTerrainType = 1 }.ToArray()
                  .SequenceEqual(H("1638a13fa4970253c78c090068a645003964c700507c46040101")),
              "the trap's knockdown throw 0x3816 as official #16797");

        // ---------- Reach of an immobile Self-area skill ----------
        var stand = new Target { Key = 1, Name = "t", HpMax = 10, Hp = 10, NpcId = 2290014, Radius = 10, Skills = [1980140] };
        check(NpcAi.AttackRangeForChecks(stand, 1980140, GameData.Embedded.NpcAnySkill(1980140)) >= 300,
              "an immobile NPC's Self skill with no range reaches its area (300), not just its body (live 2026-11-12: such traps never fired)");

        // ---------- The traps in a Drakana Heist copy ----------
        var store = new FakeStore();
        var player = await storedWorldSession("Trapper", store);
        var spawns = GameData.Embedded.Spawns(100013);
        var floorTrap = spawns.First(s => s.Name == "M_L0_Starter_HQ004_Bomb_Muti_up_1");
        var near = new Vector3d(floorTrap.Position[0] + 150, floorTrap.Position[1], floorTrap.Position[2]);
        // The far corner of the depot: no floor trap within reach while the bombs go off.
        var away = new Vector3d(floorTrap.Position[0] - 20000, floorTrap.Position[1], floorTrap.Position[2]);
        player.Teleport(100013, away, now);
        await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        check(player.InstanceKey != 0 && player.Targets.Values.Count(t => t.NpcId == 2290018) == 55 && player.Targets.Values.Count(t => t.NpcId == 2290014) == 7,
              "the Drakana Heist copy holds the 55 floor bombs and 7 floor traps of its MapData (dormant until their StartSpawner)");
        var host = (IMapEventHost)player;
        host.Spawn("M_L0_Starter_HQ004_Bomb_Muti_01_001", 0);
        player.DrainMapEventsForChecks();
        var bombs = player.Targets.Values.Where(t => t.Name == "M_L0_Starter_HQ004_Bomb_Muti_01_001").Select(t => t.Key).ToHashSet();
        var bomb = bombs.First();
        var timeline = new List<(long At, byte[] Packet)>();
        var t0 = now + 1000;
        for (var t = t0; t <= t0 + 4000; t += 50)
            foreach (var p in player.TrapTickForChecks(t)) timeline.Add((t - t0, p));
        var mine = timeline.Where(x => KeyOf(x.Packet) == bomb).ToList();
        long At(ushort op) => mine.FirstOrDefault(x => Op(x.Packet) == op).At;
        check(mine.Select(x => Op(x.Packet)).SequenceEqual(new ushort[] { 0x8d21, 0x3809, 0x3802, 0x3806, 0x8d00, 0x8d04, 0x8d21, 0x3642 })
              && At(0x8d21) == 50 && At(0x3809) == 100 && At(0x3802) == 2100 && At(0x3806) == 2150 && At(0x8d00) == 2250 && At(0x8d04) == 2300 && At(0x3642) == 3350,
              $"a floor bomb fires once and dies at the official times (+50 combat, +100 cast, +2100 blast, +2150 end, +2250 HP 0, +2300 kill notice, +3350 gone): {string.Join(",", mine.Select(x => $"{Op(x.Packet):x4}@{x.At}"))}");
        check(bombs.All(k => player.Targets[k] is { Dead: true, CorpseRemoved: true }) && player.TrapTickForChecks(t0 + 20_000).Count == 0,
              "all five bombs of Bomb_Muti_01_001 are gone after their blast and never fire again (official: 55 casts for 55 bombs)");
        check(traps.Untouchable(2290018) && GameData.Embedded.Spawns(100013).Any(s => s.NpcId == 2290018),
              "players' area skills pass the floor bombs by (NpcData bCanBeAttacked false)");

        // A hidden floor trap: the player steps within its reach.
        player.Teleport(100013, near, now + 6000);
        await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 6000);
        host.Spawn("M_L0_Starter_HQ004_Bomb_Muti_up_1", 0);
        player.DrainMapEventsForChecks();
        var trapKey = player.Targets.Values.Where(t => t.NpcId == 2290014 && !t.Dead).OrderBy(t => t.Position.DistanceSquared(near)).First().Key;
        var hpBefore = player.Resource.Hp;
        var trapLine = new List<(long At, byte[] Packet)>();
        var t1 = now + 7000;
        for (var t = t1; t <= t1 + 5000; t += 50)
            foreach (var p in player.TrapTickForChecks(t)) trapLine.Add((t - t1, p));
        var trapOwn = trapLine.Where(x => KeyOf(x.Packet) == trapKey).ToList();
        long TAt(ushort op) => trapOwn.FirstOrDefault(x => Op(x.Packet) == op).At;
        var onPlayer = trapLine.Where(x => KeyOf(x.Packet) == player.PlayerKey).Select(x => Op(x.Packet)).ToList();
        var firing = trapOwn.Where(x => Op(x.Packet) != 0x382a).ToList();
        check(trapOwn[0].Packet[0] == 0x2a && Op(trapOwn[0].Packet) == 0x382a && firing.Take(3).Select(x => Op(x.Packet)).SequenceEqual(new ushort[] { 0x3835, 0x3809, 0x8d21 }) && firing[0].At == firing[2].At
              && MaskOf(trapOwn.First(x => Op(x.Packet) == 0x3809).Packet) == 0
              && TAt(0x3802) - TAt(0x3809) == 500 && TAt(0x3806) - TAt(0x3802) == 150 && TAt(0x8d04) - TAt(0x3802) == 300 && TAt(0x382c) - TAt(0x3802) == 300
              && TAt(0x3642) - TAt(0x3802) == 4350,
              $"a floor trap fires once the player is within reach (0x3835, cast mask 0, 0x8d21), blasts 500 ms later and is gone 4350 ms after: {string.Join(",", trapOwn.Select(x => $"{Op(x.Packet):x4}@{x.At}"))}");
        check(onPlayer.Contains(0x3804) && onPlayer.Contains(0x382a) && onPlayer.Contains(0x8d00) && onPlayer.Contains(0x3816) && player.Resource.Hp < hpBefore,
              "its blast hits the player standing on it: damage and knockdown-row hits, abnormal 16022101, HP down, the knockdown throw 0x3816");
        player.ExitSharedWorld();

        // ---------- The Draco bomb blows itself up ----------
        var ai = new NpcAi(new Random(7), GameData.Embedded);
        var all = Combat.MapTargets(GameData.Embedded, 100013);
        var draco = all.Values.First(t => t.NpcId == 2290013);
        var only = new Dictionary<int, Target> { [draco.Key] = draco };
        var me = new Vector3d(draco.Position.X + 120, draco.Position.Y, draco.Position.Z);
        var lines = new List<(long At, byte[] Packet)>();
        for (var t = now; t <= now + 8000; t += 50)
            foreach (var p in ai.Tick(only, [(NpcAi.PlayerKey, me)], t, (_, _) => [])) lines.Add((t, p));
        var own = lines.Where(x => KeyOf(x.Packet) == draco.Key).ToList();
        var startAt = own.FirstOrDefault(x => Op(x.Packet) == 0x3802).At;
        var goneAt = own.FirstOrDefault(x => Op(x.Packet) == 0x3642).At;
        check(startAt > 0 && own.First(x => Op(x.Packet) == 0x3642).Packet[^1] == 7 && goneAt - startAt == 500
              && own.First(x => Op(x.Packet) == 0x3806).At - startAt == 400 && draco.CorpseRemoved && ai.TakeVanished().Contains(draco.Key),
              "a Draco bomb casts 1239040 and vanishes after its blast (0x3806 +400, 0x3642 act 7 +500, as official #16550-#16663)");

        // ---------- Placed objects of the audited maps ----------
        int Count(int map) => WorldObjects.ObjectsOn(map).Count(o => o.Pool is null && o.Rift is null);   // world-objects.json's own (pools and rifts: RiftCubeChecks)
        check(Count(1000) == 33 && Count(1010) == 4546 && Count(1011) == 2113 && Count(1110) == 5021 && Count(1111) == 1798 && Count(850015) == 25 && Count(100013) == 8,
              "objects per audited map: Poeta 33, Verteron 4546, Eltnen 2113, Altgard 5021, Morheim 1798, D_A Layer_D 25, Drakana Heist 8");
        var towers = WorldObjects.ObjectsOn(1000).Where(o => o.Env == 1201049).ToList();
        check(towers.Count == 3 && towers.All(o => o.Relationship == 15) && towers.Any(o => o.UniqueKey == -1909012937)
              && WorldObjects.ObjectsOn(1000).Count(o => o.Env == 1201224) == 23 && WorldObjects.ObjectsOn(1000).Any(o => o.Env == 1201056),
              "the Drakan base decorations of Poeta region 7 (3 towers EnvObj_Destroy, 23 bomb-box piles) and the region 4 wood wall are placed as official");
        check(WorldObjects.ObjectsOn(1110).Count(o => o.Name.StartsWith("E_Piano") || EnvUsages.Of(o.Env)?.Name?.StartsWith("E_Piano") == true) > 0
              && !WorldObjects.ObjectsOn(1110).Any(o => EnvUsages.Of(o.Env)?.Name == "E_D1_TreasureBoxRare_002" && o.Pool is null)
              && !WorldObjects.ObjectsOn(1010).Any(o => EnvUsages.Of(o.Env)?.Name == "E_HiddenCube_01_V04_001" && o.Pool is null),
              "Altgard's pianos are placed; the random pools (treasure boxes, hidden cubes) are not fixed objects (pools: WorldPools, fix/rifts-cubes)");
        var poeta = WorldObjects.ObjectsOn(1000);
        check(poeta.Select(o => (o.Env, o.UniqueKey, o.Position[0], o.Position[1], o.Position[2])).Distinct().Count() == poeta.Count,
              "no placement is listed twice");
    }
}
