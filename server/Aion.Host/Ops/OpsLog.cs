using System.Collections.Concurrent;

namespace Aion.Host.Ops;

/// <summary>
/// The last warnings and errors the host logged, for the "Server health" page and the error-burst check (Stage G).
/// Only Warning and above reach it (nothing is formatted for ordinary log lines); a repeat of the latest message within a
/// minute only bumps its count, so one noisy loop cannot push everything else out.
/// </summary>
public sealed class OpsLog
{
    public const int Keep = 80;

    public sealed class Entry(DateTimeOffset at, string level, string category, string message, string? exception)
    {
        public DateTimeOffset At { get; } = at;
        public string Level { get; } = level;
        public string Category { get; } = category;
        public string Message { get; } = message;
        public string? Exception { get; } = exception;
        public DateTimeOffset Last { get; internal set; } = at;
        public int Count { get; internal set; } = 1;
    }

    /// <summary>The one buffer of this process (the logger provider is created before dependency injection).</summary>
    public static OpsLog Shared { get; } = new();

    readonly object gate = new();
    readonly LinkedList<Entry> entries = new();
    // Times of recent errors / warnings (pruned to 10 minutes) for "how many in the last minute / 5 minutes".
    readonly Queue<long> errorTimes = new(), warningTimes = new();
    long errorsTotal, warningsTotal;

    public long ErrorsTotal => Interlocked.Read(ref errorsTotal);
    public long WarningsTotal => Interlocked.Read(ref warningsTotal);

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTimeOffset.UtcNow;
        var error = level >= LogLevel.Error;
        var name = error ? (level == LogLevel.Critical ? "critical" : "error") : "warning";
        if (message.Length > 400) message = message[..400] + "…";
        var shortCategory = category.LastIndexOf('.') is var dot and >= 0 ? category[(dot + 1)..] : category;
        lock (gate)
        {
            Interlocked.Increment(ref error ? ref errorsTotal : ref warningsTotal);
            var times = error ? errorTimes : warningTimes;
            times.Enqueue(now.ToUnixTimeMilliseconds());
            Prune(times, now.ToUnixTimeMilliseconds());
            if (entries.First?.Value is { } last && last.Level == name && last.Category == shortCategory && last.Message == message && now - last.Last < TimeSpan.FromMinutes(1))
            {
                last.Count++;
                last.Last = now;
                return;
            }
            entries.AddFirst(new Entry(now, name, shortCategory, message, exception?.GetType().Name));
            while (entries.Count > Keep) entries.RemoveLast();
        }
    }

    static void Prune(Queue<long> times, long nowMs)
    {
        while (times.Count > 0 && times.Peek() < nowMs - 600_000) times.Dequeue();
        while (times.Count > 5000) times.Dequeue();
    }

    /// <summary>Errors and warnings logged within the last <paramref name="window"/> (at most 10 minutes back).</summary>
    public (int Errors, int Warnings) Recent(TimeSpan window)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)window.TotalMilliseconds;
        lock (gate) return (errorTimes.Count(t => t >= since), warningTimes.Count(t => t >= since));
    }

    public IReadOnlyList<Entry> Latest(int count)
    {
        lock (gate) return entries.Take(count).ToList();
    }
}

/// <summary>Feeds Warning+ log lines of every category into <see cref="OpsLog.Shared"/>.</summary>
public sealed class OpsLogProvider(OpsLog log) : ILoggerProvider
{
    readonly ConcurrentDictionary<string, OpsLogger> loggers = new();
    public ILogger CreateLogger(string categoryName) => loggers.GetOrAdd(categoryName, name => new OpsLogger(log, name));
    public void Dispose() { }

    sealed class OpsLogger(OpsLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string text;
            try { text = formatter(state, exception); } catch (FormatException) { text = state?.ToString() ?? ""; }
            if (exception is not null && text.Length < 300) text += " (" + exception.Message + ")";
            log.Add(logLevel, category, text, exception);
        }
    }
}
