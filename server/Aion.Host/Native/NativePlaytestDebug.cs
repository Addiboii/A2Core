using Aion.World;

namespace Aion.Host.Native;

static class NativePlaytestDebug
{
    public static void LogLoginPacket(bool outbound, byte[] body, long nowMs)
    {
        if (PlaytestDebug.Sink is not { Packets: true } sink || body.Length < 2) return;
        var opcode = (ushort)(body[0] | body[1] << 8);
        sink.Packet(new PlaytestPacketRecord("login", outbound, opcode, body, body.Length, null, nowMs));
    }
}
