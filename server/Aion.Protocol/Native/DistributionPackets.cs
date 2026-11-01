namespace Aion.Protocol.Native;

/// <summary>
/// Party loot rolls and special distribution (usmap game opcodes; layouts CANDIDATE where no capture exists).
/// Dice: 0x381C BeginRollingDice_NT … 0x3826 RollDiceWithdraw_NT. Bid family 0x3813..0x3827 (bid treated as dice on Global).
/// </summary>
public static class DistributionOpcodes
{
    public const ushort BeginItemBiddingNt = 0x3813, BidItemRq = 0x3814, BidItemRs = 0x3815,
        BidItemWithdrawRq = 0x3816, BidItemWithdrawRs = 0x3817, BidItemNt = 0x3818, BidItemWinNt = 0x3819,
        BidItemResultNt = 0x381a, BidItemResultFailNt = 0x381b, BidItemWithdrawNt = 0x3827;
    public const ushort BeginRollingDiceNt = 0x381c, RollDiceRq = 0x381d, RollDiceRs = 0x381e,
        RollDiceWithdrawRq = 0x381f, RollDiceWithdrawRs = 0x3820, RollDiceNt = 0x3821, RollDiceWinNt = 0x3822,
        RollDiceResultNt = 0x3823, ReRollDiceNt = 0x3824, RollDiceResultFailNt = 0x3825, RollDiceWithdrawNt = 0x3826,
        SpecialDistributionListNt = 0x3828, RewardDistributionResultNt = 0x3830;
}

public static class DistributionPackets
{
    static PacketWriter Op(ushort opcode) => PacketWriter.Opcode(opcode);

    /// <summary>0x3813 BeginItemBidding_NT: bidding uid, item, count, expire time (ms).</summary>
    public static byte[] BeginItemBidding(long biddingUid, int itemId, int count, long expireTime) =>
        Op(DistributionOpcodes.BeginItemBiddingNt).I64(biddingUid).I32(itemId).I32(count).I64(expireTime).ToArray();

    /// <summary>0x3815 BidItem_RS: result u16, bidding uid, accepted price.</summary>
    public static byte[] BidItemReply(int result, long biddingUid, long price) =>
        Op(DistributionOpcodes.BidItemRs).U16(result).I64(biddingUid).I64(price).ToArray();

    /// <summary>0x3817 BidItemWithdraw_RS.</summary>
    public static byte[] BidItemWithdrawReply(int result, long biddingUid) =>
        Op(DistributionOpcodes.BidItemWithdrawRs).U16(result).I64(biddingUid).ToArray();

    /// <summary>0x3818 BidItem_NT: someone raised the bid (drop item id field carries template id).</summary>
    public static byte[] BidItemNotice(int itemId, int serverId, string nickname) =>
        Op(DistributionOpcodes.BidItemNt).I32(itemId).I32(serverId).String(nickname).ToArray();

    /// <summary>0x3819 BidItemWin_NT: interim high bid.</summary>
    public static byte[] BidItemWin(long biddingUid, int itemId, long price) =>
        Op(DistributionOpcodes.BidItemWinNt).I64(biddingUid).I32(itemId).I64(price).ToArray();

    /// <summary>0x381A BidItemResult_NT: auction winner.</summary>
    public static byte[] BidItemResult(long biddingUid, long price, int itemId, int winnerServerId, string winnerNickname, int participantCount, long earningAmount) =>
        Op(DistributionOpcodes.BidItemResultNt).I64(biddingUid).I64(price).I32(itemId).I32(winnerServerId).String(winnerNickname)
            .I32(participantCount).I64(earningAmount).ToArray();

    /// <summary>0x381B BidItemResultFail_NT: no winner.</summary>
    public static byte[] BidItemResultFail(long biddingUid, int result) =>
        Op(DistributionOpcodes.BidItemResultFailNt).I64(biddingUid).I32(result).ToArray();

    /// <summary>0x3827 BidItemWithdraw_NT: bidder withdrew.</summary>
    public static byte[] BidItemWithdraw(long biddingUid, int itemId, int serverId, string nickname) =>
        Op(DistributionOpcodes.BidItemWithdrawNt).I64(biddingUid).I32(itemId).I32(serverId).String(nickname).ToArray();

    /// <summary>0x381C BeginRollingDice_NT: dice uid, item, count, expire time (ms).</summary>
    public static byte[] BeginRollingDice(long diceUid, int itemId, int count, long expireTime) =>
        Op(DistributionOpcodes.BeginRollingDiceNt).I64(diceUid).I32(itemId).I32(count).I64(expireTime).ToArray();

    /// <summary>0x381E RollDice_RS: result u16, dice uid, rolled value (1..100 OURS).</summary>
    public static byte[] RollDiceReply(int result, long diceUid, int value) =>
        Op(DistributionOpcodes.RollDiceRs).U16(result).I64(diceUid).I32(value).ToArray();

    /// <summary>0x3820 RollDiceWithdraw_RS: pass / decline.</summary>
    public static byte[] RollDiceWithdrawReply(int result, long diceUid) =>
        Op(DistributionOpcodes.RollDiceWithdrawRs).U16(result).I64(diceUid).ToArray();

    /// <summary>0x3821 RollDice_NT: someone rolled (drop item id field carries template id in samples).</summary>
    public static byte[] RollDiceNotice(int itemId, int serverId, string nickname, int value) =>
        Op(DistributionOpcodes.RollDiceNt).I32(itemId).I32(serverId).String(nickname).I32(value).ToArray();

    /// <summary>0x3822 RollDiceWin_NT: interim win before final result (optional).</summary>
    public static byte[] RollDiceWin(long diceUid, int itemId) =>
        Op(DistributionOpcodes.RollDiceWinNt).I64(diceUid).I32(itemId).ToArray();

    /// <summary>0x3823 RollDiceResult_NT: winner.</summary>
    public static byte[] RollDiceResult(long diceUid, int itemId, int winnerServerId, string winnerNickname) =>
        Op(DistributionOpcodes.RollDiceResultNt).I64(diceUid).I32(itemId).I32(winnerServerId).String(winnerNickname).ToArray();

    /// <summary>0x3824 ReRollDice_NT: tie reroll.</summary>
    public static byte[] ReRollDice(long diceUid, int itemId) =>
        Op(DistributionOpcodes.ReRollDiceNt).I64(diceUid).I32(itemId).ToArray();

    /// <summary>0x3825 RollDiceResultFail_NT: nobody won / error.</summary>
    public static byte[] RollDiceResultFail(long diceUid, int result) =>
        Op(DistributionOpcodes.RollDiceResultFailNt).I64(diceUid).I32(result).ToArray();

    /// <summary>0x3826 RollDiceWithdraw_NT: member passed.</summary>
    public static byte[] RollDiceWithdraw(long diceUid, int itemId, int serverId, string nickname) =>
        Op(DistributionOpcodes.RollDiceWithdrawNt).I64(diceUid).I32(itemId).I32(serverId).String(nickname).ToArray();

    /// <summary>0x3828 SpecialDistributionList_NT: active roll/bid entries at login (empty list OURS).</summary>
    public static byte[] SpecialDistributionList(IReadOnlyList<SpecialDistributionEntry> rewards)
    {
        var w = Op(DistributionOpcodes.SpecialDistributionListNt).Varint(rewards.Count);
        foreach (var r in rewards)
            w.I64(r.Uid).U8(r.DistributionType).I32(r.ItemId).I32(r.ItemCount).I64(r.ExpireTime).I64(r.Value);
        return w.ToArray();
    }

    /// <summary>0x3830 RewardDistributionResult_NT (also used as LockOn in client requests; server NT only).</summary>
    public static byte[] RewardDistributionResult(int result, string charName, int serverId) =>
        Op(DistributionOpcodes.RewardDistributionResultNt).U16(result).String(charName).I32(serverId).ToArray();
}

public sealed record SpecialDistributionEntry(long Uid, byte DistributionType, int ItemId, int ItemCount, long ExpireTime, long Value);
