using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Aion.Host.Logging;

/// <summary>Aion:Logging:File: optional host log files next to the console/journal output. OURS.
/// Off by default. The directory must be absolute and outside the host's own (read-only under systemd
/// ProtectSystem=strict); the default is the user's local data folder (aion/logs); deploy/linux sets
/// /var/lib/aion/logs. Files rotate by size: server.log, server.1.log ... up to MaxFiles.
/// Idea from A2Core PR #4 (Artezianec), whose default under the host folder stopped a hosted start.</summary>
public sealed record FileLogOptions
{
    public bool Enabled { get; init; }
    public string? Directory { get; init; }
    public LogLevel MinLevel { get; init; } = LogLevel.Information;
    public long MaxFileBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxFiles { get; init; } = 10;
}

public static class FileLogging
{
    /// <summary>Adds the file logger when enabled. Never throws: a directory or file that cannot be used
    /// comes back as a warning for the caller to log once the host is built, and the host runs without it.</summary>
    public static (string? Path, string? Warning) Add(ILoggingBuilder logging, IConfiguration configuration)
    {
        var options = configuration.GetSection("Aion:Logging:File").Get<FileLogOptions>() ?? new FileLogOptions();
        if (!options.Enabled) return (null, null);
        string directory;
        try
        {
            directory = options.Directory is { Length: > 0 } configured
                ? configured
                : System.IO.Path.Combine(DefaultRoot(), "aion", "logs");
            if (!System.IO.Path.IsPathFullyQualified(directory))
                return (null, $"Log files are off: Aion:Logging:File:Directory must be an absolute path ({directory}).");
            directory = System.IO.Path.GetFullPath(directory);
            var hostDirectory = System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if ((directory + System.IO.Path.DirectorySeparatorChar).StartsWith(hostDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return (null, $"Log files are off: {directory} is inside the host folder; set Aion:Logging:File:Directory to a writable data folder.");
            var writer = RotatingFileWriter.Open(directory, "server", Math.Max(64 * 1024, options.MaxFileBytes), Math.Clamp(options.MaxFiles, 1, 100));
            var provider = new FileLoggerProvider(writer, options.MinLevel);
            logging.AddProvider(provider);
            return (writer.CurrentPath, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            return (null, $"Log files are off: {error.Message}");
        }
    }

    static string DefaultRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(root) ? System.IO.Path.GetTempPath() : root;
    }
}

sealed class FileLoggerProvider(RotatingFileWriter fileWriter, LogLevel level) : ILoggerProvider
{
    readonly ConcurrentDictionary<string, ILogger> loggers = new();
    readonly RotatingFileWriter writer = fileWriter;
    readonly LogLevel minLevel = level;

    public ILogger CreateLogger(string categoryName) => loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose() => writer.Dispose();

    sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.minLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'"))
                .Append(' ').Append(logLevel switch
                {
                    LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info",
                    LogLevel.Warning => "warn", LogLevel.Error => "fail", _ => "crit",
                })
                .Append(' ').Append(category).Append(": ").Append(formatter(state, exception));
            if (exception is not null) line.AppendLine().Append(exception);
            owner.writer.Enqueue(line.ToString());
        }
    }
}

/// <summary>One background task writes queued lines (bounded queue, oldest dropped when full) and flushes
/// whenever the queue runs dry. Past MaxFileBytes the files shift (server.log -> server.1.log ...) and the
/// oldest beyond MaxFiles is deleted. A write error is reported once on stderr; writing retries later.</summary>
sealed class RotatingFileWriter : IDisposable
{
    readonly string directory, name;
    readonly long maxBytes;
    readonly int maxFiles;
    readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(8192)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly Task loop;
    StreamWriter? text;
    long size;
    DateTime retryAt;
    bool reported;

    public string CurrentPath => System.IO.Path.Combine(directory, name + ".log");

    RotatingFileWriter(string directory, string name, long maxBytes, int maxFiles)
    {
        (this.directory, this.name, this.maxBytes, this.maxFiles) = (directory, name, maxBytes, maxFiles);
        System.IO.Directory.CreateDirectory(directory);
        OpenFile();  // the first open throws to the caller: a directory that is not writable turns file logging off
        loop = Task.Run(WriteLoopAsync);
    }

    public static RotatingFileWriter Open(string directory, string name, long maxBytes, int maxFiles) => new(directory, name, maxBytes, maxFiles);

    public void Enqueue(string line) => queue.Writer.TryWrite(line);

    void OpenFile()
    {
        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536);
        size = stream.Length;
        text = new StreamWriter(stream, new UTF8Encoding(false));
    }

    async Task WriteLoopAsync()
    {
        while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (queue.Reader.TryRead(out var line)) Write(line);
            Guard(() => text?.Flush());
        }
        Guard(() => text?.Flush());
        Close();
    }

    void Write(string line)
    {
        if (text is null)
        {
            if (DateTime.UtcNow < retryAt) return;
            if (!Guard(OpenFile)) { retryAt = DateTime.UtcNow.AddSeconds(30); return; }
        }
        if (size > 0 && size + line.Length + 2 > maxBytes && !Guard(Rotate)) { retryAt = DateTime.UtcNow.AddSeconds(30); return; }
        if (Guard(() => text!.WriteLine(line))) size += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        else retryAt = DateTime.UtcNow.AddSeconds(30);
    }

    void Rotate()
    {
        Close();
        for (var n = maxFiles - 1; n >= 1; n--)
        {
            var from = n == 1 ? CurrentPath : Numbered(n - 1);
            if (n == maxFiles - 1 && File.Exists(Numbered(n))) File.Delete(Numbered(n));
            if (File.Exists(from)) File.Move(from, Numbered(n), overwrite: true);
        }
        if (maxFiles == 1 && File.Exists(CurrentPath)) File.Delete(CurrentPath);
        OpenFile();
    }

    string Numbered(int n) => System.IO.Path.Combine(directory, $"{name}.{n}.log");

    bool Guard(Action action)
    {
        try { action(); reported = false; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!reported) Console.Error.WriteLine($"[log file] {error.Message}");
            reported = true;
            Close();
            return false;
        }
    }

    void Close()
    {
        try { text?.Dispose(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        text = null;
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        try { loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}
