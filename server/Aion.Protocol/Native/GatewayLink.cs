using System.Text;

namespace Aion.Protocol.Native;

/// <summary>
/// The local link between the game-port gateway (Aion.Gateway: keeps the players' TCP connections and the RC4 handshake) and the
/// game host behind it. Frames are the native framing (varint length + 4, then the body) carrying plaintext bodies both ways; the
/// gateway decrypts client bodies. Control bodies use opcodes the client never sends or receives (0xFEF0-0xFEF2):
/// <list type="bullet">
/// <item>0xFEF0 hello (gateway -> host, first frame): u8 version, client address, u8 resume flag, then the resume point.</item>
/// <item>0xFEF1 resume point (host -> gateway, whenever it changes): world session token, character wire id, player key, map-load ticket.</item>
/// <item>0xFEF2 close (host -> gateway): the session ended for good (logout, kick, refused); the gateway closes the client.</item>
/// </list>
/// When the host restarts, the gateway holds the client (answers its pings, sends the 50 ms clock) and reconnects with the last
/// resume point; the host reloads the character and sends a map load to where it was, so a deploy is a short load instead of a kick.
/// </summary>
public static class GatewayLink
{
    public const ushort HelloOpcode = 0xFEF0, ResumePointOpcode = 0xFEF1, CloseOpcode = 0xFEF2;
    public const byte Version = 1;

    public sealed record ResumePoint(string Token, int WireId, int PlayerKey, int LoadTicket)
    {
        public override string ToString() => $"ResumePoint [token redacted] wire {WireId} key {PlayerKey} ticket {LoadTicket}";
    }

    public sealed record Hello(string ClientAddress, ResumePoint? Resume);

    public static bool IsControl(ReadOnlySpan<byte> body) => body.Length >= 2 && body[1] == 0xFE && body[0] is 0xF0 or 0xF1 or 0xF2;
    public static ushort Opcode(ReadOnlySpan<byte> body) => body.Length >= 2 ? (ushort)(body[0] | body[1] << 8) : (ushort)0;

    public static byte[] BuildHello(string clientAddress, ResumePoint? resume)
    {
        var w = PacketWriter.Opcode(HelloOpcode).U8(Version).Bytes(Encoding.UTF8.GetBytes(clientAddress)).U8(resume is null ? 0 : 1);
        if (resume is not null) WritePoint(w, resume);
        return w.ToArray();
    }

    public static Hello ParseHello(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.U16() != HelloOpcode) throw new ProtocolException("Gateway link: first frame is not a hello");
        if (r.U8() != Version) throw new ProtocolException("Gateway link: unsupported version");
        var address = Encoding.UTF8.GetString(r.Bytes());
        var resume = r.U8() != 0 ? ReadPoint(r) : null;
        return new Hello(address, resume);
    }

    public static byte[] BuildResumePoint(ResumePoint point) => WritePoint(PacketWriter.Opcode(ResumePointOpcode), point).ToArray();

    public static ResumePoint ParseResumePoint(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.U16() != ResumePointOpcode) throw new ProtocolException("Gateway link: not a resume point");
        return ReadPoint(r);
    }

    public static byte[] BuildClose() => PacketWriter.Opcode(CloseOpcode).ToArray();

    static PacketWriter WritePoint(PacketWriter w, ResumePoint p) =>
        w.Bytes(Encoding.UTF8.GetBytes(p.Token)).I32(p.WireId).I32(p.PlayerKey).I32(p.LoadTicket);

    static ResumePoint ReadPoint(PacketReader r)
    {
        var token = Encoding.UTF8.GetString(r.Bytes());
        if (token.Length is < 1 or > 512) throw new ProtocolException("Gateway link: bad token");
        return new ResumePoint(token, r.I32(), r.I32(), r.I32());
    }
}
