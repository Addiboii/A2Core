namespace Aion.World;

/// <summary>Per-connection packet rate guard (movement vs everything else). Drops excess silently like an ignored opcode.</summary>
public sealed class ClientPacketLimiter
{
    const int WindowMs = 1000;
    const int MaxPacketsPerWindow = 240;
    const int MaxMovePacketsPerWindow = 40;

    long windowStartMs;
    int windowTotal;
    int windowMove;
    public int Dropped { get; private set; }
    public int FloodBursts { get; private set; }

    static bool IsMovement(ushort opcode) => opcode is >= 0x3700 and <= 0x371f or 0x370e;

    // The cast handshake the client blocks on: 0x383a/0x383c (u16 acks) and 0x3800 (0x3801 on accept). Held-key 0x3800
    // resends every ~17 ms; official ignores almost all duplicates during the swing (no 0x3801 flood). Rate limiting
    // still bypasses cast packets so legitimate accepts are never dropped.
    static bool IsCastHandshake(ushort opcode) => opcode is 0x383a or 0x383c or 0x3800;

    public bool Allow(ushort opcode, long nowMs)
    {
        if (opcode == 0x3602 || IsCastHandshake(opcode)) return true;
        if (nowMs - windowStartMs >= WindowMs)
        {
            windowStartMs = nowMs;
            windowTotal = 0;
            windowMove = 0;
        }
        // A movement surplus drops movement only: it used to close the window for every request, casts included.
        var move = IsMovement(opcode);
        if (move) windowMove++;
        else windowTotal++;
        if (move ? windowMove > MaxMovePacketsPerWindow : windowTotal > MaxPacketsPerWindow)
        {
            Dropped++;
            return false;
        }
        return true;
    }

    public void RecordFrameBurst(int frames, int maxFrames)
    {
        if (frames <= maxFrames) return;
        FloodBursts++;
        Dropped += frames - maxFrames;
    }
}
