using System.Collections.Concurrent;
using System.Text;
using Aion.Host.Debug;

namespace Aion.Host.Logging;

/// <summary>
/// Writes every log event that reaches the logging pipeline (same levels as configured for the host)
/// to one UTF-8 file: server-yyyyMMdd-HHmmss.log under the log directory.
/// </summary>
public sealed class ServerFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    sealed class ServerFileLogger(string category, ServerFileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.minLevel && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.file.Enqueue(Format(category, logLevel, formatter(state, exception), exception));
        }
    }

    readonly LogLevel minLevel;
    readonly ChannelFileLogWriter file;
    readonly ConcurrentDictionary<string, ServerFileLogger> loggers = new();

    public string Path => file.Path;

    ServerFileLoggerProvider(ChannelFileLogWriter file, LogLevel minLevel)
    {
        this.file = file;
        this.minLevel = minLevel;
    }

    public static ServerFileLoggerProvider? TryCreate(IConfiguration configuration)
    {
        var options = configuration.GetSection(AionConfiguration.ServerLog).Get<ServerLogOptions>() ?? new ServerLogOptions();
        if (!options.Enabled) return null;
        var debugDir = configuration.GetSection(AionConfiguration.Debug)["LogDirectory"];
        var directory = DebugOptions.ResolveLogDirectory(options.LogDirectory ?? debugDir, AppContext.BaseDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return new ServerFileLoggerProvider(new ChannelFileLogWriter(UniquePath(directory, timestamp)), options.MinLevel);
    }

    /// <summary>
    /// server-&lt;timestamp&gt;.log, or server-&lt;timestamp&gt;-host[-N].log when that name exists: start.ps1 opens its own
    /// console tee with the same pattern a moment earlier, and in the same second the host's FileStream lost the
    /// sharing race and wrote nothing (2026-10-30 00:55:24 and 01:00:43 runs left one 0-byte file).
    /// </summary>
    public static string UniquePath(string directory, string timestamp)
    {
        var path = System.IO.Path.Combine(directory, $"server-{timestamp}.log");
        for (var n = 1; File.Exists(path); n++)
            path = System.IO.Path.Combine(directory, n == 1 ? $"server-{timestamp}-host.log" : $"server-{timestamp}-host-{n}.log");
        return path;
    }

    public ILogger CreateLogger(string categoryName) =>
        loggers.GetOrAdd(categoryName, name => new ServerFileLogger(name, this));

    static string Format(string category, LogLevel level, string message, Exception? exception)
    {
        var levelText = level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
        var sb = new StringBuilder();
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        sb.Append(" [").Append(levelText).Append("] ");
        sb.Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }
        return sb.ToString();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        loggers.Clear();
        return file.DisposeAsync();
    }
}

public static class ServerFileLoggingExtensions
{
    public static ILoggingBuilder AddServerFileLog(this ILoggingBuilder logging, IConfiguration configuration)
    {
        if (ServerFileLoggerProvider.TryCreate(configuration) is not { } provider) return logging;
        var options = configuration.GetSection(AionConfiguration.ServerLog).Get<ServerLogOptions>() ?? new ServerLogOptions();
        logging.AddProvider(provider);
        logging.AddFilter<ServerFileLoggerProvider>(level => level >= options.MinLevel);
        logging.Services.AddSingleton(provider);
        return logging;
    }
}
