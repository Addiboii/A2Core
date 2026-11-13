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
        CoinShopOpenRq = 0x8c06, CoinShopOpenRs = 0x8c07, QuickSlotContextSkillRq = 0x5607, QuickSlotContextSkillRs = 0x5608,
        UseTeleportArtifactRq = 0x9236, UseTeleportArtifactRs = 0x9237, ConversionLimitListRq = 0x8d8e, ConversionLimitListRs = 0x8d8f,
        CoinShopBuyRq = 0x8c08, CoinShopBuyRs = 0x8c09, TrustLimitInfoRq = 0xe260, TrustLimitInfoRs = 0xe261;

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
        switch (Opcode(plain))
        {
            // LoadMessageCardBlockList_RQ (no body): no blocked message-card senders (usmap: result, MessageCardBlock list). CANDIDATE.
            case MessageCardBlockListRq when plain.Length == 2:
                return [PacketWriter.Opcode(MessageCardBlockListRs).U16(0).Varint(0).ToArray()];
            // SkinDecoInstanceList_RQ (no body): the reply carries only a result (usmap). CANDIDATE.
            case SkinDecoInstanceListRq when plain.Length == 2:
                return [PacketWriter.Opcode(SkinDecoInstanceListRs).U16(0).ToArray()];
            // AbyssArtifactEventInfo_RQ (no body): no Abyss events, no artifacts (usmap: result, two lists). CANDIDATE.
            case AbyssArtifactEventInfoRq when plain.Length == 2:
                return [PacketWriter.Opcode(AbyssArtifactEventInfoRs).U16(0).Varint(0).Varint(0).ToArray()];
            // RetrieveMyLatestSeasonRanking_RQ {contents type u8, class u8}: no ranking, the official empty reply.
            case MySeasonRankingRq when plain.Length >= 4:
            {
                var body = (byte[])SeasonRankingNone.Clone();
                body[2] = plain[2];
                body[3] = plain[3];
                return [PacketWriter.Opcode(MySeasonRankingRs).Raw(body).I64(now + 3_600_000).ToArray()];
            }
            // CoinShopOpen_RQ {coin shop id i32}: CoinShopOpen_RS {result u16, id, purchased limited goods map, open time i64}. OBSERVED
            // in the community fork's recordings (6 replies, 2026-10-30 to 11-07, coin shops 990003-990007): result 0, the id,
            // an empty map, the server time; none in ours. The client lists the goods from its own CoinShop table. CANDIDATE: ms.
            case CoinShopOpenRq when plain.Length >= 6:
                return [PacketWriter.Opcode(CoinShopOpenRs).U16(0).I32(BitConverter.ToInt32(plain, 2)).Varint(0).I64(now).ToArray()];
            // CoinShopBuyGoods_RQ {coin shop id i32, goods}: refused (result 1, the id, no goods) until a CoinShop goods table is
            // built, instead of left unanswered. Never recorded: the usmap's layout (CANDIDATE).
            case CoinShopBuyRq when plain.Length >= 6:
                return [PacketWriter.Opcode(CoinShopBuyRs).U16(1).I32(BitConverter.ToInt32(plain, 2)).Varint(0).ToArray()];
            // LockOnTarget_RQ: 00 | target varint (as SetMainTarget_RQ; no request sample, CANDIDATE). The reply is the
            // OBSERVED form 0000 | target varint | 0000 (target 0 = unlock); others see 0x3832 key | 00 | target varint.
            case LockOnTargetRq when plain.Length >= 3:
            {
                uint target = 0;
                if (plain.Length > 3)
                    try { target = new PacketReader(plain) { Offset = 3 }.Varint(); } catch (ProtocolException) { return []; }
                worldPlayer?.Map?.Broadcast(worldPlayer, [PacketWriter.Opcode(LockOnTargetNt).Varint(playerKey).U8(0).Varint(target).ToArray()]);
                return [PacketWriter.Opcode(LockOnTargetRs).U16(0).Varint(target).U16(0).ToArray()];
            }
            // RetrievePushRewardInfo_RQ (no body): no push rewards; ask again in an hour (OBSERVED form 0000 | i64 next | list).
            case PushRewardInfoRq when plain.Length == 2:
                return [PacketWriter.Opcode(PushRewardInfoRs).U16(0).I64(now + 3_600_000).Varint(0).ToArray()];
            // ConversionLimitList_RQ {server limit only bool} -> ConversionLimitList_RS {result u16, the flag echoed, made counts of the
            // limited conversion recipes for the server, account and character ({recipe i32, made i32} each)}. OBSERVED (ours,
            // 2026-11-04 to 06): `8f8d 0000 01 00 00 00` twice with the flag set, and once without it 12 server entries. We keep no
            // conversion limits, so all three lists are empty (CANDIDATE). Found by the community fork.
            case ConversionLimitListRq when plain.Length >= 3:
                return [PacketWriter.Opcode(ConversionLimitListRs).U16(0).Bool(plain[2] != 0).Varint(0).Varint(0).Varint(0).ToArray()];
            // UseTeleportArtifact_RQ {teleport artifact id i32} (usmap; CANDIDATE: the request was not captured, c2s): teleport to a
            // bound Kibelisk (GameSession.cs UseTeleportArtifact).
            case UseTeleportArtifactRq when plain.Length >= 6:
                return UseTeleportArtifact(BitConverter.ToInt32(plain, 2), now);
            // TrustLimitInfo_RQ (usmap position; the request is ciphered in our recordings, CANDIDATE): TrustLimitInfo_NT then
            // TrustLimitInfo_RS result 0 (OBSERVED 2026-11-06 182716 and 234912, the same moment, that order).
            case TrustLimitInfoRq:
                return [TrustLimitInfo(now), PacketWriter.Opcode(TrustLimitInfoRs).U16(0).ToArray()];
            // Reports without a reply (SilentRequests).
            case 0x3651 or 0x3601 or 0xe350 or 0xe259 or 0xffa0 or 0xffa1:
                return [];
        }
        return null;
    }
}
