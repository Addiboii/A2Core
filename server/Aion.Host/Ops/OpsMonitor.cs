using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aion.Host.Native;
using Aion.World;

namespace Aion.Host.Ops;

/// <summary>Aion:Ops: alert file and the green / yellow / red limits of the health page (Stage G).</summary>
public sealed record OpsOptions
{
    /// <summary>Alert lines are appended here (unset: &lt;DataDir&gt;\logs\alerts.log).</summary>
    public string? AlertLog { get; init; }
    /// <summary>One zone tick at 20 Hz: a map whose tick p95 is over this is red, over half of it yellow.</summary>
    public double TickBudgetMs { get; init; } = 50;
    /// <summary>Client request handling (turn wait included): p95 over this is red, over a third of it yellow.</summary>
    public double RequestBudgetMs { get; init; } = 150;
    public double DiskRedGb { get; init; } = 5;
    public double DiskYellowGb { get; init; } = 20;
    /// <summary>The newest database dump older than this is red (the nightly task missed a night and the catch-up run).</summary>
    public double BackupRedHours { get; init; } = 30;
    public double RestoreTestYellowDays { get; init; } = 35;
    /// <summary>Errors logged in 5 minutes: this many or more is red ("error burst").</summary>
    public int ErrorBurst { get; init; } = 20;
    /// <summary>A red check is written to the alert log again after this long while it stays red.</summary>
    public double RealertMinutes { get; init; } = 60;
}

/// <summary>
/// Samples <see cref="ServerMetrics"/>, <see cref="DbMetrics"/>, the process and the presence every 5 s into one-minute windows,
/// rates every area green / yellow / red, and appends a line to the alert log when an area turns red (and when it is fine
/// again). Also notes host starts and clean stops (logs/host-starts.log) to spot crash loops, and reads the backup and
/// restore-test results the scheduled tasks leave next to the dumps. <see cref="Snapshot"/> is the staff page's data.
/// </summary>
public sealed class OpsMonitor : BackgroundService
{
    public const string Ok = "ok", Warn = "warn", Bad = "bad", Unknown = "unknown";
    static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(5);
    const int WindowSamples = 12;   // one minute

    readonly OpsOptions options;
    readonly IServiceProvider services;
    readonly ILogger<OpsMonitor> logger;
    readonly string logsDir, alertLog, startsFile;
    readonly string? backupsDir;
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    readonly object gate = new();

    // Per key: snapshots of the totals at each sample (oldest first) and the largest value per sample.
    sealed class Series
    {
        public readonly Queue<LatencyStats.Totals> Totals = new();
        public readonly Queue<long> Max = new();
        public LatencyStats.Totals Window = LatencyStats.Totals.Empty;
        public long WindowMaxMicros;
    }
    readonly Dictionary<int, Series> mapSeries = [], requestSeries = [];
    readonly Series dbSeries = new();
    readonly Queue<(long At, long Packets, long Bytes, long Calls, long Received, long Refused, long DbFailed, long StoreErrors, double Cpu, TimeSpan GcPause, int Gen0, int Gen1, int Gen2)> counters = new();
    readonly Queue<(DateTimeOffset At, long Refused, long DbFailed, long Cheats)> slow = new();   // one per minute, an hour back
    readonly Dictionary<string, (string State, DateTimeOffset At)> alerted = [];
    List<DateTimeOffset> uncleanExits = [];
    object? snapshot;
    (string Overall, List<Check> Checks) rated = (Unknown, []);

    public sealed record Check(string Id, string Label, string State, string Value, string Detail);

    public OpsMonitor(IConfiguration config, IServiceProvider services, ILogger<OpsMonitor> logger, IHostApplicationLifetime lifetime)
    {
        options = config.GetSection("Aion:Ops").Get<OpsOptions>() ?? new OpsOptions();
        this.services = services;
        this.logger = logger;
        var dataDir = config["Aion:DataDir"] is { Length: > 0 } d ? d
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "A2Core") : "/var/lib/aion";
        logsDir = Path.Combine(dataDir, "logs");
        alertLog = options.AlertLog is { Length: > 0 } a ? a : Path.Combine(logsDir, "alerts.log");
        startsFile = Path.Combine(logsDir, "host-starts.log");
        backupsDir = config["Aion:Backups:Directory"] is { Length: > 0 } b ? b : null;
        lifetime.ApplicationStopping.Register(() => NoteStart("stop"));
    }

    public string AlertLogPath => alertLog;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            uncleanExits = NoteStart("start");
            if (uncleanExits.Count > 0 && DateTimeOffset.UtcNow - uncleanExits[^1] < TimeSpan.FromMinutes(5))
                Alert("crash", Bad, $"the host stopped without a clean shutdown and was started again ({uncleanExits.Count(u => DateTimeOffset.UtcNow - u < TimeSpan.FromHours(1))} times in the last hour)");
        }
        catch (Exception error) { logger.LogWarning("Health monitor: start not noted: {Error}", error.Message); }
        using var timer = new PeriodicTimer(SamplePeriod);
        do
        {
            try { Sample(); }
            catch (Exception error) { logger.LogWarning("Health monitor: sample failed: {Error}", error.Message); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // ---------------------------------------------------------------------------------------------------- sampling

    static void Advance(Series s, LatencyStats stats)
    {
        var last = s.Totals.Count > 0 ? s.Totals.Last() : null;
        // Unchanged totals keep the same object: idle maps and opcodes allocate nothing.
        var now = last is not null && last.Count == stats.Count ? last : stats.Read();
        s.Totals.Enqueue(now);
        s.Max.Enqueue(stats.TakeMax());
        while (s.Totals.Count > WindowSamples + 1) s.Totals.Dequeue();
        while (s.Max.Count > WindowSamples) s.Max.Dequeue();
        s.Window = s.Totals.Count > 1 ? now.Minus(s.Totals.Peek()) : now;
        s.WindowMaxMicros = s.Max.Count > 0 ? s.Max.Max() : 0;
    }

    static void AdvanceAll(Dictionary<int, Series> into, IEnumerable<KeyValuePair<int, LatencyStats>> source)
    {
        foreach (var (key, stats) in source)
        {
            if (!into.TryGetValue(key, out var s)) into[key] = s = new Series();
            Advance(s, stats);
        }
    }

    void Sample()
    {
        lock (gate)
        {
            AdvanceAll(mapSeries, ServerMetrics.Maps);
            AdvanceAll(requestSeries, ServerMetrics.Requests);
            Advance(dbSeries, DbMetrics.Shared.Operations);
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            counters.Enqueue((Stopwatch.GetTimestamp(), ServerMetrics.SentPackets, ServerMetrics.SentBytes, ServerMetrics.SendCalls, ServerMetrics.ReceivedBytes,
                Interlocked.Read(ref NetworkMetrics.RefusedConnections), DbMetrics.Shared.Failed, ServerMetrics.StoreErrors, process.TotalProcessorTime.TotalSeconds,
                GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
            while (counters.Count > WindowSamples + 1) counters.Dequeue();
            var cheatsTotal = ServerMetrics.Cheats.Values.Sum();
            if (slow.Count == 0 || DateTimeOffset.UtcNow - slow.Last().At >= TimeSpan.FromMinutes(1))
            {
                slow.Enqueue((DateTimeOffset.UtcNow, Interlocked.Read(ref NetworkMetrics.RefusedConnections), DbMetrics.Shared.Failed, cheatsTotal));
                while (slow.Count > 61) slow.Dequeue();
            }
            snapshot = Build(process, gc, cheatsTotal);
        }
    }

    // ---------------------------------------------------------------------------------------------------- the page's data

    static object Stats(LatencyStats.Totals w, long maxMicros, double seconds) => new
    {
        count = w.Count, perSecond = seconds > 0 ? Math.Round(w.Count / seconds, 1) : 0,
        avgMs = Math.Round(w.AverageMs, 2), p95Ms = Math.Round(w.PercentileMs(0.95), 2), p99Ms = Math.Round(w.PercentileMs(0.99), 2),
        maxMs = Math.Round(maxMicros / 1000.0, 2),
    };

    object Build(Process process, GCMemoryInfo gc, long cheatsTotal)
    {
        var first = counters.Peek();
        var last = counters.Last();
        var seconds = Math.Max(0.001, (last.At - first.At) / (double)Stopwatch.Frequency);
        var single = counters.Count < 2;
        double Rate(long a, long b) => single ? 0 : Math.Round((b - a) / seconds, 1);
        var cores = Environment.ProcessorCount;
        var cpu = single ? 0 : (last.Cpu - first.Cpu) / seconds / cores;
        var gcPause = single ? 0 : (last.GcPause - first.GcPause).TotalSeconds / seconds;

        var presence = services.GetService<WorldPresence>();
        var players = presence?.All ?? [];
        var perMap = players.GroupBy(p => p.Session.MapId ?? 0).ToDictionary(g => g.Key, g => g.Count());
        var perWorld = players.GroupBy(p => p.Session.Store?.WorldId ?? 0).Select(g => new { world = g.Key, players = g.Count() }).OrderBy(w => w.world).ToList();
        var outboxes = players.Select(p => p.Session.OutboxLength).ToList();
        var maps = mapSeries.Keys.Union(perMap.Keys).OrderBy(k => k).Select(id =>
        {
            var s = mapSeries.GetValueOrDefault(id);
            return new { map = id, players = perMap.GetValueOrDefault(id), tick = s is null ? null : Stats(s.Window, s.WindowMaxMicros, seconds),
                p95 = s is null ? 0 : s.Window.PercentileMs(0.95), count = s?.Window.Count ?? 0 };
        }).Where(m => m.players > 0 || m.count > 0).ToList();

        var allRequests = requestSeries.Values.Aggregate(LatencyStats.Totals.Empty, (sum, s) => s.Window.Count > 0 ? sum.Plus(s.Window) : sum);
        var requestMax = requestSeries.Values.Select(s => s.WindowMaxMicros).DefaultIfEmpty(0).Max();
        var opcodes = requestSeries.Where(r => r.Value.Window.Count > 0)
            .OrderByDescending(r => r.Value.Window.PercentileMs(0.95) * Math.Log(1 + r.Value.Window.Count)).Take(20)
            .Select(r => new { opcode = $"0x{r.Key:x4}", name = OpcodeNames.Get(r.Key), stats = Stats(r.Value.Window, r.Value.WindowMaxMicros, seconds) }).ToList();

        var (errors1, warnings1) = OpsLog.Shared.Recent(TimeSpan.FromMinutes(1));
        var (errors5, warnings5) = OpsLog.Shared.Recent(TimeSpan.FromMinutes(5));
        var hourAgo = slow.Peek();
        var fiveAgo = slow.Reverse().Skip(Math.Min(5, slow.Count - 1)).FirstOrDefault(hourAgo);
        var dbFailed5 = DbMetrics.Shared.Failed - fiveAgo.DbFailed;
        var refused5 = Interlocked.Read(ref NetworkMetrics.RefusedConnections) - fiveAgo.Refused;
        var cheatsHour = cheatsTotal - hourAgo.Cheats;

        var backups = BackupState();
        var disks = Disks();
        var memoryTotal = gc.TotalAvailableMemoryBytes;
        var health = new
        {
            players = players.Count, connections = presence?.Connections ?? 0, perWorld,
            maps = maps.Select(m => new { m.map, m.players, m.tick }).ToList(),
        };
        var checks = RateChecks(maps.Select(m => (m.map, m.p95, m.count)).ToList(), allRequests, cpu, process.WorkingSet64, memoryTotal, gcPause, errors5, dbFailed5,
            dbSeries.Window, refused5, outboxes.DefaultIfEmpty(0).Max(), ThreadPool.PendingWorkItemCount, cheatsHour, disks, backups);
        var overall = checks.Any(c => c.State == Bad) ? Bad : checks.Any(c => c.State == Warn) ? Warn : Ok;
        foreach (var c in checks) Alert(c.Id, c.State, $"{c.Label}: {c.Value} - {c.Detail}");
        rated = (overall, checks);
        return new
        {
            at = DateTimeOffset.UtcNow, startedAt = started, windowSeconds = Math.Round(seconds, 1), overall, checks,
            world = health,
            requests = new { all = Stats(allRequests, requestMax, seconds), slowest = opcodes },
            network = new
            {
                packetsPerSecond = Rate(first.Packets, last.Packets), bytesPerSecond = Rate(first.Bytes, last.Bytes), sendsPerSecond = Rate(first.Calls, last.Calls),
                receivedBytesPerSecond = Rate(first.Received, last.Received),
                refusedConnections = Interlocked.Read(ref NetworkMetrics.RefusedConnections), refusedLast5Minutes = refused5, blockedIps = NetworkMetrics.BlockedIps,
                outboxTotal = outboxes.Sum(), outboxMax = outboxes.DefaultIfEmpty(0).Max(), threadPoolQueue = ThreadPool.PendingWorkItemCount,
                threadPoolThreads = ThreadPool.ThreadCount,
            },
            database = new
            {
                connected = DbMetrics.Shared.Seen, calls = Stats(dbSeries.Window, dbSeries.WindowMaxMicros, seconds),
                failedTotal = DbMetrics.Shared.Failed, failedLast5Minutes = dbFailed5, executing = DbMetrics.Shared.Executing,
                waitingForConnection = DbMetrics.Shared.PendingConnectionRequests, connectionTimeouts = DbMetrics.Shared.ConnectionTimeouts,
                storeRefusals = ServerMetrics.StoreErrors,
            },
            process = new
            {
                cores, cpu = Math.Round(cpu, 3), memoryMb = process.WorkingSet64 / 1048576, machineMemoryMb = memoryTotal / 1048576,
                heapMb = GC.GetTotalMemory(false) / 1048576, gcPausePercent = Math.Round(gcPause * 100, 2),
                gcPerMinute = new { gen0 = Rate(first.Gen0, last.Gen0) * 60, gen1 = Rate(first.Gen1, last.Gen1) * 60, gen2 = Rate(first.Gen2, last.Gen2) * 60 },
                threads = process.Threads.Count, handles = process.HandleCount,
                uncleanExits24h = uncleanExits.Count(u => DateTimeOffset.UtcNow - u < TimeSpan.FromHours(24)),
            },
            cheats = new { lastHour = cheatsHour, sinceStart = ServerMetrics.Cheats, speedRejectsOnline = players.Sum(p => p.Session.SpeedRejects) },
            errors = new { lastMinute = errors1, last5Minutes = errors5, warningsLastMinute = warnings1, warningsLast5Minutes = warnings5, total = OpsLog.Shared.ErrorsTotal,
                warningsTotal = OpsLog.Shared.WarningsTotal },
            backups, disks,
        };
    }

    // ---------------------------------------------------------------------------------------------------- rating

    static string Level(double value, double yellow, double red) => value >= red ? Bad : value >= yellow ? Warn : Ok;
    static string Worst(params string[] states) => states.Contains(Bad) ? Bad : states.Contains(Warn) ? Warn : states.All(s => s == Unknown) ? Unknown : Ok;

    List<Check> RateChecks(List<(int Map, double P95, long Count)> maps, LatencyStats.Totals requests, double cpu, long workingSet, long memoryTotal, double gcPause,
        int errors5, long dbFailed5, LatencyStats.Totals db, long refused5, int outboxMax, long poolQueue, long cheatsHour,
        List<DiskState> disks, BackupInfo backups)
    {
        var list = new List<Check>();
        var busy = maps.Where(m => m.Count >= 20).OrderByDescending(m => m.P95).FirstOrDefault();
        list.Add(busy.Count == 0
            ? new Check("tick", "Map ticks", Unknown, "no ticks", "no map ticked enough in the last minute to judge")
            : new Check("tick", "Map ticks", Level(busy.P95, options.TickBudgetMs / 2, options.TickBudgetMs), $"{busy.P95:0.#} ms p95",
                $"slowest map {busy.Map}; budget {options.TickBudgetMs:0} ms per tick (20 per second)"));
        var rp95 = requests.PercentileMs(0.95);
        list.Add(requests.Count < 20
            ? new Check("requests", "Request handling", Unknown, $"{requests.Count} requests", "too few requests in the last minute to judge")
            : new Check("requests", "Request handling", Level(rp95, options.RequestBudgetMs / 3, options.RequestBudgetMs), $"{rp95:0.#} ms p95",
                $"time from a client's message to the answer; red over {options.RequestBudgetMs:0} ms"));
        list.Add(new Check("cpu", "CPU", Level(cpu, 0.7, 0.9), $"{cpu * 100:0}%", $"of all {Environment.ProcessorCount} cores"));
        var mem = memoryTotal > 0 ? workingSet / (double)memoryTotal : 0;
        list.Add(new Check("memory", "Memory", Worst(Level(mem, 0.7, 0.9), Level(gcPause, 0.05, 0.15)), $"{workingSet / 1048576} MB",
            $"{mem * 100:0}% of the machine; garbage collection pauses {gcPause * 100:0.#}% of the time"));
        list.Add(new Check("errors", "Errors", Level(errors5, 1, options.ErrorBurst), $"{errors5} in 5 min",
            errors5 >= options.ErrorBurst ? "error burst: see the list below" : "errors written to the server log"));
        var dbP95 = db.PercentileMs(0.95);
        list.Add(!DbMetrics.Shared.Seen
            ? new Check("database", "Database", Unknown, "no calls yet", "no database call since start")
            : new Check("database", "Database", Worst(Level(dbFailed5, 1, 5), db.Count >= 10 ? Level(dbP95, 100, 1000) : Ok, Level(DbMetrics.Shared.PendingConnectionRequests, 5, 50)),
                dbFailed5 > 0 ? $"{dbFailed5} failed in 5 min" : $"{dbP95:0.#} ms p95", $"{db.Count} calls in the last minute, {DbMetrics.Shared.Failed} failed since start"));
        list.Add(new Check("network", "Network", Worst(Level(outboxMax, 500, 5000), Level(poolQueue, 100, 1000), refused5 > 0 ? Warn : Ok),
            refused5 > 0 ? $"{refused5} refused" : $"queue {outboxMax}",
            $"largest player outbox {outboxMax}, work queue {poolQueue}, connections refused in 5 min {refused5} (floods / booters)"));
        list.Add(new Check("cheats", "Cheat suspicions", cheatsHour > 0 ? Warn : Ok, $"{cheatsHour} last hour", "speed / teleport / skill-rate detections (audit log: cheat_suspected)"));
        var worstDisk = disks.OrderBy(d => d.FreeGb).FirstOrDefault();
        list.Add(worstDisk is null ? new Check("disk", "Disk space", Unknown, "?", "drives not readable")
            : new Check("disk", "Disk space", worstDisk.FreeGb < options.DiskRedGb ? Bad : worstDisk.FreeGb < options.DiskYellowGb ? Warn : Ok,
                $"{worstDisk.FreeGb:0.#} GB free", $"drive {worstDisk.Drive} (server data, database, backups)"));
        list.Add(backups.Newest is not { } newest
            ? new Check("backup", "Backups", backupsDir is null ? Unknown : Bad, "none", backupsDir is null ? "no backup folder configured" : "no database dump found")
            : new Check("backup", "Backups", backups.LastRunFailed ? Bad : Level(backups.NewestAgeHours, options.BackupRedHours * 0.85, options.BackupRedHours),
                $"{backups.NewestAgeHours:0.#} h old", backups.LastRunFailed ? $"the last nightly backup failed: {backups.LastError}" : $"newest {newest}, {backups.Count} kept"));
        list.Add(backups.RestoreTestAt is not { } tested
            ? new Check("restore", "Restore test", backupsDir is null ? Unknown : Warn, "never", "the monthly restore test has not run yet")
            : new Check("restore", "Restore test", backups.RestoreTestOk != true ? Bad : (DateTimeOffset.UtcNow - tested).TotalDays > options.RestoreTestYellowDays ? Warn : Ok,
                backups.RestoreTestOk == true ? "passed" : "FAILED", $"{tested:yyyy-MM-dd}: {backups.RestoreTestSummary}"));
        var unclean = uncleanExits.Count(u => DateTimeOffset.UtcNow - u < TimeSpan.FromHours(1));
        var unclean24 = uncleanExits.Count(u => DateTimeOffset.UtcNow - u < TimeSpan.FromHours(24));
        list.Add(new Check("crash", "Crashes", unclean >= 3 ? Bad : unclean24 > 0 ? Warn : Ok, $"{unclean24} in 24 h",
            unclean >= 3 ? "crash loop: the host keeps stopping without a clean shutdown" : "host stops without a clean shutdown (deploys and restarts are clean)"));
        return list;
    }

    // ---------------------------------------------------------------------------------------------------- alerts

    void Alert(string id, string state, string text)
    {
        if (state == Unknown) return;
        var now = DateTimeOffset.UtcNow;
        var had = alerted.TryGetValue(id, out var before);
        if (state == Bad)
        {
            if (had && before.State == Bad && now - before.At < TimeSpan.FromMinutes(options.RealertMinutes)) return;
            alerted[id] = (Bad, now);
            Append($"RED {id}: {text}");
        }
        else if (had && before.State == Bad)
        {
            alerted[id] = (state, now);
            Append($"OK  {id}: {text}");
        }
    }

    void Append(string line)
    {
        var text = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z A2Core host {line}";
        logger.LogWarning("Health alert: {Line}", line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(alertLog)!);
            File.AppendAllText(alertLog, text + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The last lines of the alert log (the host's and the scheduled tasks').</summary>
    public List<string> RecentAlerts(int count)
    {
        try
        {
            if (!File.Exists(alertLog)) return [];
            using var stream = new FileStream(alertLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256 * 1024) stream.Seek(-256 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.TakeLast(count).Reverse().ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---------------------------------------------------------------------------------------------------- starts, disks, backups

    /// <summary>Appends "start"/"stop" to logs/host-starts.log; on start returns the times of starts that followed a run without a clean stop.</summary>
    List<DateTimeOffset> NoteStart(string what)
    {
        Directory.CreateDirectory(logsDir);
        List<DateTimeOffset> unclean = [];
        if (what == "start" && File.Exists(startsFile))
        {
            var lines = File.ReadAllLines(startsFile).TakeLast(400).ToList();
            var running = false;
            foreach (var line in lines.Append($"{DateTimeOffset.UtcNow:O} start"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 2 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
                if (parts[1] == "start") { if (running) unclean.Add(at); running = true; }
                else if (parts[1] == "stop") running = false;
            }
            if (lines.Count >= 400) File.WriteAllLines(startsFile, lines.TakeLast(200));
        }
        File.AppendAllText(startsFile, $"{DateTimeOffset.UtcNow:O} {what} {Environment.ProcessId}{Environment.NewLine}");
        return unclean;
    }

    public sealed record DiskState(string Drive, double FreeGb, double TotalGb);

    List<DiskState> Disks()
    {
        var roots = new[] { logsDir, backupsDir, AppContext.BaseDirectory }.OfType<string>()
            .Select(p => Path.GetPathRoot(Path.GetFullPath(p))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        var list = new List<DiskState>();
        foreach (var root in roots)
        {
            try { var d = new DriveInfo(root); list.Add(new DiskState(d.Name, Math.Round(d.AvailableFreeSpace / 1e9, 1), Math.Round(d.TotalSize / 1e9, 1))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return list;
    }

    public sealed record BackupInfo(string? Newest, double NewestAgeHours, int Count, double TotalMb, string? Oldest, bool LastRunFailed, string? LastError,
        DateTimeOffset? LastRunAt, DateTimeOffset? RestoreTestAt, bool? RestoreTestOk, string? RestoreTestSummary, JsonElement? RestoreTest);

    BackupInfo? backupCache;
    DateTimeOffset backupReadAt;

    /// <summary>Dumps in the backup folder plus backup-status.json (nightly task) and restore-test.json (monthly task); read once a minute.</summary>
    BackupInfo BackupState()
    {
        if (backupCache is not null && DateTimeOffset.UtcNow - backupReadAt < TimeSpan.FromMinutes(1)) return backupCache;
        backupReadAt = DateTimeOffset.UtcNow;
        if (backupsDir is null || !Directory.Exists(backupsDir))
            return backupCache = new BackupInfo(null, 0, 0, 0, null, false, null, null, null, null, null, null);
        var dumps = new DirectoryInfo(backupsDir).GetFiles("*.dump").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var newest = dumps.FirstOrDefault();
        JsonElement? Read(string name)
        {
            try { var path = Path.Combine(backupsDir, name); return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone() : null; }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
        static string? Str(JsonElement? e, string p) => e is { } x && x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool? Bool(JsonElement? e, string p) => e is { } x && x.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        static DateTimeOffset? Time(JsonElement? e, string p) => DateTimeOffset.TryParse(Str(e, p), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var status = Read("backup-status.json");
        var test = Read("restore-test.json");
        var lastRun = Time(status, "at");
        // A failed run counts only while no newer dump exists (a deploy's dump after it fixes the state).
        var failed = Bool(status, "ok") == false && (newest is null || lastRun is null || newest.LastWriteTimeUtc < lastRun.Value.UtcDateTime);
        return backupCache = new BackupInfo(newest?.Name, newest is null ? 0 : Math.Round((DateTime.UtcNow - newest.LastWriteTimeUtc).TotalHours, 1), dumps.Count,
            Math.Round(dumps.Sum(f => f.Length) / 1048576.0, 1), dumps.LastOrDefault()?.Name, failed, Str(status, "error"), lastRun,
            Time(test, "at"), Bool(test, "ok"), Str(test, "summary"), test);
    }

    // ---------------------------------------------------------------------------------------------------- read side

    /// <summary>The latest sample for the staff page (null before the first one).</summary>
    public object? Snapshot() { lock (gate) return snapshot; }

    /// <summary>The overall state and the checks (loopback /health/ops for deploy scripts).</summary>
    public (string Overall, List<Check> Checks) Rated() { lock (gate) return rated; }
}
