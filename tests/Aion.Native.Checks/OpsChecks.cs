using Aion.World;

/// <summary>
/// The health page's counters (Stage G, server/Aion.World/ServerMetrics.cs): histogram buckets cover every value with ~19% steps,
/// percentiles of a window come from bucket differences, the per-minute maximum resets when read, and the static call sites
/// count maps, opcodes, sends and cheat kinds.
/// </summary>
static class OpsChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Every value lands in a bucket whose upper edge is at or above it and within ~25% (exact below 8 µs).
        var ok = true;
        for (long v = 0; v < 5_000_000; v = v < 64 ? v + 1 : v * 21 / 20)
        {
            var b = LatencyStats.Bucket(v);
            var upper = LatencyStats.UpperMicros(b);
            var lower = b == 0 ? 0 : LatencyStats.UpperMicros(b - 1) + 1;
            if (v < lower || v > upper || (v >= 8 && upper > v * 1.25 + 1)) { ok = false; break; }
        }
        check(ok, "latency buckets: every value inside its bucket, buckets at most 25% wide");
        check(LatencyStats.Bucket(long.MaxValue) == LatencyStats.Buckets - 1 && LatencyStats.Bucket(-5) == 0, "latency buckets: out-of-range values clamp");

        var stats = new LatencyStats();
        for (var i = 0; i < 95; i++) stats.RecordMicros(1_000);     // 1 ms
        for (var i = 0; i < 5; i++) stats.RecordMicros(40_000);     // 40 ms
        var before = stats.Read();
        check(before.Count == 100 && Math.Abs(before.AverageMs - 2.95) < 0.01, $"latency: count and average ({before.Count}, {before.AverageMs:0.###} ms)");
        check(before.PercentileMs(0.95) is >= 1 and < 1.25 && before.PercentileMs(0.99) is >= 40 and < 50, $"latency: p95 {before.PercentileMs(0.95)} ms, p99 {before.PercentileMs(0.99)} ms");
        check(stats.TakeMax() == 40_000 && stats.TakeMax() == 0, "latency: the maximum is reported once and reset");
        for (var i = 0; i < 10; i++) stats.RecordMicros(100_000);
        var window = stats.Read().Minus(before);
        check(window.Count == 10 && window.PercentileMs(0.5) is >= 100 and < 125, $"latency: a window is the difference of two readings (p50 {window.PercentileMs(0.5)} ms)");
        check(LatencyStats.Totals.Empty.PercentileMs(0.95) == 0 && LatencyStats.Totals.Empty.AverageMs == 0, "latency: an empty window reads 0");

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ServerMetrics.MapTick(-42, t0);
        ServerMetrics.Request(0x13613, t0);
        var sentBefore = ServerMetrics.SentPackets;
        ServerMetrics.Sent(3, 120);
        ServerMetrics.Sent(0, 0);
        ServerMetrics.CheatSuspected("check_kind");
        check(ServerMetrics.Maps.Any(m => m.Key == -42 && m.Value.Count == 1), "ServerMetrics: a map tick is counted under its map");
        check(ServerMetrics.Requests.Any(r => r.Key == 0x3613 && r.Value.Count >= 1), "ServerMetrics: a request is counted under its 16-bit opcode");
        check(ServerMetrics.SentPackets - sentBefore == 3, "ServerMetrics: sends count packets, empty sends are ignored");
        check(ServerMetrics.Cheats.GetValueOrDefault("check_kind") == 1, "ServerMetrics: cheat suspicions are counted per kind");
    }
}
