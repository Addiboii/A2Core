using System.Text.Json;

namespace Aion.Host.Native;

/// <summary>
/// Research trace of one game connection's messages (Aion:Native:TraceDirectory): a timeline of
/// (ms since connect, opcode, size[, 1 for a server reply]), counts per opcode and the first bodies of each
/// opcode in each direction, so client actions (dash, menus, inventory) can be matched to their messages and to
/// what the server answered (the replies since 2026-10-20: a frozen client after a dash could not be explained
/// from the requests alone). Login messages carry credentials (0x3613 tickets): only their size is kept.
/// Written every minute and when the connection ends.
/// </summary>
public sealed class ClientTrace(string directory)
{
    const int MaxTimeline = 20000, SamplesPerOpcode = 3, MaxSampleBytes = 512;
    static readonly HashSet<int> Secret = [0x3613, 0x3905];

    readonly long started = Environment.TickCount64;
    /// <summary>One file per connection, named at its start; each write replaces it (the trace is written every minute and at the end).</summary>
    readonly string path = Path.Combine(directory, $"client-trace-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
    readonly List<long[]> timeline = [];
    readonly Dictionary<int, int> counts = [];
    readonly Dictionary<int, List<string>> samples = [];
    readonly Dictionary<int, int> replyCounts = [];
    readonly Dictionary<int, List<string>> replySamples = [];

    public void Record(byte[] plain)
    {
        if (plain.Length < 2) return;
        var opcode = plain[0] | plain[1] << 8;
        counts[opcode] = counts.GetValueOrDefault(opcode) + 1;
        if (timeline.Count < MaxTimeline) timeline.Add([Environment.TickCount64 - started, opcode, plain.Length]);
        if (Secret.Contains(opcode)) return;
        var list = samples.TryGetValue(opcode, out var existing) ? existing : samples[opcode] = [];
        if (list.Count < SamplesPerOpcode) list.Add(Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, MaxSampleBytes))));
    }

    /// <summary>A server message to this client (a reply, a tick's packet, a relayed notice).</summary>
    public void RecordReply(byte[] packet)
    {
        if (packet.Length < 2) return;
        var opcode = packet[0] | packet[1] << 8;
        replyCounts[opcode] = replyCounts.GetValueOrDefault(opcode) + 1;
        if (timeline.Count < MaxTimeline) timeline.Add([Environment.TickCount64 - started, opcode, packet.Length, 1]);
        var list = replySamples.TryGetValue(opcode, out var existing) ? existing : replySamples[opcode] = [];
        if (list.Count < SamplesPerOpcode) list.Add(Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, MaxSampleBytes))));
    }

    /// <summary>Writes the trace (replacing the connection's file) and returns its path.</summary>
    public string Write()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            note = "Game messages of one connection: client requests and, with a trailing 1 in the timeline, the server's messages; login bodies omitted (credentials).",
            counts = counts.OrderBy(c => c.Key).ToDictionary(c => $"0x{c.Key:x4}", c => c.Value),
            samples = samples.OrderBy(s => s.Key).ToDictionary(s => $"0x{s.Key:x4}", s => s.Value),
            reply_counts = replyCounts.OrderBy(c => c.Key).ToDictionary(c => $"0x{c.Key:x4}", c => c.Value),
            reply_samples = replySamples.OrderBy(s => s.Key).ToDictionary(s => $"0x{s.Key:x4}", s => s.Value),
            timeline,
        }));
        return path;
    }
}
