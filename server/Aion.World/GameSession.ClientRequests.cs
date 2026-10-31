using Aion.Protocol.Native;

namespace Aion.World;

// Small requests the client sends during ordinary play that were unanswered (host log 2026-10-16). Live opcodes:
// catalogue index + run base (0x38 run 0x3763, 0xe2 run 0xdd07, 0xe3 run 0xdd6e). Replies follow the official
// capture 20261013-144836 where it has them.
public sealed partial class GameSession
{
    public const ushort LockOnTargetRq = 0x3830, LockOnTargetRs = 0x3831, LockOnTargetNt = 0x3832,
        LoadPostRq = 0xe214, LoadPostRs = 0xe215, PresetSetRq = 0xe28f, PresetSetRs = 0xe290,
        PushRewardInfoRq = 0xe348, PushRewardInfoRs = 0xe349,
        MessageCardBlockListRq = 0xe212, MessageCardBlockListRs = 0xe213, SkinDecoInstanceListRq = 0x8d68, SkinDecoInstanceListRs = 0x8d69,
        AbyssArtifactEventInfoRq = 0xe306, AbyssArtifactEventInfoRs = 0xe307, MySeasonRankingRq = 0x8d56, MySeasonRankingRs = 0x8d57,
        CoinShopOpenRq = 0x8c06, CoinShopOpenRs = 0x8c07, QuickSlotContextSkillRq = 0x5607, QuickSlotContextSkillRs = 0x5608;

    /// <summary>
    /// Client packets that expect no reply: ClientTime_RT 0x3601 (i64 client ms), UpdateVisibleLen_RQ 0x3651 (f32 view
    /// radius, no RS in the protocol), CustomizingStart/End_RT 0xe34f/0xe350, NCGuardData_RQ 0xe259 (opaque guard data,
    /// research/e259-ncguard.md) and the 0xffa0/0xffa1 pair (no struct; 0xffa1 every 30 s). Not logged as unanswered.
    /// 0xe259 stays unanswered: replaying the official 0xe25a (2026-10-24) made the client flood hundreds of requests and
    /// drop the connection 10-50 s later in every long session (live 2026-10-24/25); unanswered it sent 4-7 per session.
    /// </summary>
    public static readonly IReadOnlySet<int> SilentRequests = new HashSet<int> { 0x3601, 0x3651, 0xe34f, 0xe350, 0xe259, 0xffa0, 0xffa1 };

    /// <summary>
    /// The official reply to a RetrieveMyLatestSeasonRanking_RQ, pushed at the world entry (#462 of capture 20261013-144836):
    /// 403e 01 00, 54 zero bytes (no ranking), then the next update time i64. Bytes 2-3 are read as the request's contents
    /// type and class (CANDIDATE); the time is replaced by the server's.
    /// </summary>
    static readonly byte[] SeasonRankingNone = [0x40, 0x3e, 0x01, 0x00, .. new byte[54]];

    List<byte[]>? ClientRequest(byte[] plain, long now)
    {
        if (AbyssClientRequest(plain, now) is { } abyssReplies) return abyssReplies;
        switch (Opcode(plain))
        {
            // LoadMessageCardBlockList_RQ (no body): no blocked message-card senders (usmap: result, MessageCardBlock list). CANDIDATE.
            case MessageCardBlockListRq when plain.Length == 2:
                return [PacketWriter.Opcode(MessageCardBlockListRs).U16(0).Varint(0).ToArray()];
            // SkinDecoInstanceList_RQ (no body): the reply carries only a result (usmap). CANDIDATE.
            case SkinDecoInstanceListRq when plain.Length == 2:
                return [PacketWriter.Opcode(SkinDecoInstanceListRs).U16(0).ToArray()];
            case WorldMapAbyssRankerInfoRq when plain.Length == 2:
                return WorldMapAbyssRankerInfo(plain);
            // RetrieveMyLatestSeasonRanking_RQ {contents type u8, class u8}: no ranking, the official empty reply.
            case MySeasonRankingRq when plain.Length >= 4:
            {
                var body = (byte[])SeasonRankingNone.Clone();
                body[2] = plain[2];
                body[3] = plain[3];
                return [PacketWriter.Opcode(MySeasonRankingRs).Raw(body).I64(now + 3_600_000).ToArray()];
            }
            // QuickSlotContextSkill (0x5607) and CoinShopOpen (0x8c06) are handled in HandleAsync main switch (persistence + shop parity).
            // LockOnTarget_RQ: 00 | target varint (as SetMainTarget_RQ; no request sample, CANDIDATE). The reply is the
            // OBSERVED form 0000 | target varint | 0000 (target 0 = unlock); others see 0x3832 key | 00 | target varint.
            case LockOnTargetRq when plain.Length >= 3:
            {
                uint target = 0;
                if (plain.Length > 3)
                    try { target = new PacketReader(plain) { Offset = 3 }.Varint(); } catch (ProtocolException) { return []; }
                worldPlayer?.Map?.Broadcast(worldPlayer, [PacketWriter.Opcode(LockOnTargetNt).Varint(playerKey).U8(0).Varint(target).ToArray()]);
                var replies = new List<byte[]> { PacketWriter.Opcode(LockOnTargetRs).U16(0).Varint(target).U16(0).ToArray() };
                if (target > 0 && options.MapEventAskNpcProximityFallback)
                    replies.AddRange(TryMapEventAskNpcInteractFallback((int)target, now));
                return replies;
            }
            // Reports without a reply (SilentRequests).
            case 0x3651 or 0x3601 or 0xe350 or 0xe259 or 0xffa0 or 0xffa1:
                return [];
        }
        return null;
    }
}
