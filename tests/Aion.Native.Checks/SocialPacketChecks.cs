using Aion.Protocol.Native;

/// <summary>
/// Checks of <see cref="SocialPackets"/> / <see cref="SocialRequests"/> (research/social-protocol.md):
/// opcodes (catalogue index + run base), byte-exact official samples (capture 20261013-144836
/// #240 FriendList_NT and #213 PersonalExchangeInfo_NT; the 20261008 capture has the same two
/// bodies), fixed layouts of the builders, and round trips of every request parser (primary and
/// alternative widths, truncated and over-long bodies rejected).
/// </summary>
public static class SocialPacketChecks
{
    static int passed;

    static void Same(string name, byte[] built, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex.Replace(" ", ""));
        if (!built.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"SocialPackets mismatch: {name}\n built {Convert.ToHexString(built)}\n  want {Convert.ToHexString(expected)}");
        passed++;
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("SocialPackets check failed: " + name);
        passed++;
    }

    static void Op(ushort actual, int expected, string name) => Check(actual == expected, $"{name} opcode 0x{actual:x4} != 0x{expected:x4}");

    static ReadOnlyMemory<byte> M(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static int Run()
    {
        passed = 0;
        Opcodes();
        OfficialSamples();
        FriendLayouts();
        PartyLayouts();
        ExchangeLayouts();
        DuelLayouts();
        RequestRoundTrips();
        return passed;
    }

    static void Opcodes()
    {
        Op(SocialOpcodes.ExchangeRequestRq, 0x5659, "ExchangeRequest_RQ");
        Op(SocialOpcodes.ExchangeCompletedNt, 0x566d, "ExchangeCompleted_NT");
        Op(SocialOpcodes.ExchangeItemListModifyRq, 0x5664, "ExchangeItemListModify_RQ");
        Op(SocialOpcodes.PersonalExchangeInfoNt, 0xe33d, "PersonalExchangeInfo_NT");
        Op(SocialOpcodes.FriendListNt, 0xe274, "FriendList_NT");
        Op(SocialOpcodes.FriendRequestRq, 0xe275, "FriendRequest_RQ");
        Op(SocialOpcodes.FriendMemoUpdateRs, 0xe288, "FriendMemoUpdate_RS");
        Op(SocialOpcodes.PartyInfoNt, 0x9200, "PartyInfo_NT");
        Op(SocialOpcodes.PartyCreateRq, 0x9204, "PartyCreate_RQ");
        Op(SocialOpcodes.PartyInviteResponseRq, 0x920a, "PartyInviteResponse_RQ");
        Op(SocialOpcodes.PartyUpdateMemberStatusNt, 0x921c, "PartyUpdateMemberStatus_NT");
        Op(SocialOpcodes.PartyInviteCancelNt, 0x922c, "PartyInviteCancel_NT");
        Op(SocialOpcodes.PartyLeaveNt, 0x9327, "PartyLeave_NT");
        Op(SocialOpcodes.Alternative(SocialOpcodes.PartyLeaveNt), 0x9244, "PartyLeave_NT alternative");
        Op(SocialOpcodes.PartyRecentMemberRq, 0xe338, "PartyRecentMember_RQ");
        Op(SocialOpcodes.DuelCallOutRq, 0x933c, "DuelCallOut_RQ");
        Op(SocialOpcodes.DuelCancelNt, 0x934a, "DuelCancel_NT");
        Op(SocialOpcodes.Alternative(SocialOpcodes.DuelCallOutRq), 0x9259, "DuelCallOut_RQ alternative");
        // The observed neighbours the bases rest on (same formula): 0x922e MapEventJoin_NT 1083,
        // 0x9232 EngravedTeleportArtifactList_NT 1087, 0x9330 BookmarkedContentList_NT 1114,
        // 0x9501 PcSocialActionInfo_NT 1142 (base 0x9073), 0xe28c PresetInfo_NT 1413, 0xe349 RetrievePushRewardInfo_RS 1499.
        Op((ushort)(SocialOpcodes.PartyBase + 1083), 0x922e, "MapEventJoin_NT");
        Op((ushort)(SocialOpcodes.PartyBase + 1087), 0x9232, "EngravedTeleportArtifactList_NT");
        Op((ushort)(SocialOpcodes.LateBase + 1114), 0x9330, "BookmarkedContentList_NT");
        Op((ushort)(SocialOpcodes.FriendBase + 1413), 0xe28c, "PresetInfo_NT");
        Op((ushort)(SocialOpcodes.E3Base + 1499), 0xe349, "RetrievePushRewardInfo_RS");
        Check(SocialOpcodes.ClientRequests.Count == 30 && SocialOpcodes.ClientRequests.Contains(SocialOpcodes.DuelCallOutAcceptRq), "client request set");
        Same("WithOpcode", SocialPackets.WithOpcode(SocialPackets.DuelRingIn(1, 2), SocialOpcodes.Alternative(SocialOpcodes.DuelRingInNt)), "6692 01000000 02000000");
    }

    static void OfficialSamples()
    {
        Same("FriendList_NT empty (official #240)", SocialPackets.FriendList([], []), "74e2 0000");
        Same("PersonalExchangeInfo_NT (official #213)", SocialPackets.PersonalExchangeInfo(0), "3de3 00000000");
    }

    static void FriendLayouts()
    {
        Same("FriendList_NT entries", SocialPackets.FriendList(
                [new FriendInfo(1204, 0x0102030405060708, "Al", Race: 2, Class: 3, Level: 10, Relation: FriendRelation.Friend, Logined: true, LastLogoutTime: 5, Memo: "m"),
                 new FriendInfo(1204, 9, "Bo", Relation: FriendRelation.Recv)],
                [new FriendBlockInfo(1204, 9, "X")]),
            "74e2 02" +
            "b404 0807060504030201 02416c 02 03 0a000000 00000000 03 01 0500000000000000 016d" +
            // second record: its bool shares the first record's bool byte (bit 1 clear), so no byte here
            "b404 0900000000000000 02426f 01 01 01000000 00000000 02 0000000000000000 00" +
            "01 b404 0900000000000000 0158");
        Same("FriendRequest_RS", SocialPackets.FriendRequestReply(0, 1204, "Bob"), "76e2 0000 b404 03426f62");
        Same("FriendRequest_NT", SocialPackets.FriendRequestNotice(new FriendInfo(1204, 7, "C", Relation: FriendRelation.Send)),
            "77e2 b404 0700000000000000 0143 01 01 01000000 00000000 01 00 0000000000000000 00");
        Same("FriendRequestCancel_RS", SocialPackets.FriendRequestCancelReply(3, 7), "79e2 0300 0700000000000000");
        Same("FriendRequestReject_RS", SocialPackets.FriendRequestRejectReply(0, 7), "7be2 0000 0700000000000000");
        Same("FriendRequestAccept_RS", SocialPackets.FriendRequestAcceptReply(0, 7), "7de2 0000 0700000000000000");
        Same("FriendRequestAccept_NT", SocialPackets.FriendRequestAcceptNotice(7), "7ee2 0700000000000000");
        Same("FriendDelete_RS", SocialPackets.FriendDeleteReply(0, 7), "80e2 0000 0700000000000000");
        Same("FriendInfoDelete_NT", SocialPackets.FriendInfoDelete(7), "81e2 0700000000000000");
        Same("FriendInfoUpdate_NT", SocialPackets.FriendInfoUpdate(new FriendUpdateInfo(1204, 7, "C", 2, 5, 0, true, 0)),
            "82e2 b404 0700000000000000 0143 02 05000000 00000000 01 0000000000000000");
        Same("FriendBlock_RS", SocialPackets.FriendBlockReply(0, 1204, 7, "C"), "84e2 0000 b404 0700000000000000 0143");
        Same("FriendBlockCancel_RS", SocialPackets.FriendBlockCancelReply(0, new FriendBlockInfo(1204, 7, "C")), "86e2 0000 b404 0700000000000000 0143");
        Same("FriendMemoUpdate_RS", SocialPackets.FriendMemoReply(0, 7, "hi"), "88e2 0000 0700000000000000 026869");
    }

    static PartyMember Member(byte number, int key, long dbid, string name, bool live = true, bool login = true) => new()
    {
        Number = number, Key = key, DbId = dbid, Nickname = name, PcId = 29, Hp = 1500, HpMax = 1500, Mp = 200, MpMax = 200,
        MapKey = 1000, Position = new Vector3d(1, 2, 3), Live = live, Level = 5, Login = login,
    };

    static void PartyLayouts()
    {
        var a = Member(0, 100001, 11, "Al");
        var b = Member(1, 100002, 12, "Bo", live: false, login: false);
        var info = new PartyInfo { Key = 77, LeaderDbId = 11, Members = [a, b] };
        var bytes = SocialPackets.PartyInfo(info, PartyUpdateReason.AddMember);
        // Decode it back field by field (usmap order) to pin the layout and the shared bool byte.
        var r = new PacketReader(bytes);
        Check(r.U16() == 0x9200 && r.I64() == 77 && r.I64() == 11 && r.I32() == 4, "PartyInfo head");
        Check(r.U8() == 3 && r.U8() == 1 && r.U8() == 2 && r.U8() == 1, "PartyInfo options");
        Check(r.I32() == 0 && r.I32() == 0 && r.I32() == 0, "PartyInfo focus target");
        Check(r.Varint() == 0 && r.Varint() == 0 && r.Varint() == 0 && r.Varint() == 2, "PartyInfo lists, member count");
        foreach (var m in new[] { a, b })
        {
            Check(r.U8() == m.Number && r.I32() == m.Key && r.U16() == 1204 && r.U16() == 1204 && r.String() == "" && r.I64() == m.DbId && r.String() == m.Nickname, "member identity " + m.Nickname);
            Check(r.I32() == 29 && r.Varint() == 1500 && r.Varint() == 1500 && r.I32() == 200 && r.I32() == 200, "member points " + m.Nickname);
            for (var n = 0; n < 4; n++) r.I32();
            Check(r.I64() == 1000 && r.I32() == 0 && r.Vec3() == new Vector3d(1, 2, 3) && r.Bool() == m.Live, "member map/pos/live " + m.Nickname);
            Check(r.String() == "" && r.I32() == 0 && r.I32() == 5 && r.I32() == 0 && r.I32() == 0, "member guild/levels " + m.Nickname);
            Check(r.Bool() && r.Bool() == m.Login && r.Varint() == 0 && r.I64() == 0 && r.U8() == 1, "member tail " + m.Nickname);
        }
        Check(r.Varint() == 0 && r.U8() == (byte)PartyUpdateReason.AddMember && r.End, "PartyInfo tail");
        // The six member bools share the byte opened at the first _live: 1,1,1 then 0,1,0 -> 0x17.
        Check(bytes[119] == 0x17, "shared bool byte (live, can_take, login of both members)");

        Same("PartyInfoUpdate_RS", SocialPackets.PartyInfoUpdateReply(0), "0292 0000");
        Same("PartyInfoUpdate_NT", SocialPackets.PartyInfoUpdateNotice(4, 3, 1, 2), "0392 04000000 03 01 02");
        Same("PartyCreate_RS", SocialPackets.PartyCreateReply(0), "0592 0000");
        Same("PartyInvite_RS", SocialPackets.PartyInviteReply(0, 1204, "Bo"), "0792 0000 b404 02426f");
        var inviter = new InvitationInfo(29, 5, 1, "Al", 1204);
        Same("PartyInvite_NT", SocialPackets.PartyInviteNotice(77, 11, inviter, 1000),
            "0992 4d00000000000000 0b00000000000000 1d000000 05000000 01000000 02416c b404 00000000 0000000000000000 00 e803000000000000");
        Same("PartyInviteResponse_RS", SocialPackets.PartyInviteResponseReply(0), "0b92 0000");
        Same("PartyInviteResponse_NT", SocialPackets.PartyInviteResponseNotice(12, 1204, "Bo", PartyInviteAnswer.Refusal), "0c92 0c00000000000000 b404 02426f 01");
        var add = SocialPackets.PartyAddMember(a);
        Check(add[0] == 0x0d && add[1] == 0x92 && add.AsSpan(2).SequenceEqual(SocialPackets.PartyReconnectMember(a).AsSpan(2)), "PartyAddMember_NT / PartyReconnectMember_NT share the member record");
        Same("PartyAbnormalAdd_NT", SocialPackets.PartyAbnormalAdd(5, [7, 8]), "0e92 05000000 02 07000000 08000000");
        Same("PartyAbnormalRemove_NT", SocialPackets.PartyAbnormalRemove(5, 7), "1a92 05000000 07000000");
        Same("PartySetLeader_RS", SocialPackets.PartySetLeaderReply(0), "1092 0000");
        Same("PartyNewLeader_NT", SocialPackets.PartyNewLeader(12), "1192 0c00000000000000");
        Same("PartyLeave_RS", SocialPackets.PartyLeaveReply(0), "1392 0000");
        Same("PartyKickout_RS", SocialPackets.PartyKickoutReply(0), "1892 0000");
        Same("PartyKickout_NT", SocialPackets.PartyKickout(false), "1992 00");
        Same("PartyUpdateMemberHPMP_NT", SocialPackets.PartyMemberHpMp(new PartyMemberPoints(5, 300, 1500, 10, 200)),
            "1b92 05000000 ac02 dc0b 0a000000 c8000000 00000000 00000000 00000000 00000000 01");
        Same("PartyUpdateMemberStatus_NT", SocialPackets.PartyMemberStatus(new PartyMemberStatus { DbId = 12, PcId = 29, MapKey = 1000, Position = new Vector3d(0, 0, 0), Level = 6 }),
            "1c92 0c00000000000000 1d000000 e803000000000000 00000000 000000000000000000000000 06000000 00000000 00000000 00 00000000 01 0000000000000000 01");
        Same("PartyDisband_RS", SocialPackets.PartyDisbandReply(0), "1e92 0000");
        Same("PartyDisband_NT", SocialPackets.PartyDisband(PartyUpdateReason.LeaveMember), "1f92 03");
        Same("PartyDisconnectMember_NT", SocialPackets.PartyDisconnectMember(12), "2192 0c00000000000000");
        Same("PartyInviteCancel_RS", SocialPackets.PartyInviteCancelReply(0, "Bo", 1204), "2b92 0000 02426f b404");
        Same("PartyInviteCancel_NT", SocialPackets.PartyInviteCancelNotice(77), "2c92 4d00000000000000");
        Same("PartyLeave_NT", SocialPackets.PartyLeaveNotice(77, PartyUpdateReason.LeaveMember), "2793 4d00000000000000 03");
        Same("PartyRecentMember_RS", SocialPackets.PartyRecentMembers(0, [new PartyRecentMember(12, 1000, 1204, "Bo", 2, 5, 0, true)]),
            "39e3 0000 01 0c00000000000000 e803000000000000 b404 02426f 02 05000000 00000000 01");
    }

    static void ExchangeLayouts()
    {
        Same("ExchangeRequest_RS", SocialPackets.ExchangeRequestReply(0, 100002, 9), "5a56 0000 a2860100 0900000000000000");
        Same("ExchangeRequested_NT", SocialPackets.ExchangeRequested(100001, new InvitationInfo(29, 5, 1, "Al", 1204), 9, 1000),
            "5b56 a1860100 1d000000 05000000 01000000 02416c b404 00000000 0000000000000000 00 0900000000000000 e803000000000000");
        Same("ExchangeAccept_RS", SocialPackets.ExchangeAcceptReply(0, 9), "5d56 0000 0900000000000000");
        Same("ExchangeAccepted_NT", SocialPackets.ExchangeAccepted(9), "5e56 0900000000000000");
        Same("ExchangeRefuse_RS", SocialPackets.ExchangeRefuseReply(0, 9), "6056 0000 0900000000000000");
        Same("ExchangeCancel_RS", SocialPackets.ExchangeCancelReply(0, 9), "6256 0000 0900000000000000");
        Same("ExchangeCanceled_NT", SocialPackets.ExchangeCanceled(9, 1), "6356 0900000000000000 01000000");
        Same("ExchangeItemListModify_RS", SocialPackets.ExchangeItemListModifyReply(0, 9, ExchangeModification.Add, new ExchangeItem(5, 3), 0),
            "6556 0000 0900000000000000 00 0500000000000000 0300000000000000 00000000");
        Same("ExchangePeerItemListModified_NT plain", SocialPackets.ExchangePeerItemListModified(9, ExchangeModification.Update, new ExchangeItemDetail(5, 152000001, 3), 100),
            "6656 0900000000000000 01 00 0500000000000000 01560f09 0300000000000000 00 64000000");
        var equip = SocialPackets.ExchangePeerItemListModified(9, ExchangeModification.Add, new ExchangeItemDetail(5, 100000001, 1, "", new EquipDetail(10, 1)), 0);
        // head 2+8+1+1+8+4+8+1, equip detail 1+4+4+1+4+4+1+1 + 1+7+8+1 + 1+12 + 7+1, cash 4
        Check(equip.Length == 33 + 20 + 17 + 13 + 8 + 4 && equip[11] == 1, "ExchangePeerItemListModified_NT with equip detail");
        Same("ExchangeItemListLock_RS", SocialPackets.ExchangeItemListLockReply(0, 9, true), "6856 0000 0900000000000000 01");
        Same("ExchangePeerItemListLock_NT", SocialPackets.ExchangePeerItemListLock(9, false), "6956 0900000000000000 00");
        Same("ExchangeConfirm_RS", SocialPackets.ExchangeConfirmReply(0, 9), "6b56 0000 0900000000000000");
        Same("ExchangePeerConfirmed_NT", SocialPackets.ExchangePeerConfirmed(9), "6c56 0900000000000000");
        Same("ExchangeCompleted_NT", SocialPackets.ExchangeCompleted(9), "6d56 0900000000000000");
    }

    static void DuelLayouts()
    {
        Same("DuelCallOut_RS", SocialPackets.DuelCallOutReply(0, 3, 1000), "3d93 0000 03000000 e803000000000000");
        Same("DuelCallOut_NT", SocialPackets.DuelCallOut(3, 100001, new InvitationInfo(29, 5, 1, "Al", 1204), 1000),
            "3e93 03000000 a1860100 1d000000 05000000 01000000 02416c b404 00000000 0000000000000000 00 e803000000000000");
        Same("DuelCallOutAccept_RS", SocialPackets.DuelAcceptReply(0, 3, 100001), "4093 0000 03000000 a1860100");
        Same("DuelCallOutAccept_NT", SocialPackets.DuelAccepted(3, 100002), "4193 03000000 a2860100");
        Same("DuelCallOutReject_RS", SocialPackets.DuelRejectReply(0, 3), "4393 0000 03000000");
        Same("DuelCallOutReject_NT", SocialPackets.DuelRejected(3, 100002, "Bo"), "4493 03000000 a2860100 02426f");
        Same("DuelStateUpdate_NT", SocialPackets.DuelStateUpdate(new DuelStatus(1, 3, DuelState.InCountDown, 100001, "Al", 100002, "Bo", 1000)),
            "4593 01000000 03000000 02 a1860100 02416c a2860100 02426f e803000000000000");
        Same("DuelComplete_NT", SocialPackets.DuelComplete(3, DuelCompleteReason.Die, 100001, "Al", "Bo"), "4693 03000000 02 a1860100 02416c 02426f");
        Same("DuelResult_NT", SocialPackets.DuelResult(3, DuelCompleteReason.RingOut, 100001, 100002, "Al", "Bo"), "4793 03000000 03 a1860100 a2860100 02416c 02426f");
        Same("DuelRingOut_NT", SocialPackets.DuelRingOut(3, 100002, 1000), "4893 03000000 a2860100 e803000000000000");
        Same("DuelRingIn_NT", SocialPackets.DuelRingIn(3, 100002), "4993 03000000 a2860100");
        Same("DuelCancel_NT", SocialPackets.DuelCancel(3, "Al", "Bo"), "4a93 03000000 02416c 02426f");
    }

    static void RequestRoundTrips()
    {
        // Friends
        Check(SocialRequests.ParseServerAndName(SocialRequests.EncodeServerAndName(SocialOpcodes.FriendRequestRq, 1204, "Elyra")) == new NamedTarget(1204, "Elyra"), "FriendRequest_RQ round trip");
        Check(SocialRequests.ParseServerAndName(M("75e2 b4040000 05 456c797261")) == new NamedTarget(1204, "Elyra"), "FriendRequest_RQ i32 server id");
        Check(SocialRequests.ParseServerAndName(M("75e2 b404 05 46696f6e")) is null, "FriendRequest_RQ truncated name");
        Check(SocialRequests.ParseServerAndName(M("83e2 b404 01 41 0000")) is null, "FriendBlock_RQ trailing byte");
        Check(SocialRequests.ParseServerAndName(M("75e2 b404 02 c328")) is null, "FriendRequest_RQ invalid UTF-8");
        Check(SocialRequests.ParseDbId(SocialRequests.EncodeDbId(SocialOpcodes.FriendDeleteRq, 0x0102030405060708)) == 0x0102030405060708, "FriendDelete_RQ round trip");
        Check(SocialRequests.ParseDbId(M("7ce2 8d06")) == 781, "FriendRequestAccept_RQ varint dbid");
        Check(SocialRequests.ParseDbId(M("7ce2")) is null, "FriendRequestAccept_RQ empty");
        var block = new FriendBlockInfo(1204, 42, "Troll");
        Check(SocialRequests.ParseFriendBlockCancel(SocialRequests.EncodeFriendBlockCancel(block)) == block, "FriendBlockCancel_RQ round trip");
        Check(SocialRequests.ParseFriendBlockCancel(M("85e2 b4040000 2a 05 54726f6c6c")) == block, "FriendBlockCancel_RQ i32 server, varint dbid");
        Check(SocialRequests.ParseFriendMemo(SocialRequests.EncodeFriendMemo(42, "guild mate")) == new FriendMemoRequest(42, "guild mate"), "FriendMemoUpdate_RQ round trip");
        Check(SocialRequests.ParseFriendMemo(SocialRequests.EncodeFriendMemo(42, "")) == new FriendMemoRequest(42, ""), "FriendMemoUpdate_RQ empty memo");

        // Party
        var settings = new PartySettingsRequest(PartyOptions.DistributionRandom, PartyOptions.JoinApproval, PartyOptions.SpecialDice, 3);
        Check(SocialRequests.ParsePartyCreate(SocialRequests.EncodePartyCreate(settings)) == settings, "PartyCreate_RQ round trip");
        Check(SocialRequests.ParsePartyInfoUpdate(SocialRequests.EncodePartyInfoUpdate(settings)) == settings, "PartyInfoUpdate_RQ round trip");
        Same("PartyInfoUpdate_RQ order", SocialRequests.EncodePartyInfoUpdate(settings), "0192 02 03 03 02");
        Check(SocialRequests.ParsePartyCreate(M("0492 01 01 01")) is null, "PartyCreate_RQ short");
        Check(SocialRequests.ParsePartyInvite(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bo")) == new NamedTarget(1204, "Bo"), "PartyInvite_RQ round trip");
        Check(SocialRequests.ParsePartyInviteCancel(SocialRequests.EncodePartyInviteCancel("Bo", 1204)) == new NamedTarget(1204, "Bo"), "PartyInviteCancel_RQ round trip");
        Check(SocialRequests.ParsePartyInviteCancel(M("2a92 02426f b4040000")) == new NamedTarget(1204, "Bo"), "PartyInviteCancel_RQ i32 server id");
        var replies = SocialRequests.ParsePartyInviteResponse(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(77, PartyInviteAnswer.Accept), new PartyInviteReply(78, PartyInviteAnswer.Refusal)]));
        Check(replies is { Count: 2 } && replies[0] == new PartyInviteReply(77, PartyInviteAnswer.Accept) && replies[1] == new PartyInviteReply(78, PartyInviteAnswer.Refusal), "PartyInviteResponse_RQ round trip");
        Check(SocialRequests.ParsePartyInviteResponse(M("0a92 01 4d 00")) is { Count: 1 } r2 && r2[0].PartyKey == 77, "PartyInviteResponse_RQ varint party key");
        Check(SocialRequests.ParsePartyInviteResponse(M("0a92 02 4d00000000000000 00")) is null, "PartyInviteResponse_RQ missing entry");
        Check(SocialRequests.ParseDbId(SocialRequests.EncodeDbId(SocialOpcodes.PartySetLeaderRq, 12)) == 12, "PartySetLeader_RQ round trip");
        Check(SocialRequests.ParseDbId(SocialRequests.EncodeDbId(SocialOpcodes.PartyKickoutRq, 12)) == 12, "PartyKickout_RQ round trip");
        Check(SocialRequests.IsEmpty(M("1292")) && SocialRequests.IsEmpty(M("1d92")) && SocialRequests.IsEmpty(M("38e3")) && !SocialRequests.IsEmpty(M("1292 00")), "empty requests");
        var focus = new FocusTargetRequest(5, 0, 1, 5, 0, 1);
        Check(SocialRequests.ParseSetPartyFocusTarget(SocialRequests.EncodeSetPartyFocusTarget(focus)) == focus, "SetPartyFocusTarget_RQ round trip");

        // Exchange
        Check(SocialRequests.ParseExchangeRequest(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, 100002)) == 100002, "ExchangeRequest_RQ round trip");
        Check(SocialRequests.ParseExchangeRequest(M("5956 a28d06")) == 100002, "ExchangeRequest_RQ varint key");
        Check(SocialRequests.ParseExchangeRequest(M("5956 a2860100 00")) is null, "ExchangeRequest_RQ 5-byte body (neither width)");
        Check(SocialRequests.ParseExchangeAnswer(SocialRequests.EncodeExchangeAnswer(SocialOpcodes.ExchangeAcceptRq, 9, 100001)) == new ExchangeAnswer(9, 100001), "ExchangeAccept_RQ round trip");
        Check(SocialRequests.ParseExchangeAnswer(SocialRequests.EncodeExchangeAnswer(SocialOpcodes.ExchangeRefuseRq, 9, 100001)) == new ExchangeAnswer(9, 100001), "ExchangeRefuse_RQ round trip");
        Check(SocialRequests.ParseExchangeAnswer(M("5c56 0900000000000000 a18d06")) == new ExchangeAnswer(9, 100001), "ExchangeAccept_RQ varint key");
        Check(SocialRequests.ParseExchangeId(SocialRequests.EncodeDbId(SocialOpcodes.ExchangeCancelRq, 9)) == 9, "ExchangeCancel_RQ round trip");
        Check(SocialRequests.ParseExchangeId(SocialRequests.EncodeDbId(SocialOpcodes.ExchangeConfirmRq, 9)) == 9, "ExchangeConfirm_RQ round trip");
        var modify = new ExchangeModifyRequest(9, ExchangeModification.Remove, new ExchangeItem(5, 3), 250);
        Check(SocialRequests.ParseExchangeItemModify(SocialRequests.EncodeExchangeItemModify(modify)) == modify, "ExchangeItemListModify_RQ round trip");
        Check(SocialRequests.ParseExchangeItemModify(M("6456 0900000000000000 02 0500000000000000 0300000000000000 fa01")) == modify, "ExchangeItemListModify_RQ varint cash");
        Check(SocialRequests.ParseExchangeLock(SocialRequests.EncodeExchangeLock(9, true)) == new ExchangeLockRequest(9, true), "ExchangeItemListLock_RQ round trip");
        Check(SocialRequests.ParseExchangeLock(SocialRequests.EncodeExchangeLock(9, false)) == new ExchangeLockRequest(9, false), "ExchangeItemListLock_RQ unlock");

        // Duel (either candidate opcode routes to the same parser)
        Check(SocialRequests.ParseDuelCallOut(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, 100002)) == 100002, "DuelCallOut_RQ round trip");
        Check(SocialRequests.ParseDuelCallOut(SocialRequests.EncodeKey(SocialOpcodes.Alternative(SocialOpcodes.DuelCallOutRq), 100002)) == 100002, "DuelCallOut_RQ alternative opcode");
        Check(SocialRequests.ParseDuelAnswer(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutAcceptRq, 3, 100001)) == new DuelAnswer(3, 100001), "DuelCallOutAccept_RQ round trip");
        Check(SocialRequests.ParseDuelAnswer(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutRejectRq, 3, 100001)) == new DuelAnswer(3, 100001), "DuelCallOutReject_RQ round trip");
        Check(SocialRequests.ParseDuelAnswer(M("3f93 03 a18d06")) == new DuelAnswer(3, 100001), "DuelCallOutAccept_RQ varint keys");
        Check(SocialRequests.ParseDuelAnswer(M("3f93")) is null && SocialRequests.ParseDuelAnswer(M("3f")) is null, "DuelCallOutAccept_RQ empty");
    }
}
