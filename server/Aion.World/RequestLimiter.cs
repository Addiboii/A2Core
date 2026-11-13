namespace Aion.World;

/// <summary>
/// Per-connection limits on the client's game requests (anti-flood, Stage C): a size cap per request and token buckets per
/// opcode class plus one for all requests together. A request over a limit is dropped (no reply, not handled). The limits
/// sit far above anything a player can produce (a person clicks a few times a second; the client pings every 50 ms and
/// reports movement about 10 times a second), so only scripted floods hit them. Plain class without I/O: the game session
/// calls it first in HandleAsync, and the network listener (or the gateway) may call it before decrypting work is done.
/// </summary>
public sealed class RequestLimiter
{
    public enum Verdict { Ok, TooLarge, TooFast }

    /// <summary>The largest request body accepted (opcode included). Client options (a zlib blob) and builder commands are the biggest.</summary>
    public const int MaxBody = 16 * 1024, MaxBodyLarge = 64 * 1024;

    /// <summary>A bucket: the burst it holds and the requests per second it refills.</summary>
    public readonly record struct Rate(double Burst, double PerSecond);

    // Ping (every 50 ms) and position reports (start/update/stop, jumps, glides, flight: ~10/s, more while turning).
    public static readonly Rate Movement = new(240, 80);
    // Casts and their 0x383a/0x383c brackets, charge and ground-target steps (a skill-rate hack is logged separately at 25/s).
    public static readonly Rate Combat = new(120, 50);
    // Every other request, per opcode (bag moves, loot, shop, quest, social): a player clicking as fast as possible stays under 10/s.
    public static readonly Rate Other = new(60, 20);
    // All requests together.
    public static readonly Rate Total = new(600, 200);

    readonly Dictionary<int, Bucket> buckets = [];
    Bucket total = new(Total.Burst, 0);

    /// <summary>Requests dropped so far (too large or too fast), for the staff overview and the flood check.</summary>
    public int Dropped { get; private set; }
    /// <summary>Requests dropped in the current 10 s window (the graded response kicks above FloodKick).</summary>
    public int DroppedInWindow { get; private set; }
    long windowStart;
    public const long WindowMs = 10_000;
    /// <summary>Dropped requests within one window that end the connection (a flood far beyond any client behaviour).</summary>
    public const int FloodKick = 600;

    struct Bucket(double tokens, long at)
    {
        public double Tokens = tokens;
        public long At = at;
    }

    public static bool IsMovement(int opcode) => opcode is 0x3602 or 0x3601 or (>= 0x3700 and <= 0x371f);
    public static bool IsCombat(int opcode) => opcode is (>= 0x3800 and <= 0x384f);
    public static int MaxBodyOf(int opcode) => opcode is 0x8d40 or 0x8d42 or 0x362e ? MaxBodyLarge : MaxBody;

    public static Rate RateOf(int opcode) => IsMovement(opcode) ? Movement : IsCombat(opcode) ? Combat : Other;

    /// <summary>Whether this request may be handled; counts it against its buckets.</summary>
    public Verdict Check(int opcode, int length, long now)
    {
        if (now - windowStart >= WindowMs) { windowStart = now; DroppedInWindow = 0; }
        if (length > MaxBodyOf(opcode)) return Drop(Verdict.TooLarge);
        // Movement and combat share one bucket per class (their opcodes alternate); others are counted per opcode.
        var key = IsMovement(opcode) ? -1 : IsCombat(opcode) ? -2 : opcode;
        var rate = RateOf(opcode);
        ref var bucket = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(buckets, key, out var existed);
        if (!existed) bucket = new Bucket(rate.Burst, now);
        if (!Take(ref bucket, rate, now)) return Drop(Verdict.TooFast);
        if (!Take(ref total, Total, now))
        {
            bucket.Tokens = Math.Min(rate.Burst, bucket.Tokens + 1);   // not spent: the request was not handled
            return Drop(Verdict.TooFast);
        }
        return Verdict.Ok;
    }

    static bool Take(ref Bucket bucket, Rate rate, long now)
    {
        var seconds = Math.Max(0, now - bucket.At) / 1000.0;
        bucket.At = Math.Max(bucket.At, now);
        bucket.Tokens = Math.Min(rate.Burst, bucket.Tokens + seconds * rate.PerSecond);
        if (bucket.Tokens < 1) return false;
        bucket.Tokens -= 1;
        return true;
    }

    Verdict Drop(Verdict verdict)
    {
        Dropped++;
        DroppedInWindow++;
        return verdict;
    }

    /// <summary>Whether the drops of the current window are a flood (the graded response's kick).</summary>
    public bool Flooding => DroppedInWindow >= FloodKick;
}
