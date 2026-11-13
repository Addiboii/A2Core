using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Dungeon boxes (GameSession.EnvBoxes.cs): the official bytes of the box reward, the summoned orb, its pickup and refusal
/// (rec 20261106-105448), a Seal reward box played in a stored session (300 Power Shards once, the second use refused), a recovery
/// orb box (orbs appear around it), an orb pickup (the abnormal, the heal, gone) and a map event summoning an orb.
/// </summary>
static class BoxChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int KeyOf(byte[] b) => (int)new PacketReader(b) { Offset = 2 }.Varint();
    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint((uint)key).Zeros(8).ToArray();
    static Vector3d F3(string hex)
    {
        var b = Convert.FromHexString(hex);
        return new Vector3d(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
    }

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- Official bytes (rec 20261106-105448, Seal_Altgard_013) ----------
        check(EnvBoxPackets.SummonedEnvObjSpawn(50604, 1000280, F3("809e13bee16b2345000080c3"), 39301, 0x01a1b0837ffa, GameSession.OrbRelationship)
                  .SequenceEqual(Convert.FromHexString("3436ac8b03000058430f00809e13bee16b2345000080c38599000000000000010106fa7f83b0a101000000000000000000001c0000")),
              "a summoned recovery orb's 0x3634 as official (#53340: desc 0, origin = the box 39301, reason 6, relationship 28, 53 bytes)");
        check(EnvBoxPackets.EnvObjReward(31463, F3("21d01dc8c3ce97c60076c846"), [(631900007, 3)])
                  .SequenceEqual(Convert.FromHexString("3ae3e77a000021d01dc8c3ce97c60076c846016707aa2503000000")),
              "an object's reward 0xe33a as official (#17555: a Monolith fragment's 3 Empyrean Traces)");
        check(EnvBoxPackets.InteractEnvObjRefused(46883, EResult.EnvObj_Fail_NotReady, 1000278)
                  .SequenceEqual(Convert.FromHexString("3736010038a3ee02000000000000000056430f00")),
              "a refused interaction's long 0x3637 as official (#54148: mask 1, 14336, the orb's EnvObjData id)");
        check(EnvBoxPackets.AbnormalHeal(12577, 12577, 71, 190000131, 4937, null).SequenceEqual(Convert.FromHexString("0538a16201a16247032c530bc926"))
              && EnvBoxPackets.AbnormalHeal(12577, 12577, 71, 190000131, 0, 4937).SequenceEqual(Convert.FromHexString("0538a16202a16247032c530bc926")),
              "the HP orb's skill-less 0x3805 at the pickup (remain) and its tick (amount) as official (#54190, #54374)");
        check(EnvBoxPackets.EnvObjDie(KeyOf(Convert.FromHexString("3836ab8a02a16201"))).SequenceEqual(Convert.FromHexString("058dab8a02"))
              && GamePackets.EnvObjDespawn(KeyOf(Convert.FromHexString("3836ab8a02a16201")), 4, 3).SequenceEqual(Convert.FromHexString("3536ab8a02040300")),
              "the orb's 0x8d05 die and 0x3635 {4, 3} as official (#54159, #54188)");
        check(PlayerPackets.AbnormalAdd(12577, [new AbnormalWire(71, 19000013, 800, 0x01a1b0838e66, CasterKey: 12577, Level: 1, RangeBase: F3("867b45c3f0023145cfd17bc3"))])
                  .SequenceEqual(Convert.FromHexString("2a38a162011147cdea21012003000000000000668e83b0a1010000a1620100867b45c3f0023145cfd17bc3")),
              "the HP orb's abnormal 19000013 (800 ms, no skill) as official (#54189)");
        check(GameSession.OrbEffect(1000277) == (19000013, 800) && GameSession.OrbEffect(1000278) == (19000015, 800) && GameSession.OrbEffect(1000279) == (19000016, 800)
              && GameSession.OrbEffect(1000280) == (19000017, 10000) && GameSession.OrbEffect(1400607) is null,
              "orb effects: EventFunc Event_EnvObj_HP/MP/STA/SPD_Marble (the abnormals OBSERVED at the pickups)");
        check(EnvUsages.Of(1000277) is { Kind: "Overlap", LifetimeMs: 30000, Range: 40, Despawn: true } && EnvUsages.Of(1400711) is { RewardOnce: true, Count: 1 }
              && EnvUsages.IdOf("Basic_RecoveryOrb_01") == 1000277,
              "env-usages carries bGetRewardOnce, LifeTime and the orbs' InteractRange (EnvObjData)");

        // ---------- A Seal dungeon's reward box: 300 Power Shards (Bound), once ----------
        const int powerShard = 533460001;
        var shardStore = new FakeStore();
        var looter = await storedWorldSession("BoxLooter", shardStore);
        var (shardMap, shardIndex) = Find(1400711, 320010);
        var shardBox = WorldObjects.ObjectsOn(shardMap)[shardIndex];
        var shardKey = WorldObjects.FirstKey + shardIndex;
        check(GameSession.HasEnvReward(shardBox) && LootTables.Embedded.HasLoot(1400711), "E_CommonRewardBox_001 has its RewardData row (reward_manual_Dungeon_Seal_1400711)");
        looter.Teleport(shardMap, shardBox.At with { X = shardBox.At.X + 50 }, now);
        await looter.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var started = await looter.HandleAsync(Use(shardKey), now + 100);
        var opened = await looter.TickAsync(now + 2700);
        var reward = opened.FirstOrDefault(b => Op(b) == 0xe33a);
        var ops = opened.Select(Op).ToList();
        check(started.Any(b => Op(b) == 0x3638) && reward is not null
              && reward.SequenceEqual(EnvBoxPackets.EnvObjReward(shardKey, shardBox.At, [(powerShard, 300)]))
              && ops.IndexOf(0x363a) < ops.IndexOf(0x3637) && ops.IndexOf(0x3637) < ops.IndexOf(0xe33a) && ops.Contains(0x363b)
              && opened.Any(b => Op(b) == 0x561b && b[^3] == EnvBoxPackets.IncreaseEnvObjReward),
              "the Seal reward box (2.5 s): 0x363a, 0x363b (each player once), 0x3637, 0xe33a with 300 Power Shards, 0x561b reason 49");
        check(shardStore.Items.Where(i => i.TemplateId == powerShard).Sum(i => i.Quantity) == 300, "300 Power Shards (Bound) are stored");
        var again = await looter.HandleAsync(Use(shardKey), now + 3000);
        await looter.TickAsync(now + 6000);
        check(again.Count == 1 && again[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(shardKey, EResult.EnvObj_Fail_CanInteractOnlyOnce, 1400711))
              && shardStore.Items.Where(i => i.TemplateId == powerShard).Sum(i => i.Quantity) == 300,
              "the same player's second use is refused (EnvObj_Fail_CanInteractOnlyOnce) and gives nothing");
        looter.ExitSharedWorld();

        // ---------- A recovery orb box: orbs appear around it, the box is spent ----------
        var orbStore = new FakeStore();
        var opener = await storedWorldSession("OrbOpener", orbStore);
        var (randomMap, randomIndex) = Find(1400607, 210001);
        var randomBox = WorldObjects.ObjectsOn(randomMap)[randomIndex];
        var randomKey = WorldObjects.FirstKey + randomIndex;
        opener.Teleport(randomMap, randomBox.At, now);
        await opener.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var spilled = await opener.HandleAsync(Use(randomKey), now + 100);
        var orbs = spilled.Where(b => Op(b) == 0x3634).ToList();
        bool Around(byte[] b, Vector3d box) =>
            b.Length == 53 && b[5] == 0 && b[6] == 0 && BitConverter.ToInt32(b, 7) is >= 1000277 and <= 1000280
            && Math.Sqrt(Math.Pow(BitConverter.ToSingle(b, 11) - box.X, 2) + Math.Pow(BitConverter.ToSingle(b, 15) - box.Y, 2)) <= 301
            && BitConverter.ToInt32(b, 23) == randomKey && b[^3] == GameSession.OrbRelationship;
        check(orbs.Count is 3 or 4 && orbs.All(b => Around(b, randomBox.At)) && orbs.Select(b => BitConverter.ToInt32(b, 7)).Distinct().Count() == orbs.Count
              && !spilled.Any(b => Op(b) == 0x363b) && spilled.Select(Op).ToList() is var seq && seq.IndexOf(0x3637) < seq.IndexOf(0x3634)
              && opener.EnvSummonsForChecks.Count == orbs.Count,
              $"the random orb box: 0x3637, then {orbs.Count} different orbs (0x3634 53 bytes, origin = the box) within 300, no 0x363b");
        check((await opener.HandleAsync(Use(randomKey), now + 200)).Single().SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(randomKey, EResult.EnvObj_Fail_CanInteractOnlyOnce, 1400607)),
              "the opened orb box cannot be opened again");
        // The orbs vanish after their 30 s life.
        var expired = await opener.TickAsync(now + 100 + 30_001);
        check(orbs.All(o => expired.Any(b => Op(b) == 0x3635 && KeyOf(b) == KeyOf(o))) && opener.EnvSummonsForChecks.Count == 0, "orbs nobody takes vanish after 30 s (0x3635)");
        opener.ExitSharedWorld();

        // ---------- Walking into an HP orb ----------
        var hpStore = new FakeStore();
        var walker = await storedWorldSession("OrbWalker", hpStore);
        var (hpMap, hpIndex) = Find(1400603, 220010);
        var hpBox = WorldObjects.ObjectsOn(hpMap)[hpIndex];
        walker.Teleport(hpMap, hpBox.At, now);
        await walker.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var hpSpilled = await walker.HandleAsync(Use(WorldObjects.FirstKey + hpIndex), now + 100);
        var hpOrb = hpSpilled.Single(b => Op(b) == 0x3634);
        var orbKey = KeyOf(hpOrb);
        check(BitConverter.ToInt32(hpOrb, 7) == 1000277, "E_All_RecoveryOrbBox_01 gives one HP orb (Basic_RecoveryOrb_01; CANDIDATE kind)");
        var taken = await walker.HandleAsync(Use(orbKey), now + 500);
        var takenOps = taken.Select(Op).ToList();
        var abnormal = taken.FirstOrDefault(b => Op(b) == 0x382a);
        check(takenOps.Take(5).SequenceEqual(new ushort[] { 0x3638, 0x363a, 0x8d6d, 0x8d05, 0x3637 }) && abnormal is not null
              && abnormal.AsSpan().IndexOf(BitConverter.GetBytes(19000013)) > 0
              && taken.Any(b => Op(b) == 0x3805 && b[2 + NativeVarint.Encode(walker.PlayerKey).Length] == 1)
              && taken[^1].SequenceEqual(GamePackets.EnvObjDespawn(orbKey, 4, 3)) && walker.ActiveBuffs.Contains(19000013)
              && walker.EnvSummonsForChecks.Count == 0,
              "walking into the HP orb: 0x3638, 0x363a, 0x8d6d, 0x8d05, 0x3637, abnormal 19000013 and its 0x3805 (remain), 0x3635 {4, 3}");
        var healTick = await walker.TickAsync(now + 1400);
        check(healTick.Any(b => Op(b) == 0x3805 && b[2 + NativeVarint.Encode(walker.PlayerKey).Length] == 2) && healTick.Any(b => Op(b) == 0x382c),
              "the HP orb heals once (0x3805 amount) and its 800 ms abnormal ends (0x382c)");
        check((await walker.HandleAsync(Use(orbKey), now + 1200)).Single().SequenceEqual(GamePackets.InteractEnvObjReply(orbKey, 1)), "a taken orb is gone");

        // ---------- A map event summons an object (CoastalCave / DaevaHunter: SummonAroundTarget Basic_RecoveryOrb_01) ----------
        walker.DrainMapEventsForChecks();
        ((IMapEventHost)walker).SummonAround("NoSuchTarget", "Basic_RecoveryOrb_02", 2, 300);
        var summoned = walker.DrainMapEventsForChecks().Where(b => Op(b) == 0x3634).ToList();
        check(summoned.Count == 2 && summoned.All(b => b.Length == 53 && BitConverter.ToInt32(b, 7) == 1000278) && walker.EnvSummonsForChecks.Count == 2,
              "SummonAroundTarget of an EnvObjData template summons the objects (two MP orbs)");
        walker.ExitSharedWorld();
    }

    /// <summary>The first placement of an EnvObjData id on a map (the map given first, as the data has it).</summary>
    static (int Map, int Index) Find(int env, int map)
    {
        var objects = WorldObjects.ObjectsOn(map);
        for (var i = 0; i < objects.Count; i++)
            if (objects[i].Env == env) return (map, i);
        throw new InvalidOperationException($"EnvObjData {env} is not on map {map}");
    }
}
