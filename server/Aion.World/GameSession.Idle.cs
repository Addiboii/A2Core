using Aion.Protocol.Native;

namespace Aion.World;

// The Oct 28 2026 patch: "the time before players are returned to the lobby due to inactivity will be reduced from 3 hours to 30
// minutes" (Patch20261028.IdleReturnToLobby). The client's keep-alive ping (0x3602) is not activity; every other request is.
public sealed partial class GameSession
{
    /// <summary>When the last request that is not a keep-alive arrived (Unix ms); 0 before the first.</summary>
    long lastActivityMs;

    /// <summary>
    /// A player in the world idle for 30 minutes gets a Kick_NT and the connection closes (the client then shows the lobby).
    /// INFERRED: the kick reason (ByAdmin, the only EKickReason value known) and that the client falls back to the lobby on a closed connection.
    /// </summary>
    List<byte[]> IdleReturnTick(long now)
    {
        if (lastActivityMs == 0 || CloseReason is not null || resources is null) return [];
        if (now - lastActivityMs < (long)Patch20261028.IdleReturnToLobby.TotalMilliseconds) return [];
        CloseReason = "idle for 30 minutes";
        return [PacketWriter.Opcode(KickNt).U8(KickByAdmin).String("").ToArray()];
    }
}
