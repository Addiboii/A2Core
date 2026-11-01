using System.Text;

namespace Aion.World;

/// <summary>Ring buffer of the last client and server game opcodes on one connection (for disconnect forensics).</summary>
public sealed class ConnectionPacketTrace
{
    const int Capacity = 40;
    readonly (bool Outbound, ushort Opcode, int Length, long Ms)[] ring = new (bool, ushort, int, long)[Capacity];
    int count;
    int next;

    public void RecordClient(ushort opcode, int length, long nowMs) => Push(false, opcode, length, nowMs);
    public void RecordServer(ushort opcode, int length, long nowMs) => Push(true, opcode, length, nowMs);

    void Push(bool outbound, ushort opcode, int length, long nowMs)
    {
        ring[next] = (outbound, opcode, length, nowMs);
        next = (next + 1) % Capacity;
        if (count < Capacity) count++;
    }

    public string FormatRecent()
    {
        if (count == 0) return "(no packets recorded)";
        var sb = new StringBuilder();
        var start = count < Capacity ? 0 : next;
        for (var i = 0; i < count; i++)
        {
            var slot = (start + i) % Capacity;
            var (outbound, opcode, length, ms) = ring[slot];
            sb.Append(outbound ? "S" : "C").Append(' ').Append(ms).Append("ms 0x").Append(opcode.ToString("x4"))
                .Append(' ').Append(length).Append("B");
            if (i + 1 < count) sb.Append(" | ");
        }
        return sb.ToString();
    }
}
