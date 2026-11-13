using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Server authority (Stage C): request limits, the safety net around every handler and a fuzz of every client request opcode.
/// The fuzz feeds random, truncated, over-long and mutated bodies of every opcode the game port can receive into an in-world
/// session (development, no store) and a stored one (FakeStore), and requires that no handler throws (GameSession.RequestErrors
/// stays empty) and that the session stays usable (still in the world, a ping still answered). FUZZ_ROUNDS=n (environment)
/// runs n extra random rounds with fresh seeds for a deeper search; the checks run the fixed seed.
/// </summary>
public static class AuthorityChecks
{
    const long Now = 1760000000123;
    const string Session = "1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b";
    static int passed;

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Authority check failed: " + name);
        passed++;
    }

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }

    static readonly byte[] Login = [.. Strings(0x3613, Session, "", "t", ""), .. new byte[9]];
    static readonly byte[] Create = new PacketWriter().U16(0x361b).String("Player1").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();
    static byte[] Select(long wireKey) => new PacketWriter().U16(0x361f).Varint(wireKey).I32(0).Vec3(Vector3d.Zero).ToArray();
    static readonly byte[] Enter = new PacketWriter().U16(0x3622).I32(1).ToArray();

    /// <summary>Lobby and world-entry requests: fuzzed before the world (a valid one would leave or re-enter it).</summary>
    static readonly HashSet<int> LobbyOpcodes = [0x3613, 0x3619, 0x361b, 0x361f, 0x3622, 0x3625, 0x3627, 0x3629, 0x362c, 0x3630];

    /// <summary>Every opcode block the client's requests come from (the 0x36-0x39 runs, layer/NPC, items, quests, social, guild).</summary>
    public static IEnumerable<int> RequestOpcodes()
    {
        (int From, int To)[] blocks = [(0x3600, 0x3a00), (0x4000, 0x4100), (0x5100, 0x5200), (0x5600, 0x5700), (0x8a00, 0x9400), (0x9700, 0x9800), (0xdd00, 0xe400)];
        var all = new SortedSet<int>(blocks.SelectMany(b => Enumerable.Range(b.From, b.To - b.From)));
        foreach (var op in SocialOpcodes.ClientRequests) { all.Add(op); all.Add(op - (SocialOpcodes.LateBase - SocialOpcodes.LateAlternativeBase)); }
        return all.Where(op => op is > 0 and <= 0xffff);
    }

    /// <summary>The bodies tried for one opcode: every short length, typical field widths, long and odd ones; random, zero, 0xff and
    /// endless-varint contents.</summary>
    static IEnumerable<byte[]> Bodies(int opcode, Random rng)
    {
        int[] lengths = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 26, 27, 30, 31, 32, 41, 53, 64, 100, rng.Next(2, 400)];
        foreach (var length in lengths)
        {
            var body = new byte[length];
            rng.NextBytes(body);
            body[0] = (byte)opcode; body[1] = (byte)(opcode >> 8);
            yield return body;
            if (length is 3 or 6 or 10 or 11 or 22 or 31)
            {
                var zero = new byte[length]; zero[0] = (byte)opcode; zero[1] = (byte)(opcode >> 8);
                yield return zero;
                var ff = Enumerable.Repeat((byte)0xff, length).ToArray(); ff[0] = (byte)opcode; ff[1] = (byte)(opcode >> 8);
                yield return ff;
                var varints = Enumerable.Repeat((byte)0x80, length).ToArray(); varints[0] = (byte)opcode; varints[1] = (byte)(opcode >> 8);
                yield return varints;
            }
        }
    }

    /// <summary>Bodies that name things the session really has (bag item ids as i64, item templates as i32, NPC keys as varint)
    /// at the usual offsets, with random fields around them: they get past the "no such item/NPC" refusals into the handlers.</summary>
    static IEnumerable<byte[]> Targeted(int opcode, Random rng, long[] wires, int[] templates, int[] npcs)
    {
        byte[] Head() => [(byte)opcode, (byte)(opcode >> 8)];
        byte[] Rand(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }
        byte[] SmallCount() => BitConverter.GetBytes((long)rng.Next(-2, 100_000));
        for (var i = 0; i < 4; i++)
        {
            var wire = BitConverter.GetBytes(wires[rng.Next(wires.Length)]);
            var template = BitConverter.GetBytes(templates[rng.Next(templates.Length)]);
            var npc = new PacketWriter().Varint(npcs[rng.Next(npcs.Length)]).ToArray();
            yield return [.. Head(), .. wire, .. Rand(rng.Next(0, 24))];
            yield return [.. Head(), .. Rand(1), .. wire, .. Rand(rng.Next(0, 24))];
            yield return [.. Head(), .. npc, .. Rand(rng.Next(0, 24))];
            yield return [.. Head(), .. npc, 1, .. wire, .. SmallCount(), .. Rand(rng.Next(0, 4))];
            yield return [.. Head(), 1, .. wire, .. SmallCount()];
            yield return [.. Head(), .. template, .. Rand(rng.Next(0, 16))];
            yield return [.. Head(), .. wire, .. wire, .. template];
        }
    }

    /// <summary>Real request shapes, mutated (a byte flipped, cut short, extended): the parsers' near misses.</summary>
    static IEnumerable<byte[]> Mutations(GameSession session, Random rng)
    {
        var at = session.Position ?? GamePackets.SpawnPosition;
        byte[][] samples =
        [
            new PacketWriter().U16(0x3800).U16(16).I32(11010000).U8(2).Varint(session.PlayerKey).F32(0).Vec3(at).U8(9).ToArray(),
            new PacketWriter().U16(0x3701).U8(2).Vec3(at).U16(1806).F32(9.9f).U8(1).U64(1).ToArray(),
            new PacketWriter().U16(0x3700).U8(2).Vec3(at).ToArray(),
            new PacketWriter().U16(0x3718).U8(2).Vec3(at).U16(0).ToArray(),
            new PacketWriter().U16(0x3833).U8(0).Varint(session.PlayerKey).ToArray(),
            new PacketWriter().U16(0xe334).String("cutscene").I64(1).ToArray(),
            new PacketWriter().U16(0x9230).U8(1).I32(1).I32(0).ToArray(),
            new PacketWriter().U16(0x8d0e).I32(1).I32(-1).ToArray(),
            new PacketWriter().U16(0x8d40).Bytes(new byte[180]).ToArray(),
            new PacketWriter().U16(0x362e).String("help").ToArray(),
        ];
        foreach (var sample in samples)
            for (var i = 0; i < 12; i++)
            {
                var body = (byte[])sample.Clone();
                switch (i % 4)
                {
                    case 0: body[rng.Next(2, body.Length)] ^= (byte)(1 << rng.Next(8)); break;
                    case 1: body = body[..rng.Next(2, body.Length)]; break;
                    case 2: body = [.. body, .. Enumerable.Range(0, rng.Next(1, 8)).Select(_ => (byte)rng.Next(256))]; break;
                    default: body[rng.Next(2, body.Length)] = (byte)rng.Next(256); body[rng.Next(2, body.Length)] = 0xff; break;
                }
                yield return body;
            }
    }

    static ItemState Item(int template, long quantity, string location, int? slot, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, location, slot, 1, System.Text.Json.JsonSerializer.SerializeToElement(new { }), System.Text.Json.JsonSerializer.SerializeToElement(new { }), wire);

    static async Task<GameSession> InWorld(GameData data, FakeStore? store, int seed)
    {
        var session = new GameSession(data, new GameSessionOptions(), new Random(seed)) { Store = store };
        await session.HandleAsync(Login, Now);
        await session.HandleAsync(Create, Now);
        await session.HandleAsync(Select(store is null ? 65536 : store.States[0].WireId << 16), Now);
        await session.HandleAsync(Enter, Now);
        if (!session.InWorld) throw new InvalidOperationException("Authority checks: the session did not enter the world");
        return session;
    }

    /// <summary>One fuzz campaign; returns the distinct handler failures found.</summary>
    static async Task<List<string>> Fuzz(GameData data, FakeStore? store, int seed)
    {
        var rng = new Random(seed);
        // A bag to aim at: gear, stacks, Kina (the stored session's handlers refuse items the character does not hold).
        store?.Items.AddRange([Item(110160001, 1, "equipment", 1, 10), Item(530126003, 30, "bag", 0, 11), Item(532400001, 1, "bag", null, 12),
            Item(GameSession.KinaItemId, 1_000_000, "bag", 2, 13), Item(530116001, 25, "bag", 3, 14), Item(110160001, 1, "bag", 4, 15), Item(530126003, 5, "storage", 0, 16)]);
        var session = await InWorld(data, store, seed);
        var wires = store?.Items.Select(i => i.WireId).ToArray() is { Length: > 0 } w ? w : [10L];
        var templates = store?.Items.Select(i => i.TemplateId).Distinct().ToArray() is { Length: > 0 } tpl ? tpl : [110160001];
        if (store is not null && !(await session.HandleAsync([0x10, 0x56], Now)).Any(b => b.AsSpan().IndexOf(BitConverter.GetBytes(530126003)) >= 0))
            throw new InvalidOperationException("Authority checks: the seeded bag did not reach the session");
        var npcs = session.Targets.Values.OrderBy(t => t.Position.Distance2d(session.ServerPosition!.Value)).Take(8).Select(t => t.Key).ToArray();
        var now = Now + 1000;
        var errors = new SortedSet<string>();
        var reentries = 0;
        var count = 0;
        async Task Feed(byte[] body)
        {
            now += 5;
            count++;
            try { await session.HandleAsync(body, now); }
            catch (Exception error) { errors.Add($"ESCAPED 0x{body[0] | body[1] << 8:x4}: {error.GetType().Name} {error.Message}"); }
            if (session.CloseReason is { } closed) throw new InvalidOperationException($"Authority checks: the fuzz closed the session ({closed}); errors so far: {string.Join("; ", session.RequestErrors.Take(5))}");
            if (!session.InWorld)
            {
                reentries++;
                await session.HandleAsync(Select(store is null ? 65536 : store.States[0].WireId << 16), now);
                await session.HandleAsync(Enter, now);
            }
        }
        var ops = RequestOpcodes().ToList();
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (LobbyOpcodes.Contains(op)) continue;
            foreach (var body in Bodies(op, rng)) await Feed(body);
            if (npcs.Length > 0) foreach (var body in Targeted(op, rng, wires, templates, npcs)) await Feed(body);
            now += 400;   // below every rate limit: the fuzz looks for handler failures, not drops
            if (i % 64 == 0) await session.TickAsync(now);
            if (session.RequestErrors.Count > 0)
            {
                foreach (var e in session.RequestErrors) errors.Add(e);
                session.RequestErrors.Clear();
            }
        }
        foreach (var body in Mutations(session, rng)) await Feed(body);
        await session.TickAsync(now += 1000);
        foreach (var e in session.RequestErrors) errors.Add(e);
        session.RequestErrors.Clear();
        // Still a working session: in the world, a ping answered.
        var ping = await session.HandleAsync(new PacketWriter().U16(0x3602).U64(9).ToArray(), now += 1000);
        if (!session.InWorld || ping.Count != 1) errors.Add("the session did not survive the fuzz");
        session.ExitSharedWorld();
        Console.WriteLine($"  fuzz seed {seed} ({(store is null ? "development" : "stored")}): {count} requests over {ops.Count} opcodes, {reentries} world re-entries, {errors.Count} failures");
        return [.. errors];
    }

    /// <summary>The lobby requests, fuzzed before any character is in the world.</summary>
    static async Task<List<string>> FuzzLobby(GameData data, int seed)
    {
        var rng = new Random(seed);
        var errors = new SortedSet<string>();
        var store = new FakeStore();
        var session = new GameSession(data, new GameSessionOptions(), new Random(seed)) { Store = store };
        await session.HandleAsync(Login, Now);
        await session.HandleAsync(Create, Now);
        var now = Now;
        foreach (var op in LobbyOpcodes.Where(o => o != 0x3613))
            foreach (var body in Bodies(op, rng))
            {
                try { await session.HandleAsync(body, now += 20); }
                catch (Exception error) { errors.Add($"ESCAPED 0x{op:x4}: {error.GetType().Name} {error.Message}"); }
            }
        foreach (var e in session.RequestErrors) errors.Add(e);
        return [.. errors];
    }

    public static async Task<int> Run(GameData data)
    {
        passed = 0;
        Limits();
        await SafetyNet(data);
        await Skills(data);
        await NpcReach(data);
        await KibeliskReach(data);
        await Movement(data);
        await OffMesh(data);
        await Equip(data);
        await CheatRecordKeepsTurn(data);
        var failures = new List<string>();
        failures.AddRange(await FuzzLobby(data, 17));
        failures.AddRange(await Fuzz(data, null, 23));
        var stored = new FakeStore();
        failures.AddRange(await Fuzz(data, stored, 29));
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("FUZZ_ROUNDS"), out var r) ? r : 0;
        for (var i = 0; i < rounds; i++)
        {
            failures.AddRange(await Fuzz(data, null, 1000 + i));
            failures.AddRange(await Fuzz(data, new FakeStore(), 2000 + i));
        }
        if (failures.Count > 0) Console.WriteLine("  fuzz failures:\n    " + string.Join("\n    ", failures.Distinct()));
        Check(failures.Count == 0, $"no request handler fails on malformed input ({failures.Distinct().Count()} distinct failures, listed above)");
        return passed;
    }

    static byte[] Cast(int skill, int target, Vector3d at) => new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(at).U8(9).ToArray();

    static async Task Skills(GameData data)
    {
        var session = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(3));
        await session.HandleAsync(Login, Now);
        await session.HandleAsync(Create, Now);
        await session.HandleAsync(Select(65536), Now);
        await session.HandleAsync(Enter, Now);
        var gladiator = GameData.Embedded.Profile(1, 1)!;
        var basic = data.LearnedSkills(gladiator.Class, 1).Select(s => s.SkillId)
            .First(s => data.Skill(s)?.Damage.Length > 0 && data.Skill(s)!.Cost.Mp == 0 && data.Skill(s)!.CooldownMs > 0 && GameSession.SkillUseOf(s) is { Range: > 0 });
        var range = GameSession.SkillUseOf(basic)!.Value.Range;
        var dummy = session.Targets[Combat.TrainingDummyKey];
        var me = dummy.Position with { X = dummy.Position.X - 300 };
        await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(me).ToArray(), Now + 5000);
        var hp = dummy.Hp;
        var now = Now + 10_000;
        // Far beyond the client table's use range: refused (Skill_TooFar), nothing spent, no damage, logged.
        dummy.Position = me with { X = me.X + range + dummy.Radius + GameSession.RangeRefuseSlackCm + 500 };
        var far = await session.HandleAsync(Cast(basic, dummy.Key, dummy.Position), now);
        Check(far.Count == 1 && far[0].SequenceEqual(GamePackets.CastResult(basic, EResult.Skill_TooFar)) && dummy.Hp == hp
              && session.Suspicions.GetValueOrDefault("skill_range") == 1, "a cast far beyond the skill's use range is refused (Skill_TooFar) and logged");
        // A little beyond (latency, both moving): allowed, only logged.
        dummy.Position = me with { X = me.X + range + dummy.Radius + GameSession.RangeLogSlackCm + 200 };
        var near = await session.HandleAsync(Cast(basic, dummy.Key, dummy.Position), now += 100);
        Check(near.Any(b => b[0] == 0x02 && b[1] == 0x38) && session.Suspicions.GetValueOrDefault("skill_range") == 2, "a little beyond the range: cast, only logged");
        // In range: no suspicion.
        dummy.Position = me with { X = me.X + range * 0.8 };
        var inRange = await session.HandleAsync(Cast(basic, dummy.Key, dummy.Position), now += 60_000);
        Check(inRange.Any(b => b[0] == 0x02 && b[1] == 0x38) && session.Suspicions.GetValueOrDefault("skill_range") == 2, "in range: cast, nothing logged");
        // Self-centred casts: an absolute location 50 m from the caster is replaced by the server position (the area is picked around
        // the caster); a short relative vector (dodges, OBSERVED) is left alone.
        var self = data.LearnedSkills(gladiator.Class, 1).Select(s => s.SkillId).FirstOrDefault(s => s != basic && data.Skill(s) is { Cost.Mp: 0 } && GameSession.SkillUseOf(s) is { Kind: 0 or 2 });
        if (self != 0)
        {
            await session.HandleAsync(Cast(self, session.PlayerKey, new Vector3d(100, 0, 0)), now += 60_000);
            Check(session.Suspicions.GetValueOrDefault("skill_location") == 0, "a relative dash vector is not a far location");
            var moved = await session.HandleAsync(Cast(self, session.PlayerKey, me with { X = me.X + 5000 }), now += 60_000);
            var start = moved.FirstOrDefault(b => b[0] == 0x02 && b[1] == 0x38);
            Check(session.Suspicions.GetValueOrDefault("skill_location") == 1 && (start is null || !Convert.ToHexString(start).Contains(Convert.ToHexString(BitConverter.GetBytes((float)(me.X + 5000))))),
                  "a self-centred cast 50 m away is moved to the caster's server position and logged");
        }
        session.ExitSharedWorld();
    }

    static async Task NpcReach(GameData data)
    {
        async Task<(GameSession Session, List<byte[]> Replies)> OpenAt(double offset)
        {
            var session = await InWorld(data, null, 8);
            var start = session.ServerPosition!.Value;
            var merchant = session.Targets.Values.Where(t => ShopTables.Embedded.IsMerchant(t.NpcId)).MinBy(t => t.Position.Distance2d(start))!;
            var goal = merchant.Position with { X = merchant.Position.X + offset };
            // Walk there at running speed (30 m every 3 s): the speed check rejects a jump.
            var now = Now + 5000;
            var at = start;
            while (at.Distance2d(goal) > 1)
            {
                var step = Math.Min(3000, at.Distance2d(goal));
                at = new Vector3d(at.X + (goal.X - at.X) * step / at.Distance2d(goal), at.Y + (goal.Y - at.Y) * step / at.Distance2d(goal), goal.Z);
                await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at).ToArray(), now += 3000);
            }
            if (session.ServerPosition!.Value.Distance2d(goal) > 1) throw new InvalidOperationException("Authority checks: the walk to the merchant was rejected");
            var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopOpenRq).Varint(merchant.Key).ToArray(), now + 100);
            session.ExitSharedWorld();
            return (session, replies);
        }
        var (near, nearReplies) = await OpenAt(200);
        Check(BitConverter.ToUInt16(nearReplies.Single(), 2) == 0 && near.Suspicions.Count == 0, "a shop opened next to its merchant: result 0, nothing logged");
        var (far, farReplies) = await OpenAt(GameSession.NpcRefuseReachCm + 2000);
        Check(BitConverter.ToUInt16(farReplies.Single(), 2) == 1 && far.Suspicions.GetValueOrDefault("npc_reach") == 1, "a shop opened from across the map is refused and logged");
        var (mid, midReplies) = await OpenAt(GameSession.NpcLogReachCm + 1000);
        Check(BitConverter.ToUInt16(midReplies.Single(), 2) == 0 && mid.Suspicions.GetValueOrDefault("npc_reach") == 1, "a shop opened a little too far: allowed, logged");
    }

    static async Task KibeliskReach(GameData data)
    {
        // Bot finding 2026-11-09: a Kibelisk bound from anywhere on the map. Next to it: bound, nothing logged; from across the map:
        // EnvObj_Fail_TooFar, nothing engraved, logged.
        async Task<(GameSession Session, List<byte[]> Replies, int Artifact)> BindAt(double offset)
        {
            var session = await InWorld(data, null, 12);
            var placed = Kibelisks.OnMap(session.MapId!.Value);
            if (placed.Count == 0) throw new InvalidOperationException("Authority checks: no Kibelisk on the start map");
            var start = session.ServerPosition!.Value;
            var stone = new Vector3d(placed[0].Position[0], placed[0].Position[1], placed[0].Position[2]);
            var goal = stone with { X = stone.X + offset };
            var now = Now + 5000;
            var at = start;
            while (at.Distance2d(goal) > 1)
            {
                var step = Math.Min(3000, at.Distance2d(goal));
                at = new Vector3d(at.X + (goal.X - at.X) * step / at.Distance2d(goal), at.Y + (goal.Y - at.Y) * step / at.Distance2d(goal), goal.Z);
                await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at).ToArray(), now += 3000);
            }
            if (session.ServerPosition!.Value.Distance2d(goal) > 1) throw new InvalidOperationException("Authority checks: the walk to the Kibelisk was rejected");
            var replies = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint(Kibelisks.FirstKey).I64(0).I32(placed[0].EnvObj).ToArray(), now + 100);
            session.ExitSharedWorld();
            return (session, replies, placed[0].ArtifactId);
        }
        var (near, nearReplies, artifact) = await BindAt(150);
        Check(nearReplies.Any(b => b.SequenceEqual(GamePackets.EngravedTeleportArtifactAdd(artifact))) && !near.Suspicions.ContainsKey("kibelisk_reach"),
              "a Kibelisk bound next to it: engraved, nothing logged");
        var (far, farReplies, _) = await BindAt(Kibelisks.InteractRange + WorldObjects.InteractSlack + 3000);
        Check(farReplies.Single().SequenceEqual(GamePackets.InteractEnvObjReply(Kibelisks.FirstKey, EResult.EnvObj_Fail_TooFar))
              && far.Suspicions.GetValueOrDefault("kibelisk_reach") == 1, "a Kibelisk bound from across the map: EnvObj_Fail_TooFar, not engraved, logged");
    }

    static async Task Movement(GameData data)
    {
        var session = await InWorld(data, null, 9);
        var start = session.ServerPosition!.Value;
        var now = Now + 5000;
        byte[] Glide(Vector3d at) => new PacketWriter().U16(0x3706).U8(0).Vec3(at).F32(0).ToArray();
        byte[] Glided(Vector3d at) => new PacketWriter().U16(0x3707).Vec3(at).F32(0).ToArray();
        await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(start).ToArray(), now);
        // A glide start without a wing: logged only (the client offers no glide without one).
        var glideStart = await session.HandleAsync(Glide(start with { Z = start.Z + 100 }), now += 100);
        Check(session.Suspicions.GetValueOrDefault("fly_without_wing") == 1 && session.ServerPosition!.Value.Z == (float)(start.Z + 100) && glideStart.All(b => b[0] != 0x3f),
              "a glide start without a wing is logged, not refused");
        // A glide at a normal pace: accepted, nothing logged.
        var at = start;
        for (var i = 0; i < 10; i++) { at = at with { X = at.X + 150 }; await session.HandleAsync(Glided(at), now += 100); }
        Check(session.ServerPosition!.Value.X == (float)at.X && session.Suspicions.GetValueOrDefault("air_speed") == 0, "a glide at a normal pace is accepted");
        // Faster than the loose air budget: still accepted, logged.
        at = at with { X = at.X + AuthorityDistance(0.1) + 1000 };
        await session.HandleAsync(Glided(at), now += 100);
        Check(session.ServerPosition!.Value.X == (float)at.X && session.Suspicions.GetValueOrDefault("air_speed") == 1, "a glide faster than the loose budget is accepted and logged");
        // A teleport-sized jump in a glide report (it used to be accepted and to restart the check): rejected back to the last point.
        var before = session.ServerPosition!.Value;
        var jump = await session.HandleAsync(Glided(at with { X = at.X + 100_000 }), now += 100);
        Check(session.ServerPosition == before && jump.Count == 1 && jump[0][0] == 0x3f && jump[0][1] == 0x37 && session.Suspicions.GetValueOrDefault("teleport") == 1,
              "a 1 km jump in a glide report is rejected (rubber-band) and logged as a teleport");
        session.ExitSharedWorld();
    }

    /// <summary>LOG-ONLY navmesh check: a square of floor 100 m around the spawn; walking on it logs nothing, a long walk off it is
    /// logged once and every report is still accepted.</summary>
    static async Task OffMesh(GameData data)
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-authority-nav-" + Environment.ProcessId);
        var spawn = GamePackets.SpawnPosition;
        var geometry = data.GeometryMap(GamePackets.SpawnMapId);
        Directory.CreateDirectory(Path.Combine(dir, geometry.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using (var file = File.Create(Path.Combine(dir, geometry.ToString(System.Globalization.CultureInfo.InvariantCulture), "nav.bin")))
        using (var w = new BinaryWriter(file))
        {
            w.Write("AIONNAV1"u8); w.Write(1); w.Write(1);
            (float X, float Y)[] square = [((float)spawn.X - 5000, (float)spawn.Y - 5000), ((float)spawn.X + 5000, (float)spawn.Y - 5000), ((float)spawn.X + 5000, (float)spawn.Y + 5000), ((float)spawn.X - 5000, (float)spawn.Y + 5000)];
            w.Write((byte)63); w.Write((byte)4);
            foreach (var (x, y) in square) { w.Write(x); w.Write(y); w.Write((float)spawn.Z); }
            w.Write((ushort)0);
            w.Write((ushort)2);
            foreach (var tri in new[] { (0, 1, 2), (0, 2, 3) })
                foreach (var i in new[] { tri.Item1, tri.Item2, tri.Item3 }) { w.Write(square[i].X); w.Write(square[i].Y); w.Write((float)spawn.Z); }
        }
        try
        {
            var navs = new NavMeshStore(dir);
            Check(navs.For(geometry) is not null, "test navmesh loads");
            var session = new GameSession(data, new GameSessionOptions { Nav = navs }, new Random(10));
            await session.HandleAsync(Login, Now);
            await session.HandleAsync(Create, Now);
            await session.HandleAsync(Select(65536), Now);
            await session.HandleAsync(Enter, Now);
            var now = Now + 1000;
            await session.TickAsync(now);
            var at = session.ServerPosition!.Value;
            for (var i = 0; i < 25; i++) { at = at with { X = at.X + 100 }; await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at).ToArray(), now += 200); }
            Check(session.OffMeshReports == 0 && session.Suspicions.GetValueOrDefault("off_navmesh") == 0, "walking on the navmesh logs nothing");
            at = at with { X = spawn.X + 6000 };   // a few metres beyond the floor's edge, reached at walking speed
            for (var i = 0; i < 30; i++) { at = at with { X = at.X + 100 }; await session.HandleAsync(new PacketWriter().U16(0x3701).U8(2).Vec3(at).ToArray(), now += 1000); }
            Check(session.OffMeshReports >= GameSession.OffMeshLogStreak && session.Suspicions.GetValueOrDefault("off_navmesh") == 1 && session.ServerPosition!.Value.X == (float)at.X,
                  "a long walk off the navmesh is logged once (LOG-ONLY: every report accepted)");
            session.ExitSharedWorld();
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>Gear above the character's level is refused (the client never sends it); gear it may wear still equips.</summary>
    static async Task Equip(GameData data)
    {
        var store = new FakeStore();
        store.Items.AddRange([Item(110120003, 1, "bag", 0, 40), Item(110160001, 1, "bag", 1, 41)]);   // a level-45 greatsword, the level-1 one
        var session = await InWorld(data, store, 12);
        Check(data.Item(110120003)!.PermitLevel > session.Level && data.Item(110160001)!.PermitLevel <= session.Level, "test gear levels");
        byte[] EquipRq(long wire) => new PacketWriter().U16(0xe247).I64(wire).U8(1).ToArray();
        var high = await session.HandleAsync(EquipRq(40), Now + 1000);
        Check(high.Count == 1 && BitConverter.ToUInt16(high[0], 2) == 1 && store.Items.Single(i => i.WireId == 40).Location == "bag"
              && session.Suspicions.GetValueOrDefault("equip_refused") == 1, "a level-45 weapon on a level-1 character is refused and logged");
        var low = await session.HandleAsync(EquipRq(41), Now + 2000);
        Check(BitConverter.ToUInt16(low[0], 2) == 0x0430 && session.Suspicions.GetValueOrDefault("equip_refused") == 1, "a level-1 weapon equips");
        session.ExitSharedWorld();
    }

    /// <summary>
    /// World freeze 2026-11-09 (build fce1352): a suspicion's audit row is written by a background task. Through the connection's
    /// TurnStore it released the world turn the connection's loop held, the loop released it again when it left (the second
    /// release threw), and the background re-take was never given back: every bot and player waited for the world lock forever.
    /// The write must leave the loop's turn alone, and the world must be free once the loop leaves its turn.
    /// </summary>
    static async Task CheatRecordKeepsTurn(GameData data)
    {
        var store = new FakeStore();
        store.Items.Add(Item(110120003, 1, "bag", 0, 40));   // a level-45 greatsword: equipping it is refused and logged
        var session = await InWorld(data, store, 31);
        var world = new SharedWorld(data);
        var turn = new WorldTurn(world);
        session.Store = TurnStore.Wrap(store, turn);
        var heldDuringWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CheatRecorded = async () => { heldDuringWrite.TrySetResult(turn.Held); await finishWrite.Task; };
        await turn.EnterWorldAsync(default);
        await session.HandleAsync(new PacketWriter().U16(0xe247).I64(40).U8(1).ToArray(), Now + 1000);
        Check(session.Suspicions.GetValueOrDefault("equip_refused") == 1, "the refused equip is logged");
        Check(await heldDuringWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)) && turn.Held,
            "a background cheat record does not release the world turn the connection's loop holds");
        var left = true;
        try { turn.Exit(); } catch (SemaphoreFullException) { left = false; }
        finishWrite.SetResult();
        await Task.Delay(50);
        var next = new WorldTurn(world);
        Check(left && next.EnterWorldAsync(default).Wait(2000), "after the loop leaves its turn the world is free (no freeze)");
        next.Exit();
        session.ExitSharedWorld();
    }

    static double AuthorityDistance(double seconds) => GameSession.AirSpeedOf(0x3707) * seconds + GameSession.LooseSlackCm;

    static void Limits()
    {
        var limits = new RequestLimiter();
        var t = Now;
        // A client's normal traffic: ping every 50 ms, ~12 movement reports and ~4 casts per second, a click or two; 60 s of it.
        var refused = 0;
        for (var ms = 0; ms < 60_000; ms += 50)
        {
            if (limits.Check(0x3602, 10, t + ms) != RequestLimiter.Verdict.Ok) refused++;
            if (ms % 100 == 0 && limits.Check(0x3701, 31, t + ms) != RequestLimiter.Verdict.Ok) refused++;
            if (ms % 250 == 0 && limits.Check(0x3800, 30, t + ms) != RequestLimiter.Verdict.Ok) refused++;
            if (ms % 500 == 0 && limits.Check(0x5612, 19, t + ms) != RequestLimiter.Verdict.Ok) refused++;
        }
        Check(refused == 0 && limits.Dropped == 0, "a busy player's normal traffic is never limited");
        // Bursts a person can make: 50 loot clicks at once, 200 movement reports in one second after a lag spike.
        var burst = new RequestLimiter();
        Check(Enumerable.Range(0, 50).All(i => burst.Check(0x5618, 4, t) == RequestLimiter.Verdict.Ok)
              && Enumerable.Range(0, 200).All(i => burst.Check(0x3701, 31, t + i * 5) == RequestLimiter.Verdict.Ok), "bursts (a lag spike, quick clicks) pass");
        var flood = new RequestLimiter();
        var floodDrops = Enumerable.Range(0, 500).Count(i => flood.Check(0x5618, 4, t) != RequestLimiter.Verdict.Ok);
        Check(floodDrops == 500 - (int)RequestLimiter.Other.Burst && !flood.Flooding, "a scripted flood of one request is cut to its burst; not yet a kick");
        Check(new RequestLimiter().Check(0x5612, RequestLimiter.MaxBody + 1, t) == RequestLimiter.Verdict.TooLarge
              && new RequestLimiter().Check(0x8d40, RequestLimiter.MaxBody + 1, t) == RequestLimiter.Verdict.Ok, "size limits (client options may be larger)");
        // The refill: a second later the bucket allows its rate again.
        Check(flood.Check(0x5618, 4, t + 1000) == RequestLimiter.Verdict.Ok, "the bucket refills");
    }

    static async Task SafetyNet(GameData data)
    {
        var session = await InWorld(data, null, 5);
        var now = Now + 1000;
        // A flood of one request: dropped beyond the burst, then the kick (level 3) once far beyond any client.
        var replies = 0;
        for (var i = 0; i < RequestLimiter.FloodKick + (int)RequestLimiter.Other.Burst + 10 && session.CloseReason is null; i++)
            replies += (await session.HandleAsync([0x60, 0xe2], now)).Count;
        Check(session.CloseReason == "kicked: request_flood" && session.Suspicions.GetValueOrDefault("request_flood") == 1
              && session.Suspicions.GetValueOrDefault("request_rate") > 0, "a request flood is dropped, then kicked (graded response, logged as an alert)");
        Check((await session.HandleAsync(new PacketWriter().U16(0x3602).U64(1).ToArray(), now)).Count == 0, "nothing is handled after the kick");
        session.ExitSharedWorld();
        var big = await InWorld(data, null, 6);
        Check((await big.HandleAsync([0x60, 0xe2, .. new byte[RequestLimiter.MaxBody]], now)).Count == 0 && big.Suspicions.GetValueOrDefault("request_size") == 1
              && big.CloseReason is null, "an oversized request is dropped and logged, the session goes on");
        big.ExitSharedWorld();
    }
}
