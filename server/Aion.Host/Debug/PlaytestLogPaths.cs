namespace Aion.Host.Debug;

public static class PlaytestLogPaths
{
    /// <summary>Playtest JSONL file for the local calendar day (matches operator-local midnight, not UTC).</summary>
    public static string DebugFileName(DateTime localNow) => $"debug-{localNow:yyyyMMdd}.log";
}
