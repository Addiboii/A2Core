using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// World buffs passed through (fix/flight-rings-world-buffs, WorldBuffs.cs): the flight rings' abnormals, stats and flight points as
/// the official server sent them (rec 20261107-012837, the Dungeon_0005 rings), the Abyss rings placed in the open world, the
/// Overlap objects' EventFunc abnormals (Od bubbles, FP orb, buff orbs), FlyCost off the flight drain.
/// </summary>
static class WorldBuffChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int KeyOf(byte[] b) => (int)new PacketReader(b) { Offset = 2 }.Varint();
    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint((uint)key).Zeros(8).ToArray();
    static Vector3d F3(string hex)
    {
        var b = Convert.FromHexString(hex);
        return new Vector3d(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
    }

    /// <summary>The first abnormal's uid and id of a 0x382a / 0x382b.</summary>
    static (int Uid, int Abnormal) AbnormalOf(byte[] b)
    {
        var r = new PacketReader(b) { Offset = 2 };
        r.Varint();
        if (Op(b) == 0x382a) r.Varint();
        r.U8();
        return ((int)r.Varint(), r.I32());
    }

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Official bytes (rec 20261107-012837, player key 5775, the first ring #8706-#8720, the second #8806-#8822) ----------
        const int key = 5775;
        check(PlayerPackets.AbnormalAdd(key, [new AbnormalWire(39, 19000064, 3000, 0x01a1b3a16c4f, CasterKey: key, Level: 1, RangeBase: F3("691032c539b488c6da5faec1"))])
                  .SequenceEqual(Convert.FromHexString("2a388f2d01112700eb2101b80b0000000000004f6ca1b3a10100008f2d0100691032c539b488c6da5faec1"))
              && PlayerPackets.AbnormalAdd(key, [new AbnormalWire(40, 19000065, 100, 0x01a1b3a160fb, CasterKey: key, Level: 1, RangeBase: F3("691032c539b488c6da5faec1"))])
                  .SequenceEqual(Convert.FromHexString("2a388f2d01112801eb21016400000000000000fb60a1b3a10100008f2d0100691032c539b488c6da5faec1")),
              "the flight ring's abnormals 19000064 (3000 ms) and 19000065 (100 ms) as official (#8710, #8711)");
        check(GamePackets.CharStatsUpdate(key, [(214, 15000), (247, 10000)], 0).SequenceEqual(Convert.FromHexString("4a368f2d02d600983a0000f700102700000000000000000000"))
              && GamePackets.CharStatsUpdate(key, [(214, 0), (247, 0)], 0).SequenceEqual(Convert.FromHexString("4a368f2d02d60000000000f700000000000000000000000000"))
              && GamePackets.CharStatsUpdate(key, 0).SequenceEqual(Convert.FromHexString("4a368f2d000000000000000000")),
              "the ring's 0x364a FlySpeed 214 = 15000 / FlyCost 247 = 10000, back to 0 at the end, empty at a second ring (#8712, #9010, #8811)");
        check(EnvBoxPackets.AbnormalHeal(key, key, 40, 190000651, 0, 100000).SequenceEqual(Convert.FromHexString("05388f2d028f2d280b2e530ba08d06"))
              && PlayerPackets.AbnormalRemove(key, [(40, 1)]).SequenceEqual(Convert.FromHexString("2c388f2d01002801"))
              && GamePackets.CharPoints(key, [(CharPoint.Fp, 250000)]).SequenceEqual(Convert.FromHexString("008d8f2d01010690d00300")),
              "the Fot tick: 0x3805 mask 2 effect 190000651 amount 100000, 0x382c reason 1, 0x8d00 FP 250000 as official (#8718-#8720)");
        check(PlayerPackets.AbnormalChange(key, new AbnormalWire(39, 19000064, 3000, 0x01a1b3a1725e, CasterKey: key, Level: 1, RangeBase: F3("0cd6cfc5041529c6b9a4e644")), true)
                  .SequenceEqual(Convert.FromHexString("2b388f2d112700eb2101b80b0000000000005e72a1b3a10100008f2d01020cd6cfc5041529c6b9a4e644")),
              "a second ring within 3 s renews 19000064 with 0x382b (same uid, show 1) as official (#8809)");

        // ---------- Data ----------
        check(AbnormalTable.Embedded.Of(19000064) is { Effects: [{ Type: "StatChange", Values: ["FlySpeed", "15000", ..] }, { Type: "StatChange", Values: ["FlyCost", "10000", ..] }] }
              && AbnormalTable.Embedded.Of(19000065) is { Effects: [{ Id: 190000651, Type: "Fot" } fot] } && fot.Int(1) == 100000
              && AbnormalTable.Embedded.Of(19000010) is { Effects.Count: 2 } && AbnormalTable.Embedded.Of(19000063)?.Effects[0].Int(1) == 50000
              && AbnormalTable.Embedded.Of(19000028)?.Effects[0].Type == "Fot",
              "abnormal-table.json carries the EventFunc abnormals: the rings' FlySpeed/FlyCost and Fot rows, the FP orb's Fot");
        check(WorldBuffs.Ring(1401100).SequenceEqual(WorldBuffs.RingNormal) && WorldBuffs.Ring(1100033).SequenceEqual(WorldBuffs.RingNormal)
              && WorldBuffs.Ring(1100016).SequenceEqual(WorldBuffs.RingAbyss) && WorldBuffs.Ring(1100401).SequenceEqual(WorldBuffs.RingAbyss)
              && WorldBuffs.RingNormal.SequenceEqual([new WorldBuffs.Effect(19000064, 3000), new WorldBuffs.Effect(19000065, 100)])
              && WorldBuffs.RingAbyss.SequenceEqual([new WorldBuffs.Effect(19000010, 3000), new WorldBuffs.Effect(19000063, 100)]),
              "rings: EventFunc AddAbnormal_FlightRing_Normal (19000064 3 s + 19000065 0.1 s), the Abyss rings AddAbnormal_FlightRing_Abyss");
        static int First(int env) => WorldBuffs.Overlap(env) is [var e, ..] ? e.Abnormal : 0;
        check(First(1000277) == 19000013 && First(1000239) == 19000019 && First(1000241) == 19000021 && First(1200527) == 19000028
              && First(1401151) == 19005071 && First(1000514) == 19005151 && First(1401331) == 30016 && First(1401740) == 30046
              && First(1402034) == 19003301 && First(1100045) == 19000032 && First(1100049) == 19000044 && First(1200694) == 19000038
              && First(1100100) == 19000055 && First(1100105) == 0 && First(1100023) == 0 && First(1000281) == 0,
              "Overlap objects -> EventFunc abnormals: recovery orbs, Od bubbles, the FP orb, buff orbs, arena orbs, daily coins, racing, field events");
        foreach (var (map, count) in new[] { (20, 49), (23, 32), (80, 25) })
            check(WorldObjects.RingsOn(map).Count == count && WorldObjects.RingsOn(map).All(i => WorldObjects.ObjectsOn(map)[i] is { FlightRing: true, LongRange: true }),
                  $"map {map} has its {count} Abyss flight rings (MapData E_AR1/E_AB1_FlyCircle_001; missing before)");

        // ---------- Flying through the subspace's ring ----------
        var flyer = await storedWorldSession("RingFlyer", new FakeStore());
        var rings = WorldObjects.RingsOn(200005);
        var ring = WorldObjects.ObjectsOn(200005)[rings[0]];
        var ringKey = WorldObjects.FirstKey + rings[0];
        flyer.Teleport(200005, ring.At, now);
        await flyer.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await flyer.TickAsync(now + 50);
        var fpMax = flyer.AscendForChecks(0) is { Count: > 0 } ? flyer.Flight.FpMax : 0;
        flyer.AscendForChecks(fpMax - 64_500);
        var pk = flyer.PlayerKey;
        var t0 = now + 1000;
        var passed = await flyer.HandleAsync(Use(ringKey), t0);
        var add64 = passed.FirstOrDefault(b => Op(b) == 0x382a && AbnormalOf(b).Abnormal == 19000064);
        var add65 = passed.FirstOrDefault(b => Op(b) == 0x382a && AbnormalOf(b).Abnormal == 19000065);
        check(fpMax > 0 && passed.Select(Op).SequenceEqual(new ushort[] { 0x3637, 0x382a, 0x382a, 0x364a }) && passed[0].SequenceEqual(GamePackets.InteractEnvObjReply(ringKey))
              && add64 is not null && add65 is not null && passed[3].SequenceEqual(GamePackets.CharStatsUpdate(pk, [(214, 15000), (247, 10000)], 0))
              && flyer.ActiveBuffs.Contains(19000064) && flyer.ActiveBuffs.Contains(19000065),
              "flying through a ring: 0x3637, 0x382a 19000064, 0x382a 19000065, 0x364a {FlySpeed 15000, FlyCost 10000} - nothing else (official)");
        check(flyer.FlightDrain(GameSession.FlyDrain) == 0 && flyer.FlightDrain(GameSession.FlySprintDrain) == 5000,
              "while 19000064 lasts FlyCost takes 10000 FP/s off the drain: none in plain flight, 500 per 100 ms running (official)");
        var uid65 = AbnormalOf(add65!).Uid;
        var tick = await flyer.TickAsync(t0 + 100);
        var heal = tick.FindIndex(b => b.SequenceEqual(EnvBoxPackets.AbnormalHeal(pk, pk, uid65, 190000651, 0, 100000)));
        var removed = tick.FindIndex(b => b.SequenceEqual(PlayerPackets.AbnormalRemove(pk, [(uid65, 1)])));
        var fpAt = tick.FindLastIndex(b => b.SequenceEqual(GamePackets.CharPoints(pk, [(CharPoint.Fp, fpMax)])));
        check(heal >= 0 && removed > heal && fpAt > removed && flyer.Flight.Fp == fpMax && !flyer.ActiveBuffs.Contains(19000065)
              && !tick.Skip(heal).Take(fpAt - heal).Any(b => Op(b) is 0x364a or 0x3656),
              "100 ms later the Fot tick: 0x3805 (amount 100000), 0x382c, 0x8d00 FP at the maximum (official order, no stat update)");
        var uid64 = AbnormalOf(add64!).Uid;
        var again = await flyer.HandleAsync(Use(ringKey), t0 + 1000);
        check(again.Select(Op).SequenceEqual(new ushort[] { 0x3637, 0x382b, 0x382a, 0x364a }) && AbnormalOf(again[1]) == (uid64, 19000064)
              && again[1][^13] == 2 && again[3].SequenceEqual(GamePackets.CharStatsUpdate(pk, 0)),
              "a second ring within 3 s: 0x3637, 0x382b (the same uid, show 1), a new 0x382a 19000065, an empty 0x364a (official)");
        await flyer.TickAsync(t0 + 1100);
        var ended = await flyer.TickAsync(t0 + 4000 + 10);
        check(ended.Any(b => b.SequenceEqual(PlayerPackets.AbnormalRemove(pk, [(uid64, 1)])))
              && ended.Any(b => b.SequenceEqual(GamePackets.CharStatsUpdate(pk, [(214, 0), (247, 0)], 0))) && !ended.Any(b => Op(b) == 0x3656)
              && flyer.FlightDrain(GameSession.FlyDrain) == GameSession.FlyDrain,
              "3 s after the last ring 19000064 ends: 0x382c reason 1 and 0x364a {214 = 0, 247 = 0} (no combat power update); the drain is back");
        flyer.Teleport(200005, ring.At with { X = ring.At.X + GameSession.RingReach + 1000 }, t0 + 5000);
        check((await flyer.HandleAsync(Use(ringKey), t0 + 5100)).Single().SequenceEqual(GamePackets.InteractEnvObjReply(ringKey, 1)),
              "a ring pass reported far from the ring is refused");
        flyer.ExitSharedWorld();

        // ---------- An Abyss ring in the open world (Reshanta A): seen from afar, 19000010 + 19000063 ----------
        var abyssFlyer = await storedWorldSession("AbyssRingFlyer", new FakeStore());
        var abyssIndex = WorldObjects.RingsOn(20)[0];
        var abyssRing = WorldObjects.ObjectsOn(20)[abyssIndex];
        var abyssKey = WorldObjects.FirstKey + abyssIndex;
        abyssFlyer.Teleport(20, abyssRing.At with { X = abyssRing.At.X + 10_000 }, now);
        var seen = await abyssFlyer.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);   // the load shows what is in view
        seen.AddRange(await abyssFlyer.TickAsync(now + 50));
        check(seen.Any(b => Op(b) == 0x3634 && KeyOf(b) == abyssKey), "an open-world flight ring 100 m away is spawned (RingRange; objects otherwise within 60 m)");
        abyssFlyer.Teleport(20, abyssRing.At, now + 100);
        await abyssFlyer.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 100);
        abyssFlyer.AscendForChecks(0);
        var abyssPassed = await abyssFlyer.HandleAsync(Use(abyssKey), now + 1000);
        var abyss63 = abyssPassed.FirstOrDefault(b => Op(b) == 0x382a && AbnormalOf(b).Abnormal == 19000063);
        var abyssTick = await abyssFlyer.TickAsync(now + 1100);
        check(abyssPassed.Any(b => Op(b) == 0x382a && AbnormalOf(b).Abnormal == 19000010) && abyss63 is not null
              && abyssTick.Any(b => b.SequenceEqual(EnvBoxPackets.AbnormalHeal(abyssFlyer.PlayerKey, abyssFlyer.PlayerKey, AbnormalOf(abyss63).Uid, 190000631, 0, 50000)))
              && abyssFlyer.Flight.Fp >= 50_000,
              "an Abyss ring gives 19000010 (FlySpeed/FlyCost 3 s) and 19000063: 50000 FP 100 ms later");
        abyssFlyer.ExitSharedWorld();

        // ---------- A placed buff orb (the Awakening dungeon): its EventFunc abnormal, the orb spent ----------
        var orbWalker = await storedWorldSession("BuffOrbWalker", new FakeStore());
        var orbMap = 910011;
        var orbIndex = Enumerable.Range(0, WorldObjects.ObjectsOn(orbMap).Count).First(i => WorldObjects.ObjectsOn(orbMap)[i].Env == 1401151);
        var orb = WorldObjects.ObjectsOn(orbMap)[orbIndex];
        orbWalker.Teleport(orbMap, orb.At, now);
        await orbWalker.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var orbTaken = await orbWalker.HandleAsync(Use(WorldObjects.FirstKey + orbIndex), now + 500);
        var orbOps = orbTaken.Select(Op).ToList();
        check(orbTaken.Any(b => Op(b) == 0x382a && AbnormalOf(b).Abnormal == 19005071) && orbWalker.ActiveBuffs.Contains(19005071)
              && orbOps.IndexOf(0x3637) < orbOps.IndexOf(0x382a) && orbOps.Contains(0x364a),
              "walking into a buff orb (E_Dungeon_Awaken_BuffOrb_01): EventFunc Event_EnvObj_BuffOrb_01, abnormal 19005071 (AmplifyAllDamage) and the stats");
        orbWalker.ExitSharedWorld();
    }
}
