using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Protocol.Native;
using Aion.World;
using Npgsql;

namespace Aion.Host.Native;

/// <summary>
/// Bots (USER 2026-11-08: populate the server, 50 Elyos and 50 Asmodians that play like players). Each bot is a headless player: a
/// stored character on its own bot account (provider 'bot'), a normal GameSession on the shared world, and a brain that sends the
/// same requests a game client sends (login, character select, world entry, movement). Everything the server does for players
/// therefore works for bots, and real players see them like any other player. Stage 1: bots log in and live in the world.
/// Stage 2 (<see cref="BotBrain"/>): they hunt, fight with their class skills, loot, rest, flee, die and revive, level up and spread out.
/// Classes go round in turn per race (an even mix), the gender is random. Count: server-rules.xml bots/count (0 = off).
/// </summary>
public sealed class BotService(IServiceProvider services, NativeOptions options, NpgsqlDataSource source, WorldPresence presence, ILogger<BotService> logger)
    : BackgroundService
{
    readonly List<BotRun> bots = [];

    /// <summary>Bots and how many are in the world (staff overview).</summary>
    public int Count => bots.Count;
    public int InWorld => bots.Count(b => b.InWorld);

    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> botAccounts = new();

    /// <summary>Whether an account is one of the bots' (the staff live map shows bots apart from players).</summary>
    public bool IsBotAccount(Guid? accountId) => accountId is { } id && botAccounts.ContainsKey(id);

    sealed class BotRun(int index, int race, VerifiedAccount account)
    {
        public int Index { get; } = index;
        public int Race { get; } = race;
        public VerifiedAccount Account { get; } = account;
        public bool InWorld { get; set; }
        public string? Name { get; set; }
        public BotBrain? Brain { get; set; }
        public GameSession? Session { get; set; }
        public int Level { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var count = ServerRules.Embedded.BotCount;
        if (count <= 0 || options.GamePort is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);   // the world and the listeners first
            var looks = await LooksAsync(ct);
            var data = GameData.Embedded;
            var creations = new Dictionary<(int Race, int Gender), List<int>>();
            for (var c = 1; c <= 64; c++)
                for (var g = 1; g <= 2; g++)
                    if (data.Profile(c, g) is { } p)
                        (creations.TryGetValue((p.Race, g), out var list) ? list : creations[(p.Race, g)] = []).Add(c);
            var runs = new List<Task> { Task.Run(() => MonitorAsync(ct), ct) };
            for (var i = 1; i <= count && !ct.IsCancellationRequested; i++)
            {
                var race = i % 2 == 1 ? 1 : 2;   // half Elyos (1), half Asmodians (2)
                var bot = new BotRun(i, race, await AccountAsync($"bot-{i:0000}", ct));
                bots.Add(bot);
                botAccounts[bot.Account.AccountId] = 0;
                runs.Add(Task.Run(() => RunAsync(bot, creations, looks, ct), ct));
                await Task.Delay(1500, ct);   // one at a time
            }
            logger.LogInformation("Bots started: {Count}", count);
            await Task.WhenAll(runs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // A bot failure never stops the server (live 2026-11-09: a database refusal at the first bot account stopped the host).
        catch (Exception error) { logger.LogError(error, "Bots stopped: {Error}", error.Message); }
    }

    /// <summary>The bot account (provider 'bot'), created once.</summary>
    async Task<VerifiedAccount> AccountAsync(string subject, CancellationToken ct)
    {
        return new VerifiedAccount(await services.GetRequiredService<Aion.Infrastructure.Postgres.PostgresAccountAuthentication>().BotAccountAsync(subject, ct));
    }

    /// <summary>Real characters' looks by race and gender (bots copy them, so they look like players).</summary>
    async Task<Dictionary<(int Race, int Gender), List<byte[]>>> LooksAsync(CancellationToken ct)
    {
        var looks = new Dictionary<(int, int), List<byte[]>>();
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = new NpgsqlCommand("SELECT ch.race,ch.gender,ch.appearance FROM game.characters ch JOIN auth.accounts a USING(account_id) "
            + "WHERE a.provider<>'bot' AND length(ch.appearance)>8", c);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetInt32(0), r.GetInt32(1));
            (looks.TryGetValue(key, out var list) ? list : looks[key] = []).Add((byte[])r.GetValue(2));
        }
        return looks;
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();


    // ---------------- monitor (USER 2026-11-09: "fully monitor the server process and get it at full speed") ----------------

    readonly object statsLock = new();
    List<double> sendSamples = [], tickSamples = [];
    readonly DateTime started = DateTime.UtcNow;

    void Sample(List<double> into, double ms) { lock (statsLock) if (into.Count < 200_000) into.Add(ms); }

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    /// <summary>
    /// Every minute: what the bots do (modes, levels, kills, deaths, quests, reports, errors), how fast the server answers them
    /// (request and tick time: average, 95th percentile, worst) and the server process (CPU, memory, threads, GC). Written to
    /// logs/bot-status.json (latest) and appended to logs/bot-status.jsonl (history). When the server is slow the bots think
    /// less often (BotBrain.Slowdown 1/2/4), so real players keep their speed.
    /// </summary>
    async Task MonitorAsync(CancellationToken ct)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var lastCpu = process.TotalProcessorTime;
        var lastWall = DateTime.UtcNow;
        var lastGen2 = GC.CollectionCount(2);
        var last = new Dictionary<int, (int Kills, int Deaths, int Rewards, int Reports)>();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            try
            {
                List<double> sends, ticks;
                lock (statsLock) { (sends, ticks) = (sendSamples, tickSamples); sendSamples = []; tickSamples = []; }
                sends.Sort(); ticks.Sort();
                process.Refresh();
                var cpu = process.TotalProcessorTime; var wall = DateTime.UtcNow;
                var cpuPercent = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (wall - lastWall).TotalMilliseconds) / Environment.ProcessorCount * 100;
                (lastCpu, lastWall) = (cpu, wall);
                var brains = bots.Where(b => b.Brain is not null).ToList();
                int dk = 0, dd = 0, dr = 0, drep = 0;
                foreach (var b in brains)
                {
                    var br = b.Brain!;
                    var before = last.GetValueOrDefault(b.Index);
                    dk += br.Kills - before.Kills; dd += br.Deaths - before.Deaths; dr += br.QuestRewards - before.Rewards; drep += br.Reports - before.Reports;
                    last[b.Index] = (br.Kills, br.Deaths, br.QuestRewards, br.Reports);
                }
                var p95 = Pct(sends, 0.95);
                // Real players first: bots think less often when requests get slow or the process is busy (tick budget).
                BotBrain.Slowdown = p95 > 400 || cpuPercent > 70 ? 4 : p95 > 150 || cpuPercent > 45 ? 2 : 1;
                var levels = brains.Select(b => b.Level).ToList();
                var status = new
                {
                    time = wall.ToString("u"), uptimeMinutes = Math.Round((wall - started).TotalMinutes),
                    bots = new
                    {
                        total = bots.Count, inWorld = bots.Count(b => b.InWorld),
                        doing = brains.GroupBy(b => b.Brain!.Doing).ToDictionary(g => g.Key, g => g.Count()),
                        level = new { avg = levels.Count > 0 ? Math.Round(levels.Average(), 1) : 0, max = levels.DefaultIfEmpty().Max() },
                        lastMinute = new { kills = dk, deaths = dd, questRewards = dr, stuckReports = drep },
                        sinceStart = new
                        {
                            kills = brains.Sum(b => b.Brain!.Kills), deaths = brains.Sum(b => b.Brain!.Deaths), questRewards = brains.Sum(b => b.Brain!.QuestRewards),
                            casts = brains.Sum(b => b.Brain!.Casts), loots = brains.Sum(b => b.Brain!.Loots), teleports = brains.Sum(b => b.Brain!.Teleports),
                            stuckReports = brains.Sum(b => b.Brain!.Reports), cheatFlags = brains.Sum(b => b.Brain!.Flags), lootLooks = brains.Sum(b => b.Brain!.LootLooks), lootSeen = brains.Sum(b => b.Brain!.LootSeen), errors = brains.Sum(b => b.Brain!.Errors),
                            questSteps = brains.Sum(b => b.Brain!.StepsDone), unstucks = brains.Sum(b => b.Brain!.Unstucks), gathers = brains.Sum(b => b.Brain!.Gathers),
                        },
                        slowdown = BotBrain.Slowdown,
                    },
                    server = new
                    {
                        requestsPerMinute = sends.Count,
                        requestMs = new { avg = sends.Count > 0 ? Math.Round(sends.Average(), 2) : 0, p95 = Math.Round(p95, 2), max = Math.Round(sends.LastOrDefault(), 1) },
                        tickMs = new { avg = ticks.Count > 0 ? Math.Round(ticks.Average(), 2) : 0, p95 = Math.Round(Pct(ticks, 0.95), 2), max = Math.Round(ticks.LastOrDefault(), 1) },
                        cpuPercent = Math.Round(cpuPercent, 1), memoryMb = process.WorkingSet64 / 1048576, gcHeapMb = GC.GetTotalMemory(false) / 1048576,
                        threads = process.Threads.Count, gen2PerMinute = GC.CollectionCount(2) - lastGen2,
                        threadPoolQueue = ThreadPool.PendingWorkItemCount,
                    },
                };
                lastGen2 = GC.CollectionCount(2);
                var dir = Path.GetFullPath(Path.Combine(options.TraceDirectory is { Length: > 0 } t ? t : AppContext.BaseDirectory, "..", "logs"));
                Directory.CreateDirectory(dir);
                var json = System.Text.Json.JsonSerializer.Serialize(status);
                await File.WriteAllTextAsync(Path.Combine(dir, "bot-status.json"), json, ct);
                await File.AppendAllTextAsync(Path.Combine(dir, "bot-status.jsonl"), json + Environment.NewLine, ct);
                // Each bot: where it is and what it works on (staff and the development session read it).
                var detail = brains.Select(b => new
                {
                    b.Index, b.Name, b.Level, map = b.Session?.MapId, at = b.Session?.BotPosition is { } p ? new[] { Math.Round(p.X), Math.Round(p.Y), Math.Round(p.Z) } : null,
                    doing = b.Brain!.Doing, quest = b.Brain.Quest, goal = b.Brain.Goal, idler = b.Brain.Idler, kills = b.Brain.Kills, rewards = b.Brain.QuestRewards,
                    reports = b.Brain.Reports, sight = b.Brain.Sight,
                });
                await File.WriteAllTextAsync(Path.Combine(dir, "bot-detail.json"), System.Text.Json.JsonSerializer.Serialize(detail), ct);
                WriteFindings(dir);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning("Bot monitor: {Error}", error.Message); }
        }
    }

    static readonly object reportLock = new();

    /// <summary>
    /// A bot's stuck report (USER 2026-11-09: "make sure all the quests they stuck reports and you pick up and resolve"): one JSON line
    /// in logs/bot-reports.jsonl next to the traces folder (G:\A2Core\logs on the Dell), read by the development session.
    /// </summary>
    void WriteReport(object report)
    {
        try
        {
            var dir = Path.GetFullPath(Path.Combine(options.TraceDirectory is { Length: > 0 } t ? t : AppContext.BaseDirectory, "..", "logs"));
            Directory.CreateDirectory(dir);
            var line = System.Text.Json.JsonSerializer.Serialize(report) + Environment.NewLine;
            lock (reportLock) File.AppendAllText(Path.Combine(dir, "bot-reports.jsonl"), line);
        }
        catch (Exception error) { logger.LogWarning("Bot report not written: {Error}", error.Message); }
    }

    /// <summary>
    /// The bots' findings for the development session (USER 2026-11-09: bots as automated testers): logs/bot-findings.md, rebuilt every
    /// minute from logs/bot-reports.jsonl - grouped by what failed (reason, quest, objective or map event step), with counts, bots, maps,
    /// a sample position and the server's reply, newest first. Aion:Bots:FindingsFile writes a second copy (the project log folder).
    /// </summary>
    void WriteFindings(string dir)
    {
        try
        {
            var reports = Path.Combine(dir, "bot-reports.jsonl");
            if (!File.Exists(reports)) return;
            var text = BotFindings.Summarise(TailLines(reports, 5_000_000), DateTime.UtcNow);
            File.WriteAllText(Path.Combine(dir, "bot-findings.md"), text);
            if (services.GetService<IConfiguration>()?["Aion:Bots:FindingsFile"] is { Length: > 0 } extra) File.WriteAllText(extra, text);
        }
        catch (Exception error) { logger.LogWarning("Bot findings not written: {Error}", error.Message); }
    }

    /// <summary>The last <paramref name="maxBytes"/> of a file as lines (the reports file only grows).</summary>
    static IEnumerable<string> TailLines(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (stream.Position > 0) reader.ReadLine();   // a cut line
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    async Task RunAsync(BotRun bot, Dictionary<(int Race, int Gender), List<int>> creations, Dictionary<(int Race, int Gender), List<byte[]>> looks, CancellationToken ct)
    {
        var rng = new Random(bot.Index * 7919 + Environment.TickCount);
        var world = services.GetService<SharedWorld>();
        var turn = world is null ? null : new WorldTurn(world);
        var store = new ServiceNativeCharacterStore("", new WorldIdentity(bot.Account, options.WorldId), services.GetRequiredService<IWorldSessions>(),
            services.GetRequiredService<IWireCharacterRepository>(), services.GetRequiredService<IGameplayStore>(), services.GetRequiredService<CharacterPersistenceService>());
        var session = new GameSession(GameData.Embedded, new GameSessionOptions
        {
            WorldId = options.WorldId, World = world, Terrain = services.GetService<TerrainStore>(), Nav = services.GetService<NavMeshStore>(),
            WorldRace = services.GetService<WorldChoices>() is { } choices ? choices.Race : null,
        }, new Random(rng.Next())) { Store = turn is null ? store : TurnStore.Wrap(store, turn), QuietNotes = true };

        // The same turn-taking as a client connection: map work in parallel per map, world work alone.
        async Task<List<byte[]>> InTurnAsync(Func<Task<List<byte[]>>> work, Func<bool> mapWork)
        {
            if (turn is null) return await work();
            if (session.SharedMap is { } map)
            {
                await turn.EnterMapAsync(map, ct);
                var handled = false;
                List<byte[]> done = [];
                try { if (session.SharedMap == map && mapWork()) { handled = true; done = await work(); } }
                finally { turn.Exit(); }
                if (handled) return done;
            }
            await turn.EnterWorldAsync(ct);
            try { return await work(); }
            finally { turn.Exit(); }
        }
        var lastReplies = "";
        async Task Send(byte[] request)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var replies = await InTurnAsync(() => session.HandleAsync(request, Now(), ct), () => GameSession.IsMapRequest(request));
            // The answer to the last request (opcodes, first bytes): a report quotes what the server said.
            if (request.Length >= 2 && (request[0] | request[1] << 8) is not (0x3700 or 0x3701 or 0x3718 or 0x3800 or 0x3622))
                lastReplies = $"0x{request[0] | request[1] << 8:x4} -> " + (replies.Count == 0 ? "no reply" : string.Join(" ", replies.Take(6).Select(r =>
                    r.Length < 2 ? "?" : $"0x{r[0] | r[1] << 8:x4}" + (r.Length > 2 ? ":" + Convert.ToHexString(r, 2, Math.Min(10, r.Length - 2)) : ""))));
            if (request.Length >= 2) ServerMetrics.Request(request[0] | request[1] << 8, t0);   // health page (bots are real load)
            Sample(sendSamples, clock.Elapsed.TotalMilliseconds);
            bot.Level = session.Level;
        }

        presence.Connected();
        try
        {
            // Login, character (created on the first run), selection, world entry - the client's requests.
            await Send(new PacketWriter().U16(0x3613).String($"{bot.Index}:{bot.Account.AccountId}").String("").String("t").String("").Zeros(9).ToArray());
            var lobby = await store.LobbyAsync(ct);
            for (var attempt = 0; lobby.Count == 0 && attempt < 8; attempt++)
            {
                var gender = rng.Next(1, 3);
                if (!creations.TryGetValue((bot.Race, gender), out var classes) || classes.Count == 0) continue;
                var look = looks.TryGetValue((bot.Race, gender), out var faces) && faces.Count > 0 ? faces[rng.Next(faces.Count)] : [0x0c, 0x64];
                await Send(new PacketWriter().U16(0x361b).String(BotNames.Next(rng)).U8((byte)gender).U32((uint)classes[(bot.Index / 2 + attempt) % classes.Count])
                    .Bytes(look).U8(1).U8(1).U8(1).ToArray());   // voice 1, slot 1, skip the tutorial
                lobby = await store.LobbyAsync(ct);
            }
            if (lobby.Count == 0) { logger.LogWarning("Bot {Index}: no character could be created", bot.Index); return; }
            await Send(new PacketWriter().U16(0x361f).Varint((uint)(lobby[0].WireId << 16)).I32(0).Vec3(Vector3d.Zero).ToArray());
            // The client asks for its bag and equipment (0x5610) - without it the server showed bots with nothing on (live 2026-11-09).
            await Send(new PacketWriter().U16(0x5610).ToArray());
            await Send(new PacketWriter().U16(0x3622).I32(1).ToArray());
            bot.Name = session.CharacterName;
            bot.InWorld = session.MapId is not null;
            if (bot.Name is not null) presence.Enter(bot.Name, session, bot.Account.AccountId);
            logger.LogInformation("Bot {Index} {Name} entered map {Map}", bot.Index, bot.Name, session.MapId);
            // What the bot sees, read in its map turn (the monsters move under the map lock).
            async Task<(List<GameSession.BotSight>, List<Vector3d>)> Look()
            {
                List<GameSession.BotSight> monsters = [];
                List<Vector3d> players = [];
                await InTurnAsync(() => { monsters = session.BotScan(BotBrain.ScanRadius); players = session.BotPlayersNear(BotBrain.CrowdRange * 2); return Task.FromResult(new List<byte[]>()); }, () => true);
                return (monsters, players);
            }
            async Task<List<(int Key, int NpcId, Vector3d At)>> Merchants()
            {
                List<(int, int, Vector3d)> found = [];
                await InTurnAsync(() => { found = session.BotMerchants(30_000); return Task.FromResult(new List<byte[]>()); }, () => true);
                return found;
            }
            async Task<List<(int Key, int NpcId, Vector3d At, bool Hostile, string Name)>> Npcs(double radius)
            {
                List<(int, int, Vector3d, bool, string)> found = [];
                await InTurnAsync(() => { found = session.BotNpcs(radius); return Task.FromResult(new List<byte[]>()); }, () => true);
                return found;
            }
            // A few bots (one in twenty) idle in town; all others quest and hunt.
            var brain = new BotBrain(session, Send, Look, services.GetService<NavMeshStore>(), rng, Merchants, Npcs, WriteReport) { Idler = bot.Index % 20 == 0 };
            brain.LastReplies = () => lastReplies;
            bot.Brain = brain;
            bot.Session = session;
            await brain.RunAsync(async now =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var replies = await InTurnAsync(() => session.TickAsync(now, ct), () => !session.NeedsWorldTick(now));
                ServerMetrics.MapTick(session.MapId ?? 0, t0);
                Sample(tickSamples, clock.Elapsed.TotalMilliseconds);
                return replies;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { logger.LogWarning(error, "Bot {Index} stopped", bot.Index); }
        finally
        {
            bot.InWorld = false;
            presence.Disconnected();
            try
            {
                if (bot.Name is not null) presence.Leave(bot.Name, session);
                if (turn is not null)
                {
                    await turn.EnterWorldAsync(CancellationToken.None);
                    try { session.ExitSharedWorld(); }
                    finally { turn.Exit(); }
                }
                session.Abandon();
                await session.CheckpointAsync(Now(), force: true, CancellationToken.None);
            }
            catch (Exception error) { logger.LogWarning("Bot {Index}: leaving failed: {Error}", bot.Index, error.Message); }
        }
    }
}
