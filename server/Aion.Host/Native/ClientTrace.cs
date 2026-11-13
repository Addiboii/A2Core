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
public sealed class ClientTrace
{
    readonly string directory;

    public ClientTrace(string directory)
    {
        this.directory = directory;
        path = Path.Combine(directory, $"client-trace-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json");
        Housekeep(directory);
    }

    const int MaxTimeline = 200_000, SamplesPerOpcode = 3, MaxSampleBytes = 512;
    static readonly HashSet<int> Secret = [0x3613, 0x3905];
    /// <summary>
    /// Messages kept in full (every body, up to FullSamples each way): object interaction (0x3636-0x363b), skill casts and
    /// their results (0x3800-0x3806), target selection (0x3833), abnormals (0x382a/0x382c) and item use (0x5612-0x5618), so a
    /// live test of a quest object or a bomb shows each request and what the server answered (2026-11-07).
    /// </summary>
    static bool Full(int opcode) => opcode is (>= 0x3636 and <= 0x363b) or (>= 0x3800 and <= 0x3806) or 0x3833 or 0x382a or 0x382c
        or (>= 0x5612 and <= 0x5618)
        // Quest scripts and NPCs (2026-11-07, the Brigade ending): progress/join, env state, event functions, NPC appears, talks.
        or 0x922d or 0x922e or 0x4001 or 0x4002 or 0x8d15 or 0x3641 or (>= 0xe240 and <= 0xe243) or 0x8d2b or 0x8d2c or 0x3621;
    const int FullSamples = 2000;
    static int Keep(int opcode) => Full(opcode) ? FullSamples : SamplesPerOpcode;

    readonly long started = Environment.TickCount64;
    /// <summary>One file per connection, named at its start; each write replaces it (the trace is written every minute and at the end).</summary>
    readonly string path;
    readonly List<long[]> timeline = [];
    /// <summary>
    /// The body (hex) of every timeline row, same index (2026-11-07, for the auto checker's byte-level comparison): empty for
    /// login messages (credentials) and the 50 ms clock pair, cut at MaxBodyHex characters.
    /// </summary>
    readonly List<string> bodies = [];
    const int MaxBodyHex = 4096;
    readonly Dictionary<int, int> counts = [];
    readonly Dictionary<int, List<string>> samples = [];
    readonly Dictionary<int, int> replyCounts = [];
    readonly Dictionary<int, List<string>> replySamples = [];

    /// <summary>
    /// The 50 ms clock pair (client 0x3601, server 0x3600) stays out of the timeline (still counted): about 40 rows a second
    /// filled the old 20,000-row timeline in ~8 minutes, so later fights were never recorded (no casts in any trace before 2026-11-06).
    /// </summary>
    static bool Periodic(int opcode) => opcode is 0x3600 or 0x3601;

    public void Record(byte[] plain)
    {
        if (plain.Length < 2) return;
        var opcode = plain[0] | plain[1] << 8;
        counts[opcode] = counts.GetValueOrDefault(opcode) + 1;
        if (timeline.Count < MaxTimeline && !Periodic(opcode))
        {
            timeline.Add([Environment.TickCount64 - started, opcode, plain.Length]);
            bodies.Add(Secret.Contains(opcode) ? "" : Hex(plain));
        }
        if (Secret.Contains(opcode)) return;
        var list = samples.TryGetValue(opcode, out var existing) ? existing : samples[opcode] = [];
        if (list.Count < Keep(opcode)) list.Add(Convert.ToHexString(plain.AsSpan(0, Math.Min(plain.Length, MaxSampleBytes))));
    }

    /// <summary>A server message to this client (a reply, a tick's packet, a relayed notice).</summary>
    public void RecordReply(byte[] packet)
    {
        if (packet.Length < 2) return;
        var opcode = packet[0] | packet[1] << 8;
        replyCounts[opcode] = replyCounts.GetValueOrDefault(opcode) + 1;
        if (timeline.Count < MaxTimeline && !Periodic(opcode))
        {
            timeline.Add([Environment.TickCount64 - started, opcode, packet.Length, 1]);
            bodies.Add(Hex(packet));
        }
        var list = replySamples.TryGetValue(opcode, out var existing) ? existing : replySamples[opcode] = [];
        if (list.Count < Keep(opcode)) list.Add(Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, MaxSampleBytes))));
    }

    static string Hex(byte[] b) => Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, MaxBodyHex / 2)));

    /// <summary>Old traces: gzipped after a day, deleted after 30 days (once per process, when the first trace starts).</summary>
    static readonly object housekeeping = new();
    static bool housekept;

    public static void Housekeep(string directory)
    {
        lock (housekeeping)
        {
            if (housekept) return;
            housekept = true;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var file in Directory.EnumerateFiles(directory, "client-trace-*.json*"))
                {
                    var age = now - File.GetLastWriteTimeUtc(file);
                    if (age > TimeSpan.FromDays(30)) { File.Delete(file); continue; }
                    if (file.EndsWith(".json", StringComparison.Ordinal) && age > TimeSpan.FromDays(1))
                    {
                        using (var input = File.OpenRead(file))
                        using (var output = File.Create(file + ".gz"))
                        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
                            input.CopyTo(gzip);
                        File.SetLastWriteTimeUtc(file + ".gz", File.GetLastWriteTimeUtc(file));
                        File.Delete(file);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
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
            bodies,
        }));
        return path;
    }
}
