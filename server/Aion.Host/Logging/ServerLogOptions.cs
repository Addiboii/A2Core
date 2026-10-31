namespace Aion.Host.Logging;

/// <summary>Full host log mirror (all categories written to a UTF-8 file alongside the console).</summary>
public sealed record ServerLogOptions
{
    /// <summary>When false, no server-*.log file is created (console only).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Directory for server-&lt;timestamp&gt;.log (default: research/local/logs).</summary>
    public string? LogDirectory { get; init; }
    /// <summary>Minimum level (default: same as default logging, Information).</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;
}
