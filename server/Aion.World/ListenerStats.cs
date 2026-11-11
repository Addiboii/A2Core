using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Aion.World;

/// <summary>
/// Counts and timings of one native listener since start-up, for the staff diagnostics API (OURS): connections
/// accepted and open, refusals and connection ends by reason, malformed requests skipped, and per opcode the number of
/// requests and their handling time. Never a body, a name, a chat text or an address: only numbers and fixed reason
/// codes (anything else is counted as "other"). Thread-safe; memory is bounded by the 65,536 opcodes.
/// </summary>
public sealed partial class ListenerStats(string name)
{
    public string Name { get; } = name;
    long accepted, active, malformed;
    readonly ConcurrentDictionary<string, long> refusals = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, long> ends = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<int, Timing> opcodes = new();

    sealed class Timing
    {
        public long Count, TotalTicks, MaxTicks;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex ReasonCode();

    /// <summary>A reason as a fixed code: lower-case letters, digits and underscores, at most 32; else "other".</summary>
    public static string Reason(string? reason) => reason is not null && ReasonCode().IsMatch(reason) ? reason : "other";

    public void Accepted()
    {
        Interlocked.Increment(ref accepted);
        Interlocked.Increment(ref active);
    }

    public void Ended(string reason)
    {
        Interlocked.Decrement(ref active);
        ends.AddOrUpdate(Reason(reason), 1, (_, n) => n + 1);
    }

    public void Refused(string reason) => refusals.AddOrUpdate(Reason(reason), 1, (_, n) => n + 1);

    public void Malformed() => Interlocked.Increment(ref malformed);

    /// <summary>One request of this opcode (0..65535; anything else is not recorded) handled in this many Stopwatch ticks.</summary>
    public void Handled(int opcode, long elapsedTicks)
    {
        if (opcode is < 0 or > 0xffff) return;
        var timing = opcodes.GetOrAdd(opcode, _ => new Timing());
        Interlocked.Increment(ref timing.Count);
        Interlocked.Add(ref timing.TotalTicks, Math.Max(0, elapsedTicks));
        var max = Interlocked.Read(ref timing.MaxTicks);
        while (elapsedTicks > max)
        {
            var seen = Interlocked.CompareExchange(ref timing.MaxTicks, elapsedTicks, max);
            if (seen == max) break;
            max = seen;
        }
    }

    public sealed record OpcodeView(string Opcode, long Count, double TotalMs, double MeanMs, double MaxMs);
    public sealed record View(string Name, long Accepted, long Active, long MalformedSkips, IReadOnlyDictionary<string, long> Refusals,
        IReadOnlyDictionary<string, long> Ends, IReadOnlyList<OpcodeView> Opcodes);

    static double Ms(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 3);

    /// <summary>A snapshot; opcodes by request count, the busiest first, at most <paramref name="maxOpcodes"/>.</summary>
    public View Read(int maxOpcodes = 500) => new(Name, Interlocked.Read(ref accepted), Interlocked.Read(ref active), Interlocked.Read(ref malformed),
        new SortedDictionary<string, long>(refusals, StringComparer.Ordinal), new SortedDictionary<string, long>(ends, StringComparer.Ordinal),
        opcodes.Select(o => (Opcode: o.Key, Count: Interlocked.Read(ref o.Value.Count), Total: Interlocked.Read(ref o.Value.TotalTicks), Max: Interlocked.Read(ref o.Value.MaxTicks)))
            .Where(o => o.Count > 0).OrderByDescending(o => o.Count).ThenBy(o => o.Opcode).Take(maxOpcodes)
            .Select(o => new OpcodeView($"0x{o.Opcode:x4}", o.Count, Ms(o.Total), Ms(o.Total / o.Count), Ms(o.Max))).ToList());
}

/// <summary>The native listeners' statistics (one per listener), a singleton of the host.</summary>
public sealed class ListenerDiagnostics
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public ListenerStats Login { get; } = new("login");
    public ListenerStats Game { get; } = new("game");
    public IReadOnlyList<ListenerStats> All => [Login, Game];
}
