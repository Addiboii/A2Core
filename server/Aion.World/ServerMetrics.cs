using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace Aion.World;

/// <summary>
/// A lock-free latency histogram (Stage G health page): counts per bucket of a log2 scale with 4 steps per doubling
/// (about 19% resolution) from 1 µs to ~35 minutes, plus count, sum and the largest value since the last
/// <see cref="TakeMax"/>. Recording is three interlocked adds and a compare loop for the maximum: no allocation, no lock.
/// </summary>
public sealed class LatencyStats
{
    public const int Buckets = 128;
    readonly long[] counts = new long[Buckets];
    long count, sumMicros, maxMicros;

    /// <summary>The bucket of a duration in microseconds: 0..7 exact, then 4 per power of two.</summary>
    public static int Bucket(long micros)
    {
        if (micros < 8) return (int)Math.Max(0, micros);
        var log = BitOperations.Log2((ulong)micros);           // 3..62
        var step = (int)(micros >> (log - 2)) & 3;              // the 2 bits under the top bit
        return Math.Min(Buckets - 1, (log - 1) * 4 + step);     // log 3 -> 8..11
    }

    /// <summary>The largest duration (µs) that falls into <paramref name="bucket"/> (the upper edge, reported as the percentile).</summary>
    public static long UpperMicros(int bucket)
    {
        if (bucket < 8) return bucket;
        var log = bucket / 4 + 1;
        var step = bucket % 4;
        return ((4L + step + 1) << (log - 2)) - 1;
    }

    /// <summary>Records one duration measured from <see cref="Stopwatch.GetTimestamp"/> <paramref name="startTimestamp"/> to now.</summary>
    public void RecordSince(long startTimestamp) => RecordMicros((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency);

    public void RecordMicros(long micros)
    {
        if (micros < 0) micros = 0;
        Interlocked.Increment(ref counts[Bucket(micros)]);
        Interlocked.Increment(ref count);
        Interlocked.Add(ref sumMicros, micros);
        var max = Volatile.Read(ref maxMicros);
        while (micros > max)
        {
            var seen = Interlocked.CompareExchange(ref maxMicros, micros, max);
            if (seen == max) break;
            max = seen;
        }
    }

    /// <summary>How many durations were recorded since start (cheap: no bucket copy).</summary>
    public long Count => Interlocked.Read(ref count);

    /// <summary>The largest duration (µs) since the previous call, and resets it (one reader: the health sampler).</summary>
    public long TakeMax() => Interlocked.Exchange(ref maxMicros, 0);

    /// <summary>Totals since start (copy of the buckets).</summary>
    public Totals Read()
    {
        var copy = new long[Buckets];
        for (var i = 0; i < Buckets; i++) copy[i] = Volatile.Read(ref counts[i]);
        return new Totals(Interlocked.Read(ref count), Interlocked.Read(ref sumMicros), copy);
    }

    public sealed record Totals(long Count, long SumMicros, long[] Counts)
    {
        public static readonly Totals Empty = new(0, 0, new long[Buckets]);

        public Totals Minus(Totals earlier)
        {
            var d = new long[Buckets];
            for (var i = 0; i < Buckets; i++) d[i] = Counts[i] - earlier.Counts[i];
            return new Totals(Count - earlier.Count, SumMicros - earlier.SumMicros, d);
        }

        public Totals Plus(Totals other)
        {
            var d = new long[Buckets];
            for (var i = 0; i < Buckets; i++) d[i] = Counts[i] + other.Counts[i];
            return new Totals(Count + other.Count, SumMicros + other.SumMicros, d);
        }

        public double AverageMs => Count > 0 ? SumMicros / 1000.0 / Count : 0;

        /// <summary>The <paramref name="p"/> quantile (0..1) in ms, as the upper edge of its bucket.</summary>
        public double PercentileMs(double p)
        {
            if (Count <= 0) return 0;
            var rank = (long)Math.Ceiling(Count * p);
            long seen = 0;
            for (var i = 0; i < Buckets; i++)
            {
                seen += Counts[i];
                if (seen >= rank) return UpperMicros(i) / 1000.0;
            }
            return UpperMicros(Buckets - 1) / 1000.0;
        }
    }
}

/// <summary>
/// Process-wide game server counters for the staff "Server health" page and the alerts (Stage G). Call sites are one line
/// each and allocation-free after the first use of a map or opcode:
/// <code>
/// var t0 = Stopwatch.GetTimestamp(); ... ServerMetrics.MapTick(mapId, t0);     // a map's (or a session's) tick
/// var t0 = Stopwatch.GetTimestamp(); ... ServerMetrics.Request(opcode, t0);    // one client request, turn wait included
/// ServerMetrics.Sent(packets, bytes);                                           // one socket write
/// </code>
/// The host's OpsMonitor samples these every few seconds and turns them into windows (avg / p95 / max per minute).
/// </summary>
public static class ServerMetrics
{
    static readonly ConcurrentDictionary<int, LatencyStats> maps = new();
    static readonly ConcurrentDictionary<int, LatencyStats> requests = new();
    static readonly ConcurrentDictionary<string, StrongBox> cheats = new();
    static long sentPackets, sentBytes, sendCalls, receivedBytes, storeErrors;

    public sealed class StrongBox { public long Value; }

    /// <summary>One tick of map <paramref name="mapId"/> (0: not in a map) that started at <paramref name="startTimestamp"/>.</summary>
    public static void MapTick(int mapId, long startTimestamp) => maps.GetOrAdd(mapId, static _ => new LatencyStats()).RecordSince(startTimestamp);

    /// <summary>One handled client request of <paramref name="opcode"/> that started at <paramref name="startTimestamp"/>.</summary>
    public static void Request(int opcode, long startTimestamp) => requests.GetOrAdd(opcode & 0xFFFF, static _ => new LatencyStats()).RecordSince(startTimestamp);

    /// <summary>One socket write of <paramref name="packets"/> messages, <paramref name="bytes"/> bytes on the wire.</summary>
    public static void Sent(int packets, int bytes)
    {
        if (packets <= 0) return;
        Interlocked.Add(ref sentPackets, packets);
        Interlocked.Add(ref sentBytes, bytes);
        Interlocked.Increment(ref sendCalls);
    }

    /// <summary>Bytes read from a client socket.</summary>
    public static void Received(int bytes) { if (bytes > 0) Interlocked.Add(ref receivedBytes, bytes); }

    /// <summary>A request the store refused (constraint, missing row): the session's PersistenceErrors grew.</summary>
    public static void StoreError(int count = 1) => Interlocked.Add(ref storeErrors, count);

    /// <summary>A cheat_suspected audit event (speed, teleport, skill_rate...).</summary>
    public static void CheatSuspected(string kind) => Interlocked.Increment(ref cheats.GetOrAdd(kind, static _ => new StrongBox()).Value);

    public static IEnumerable<KeyValuePair<int, LatencyStats>> Maps => maps;
    public static IEnumerable<KeyValuePair<int, LatencyStats>> Requests => requests;
    public static IReadOnlyDictionary<string, long> Cheats => cheats.ToDictionary(c => c.Key, c => Interlocked.Read(ref c.Value.Value));
    public static long SentPackets => Interlocked.Read(ref sentPackets);
    public static long SentBytes => Interlocked.Read(ref sentBytes);
    public static long SendCalls => Interlocked.Read(ref sendCalls);
    public static long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public static long StoreErrors => Interlocked.Read(ref storeErrors);
}
