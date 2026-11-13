using Aion.Protocol.Native;

namespace Aion.World;

// SubscribeList_NT 0xe33b: which subscription benefits are active. OBSERVED layout (official 20261106-105448, the user's
// Special Quai Membership): U8 count, then per benefit U8 ESubscribeType, I32 membership data id, I64 start ms, I64 end ms.
// The client unlocks each benefit it finds in the list. In free mode (server-rules.xml subscription free="true", the user's
// choice 2026-11-07) every benefit is listed for everyone with ten years left; otherwise the list stays empty (as before).
// Benefit -> membership mapping from gamedata/subscriptions.xml (Wind Breeze 2 and Tsenka 3 are PROVISIONAL there).
public sealed partial class GameSession
{
    static readonly (byte Type, int Data)[] FreeSubscriptions =
    [
        (1, 2), (2, 2), (3, 2), (27, 2), (25, 2),                          // Wind Breeze: RemoteStorage, Exchange, TradeExPanded, TradeExchangeOpen, Shop
        (4, 3), (26, 3), (28, 3), (29, 3), (30, 3), (32, 3), (33, 3),      // Tsenka: OdenergyCube/Charge, FieldEventRankCount, DailyDungeonUse, AbyssUse, ChatRace, Gather
        (34, 4), (35, 4), (36, 4), (37, 4), (38, 4), (39, 4), (40, 4), (41, 4), (42, 4), (43, 4),   // Special Quai (+ free-only Exchange2/AbyssUse2)
        (44, 8),                                                           // Pet Auto-Loot
    ];

    static byte[] SubscribeList(long now)
    {
        var w = PacketWriter.Opcode(0xe33b);
        if (!ServerRules.Embedded.SubscriptionFree) return w.U8(0).ToArray();
        long start = now - 86_400_000L, end = now + 3650L * 86_400_000L;
        w.U8((byte)FreeSubscriptions.Length);
        foreach (var (type, data) in FreeSubscriptions) w.U8(type).I32(data).I64(start).I64(end);
        return w.ToArray();
    }
}
