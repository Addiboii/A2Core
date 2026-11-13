using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Empyrean Traces and Monoliths (GameSession.EmpyreanTraces.cs): the official bytes of a trace's 0x3634 and of MonolithList_NT
/// (rec 20261106-105448 #637 / #357), the traces placed on the six maps, a trace collected in a stored session in the official
/// order (tokens, region count, spawn achievement), refused the second time, and a resonance (tokens used, level rewards, stats).
/// </summary>
static class EmpyreanTraceChecks
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
        // ---------- Official bytes ----------
        check(MonolithPackets.TraceSpawn(31463, 1300865, F3("21d01dc8c3ce97c60076c846"), 247.5, 0x01a1a56779da, 757932965, 9, 125010210)
                  .SequenceEqual(Convert.FromHexString("3436e7f5010c0081d9130021d01dc8c3ce97c60076c8460000000000807743010101da7967a5a10100000000000000000000a5232d2d09002281730700")),
              "an Empyrean Trace's 0x3634 as official (#637: mask 0x0c, unique key, EnvObj_Dark, achievement 125010210 after the phase)");
        check(MonolithPackets.MonolithList([(2001, 6), (2002, 0), (4001, 0)])
                  .SequenceEqual(Convert.FromHexString("0be303d107000006000000d207000000000000a10f000000000000")),
              "MonolithList_NT 0xe30b as official (#357: Altgard 6, Morheim 0, Reshanta 0)");

        // ---------- Placed: every field map's traces, with their spawn achievements ----------
        int Traces(int map) => WorldObjects.ObjectsOn(map).Count(o => o.Achievement != 0 && MonolithTables.Embedded.Traces.ContainsKey(o.Env));
        check(Traces(1010) == 559 && Traces(1011) == 140 && Traces(1110) == 559 && Traces(1111) == 140 && Traces(20) == 200 && Traces(22) == 100,
              "Empyrean Traces placed: Verteron 559, Eltnen 140, Altgard 559, Morheim 140, Lower Reshanta 200, Middle Reshanta 100");
        var ground129 = WorldObjects.ObjectsOn(1110).FirstOrDefault(o => o.Name == "E_D1_GodFragment_01_Ground_129");
        check(ground129 is { Env: 1300865, Achievement: 125010210, UniqueKey: 757932965, Relationship: 9, InteractTime: 2500 },
              "E_D1_GodFragment_01_Ground_129 as the official spawn carries it (achievement 125010210, unique key, EnvObj_Dark, 2.5 s)");
        check(WorldObjects.ObjectsOn(1110).Any(o => o.Env == 1300862 && o.Monolith is [2001]) && WorldObjects.ObjectsOn(20).Any(o => o.Monolith is [3001, 4001]),
              "the Monoliths are placed (Altgard 2001; Reshanta's Elyos Monolith resonates 3001 and 4001)");
        var altgard = MonolithTables.Embedded.Of(2001);
        check(altgard is { Race: 2, Token: 631900007, MaxCount: 560 } && altgard.LevelAt(0) == 1 && altgard.LevelAt(1) == 2 && altgard.LevelAt(560) == 30
              && altgard.Levels[1].Stats.GetValueOrDefault("Life") == 1 && MonolithTables.Embedded.Vouchers[567200006] == (2001, 1110),
              "Monolith data: Altgard token 631900007, level 2 at 1 (Life +1), level 30 at 560 (OBSERVED count 560), voucher -> Altgard on 1110");

        // ---------- Collecting one in a stored session ----------
        var store = new FakeStore();
        var player = await storedWorldSession("TraceCollector", store);
        var race = player.RaceForChecks;
        var (map, env, token, monolithId, tokens) = race == 1 ? (1010, 1200860, 631900001, 1001, 3) : (1110, 1300865, 631900007, 2001, 3);
        var objects = WorldObjects.ObjectsOn(map);
        var index = Enumerable.Range(0, objects.Count).First(i => objects[i].Env == env && objects[i].Achievement != 0);
        var trace = objects[index];
        var key = WorldObjects.FirstKey + index;
        var region = AchievementTables.Embedded.OfType("UseEnvObj").First(e => e.Envs?.Contains(env) == true && AchievementTables.Embedded.GroupOf(e)?.Race == race).Id;
        var shown = player.Teleport(map, trace.At with { X = trace.At.X + 40 }, now);
        shown.AddRange(await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now));
        shown.AddRange(await player.TickAsync(now + 50));
        var spawn = shown.FirstOrDefault(b => Op(b) == 0x3634 && KeyOf(b) == key);
        check(spawn is not null && spawn.Length > 8 && spawn[^5..^1].SequenceEqual(BitConverter.GetBytes(trace.Achievement)) && spawn.Contains((byte)0x0c),
              "the trace comes into view with its spawn achievement (0x3634 mask 0x0c)");
        var started = await player.HandleAsync(Use(key), now + 100);
        var done = await player.TickAsync(now + 2700);
        var ops = done.Select(Op).ToList();
        int At(ushort op, int from = 0) => ops.FindIndex(from, o => o == op);
        check(started.Count >= 2 && Op(started[0]) == 0x3637 && Op(started[1]) == 0x3638, "the reply 0x3637 comes with 0x3638 at the start (OBSERVED)");
        check(At(0x363a) >= 0 && At(0x363a) < At(0x8d6d) && At(0x8d6d) < At(0x363b) && At(0x363b) < At(0x5684) && At(0x5684) < At(0xe33a)
              && At(0xe33a) < At(0x561b) && At(0x561b) < At(0x5684, At(0xe33a)) && !ops.Contains(0x3637),
              "collected in the official order: 0x363a, 0x8d6d, 0x363b, 0x5684 (region), 0xe33a, 0x561b, 0x5684 (spawn achievement)");
        check(done.First(b => Op(b) == 0xe33a).SequenceEqual(EnvBoxPackets.EnvObjReward(key, trace.At, [(token, tokens)]))
              && store.Items.Where(i => i.TemplateId == token).Sum(i => i.Quantity) == tokens,
              $"the trace gives {tokens} Empyrean Traces ({token}), stored");
        check(player.AchievementCount(trace.Achievement) == 1 && player.AchievementCount(region) == 1,
              "the spawn's own achievement completes and the region count goes up");
        var update = done.First(b => Op(b) == 0x5684);
        check(BitConverter.ToInt64(update, 11) != 0, "the region count's 0x5684 carries the time also before completion (OBSERVED count 2 of 5 with a time)");
        var again = await player.HandleAsync(Use(key), now + 3000);
        check(again.Count == 1 && again[0].SequenceEqual(EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_CanInteractOnlyOnce, env))
              && store.Items.Where(i => i.TemplateId == token).Sum(i => i.Quantity) == tokens,
              "a collected trace is refused the second time and gives nothing");

        // ---------- Resonating with the Monolith ----------
        var monolith = MonolithTables.Embedded.Of(monolithId)!;
        var lifeBefore = player.MonolithStatForChecks("Life");
        var resonated = await player.HandleAsync(new PacketWriter().U16(MonolithPackets.MonolithResonateRq).I32(monolithId).I32(1).I32(0).ToArray(), now + 4000);
        var reply = resonated.FirstOrDefault(b => Op(b) == MonolithPackets.MonolithResonateRs);
        var levelTwo = monolith.Levels.First(l => l.Number == 2);
        check(reply is not null && reply.AsSpan(0, 24).SequenceEqual(MonolithPackets.ResonateReply(0, monolithId, token, 1, 0, 1, []).AsSpan(0, 24))
              && store.Monoliths.GetValueOrDefault(monolithId) == 1 && player.MonolithCount(monolithId) == 1
              && store.Items.Where(i => i.TemplateId == token).Sum(i => i.Quantity) == tokens - 1
              && levelTwo.Items.All(r => store.Items.Any(i => i.TemplateId == r.Item)),
              "resonating one token: 0xe30e result 0 (0 -> 1), the token used, level 2's items granted, the count stored");
        check(levelTwo.Stats.Count > 0 && levelTwo.Stats.All(s => player.MonolithStatForChecks(s.Key) == s.Value) && player.MonolithStatForChecks("Life") >= lifeBefore
              && resonated.Any(b => Op(b) == 0x364a || Op(b) == 0x3649),
              "level 2's stats count on the stat sheet (refreshed)");
        var tooMany = await player.HandleAsync(new PacketWriter().U16(MonolithPackets.MonolithResonateRq).I32(monolithId).I32(50).I32(0).ToArray(), now + 5000);
        check(tooMany.Count == 1 && BitConverter.ToUInt16(tooMany[0], 2) == EResult.MonolithResonate_Fail_NotEnoughCountItem && store.Monoliths[monolithId] == 1,
              "more tokens than the bag holds: refused (MonolithResonate_Fail_NotEnoughCountItem), nothing changes");
        var otherRace = MonolithTables.Embedded.OfRace(race == 1 ? 2 : 1).First().Id;
        var foreign = await player.HandleAsync(new PacketWriter().U16(MonolithPackets.MonolithResonateRq).I32(otherRace).I32(1).I32(0).ToArray(), now + 5100);
        check(foreign.Count == 1 && BitConverter.ToUInt16(foreign[0], 2) == EResult.Monolith_Fail_NotUnlock, "the other race's Monolith is refused");
        var sync = await player.HandleAsync(new PacketWriter().U16(MonolithPackets.SyncMonolithAccountAchievementsRq).I32(0).ToArray(), now + 5200);
        check(sync.Count == 1 && Op(sync[0]) == MonolithPackets.SyncMonolithAccountAchievementsRs, "SyncMonolithAccountAchievements is answered (nothing to sync)");
        player.ExitSharedWorld();
    }
}

sealed partial class FakeStore
{
    // Monoliths (migration 090) in memory.
    public Dictionary<int, int> Monoliths { get; } = [];
    public Task<IReadOnlyDictionary<int, int>> MonolithsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>(Monoliths));
    public Task<bool> ResonateMonolithAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int monolith, int previous, int count,
        IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct)
    {
        if (Monoliths.GetValueOrDefault(monolith) != previous) return Task.FromResult(false);
        foreach (var (item, revision, n) in costs)
            if (!Consume(item, revision, n)) return Task.FromResult(false);
        Monoliths[monolith] = count;
        GrantLootAsync(character, rewards, ct);
        return Task.FromResult(true);
    }
}
