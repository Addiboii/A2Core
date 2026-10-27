using Aion.Protocol.Native;

namespace Aion.World;

// Staff actions from the dashboard, run on the player's own loop (InvokeAsync). Kick_NT 0x8d2e {EKickReason u8, detail}
// and Notice_NT 0x8d2f {font size u16, font colour, messages[], default culture i32}: live opcodes by the 0x8d run rule
// (catalogue index + 0x89d1, as GatherSourceStart_RQ 0x8d30 right after them); layouts from the usmap, CANDIDATE (the
// colour string's format and the size's unit are not observed).
public sealed partial class GameSession
{
    public const ushort KickNt = 0x8d2e, NoticeNt = 0x8d2f;
    const byte KickByAdmin = 6;   // EKickReason ByAdmin

    /// <summary>Set when staff kicked this player: the connection closes after sending what is pending.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>
    /// A notice in the middle of the screen (MessageType NormalNotice: HudType SpecialNotice, ~10 s). Client handler
    /// 0x8ff4eb0 (OBSERVED, research/local/notice-20261017): u16 font size (0 = the widget's 20), font colour for
    /// FColor::FromHex (RRGGBB; empty = default), one text per culture [ko, de, en, es, fr, ja, pt, ru, zh-CN, zh-TW]
    /// (ELanguageCode - 1), then the fallback culture index (2 = en-US). An identical text within 10 s is dropped.
    /// </summary>
    public static byte[] Notice(string text, int size = 20, string color = "FFFFFF")
    {
        const int Cultures = 10, EnUs = 2;
        var clipped = Clip(text, 255);
        var writer = PacketWriter.Opcode(NoticeNt).U16(Math.Clamp(size, 0, 200)).String(color).Varint(Cultures);
        for (var i = 0; i < Cultures; i++) writer.String(clipped);
        return writer.I32(EnUs).ToArray();
    }

    static string Clip(string text, int bytes)
    {
        while (System.Text.Encoding.UTF8.GetByteCount(text) > bytes) text = text[..^1];
        return text;
    }

    /// <summary>Kicked by staff: Kick_NT, then the connection closes.</summary>
    public List<byte[]> StaffKick(string detail)
    {
        CloseReason = "kicked by staff";
        return [PacketWriter.Opcode(KickNt).U8(KickByAdmin).String(Clip(detail, 200)).ToArray()];
    }

    /// <summary>Revived where the player fell, or HP and MP filled.</summary>
    public List<byte[]> StaffRestore(long now)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        if (dead) return Revive(now, inPlace: true);
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        return [resources.Update()];
    }

    /// <summary>A stuck player back at the current map's start point (else its respawn point).</summary>
    public List<byte[]> StaffUnstuck(long now)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        var start = data.MapStart(worldMap)?.Position ?? spawn?.Position ?? GamePackets.SpawnPosition;
        if (dead) dead = false;
        return Teleport(worldMap, start, now);
    }

    /// <summary>A map load to a map's start point or the given position.</summary>
    public List<byte[]> StaffTeleport(int mapId, Vector3d? at, long now)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        var target = at ?? data.MapStart(mapId)?.Position ?? throw new ArgumentException("That map has no start point; give a position.");
        return Teleport(mapId, target, now);
    }
}
