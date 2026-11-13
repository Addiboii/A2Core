using System.Net;

namespace Aion.Host.Native;

/// <summary>Process-wide network protection counters for the staff server-health view.</summary>
public static class NetworkMetrics
{
    static readonly List<ConnectionGuard> guards = new();
    static readonly object gate = new();
    /// <summary>Connection attempts refused by the per-IP guards since start (DDoS / booter protection).</summary>
    public static long RefusedConnections;
    public static void Register(ConnectionGuard guard) { lock (gate) guards.Add(guard); }
    /// <summary>Source IPs currently on a cooldown block across all native ports.</summary>
    public static int BlockedIps { get { lock (gate) return guards.Sum(g => g.BlockedIps); } }
}

/// <summary>
/// Per-IP connection protection for the native game ports (anti-DDoS / anti-booting, user request 2026-11-08). One source IP may
/// hold at most <see cref="maxPerIp"/> connections at once and open at most <see cref="maxNewPerMinute"/> new ones per minute;
/// crossing either puts the IP on a cooldown block for <see cref="blockMs"/>. The OS/firewall handles SYN floods before this; this
/// stops a single host opening thousands of real sockets or hammering the login (the common "booter"). Loopback is never limited.
/// Entries exist only for real accepted TCP peers, pruned as they expire, so memory is bounded by the number of distinct sources.
/// </summary>
public sealed class ConnectionGuard(int maxPerIp, int maxNewPerMinute, long blockMs)
{
    sealed class Entry
    {
        public int Open;
        public readonly Queue<long> Recent = new();
        public long BlockedUntil;
    }

    readonly Dictionary<IPAddress, Entry> entries = new();
    readonly object gate = new();
    long lastSweep;

    /// <summary>How many connection attempts were refused (blocked IPs or over a limit): for the staff overview.</summary>
    public long Refused { get; private set; }
    /// <summary>IPs currently on a cooldown block.</summary>
    public int BlockedIps { get { lock (gate) { var now = Environment.TickCount64; return entries.Values.Count(e => now < e.BlockedUntil); } } }

    /// <summary>True when the connection from <paramref name="ip"/> is allowed (and counted); false to drop it.</summary>
    public bool TryAccept(IPAddress ip, long nowMs)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        lock (gate)
        {
            Sweep(nowMs);
            if (!entries.TryGetValue(ip, out var e)) entries[ip] = e = new Entry();
            if (nowMs < e.BlockedUntil) { Refused++; System.Threading.Interlocked.Increment(ref NetworkMetrics.RefusedConnections); return false; }
            while (e.Recent.Count > 0 && e.Recent.Peek() < nowMs - 60_000) e.Recent.Dequeue();
            if (e.Open >= maxPerIp || e.Recent.Count >= maxNewPerMinute)
            {
                e.BlockedUntil = nowMs + blockMs;   // flooding: cool this IP off
                Refused++;
                System.Threading.Interlocked.Increment(ref NetworkMetrics.RefusedConnections);
                return false;
            }
            e.Open++;
            e.Recent.Enqueue(nowMs);
            return true;
        }
    }

    /// <summary>A connection from <paramref name="ip"/> closed (must balance every allowed TryAccept).</summary>
    public void Release(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return;
        lock (gate)
        {
            if (!entries.TryGetValue(ip, out var e)) return;
            if (e.Open > 0) e.Open--;
            if (e.Open == 0 && e.Recent.Count == 0 && Environment.TickCount64 >= e.BlockedUntil) entries.Remove(ip);
        }
    }

    /// <summary>Drops idle, unblocked entries at most once a minute (keeps the map small under a churn of distinct IPs).</summary>
    void Sweep(long nowMs)
    {
        if (nowMs - lastSweep < 60_000) return;
        lastSweep = nowMs;
        foreach (var (ip, e) in entries.ToList())
        {
            while (e.Recent.Count > 0 && e.Recent.Peek() < nowMs - 60_000) e.Recent.Dequeue();
            if (e.Open == 0 && e.Recent.Count == 0 && nowMs >= e.BlockedUntil) entries.Remove(ip);
        }
    }
}
