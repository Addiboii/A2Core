namespace Aion.Protocol.Native;

// Social features between players: friends, party, player-to-player exchange (trade) and duel.
// Reference: research/social-protocol.md (opcodes with their evidence, layouts, server flow).
//
// Opcodes are catalogue index + run base (research/local/gameserver-struct-order.json):
//   Exchange  0x54ca (0x56 run, bracketed by observed 0x564b = 385 and 0x5672 = 424: no gap) - strong
//   Friend    0xdd07 (0xe2 run; 0xe274 FriendList_NT OBSERVED at world entry, body 0000)  - OBSERVED
//   Party     0x8df3 (0x92 run; 0x922d..0x9233 = 1082..1088 OBSERVED; 0x9200 = 1037 is the
//             first index after the 0x90 run 988..1036, pinned by the client's ActionProtocol
//             opcode predicate)                                                          - CANDIDATE
//   Late run  0x8ed6 (0x93 run; 0x9330/0x9331 = 1114/1115 BookmarkedContentList_NT and
//             PartyDungeonChallenge_NT, both "00" at world entry): Duel*, PartyLeave_NT  - CANDIDATE
//             (alternative: the 0x92 base continues, 0x8df3; see SocialOpcodes.Alternative)
//   0xe3 run  0xdd6e (0xe324..0xe350 nine observed opcodes fit): PartyRecentMember,
//             PersonalExchangeInfo_NT (0xe33d OBSERVED, body 00000000)                  - strong
//
// Wire widths (CANDIDATE where no sample exists; one helper per kind so they change in one place):
//   _result u16 (every observed RS), *_server_id u16 (0x3633/0x3645/login rows), entity keys
//   i32 (0xe240/0xe241 NPC key i32; ActionProtocol keys are varints, none here), dbid/party
//   key/exchange id/time i64, other ints i32, Int64 hp varint (as in 0x3645), enums u8,
//   bools packed (PacketWriter.Bool), strings u8-length UTF-8, arrays varint count. No presence
//   masks: none of these structs has a sample with optional fields.

/// <summary>Opcodes of the social families (catalogue index + run base, see the file header).</summary>
public static class SocialOpcodes
{
    public const int ExchangeBase = 0x54ca, FriendBase = 0xdd07, PartyBase = 0x8df3, LateBase = 0x8ed6, LateAlternativeBase = 0x8df3, E3Base = 0xdd6e;

    // Exchange (player-to-player trade), catalogue 399..419.
    public const ushort ExchangeRequestRq = ExchangeBase + 399, ExchangeRequestRs = ExchangeBase + 400, ExchangeRequestedNt = ExchangeBase + 401,
        ExchangeAcceptRq = ExchangeBase + 402, ExchangeAcceptRs = ExchangeBase + 403, ExchangeAcceptedNt = ExchangeBase + 404,
        ExchangeRefuseRq = ExchangeBase + 405, ExchangeRefuseRs = ExchangeBase + 406,
        ExchangeCancelRq = ExchangeBase + 407, ExchangeCancelRs = ExchangeBase + 408, ExchangeCanceledNt = ExchangeBase + 409,
        ExchangeItemListModifyRq = ExchangeBase + 410, ExchangeItemListModifyRs = ExchangeBase + 411, ExchangePeerItemListModifiedNt = ExchangeBase + 412,
        ExchangeItemListLockRq = ExchangeBase + 413, ExchangeItemListLockRs = ExchangeBase + 414, ExchangePeerItemListLockNt = ExchangeBase + 415,
        ExchangeConfirmRq = ExchangeBase + 416, ExchangeConfirmRs = ExchangeBase + 417, ExchangePeerConfirmedNt = ExchangeBase + 418,
        ExchangeCompletedNt = ExchangeBase + 419;

    /// <summary>0xe33d PersonalExchangeInfo_NT (catalogue 1487), OBSERVED at world entry.</summary>
    public const ushort PersonalExchangeInfoNt = E3Base + 1487;

    // Friends, catalogue 1389..1409.
    public const ushort FriendListNt = FriendBase + 1389, FriendRequestRq = FriendBase + 1390, FriendRequestRs = FriendBase + 1391, FriendRequestNt = FriendBase + 1392,
        FriendRequestCancelRq = FriendBase + 1393, FriendRequestCancelRs = FriendBase + 1394,
        FriendRequestRejectRq = FriendBase + 1395, FriendRequestRejectRs = FriendBase + 1396,
        FriendRequestAcceptRq = FriendBase + 1397, FriendRequestAcceptRs = FriendBase + 1398, FriendRequestAcceptNt = FriendBase + 1399,
        FriendDeleteRq = FriendBase + 1400, FriendDeleteRs = FriendBase + 1401, FriendInfoDeleteNt = FriendBase + 1402, FriendInfoUpdateNt = FriendBase + 1403,
        FriendBlockRq = FriendBase + 1404, FriendBlockRs = FriendBase + 1405, FriendBlockCancelRq = FriendBase + 1406, FriendBlockCancelRs = FriendBase + 1407,
        FriendMemoUpdateRq = FriendBase + 1408, FriendMemoUpdateRs = FriendBase + 1409;

    // Party, catalogue 1037..1081.
    public const ushort PartyInfoNt = PartyBase + 1037, PartyInfoUpdateRq = PartyBase + 1038, PartyInfoUpdateRs = PartyBase + 1039, PartyInfoUpdateNt = PartyBase + 1040,
        PartyCreateRq = PartyBase + 1041, PartyCreateRs = PartyBase + 1042, PartyInviteRq = PartyBase + 1043, PartyInviteRs = PartyBase + 1044,
        PartyInviteStartNt = PartyBase + 1045, PartyInviteNt = PartyBase + 1046,
        PartyInviteResponseRq = PartyBase + 1047, PartyInviteResponseRs = PartyBase + 1048, PartyInviteResponseNt = PartyBase + 1049,
        PartyAddMemberNt = PartyBase + 1050, PartyAbnormalAddNt = PartyBase + 1051,
        PartySetLeaderRq = PartyBase + 1052, PartySetLeaderRs = PartyBase + 1053, PartyNewLeaderNt = PartyBase + 1054,
        PartyLeaveRq = PartyBase + 1055, PartyLeaveRs = PartyBase + 1056,
        SetPartyFocusTargetRq = PartyBase + 1057, SetPartyFocusTargetRs = PartyBase + 1058, PartyFocusTargetNt = PartyBase + 1059,
        PartyKickoutRq = PartyBase + 1060, PartyKickoutRs = PartyBase + 1061, PartyKickoutNt = PartyBase + 1062,
        PartyAbnormalRemoveNt = PartyBase + 1063, PartyUpdateMemberHpMpNt = PartyBase + 1064, PartyUpdateMemberStatusNt = PartyBase + 1065,
        PartyDisbandRq = PartyBase + 1066, PartyDisbandRs = PartyBase + 1067, PartyDisbandNt = PartyBase + 1068,
        PartySetTargetByBossNt = PartyBase + 1069, PartyDisconnectMemberNt = PartyBase + 1070, PartyReconnectMemberNt = PartyBase + 1071,
        PartyMemberItemLootingNt = PartyBase + 1072, PartyMemberItemLootingFailNt = PartyBase + 1073,
        ForceMemberItemLootingNt = 0x3aae, ForceMemberItemLootingFailNt = 0x3aaf,
        ForceCreateRq = 0x3a8a, ForceCreateRs = 0x3a8b, ForceJoinRq = 0x3abd, ForceJoinRs = 0x3abe,
        PartyRecommendRq = PartyBase + 1077, PartyRecommendRs = PartyBase + 1078,
        PartyInviteCancelRq = PartyBase + 1079, PartyInviteCancelRs = PartyBase + 1080, PartyInviteCancelNt = PartyBase + 1081;

    /// <summary>PartyLeave_NT (catalogue 1105) in the late run: 0x9327; alternative 0x9244.</summary>
    public const ushort PartyLeaveNt = LateBase + 1105;

    // Party recent members, catalogue 1482/1483 (0xe3 run).
    public const ushort PartyRecentMemberRq = E3Base + 1482, PartyRecentMemberRs = E3Base + 1483;

    // Duel, catalogue 1126..1140, late run (CANDIDATE 0x933c..0x934a; alternative 0x9259..0x9267).
    public const ushort DuelCallOutRq = LateBase + 1126, DuelCallOutRs = LateBase + 1127, DuelCallOutNt = LateBase + 1128,
        DuelCallOutAcceptRq = LateBase + 1129, DuelCallOutAcceptRs = LateBase + 1130, DuelCallOutAcceptNt = LateBase + 1131,
        DuelCallOutRejectRq = LateBase + 1132, DuelCallOutRejectRs = LateBase + 1133, DuelCallOutRejectNt = LateBase + 1134,
        DuelStateUpdateNt = LateBase + 1135, DuelCompleteNt = LateBase + 1136, DuelResultNt = LateBase + 1137,
        DuelRingOutNt = LateBase + 1138, DuelRingInNt = LateBase + 1139, DuelCancelNt = LateBase + 1140;

    /// <summary>The same catalogue entry under the alternative late base (0x92 run continued): 0x933c -> 0x9259.</summary>
    public static ushort Alternative(ushort lateOpcode) => (ushort)(lateOpcode - (LateBase - LateAlternativeBase));

    /// <summary>Every opcode of these families a client sends (for dispatch and logging).</summary>
    public static readonly IReadOnlySet<ushort> ClientRequests = new HashSet<ushort>
    {
        ExchangeRequestRq, ExchangeAcceptRq, ExchangeRefuseRq, ExchangeCancelRq, ExchangeItemListModifyRq, ExchangeItemListLockRq, ExchangeConfirmRq,
        FriendRequestRq, FriendRequestCancelRq, FriendRequestRejectRq, FriendRequestAcceptRq, FriendDeleteRq, FriendBlockRq, FriendBlockCancelRq, FriendMemoUpdateRq,
        PartyInfoUpdateRq, PartyCreateRq, PartyInviteRq, PartyInviteResponseRq, PartySetLeaderRq, PartyLeaveRq, SetPartyFocusTargetRq,
        PartyKickoutRq, PartyDisbandRq, PartyRecommendRq, PartyInviteCancelRq, PartyRecentMemberRq,
        DuelCallOutRq, DuelCallOutAcceptRq, DuelCallOutRejectRq,
    };
}

/// <summary>EFriendRelationType.</summary>
public enum FriendRelation : byte { None = 0, Send = 1, Recv = 2, Friend = 3 }

/// <summary>EPartyInfoUpdateReason.</summary>
public enum PartyUpdateReason : byte
{
    Create = 0, Join = 1, AddMember = 2, LeaveMember = 3, KickMember = 4, Reconnect = 5, ChangeForceMemberParty = 6,
    CommunityServerDisconnect = 7, DungeonExpired = 8, ForceJoin = 9, LeaveMemberBySystem = 10, PartyToForce = 11, KickVote = 12,
}

/// <summary>EPartyInviteResponse.</summary>
public enum PartyInviteAnswer : byte { Accept = 0, Refusal = 1, RefusalByOption = 2, RefusalAlreadyInParty = 3, Expired = 4 }

/// <summary>EPartyDistributionType / EPartyJoinType / ESpecialDistributionType values used as defaults.</summary>
public static class PartyOptions
{
    public const byte DistributionManual = 1, DistributionRandom = 2, DistributionSequence = 3, DistributionLeader = 4;
    public const byte JoinFree = 1, JoinApproval = 2;
    public const byte SpecialNormal = 1, SpecialBid = 2, SpecialDice = 3;
}

/// <summary>EItemModificationType for the exchange item list.</summary>
public enum ExchangeModification : byte { Add = 0, Update = 1, Remove = 2 }

/// <summary>EDuelState.</summary>
public enum DuelState : byte { None = 0, InWaiting = 1, InCountDown = 2, InBattle = 3, InComplete = 4, Expired = 5 }

/// <summary>EDuelCompleteReason.</summary>
public enum DuelCompleteReason : byte { None = 0, Draw = 1, Die = 2, RingOut = 3, MapOut = 4, TimeOut = 5 }

/// <summary>DevPacketData_Common_FriendInfo. Race ERace (1 Light, 2 Dark); Class ECharacterClass (2 Gladiator .. 9 Chanter).</summary>
public sealed record FriendInfo(int ServerId, long CharDbId, string Nickname, byte Race = 1, byte Class = 1, int Level = 1,
    int ConquerorLevel = 0, FriendRelation Relation = FriendRelation.Friend, bool Logined = false, long LastLogoutTime = 0, string Memo = "");

/// <summary>DevPacketData_Common_FriendBlockInfo.</summary>
public sealed record FriendBlockInfo(int ServerId, long CharDbId, string Nickname);

/// <summary>DevPacketData_Common_FriendUpdateInfo (online state, level, class changes).</summary>
public sealed record FriendUpdateInfo(int ServerId, long CharDbId, string Nickname, byte Class = 1, int Level = 1, int ConquerorLevel = 0,
    bool Logined = false, long LastLogoutTime = 0);

/// <summary>DevPacketData_Common_InvitationInfo: who invites (party), requests a trade or challenges to a duel.</summary>
public sealed record InvitationInfo(int PcId, int Level, int EquipItemLevel, string Nickname, int ServerId, int ConquerorLevel = 0,
    long CombatPower = 0, byte MentoringRole = 0);

/// <summary>DevPacketData_Common_PartyRebirthSkillInfo.</summary>
public sealed record PartyRebirthSkill(int SkillId, byte SkillLevel, int Cooltime);

/// <summary>DevPacketData_Common_PartyMemberInfo (usmap order). Number is the member slot (0-based CANDIDATE).</summary>
public sealed record PartyMember
{
    public byte Number { get; init; }
    public int Key { get; init; }
    public int BornServerId { get; init; } = 1204;
    public int CurrentServerId { get; init; } = 1204;
    public string GameAccountId { get; init; } = "";
    public long DbId { get; init; }
    public string Nickname { get; init; } = "";
    public int PcId { get; init; } = 1;
    public long Hp { get; init; } = 100;
    public long HpMax { get; init; } = 100;
    public int Mp { get; init; } = 100;
    public int MpMax { get; init; } = 100;
    public int Ap { get; init; }
    public int ApMax { get; init; }
    public int Dp { get; init; }
    public int DpMax { get; init; }
    /// <summary>_map_key (i64): the map instance; the map id is our CANDIDATE value.</summary>
    public long MapKey { get; init; }
    public int SubzoneId { get; init; }
    public Vector3d Position { get; init; }
    public bool Live { get; init; } = true;
    public string GuildName { get; init; } = "";
    public int GuildEmblemId { get; init; }
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public bool CanTakeDropItem { get; init; } = true;
    public bool Login { get; init; } = true;
    public IReadOnlyList<PartyRebirthSkill> RebirthSkills { get; init; } = [];
    public long CombatPower { get; init; }
    /// <summary>ENetworkLatencyState: 1 Fast .. 5 NoSignal.</summary>
    public byte NetworkLatency { get; init; } = 1;
}

/// <summary>DevPacketData_Common_PartyInfo. Markers, scan and map-pin lists are always sent empty.</summary>
public sealed record PartyInfo
{
    public long Key { get; init; }
    public long LeaderDbId { get; init; }
    /// <summary>_limit_member (AION2 party size; 4 is our CANDIDATE default).</summary>
    public int LimitMember { get; init; } = 4;
    public byte Distribution { get; init; } = PartyOptions.DistributionSequence;
    public byte SpecialDistribution { get; init; } = PartyOptions.SpecialNormal;
    /// <summary>EItemGrade threshold for the special distribution (2 Rare).</summary>
    public byte SpecialDistributionGrade { get; init; } = 2;
    public byte JoinType { get; init; } = PartyOptions.JoinFree;
    public int FocusTargetKey { get; init; }
    public int FocusPartsId { get; init; }
    public int FocusTargetType { get; init; }
    public IReadOnlyList<PartyMember> Members { get; init; } = [];
}

/// <summary>PartyUpdateMemberHPMP_NT fields.</summary>
public sealed record PartyMemberPoints(int Key, long Hp, long HpMax, int Mp, int MpMax, int Ap = 0, int ApMax = 0, int Dp = 0, int DpMax = 0, bool Live = true);

/// <summary>PartyUpdateMemberStatus_NT fields (position, map, level...).</summary>
public sealed record PartyMemberStatus
{
    public long DbId { get; init; }
    public int PcId { get; init; } = 1;
    public long MapKey { get; init; }
    public int SubzoneId { get; init; }
    public Vector3d Position { get; init; }
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public string GuildName { get; init; } = "";
    public int GuildEmblemId { get; init; }
    public bool CanTakeDropItem { get; init; } = true;
    public long CombatPower { get; init; }
    public byte NetworkLatency { get; init; } = 1;
}

/// <summary>DevPacketData_Common_PartyRecentMemberInfo.</summary>
public sealed record PartyRecentMember(long CharDbId, long UpdateTime, int ServerId, string Nickname, byte Class = 1, int Level = 1,
    int ConquerorLevel = 0, bool Logined = false);

/// <summary>DevPacketData_Common_PartyRecommendCharInfo (CANDIDATE layout from usmap).</summary>
public sealed record PartyRecommendCandidate(long DbId, int BornServerId, int CurrentServerId, string Nickname, int PcId = 1,
    string GuildName = "", int GuildEmblemId = 0, int Level = 1, int ConquerorLevel = 0, bool Logined = true);

/// <summary>DevPacketData_Common_ExchangeItem: an item of the own offer (item uid, count).</summary>
public sealed record ExchangeItem(long ItemKey, long Count);

/// <summary>DevPacketData_Common_ExchangeItemDetail: an item of the peer's offer as shown to the other side.</summary>
public sealed record ExchangeItemDetail(long ItemKey, int ItemId, long Count, string Maker = "", EquipDetail? Equip = null);

/// <summary>DuelStateUpdate_NT fields.</summary>
public sealed record DuelStatus(int RingKey, int RoomKey, DuelState State, int ChallengerKey, string ChallengerNickname,
    int AccepterKey, string AccepterNickname, long Now);

/// <summary>Server messages of the social families (see the file header for widths and evidence).</summary>
public static class SocialPackets
{
    // ---------- width helpers (one place per CANDIDATE width) ----------
    internal static PacketWriter Result(this PacketWriter w, int result) => w.U16(result);
    internal static PacketWriter ServerId(this PacketWriter w, int serverId) => w.U16(serverId);
    internal static PacketWriter Key(this PacketWriter w, int key) => w.I32(key);
    internal static PacketWriter Hp(this PacketWriter w, long hp) => w.Varint(Math.Clamp(hp, 0, uint.MaxValue));

    static PacketWriter Op(ushort opcode) => PacketWriter.Opcode(opcode);

    /// <summary>Replace the opcode of a built message (e.g. to try <see cref="SocialOpcodes.Alternative"/>).</summary>
    public static byte[] WithOpcode(byte[] message, ushort opcode)
    {
        var copy = (byte[])message.Clone();
        copy[0] = (byte)opcode;
        copy[1] = (byte)(opcode >> 8);
        return copy;
    }

    static void WriteFriend(PacketWriter w, FriendInfo f) =>
        w.ServerId(f.ServerId).I64(f.CharDbId).String(f.Nickname).U8(f.Race).U8(f.Class).I32(f.Level).I32(f.ConquerorLevel)
            .U8((byte)f.Relation).Bool(f.Logined).I64(f.LastLogoutTime).String(f.Memo);

    static void WriteBlock(PacketWriter w, FriendBlockInfo b) => w.ServerId(b.ServerId).I64(b.CharDbId).String(b.Nickname);

    static void WriteInvitation(PacketWriter w, InvitationInfo i) =>
        w.I32(i.PcId).I32(i.Level).I32(i.EquipItemLevel).String(i.Nickname).ServerId(i.ServerId).I32(i.ConquerorLevel).I64(i.CombatPower).U8(i.MentoringRole);

    static void WriteMember(PacketWriter w, PartyMember m)
    {
        w.U8(m.Number).Key(m.Key).ServerId(m.BornServerId).ServerId(m.CurrentServerId).String(m.GameAccountId).I64(m.DbId).String(m.Nickname)
            .I32(m.PcId).Hp(m.Hp).Hp(m.HpMax).I32(m.Mp).I32(m.MpMax).I32(m.Ap).I32(m.ApMax).I32(m.Dp).I32(m.DpMax)
            .I64(m.MapKey).I32(m.SubzoneId).Vec3(m.Position).Bool(m.Live).String(m.GuildName).I32(m.GuildEmblemId)
            .I32(m.Level).I32(m.ConquerorLevel).I32(m.EquipItemLevel).Bool(m.CanTakeDropItem).Bool(m.Login)
            .Varint(m.RebirthSkills.Count);
        foreach (var s in m.RebirthSkills) w.I32(s.SkillId).U8(s.SkillLevel).I32(s.Cooltime);
        w.I64(m.CombatPower).U8(m.NetworkLatency);
    }

    /// <summary>Common_EquipItemDetail in the layout of the observed Common_Item detail (GamePackets.WriteItem).</summary>
    static void WriteEquipDetail(PacketWriter w, EquipDetail d) =>
        w.U8(d.EnchantLevel).I32(0).I32(d.EnchantBonusProb).U8(d.SurpassEnchantLevel).I32(d.SurpassEnchantBonusProb).I32(d.ItemLevel).Bool(false).U8(d.EquipSlot)
            .Varint(1).Zeros(7).I64(d.SoulBindOwner).U8(d.SoulBindCount)
            .Varint(3).Zeros(12)
            .Zeros(7).U8(0);

    // ---------- Friends ----------

    /// <summary>FriendList_NT (0xe274): friends (all relation types: friends, sent and received requests), then blocks. OBSERVED empty: 0000.</summary>
    public static byte[] FriendList(IReadOnlyList<FriendInfo> friends, IReadOnlyList<FriendBlockInfo> blocks)
    {
        var w = Op(SocialOpcodes.FriendListNt).Varint(friends.Count);
        foreach (var f in friends) WriteFriend(w, f);
        w.Varint(blocks.Count);
        foreach (var b in blocks) WriteBlock(w, b);
        return w.ToArray();
    }

    /// <summary>FriendRequest_RS (0xe276): result, target server id, target name (echo of the request).</summary>
    public static byte[] FriendRequestReply(int result, int serverId, string nickname) =>
        Op(SocialOpcodes.FriendRequestRs).Result(result).ServerId(serverId).String(nickname).ToArray();

    /// <summary>FriendRequest_NT (0xe277): a friend entry to add (to the target: relation Recv; to the requester: Send).</summary>
    public static byte[] FriendRequestNotice(FriendInfo info)
    {
        var w = Op(SocialOpcodes.FriendRequestNt);
        WriteFriend(w, info);
        return w.ToArray();
    }

    /// <summary>FriendRequestCancel_RS (0xe279).</summary>
    public static byte[] FriendRequestCancelReply(int result, long friendDbId) => Op(SocialOpcodes.FriendRequestCancelRs).Result(result).I64(friendDbId).ToArray();
    /// <summary>FriendRequestReject_RS (0xe27b).</summary>
    public static byte[] FriendRequestRejectReply(int result, long friendDbId) => Op(SocialOpcodes.FriendRequestRejectRs).Result(result).I64(friendDbId).ToArray();
    /// <summary>FriendRequestAccept_RS (0xe27d).</summary>
    public static byte[] FriendRequestAcceptReply(int result, long friendDbId) => Op(SocialOpcodes.FriendRequestAcceptRs).Result(result).I64(friendDbId).ToArray();
    /// <summary>FriendRequestAccept_NT (0xe27e): to the original requester, the accepter's dbid.</summary>
    public static byte[] FriendRequestAcceptNotice(long friendDbId) => Op(SocialOpcodes.FriendRequestAcceptNt).I64(friendDbId).ToArray();
    /// <summary>FriendDelete_RS (0xe280).</summary>
    public static byte[] FriendDeleteReply(int result, long friendDbId) => Op(SocialOpcodes.FriendDeleteRs).Result(result).I64(friendDbId).ToArray();
    /// <summary>FriendInfoDelete_NT (0xe281): remove an entry (deleted friend, cancelled/rejected request).</summary>
    public static byte[] FriendInfoDelete(long friendDbId) => Op(SocialOpcodes.FriendInfoDeleteNt).I64(friendDbId).ToArray();

    /// <summary>FriendInfoUpdate_NT (0xe282): online state / level / class of an entry.</summary>
    public static byte[] FriendInfoUpdate(FriendUpdateInfo u) =>
        Op(SocialOpcodes.FriendInfoUpdateNt).ServerId(u.ServerId).I64(u.CharDbId).String(u.Nickname).U8(u.Class).I32(u.Level).I32(u.ConquerorLevel)
            .Bool(u.Logined).I64(u.LastLogoutTime).ToArray();

    /// <summary>FriendBlock_RS (0xe284): result, server id, blocked dbid, name.</summary>
    public static byte[] FriendBlockReply(int result, int serverId, long targetDbId, string nickname) =>
        Op(SocialOpcodes.FriendBlockRs).Result(result).ServerId(serverId).I64(targetDbId).String(nickname).ToArray();

    /// <summary>FriendBlockCancel_RS (0xe286).</summary>
    public static byte[] FriendBlockCancelReply(int result, FriendBlockInfo info)
    {
        var w = Op(SocialOpcodes.FriendBlockCancelRs).Result(result);
        WriteBlock(w, info);
        return w.ToArray();
    }

    /// <summary>FriendMemoUpdate_RS (0xe288).</summary>
    public static byte[] FriendMemoReply(int result, long friendDbId, string memo) =>
        Op(SocialOpcodes.FriendMemoUpdateRs).Result(result).I64(friendDbId).String(memo).ToArray();

    // ---------- Party ----------

    /// <summary>PartyInfo_NT (0x9200): the whole party and why it is sent (Create, Join, AddMember, ...).</summary>
    public static byte[] PartyInfo(PartyInfo p, PartyUpdateReason reason)
    {
        var w = Op(SocialOpcodes.PartyInfoNt).I64(p.Key).I64(p.LeaderDbId).I32(p.LimitMember)
            .U8(p.Distribution).U8(p.SpecialDistribution).U8(p.SpecialDistributionGrade).U8(p.JoinType)
            .Key(p.FocusTargetKey).I32(p.FocusPartsId).I32(p.FocusTargetType)
            .Varint(0).Varint(0).Varint(0)       // target markers, world markers, leader scan list
            .Varint(p.Members.Count);
        foreach (var m in p.Members) WriteMember(w, m);
        return w.Varint(0).U8((byte)reason).ToArray();   // map pins, reason
    }

    /// <summary>PartyInfoUpdate_RS (0x9202).</summary>
    public static byte[] PartyInfoUpdateReply(int result) => Op(SocialOpcodes.PartyInfoUpdateRs).Result(result).ToArray();

    /// <summary>PartyInfoUpdate_NT (0x9203): new loot settings to every member.</summary>
    public static byte[] PartyInfoUpdateNotice(int limitMember, byte distribution, byte specialDistribution, byte specialGrade) =>
        Op(SocialOpcodes.PartyInfoUpdateNt).I32(limitMember).U8(distribution).U8(specialDistribution).U8(specialGrade).ToArray();

    /// <summary>PartyCreate_RS (0x9205).</summary>
    public static byte[] PartyCreateReply(int result) => Op(SocialOpcodes.PartyCreateRs).Result(result).ToArray();

    /// <summary>PartyInvite_RS (0x9207): to the inviter.</summary>
    public static byte[] PartyInviteReply(int result, int serverId, string targetName) =>
        Op(SocialOpcodes.PartyInviteRs).Result(result).ServerId(serverId).String(targetName).ToArray();

    /// <summary>PartyInvite_NT (0x9209): to the invitee; answered with PartyInviteResponse_RQ naming the party key.</summary>
    public static byte[] PartyInviteNotice(long partyKey, long inviterDbId, InvitationInfo inviter, long expireTime)
    {
        var w = Op(SocialOpcodes.PartyInviteNt).I64(partyKey).I64(inviterDbId);
        WriteInvitation(w, inviter);
        return w.I64(expireTime).ToArray();
    }

    /// <summary>PartyInviteResponse_RS (0x920b): to the invitee.</summary>
    public static byte[] PartyInviteResponseReply(int result) => Op(SocialOpcodes.PartyInviteResponseRs).Result(result).ToArray();

    /// <summary>PartyInviteResponse_NT (0x920c): to the inviter, the invitee's answer.</summary>
    public static byte[] PartyInviteResponseNotice(long applicantDbId, int applicantServerId, string name, PartyInviteAnswer response) =>
        Op(SocialOpcodes.PartyInviteResponseNt).I64(applicantDbId).ServerId(applicantServerId).String(name).U8((byte)response).ToArray();

    /// <summary>PartyAddMember_NT (0x920d): to the existing members.</summary>
    public static byte[] PartyAddMember(PartyMember member)
    {
        var w = Op(SocialOpcodes.PartyAddMemberNt);
        WriteMember(w, member);
        return w.ToArray();
    }

    /// <summary>PartyAbnormalAdd_NT (0x920e): member key and abnormal ids.</summary>
    public static byte[] PartyAbnormalAdd(int key, IReadOnlyList<int> abnormalIds)
    {
        var w = Op(SocialOpcodes.PartyAbnormalAddNt).Key(key).Varint(abnormalIds.Count);
        foreach (var id in abnormalIds) w.I32(id);
        return w.ToArray();
    }

    /// <summary>PartyAbnormalRemove_NT (0x921a).</summary>
    public static byte[] PartyAbnormalRemove(int key, int abnormalId) => Op(SocialOpcodes.PartyAbnormalRemoveNt).Key(key).I32(abnormalId).ToArray();

    /// <summary>PartySetLeader_RS (0x9210).</summary>
    public static byte[] PartySetLeaderReply(int result) => Op(SocialOpcodes.PartySetLeaderRs).Result(result).ToArray();
    /// <summary>PartyNewLeader_NT (0x9211): to every member.</summary>
    public static byte[] PartyNewLeader(long leaderDbId) => Op(SocialOpcodes.PartyNewLeaderNt).I64(leaderDbId).ToArray();
    /// <summary>PartyLeave_RS (0x9213).</summary>
    public static byte[] PartyLeaveReply(int result) => Op(SocialOpcodes.PartyLeaveRs).Result(result).ToArray();
    /// <summary>PartyKickout_RS (0x9218): to the leader.</summary>
    public static byte[] PartyKickoutReply(int result) => Op(SocialOpcodes.PartyKickoutRs).Result(result).ToArray();
    /// <summary>PartyKickout_NT (0x9219): to the kicked member (by vote or by the leader).</summary>
    public static byte[] PartyKickout(bool byVote) => Op(SocialOpcodes.PartyKickoutNt).Bool(byVote).ToArray();

    /// <summary>PartyUpdateMemberHPMP_NT (0x921b).</summary>
    public static byte[] PartyMemberHpMp(PartyMemberPoints p) =>
        Op(SocialOpcodes.PartyUpdateMemberHpMpNt).Key(p.Key).Hp(p.Hp).Hp(p.HpMax).I32(p.Mp).I32(p.MpMax).I32(p.Ap).I32(p.ApMax)
            .I32(p.Dp).I32(p.DpMax).Bool(p.Live).ToArray();

    /// <summary>PartyUpdateMemberStatus_NT (0x921c): map, position, level of a member.</summary>
    public static byte[] PartyMemberStatus(PartyMemberStatus s) =>
        Op(SocialOpcodes.PartyUpdateMemberStatusNt).I64(s.DbId).I32(s.PcId).I64(s.MapKey).I32(s.SubzoneId).Vec3(s.Position)
            .I32(s.Level).I32(s.ConquerorLevel).I32(s.EquipItemLevel).String(s.GuildName).I32(s.GuildEmblemId)
            .Bool(s.CanTakeDropItem).I64(s.CombatPower).U8(s.NetworkLatency).ToArray();

    /// <summary>PartyMemberItemLooting_NT (0x9228): a member received a distributed item.</summary>
    public static byte[] PartyMemberItemLooting(string nickname, int serverId, int dropItemId) =>
        Op(SocialOpcodes.PartyMemberItemLootingNt).String(nickname).I32(serverId).I32(dropItemId).ToArray();

    /// <summary>ForceMemberItemLooting_NT (0x3AAE): alliance member received distributed loot.</summary>
    public static byte[] ForceMemberItemLooting(string nickname, int serverId, int dropItemId) =>
        Op(SocialOpcodes.ForceMemberItemLootingNt).String(nickname).I32(serverId).I32(dropItemId).ToArray();

    /// <summary>ForceCreate_RS (0x3A8B): result, force key.</summary>
    public static byte[] ForceCreateReply(int result, long forceKey) =>
        Op(SocialOpcodes.ForceCreateRs).U16(result).I64(forceKey).ToArray();

    /// <summary>ForceJoin_RS (0x3ABE).</summary>
    public static byte[] ForceJoinReply(int result) => Op(SocialOpcodes.ForceJoinRs).U16(result).ToArray();

    /// <summary>PartyDisband_RS (0x921e).</summary>
    public static byte[] PartyDisbandReply(int result) => Op(SocialOpcodes.PartyDisbandRs).Result(result).ToArray();
    /// <summary>PartyDisband_NT (0x921f): to every member when the party ends.</summary>
    public static byte[] PartyDisband(PartyUpdateReason reason) => Op(SocialOpcodes.PartyDisbandNt).U8((byte)reason).ToArray();
    /// <summary>PartyDisconnectMember_NT (0x9221): a member went offline (stays in the party).</summary>
    public static byte[] PartyDisconnectMember(long memberDbId) => Op(SocialOpcodes.PartyDisconnectMemberNt).I64(memberDbId).ToArray();

    /// <summary>PartyReconnectMember_NT (0x9222): a member came back online.</summary>
    public static byte[] PartyReconnectMember(PartyMember member)
    {
        var w = Op(SocialOpcodes.PartyReconnectMemberNt);
        WriteMember(w, member);
        return w.ToArray();
    }

    /// <summary>PartyInviteCancel_RS (0x922b): to the inviter.</summary>
    public static byte[] PartyInviteCancelReply(int result, string targetNickname, int targetServerId) =>
        Op(SocialOpcodes.PartyInviteCancelRs).Result(result).String(targetNickname).ServerId(targetServerId).ToArray();
    /// <summary>PartyInviteCancel_NT (0x922c): to the invitee, the withdrawn party key.</summary>
    public static byte[] PartyInviteCancelNotice(long partyKey) => Op(SocialOpcodes.PartyInviteCancelNt).I64(partyKey).ToArray();

    /// <summary>PartyLeave_NT (late run 0x9327, alternative 0x9244; CANDIDATE opcode): to the member who left / was removed.</summary>
    public static byte[] PartyLeaveNotice(long partyKey, PartyUpdateReason reason) => Op(SocialOpcodes.PartyLeaveNt).I64(partyKey).U8((byte)reason).ToArray();

    /// <summary>PartyRecommend_RS (0x9228): result, recommend type, candidates.</summary>
    public static byte[] PartyRecommendReply(int result, int recommendType, IReadOnlyList<PartyRecommendCandidate> candidates)
    {
        var w = Op(SocialOpcodes.PartyRecommendRs).U16(result).I32(recommendType).Varint(candidates.Count);
        foreach (var c in candidates)
            w.I64(c.DbId).ServerId(c.BornServerId).ServerId(c.CurrentServerId).String(c.Nickname).I32(c.PcId)
                .String(c.GuildName).I32(c.GuildEmblemId).I32(c.Level).I32(c.ConquerorLevel).Bool(c.Logined);
        return w.ToArray();
    }

    /// <summary>PartyRecentMember_RS (0xe339): result and recent party members.</summary>
    public static byte[] PartyRecentMembers(int result, IReadOnlyList<PartyRecentMember> members)
    {
        var w = Op(SocialOpcodes.PartyRecentMemberRs).Result(result).Varint(members.Count);
        foreach (var m in members)
            w.I64(m.CharDbId).I64(m.UpdateTime).ServerId(m.ServerId).String(m.Nickname).U8(m.Class).I32(m.Level).I32(m.ConquerorLevel).Bool(m.Logined);
        return w.ToArray();
    }

    // ---------- Exchange (player-to-player trade) ----------

    /// <summary>ExchangeRequest_RS (0x565a): to the requester, with the new exchange id.</summary>
    public static byte[] ExchangeRequestReply(int result, int targetKey, long exchangeId) =>
        Op(SocialOpcodes.ExchangeRequestRs).Result(result).Key(targetKey).I64(exchangeId).ToArray();

    /// <summary>ExchangeRequested_NT (0x565b): to the target.</summary>
    public static byte[] ExchangeRequested(int inviterKey, InvitationInfo inviter, long exchangeId, long expireTime)
    {
        var w = Op(SocialOpcodes.ExchangeRequestedNt).Key(inviterKey);
        WriteInvitation(w, inviter);
        return w.I64(exchangeId).I64(expireTime).ToArray();
    }

    /// <summary>ExchangeAccept_RS (0x565d): to the target who accepted.</summary>
    public static byte[] ExchangeAcceptReply(int result, long exchangeId) => Op(SocialOpcodes.ExchangeAcceptRs).Result(result).I64(exchangeId).ToArray();
    /// <summary>ExchangeAccepted_NT (0x565e): to the requester (the trade window opens).</summary>
    public static byte[] ExchangeAccepted(long exchangeId) => Op(SocialOpcodes.ExchangeAcceptedNt).I64(exchangeId).ToArray();
    /// <summary>ExchangeRefuse_RS (0x5660).</summary>
    public static byte[] ExchangeRefuseReply(int result, long exchangeId) => Op(SocialOpcodes.ExchangeRefuseRs).Result(result).I64(exchangeId).ToArray();
    /// <summary>ExchangeCancel_RS (0x5662).</summary>
    public static byte[] ExchangeCancelReply(int result, long exchangeId) => Op(SocialOpcodes.ExchangeCancelRs).Result(result).I64(exchangeId).ToArray();
    /// <summary>ExchangeCanceled_NT (0x5663): to the other side (refused, cancelled, moved away); reason code ours.</summary>
    public static byte[] ExchangeCanceled(long exchangeId, int reason) => Op(SocialOpcodes.ExchangeCanceledNt).I64(exchangeId).I32(reason).ToArray();

    /// <summary>ExchangeItemListModify_RS (0x5665): echo of the own change.</summary>
    public static byte[] ExchangeItemListModifyReply(int result, long exchangeId, ExchangeModification type, ExchangeItem item, int gameCash) =>
        Op(SocialOpcodes.ExchangeItemListModifyRs).Result(result).I64(exchangeId).U8((byte)type).I64(item.ItemKey).I64(item.Count).I32(gameCash).ToArray();

    /// <summary>ExchangePeerItemListModified_NT (0x5666): the peer's change, with item details. Detail presence byte (bit 0 equip detail) as in Common_Item (CANDIDATE).</summary>
    public static byte[] ExchangePeerItemListModified(long exchangeId, ExchangeModification type, ExchangeItemDetail item, int gameCash)
    {
        var w = Op(SocialOpcodes.ExchangePeerItemListModifiedNt).I64(exchangeId).U8((byte)type)
            .U8(item.Equip is null ? 0 : 1).I64(item.ItemKey).I32(item.ItemId).I64(item.Count).String(item.Maker);
        if (item.Equip is { } e) WriteEquipDetail(w, e);
        return w.I32(gameCash).ToArray();
    }

    /// <summary>ExchangeItemListLock_RS (0x5668).</summary>
    public static byte[] ExchangeItemListLockReply(int result, long exchangeId, bool locked) =>
        Op(SocialOpcodes.ExchangeItemListLockRs).Result(result).I64(exchangeId).Bool(locked).ToArray();
    /// <summary>ExchangePeerItemListLock_NT (0x5669).</summary>
    public static byte[] ExchangePeerItemListLock(long exchangeId, bool locked) => Op(SocialOpcodes.ExchangePeerItemListLockNt).I64(exchangeId).Bool(locked).ToArray();
    /// <summary>ExchangeConfirm_RS (0x566b).</summary>
    public static byte[] ExchangeConfirmReply(int result, long exchangeId) => Op(SocialOpcodes.ExchangeConfirmRs).Result(result).I64(exchangeId).ToArray();
    /// <summary>ExchangePeerConfirmed_NT (0x566c).</summary>
    public static byte[] ExchangePeerConfirmed(long exchangeId) => Op(SocialOpcodes.ExchangePeerConfirmedNt).I64(exchangeId).ToArray();
    /// <summary>ExchangeCompleted_NT (0x566d): to both, after the items moved (inventory changes go as 0x561b).</summary>
    public static byte[] ExchangeCompleted(long exchangeId) => Op(SocialOpcodes.ExchangeCompletedNt).I64(exchangeId).ToArray();

    /// <summary>PersonalExchangeInfo_NT (0xe33d): trades used (i32). OBSERVED at world entry: 00000000.</summary>
    public static byte[] PersonalExchangeInfo(int useCount) => Op(SocialOpcodes.PersonalExchangeInfoNt).I32(useCount).ToArray();

    // ---------- Duel (opcodes CANDIDATE, see SocialOpcodes) ----------

    /// <summary>DuelCallOut_RS: to the challenger.</summary>
    public static byte[] DuelCallOutReply(int result, int roomKey, long expireTime) =>
        Op(SocialOpcodes.DuelCallOutRs).Result(result).Key(roomKey).I64(expireTime).ToArray();

    /// <summary>DuelCallOut_NT: to the challenged player.</summary>
    public static byte[] DuelCallOut(int roomKey, int challengerKey, InvitationInfo challenger, long expireTime)
    {
        var w = Op(SocialOpcodes.DuelCallOutNt).Key(roomKey).Key(challengerKey);
        WriteInvitation(w, challenger);
        return w.I64(expireTime).ToArray();
    }

    /// <summary>DuelCallOutAccept_RS: to the accepter.</summary>
    public static byte[] DuelAcceptReply(int result, int roomKey, int challengerKey) =>
        Op(SocialOpcodes.DuelCallOutAcceptRs).Result(result).Key(roomKey).Key(challengerKey).ToArray();
    /// <summary>DuelCallOutAccept_NT: to the challenger.</summary>
    public static byte[] DuelAccepted(int roomKey, int accepterKey) => Op(SocialOpcodes.DuelCallOutAcceptNt).Key(roomKey).Key(accepterKey).ToArray();
    /// <summary>DuelCallOutReject_RS: to the rejecter.</summary>
    public static byte[] DuelRejectReply(int result, int roomKey) => Op(SocialOpcodes.DuelCallOutRejectRs).Result(result).Key(roomKey).ToArray();
    /// <summary>DuelCallOutReject_NT: to the challenger.</summary>
    public static byte[] DuelRejected(int roomKey, int accepterKey, string accepterNickname) =>
        Op(SocialOpcodes.DuelCallOutRejectNt).Key(roomKey).Key(accepterKey).String(accepterNickname).ToArray();

    /// <summary>DuelStateUpdate_NT: to both (InCountDown, InBattle, InComplete...).</summary>
    public static byte[] DuelStateUpdate(DuelStatus s) =>
        Op(SocialOpcodes.DuelStateUpdateNt).Key(s.RingKey).Key(s.RoomKey).U8((byte)s.State).Key(s.ChallengerKey).String(s.ChallengerNickname)
            .Key(s.AccepterKey).String(s.AccepterNickname).I64(s.Now).ToArray();

    /// <summary>DuelComplete_NT: broadcast (nearby players see who won).</summary>
    public static byte[] DuelComplete(int roomKey, DuelCompleteReason reason, int winnerKey, string winnerNickname, string loserNickname) =>
        Op(SocialOpcodes.DuelCompleteNt).Key(roomKey).U8((byte)reason).Key(winnerKey).String(winnerNickname).String(loserNickname).ToArray();

    /// <summary>DuelResult_NT: to both duelists.</summary>
    public static byte[] DuelResult(int roomKey, DuelCompleteReason reason, int winnerKey, int loserKey, string winnerNickname, string loserNickname) =>
        Op(SocialOpcodes.DuelResultNt).Key(roomKey).U8((byte)reason).Key(winnerKey).Key(loserKey).String(winnerNickname).String(loserNickname).ToArray();

    /// <summary>DuelRingOut_NT: a duelist left the ring; ring_out_time is when it counts as a loss (CANDIDATE meaning).</summary>
    public static byte[] DuelRingOut(int roomKey, int charKey, long ringOutTime) => Op(SocialOpcodes.DuelRingOutNt).Key(roomKey).Key(charKey).I64(ringOutTime).ToArray();
    /// <summary>DuelRingIn_NT: back inside the ring.</summary>
    public static byte[] DuelRingIn(int roomKey, int charKey) => Op(SocialOpcodes.DuelRingInNt).Key(roomKey).Key(charKey).ToArray();
    /// <summary>DuelCancel_NT: the challenge or duel was cancelled.</summary>
    public static byte[] DuelCancel(int roomKey, string challengerNickname, string accepterNickname) =>
        Op(SocialOpcodes.DuelCancelNt).Key(roomKey).String(challengerNickname).String(accepterNickname).ToArray();
}

/// <summary>A request naming a character by server id and name (FriendRequest_RQ, FriendBlock_RQ, PartyInvite_RQ, PartyInviteCancel_RQ).</summary>
public sealed record NamedTarget(int ServerId, string Name);

/// <summary>FriendMemoUpdate_RQ.</summary>
public sealed record FriendMemoRequest(long FriendDbId, string Memo);

/// <summary>PartyCreate_RQ / PartyInfoUpdate_RQ settings (the two requests order them differently on the wire).</summary>
public sealed record PartySettingsRequest(byte Distribution, byte JoinType, byte SpecialDistribution, byte SpecialDistributionGrade);

/// <summary>One entry of PartyInviteResponse_RQ.</summary>
public sealed record PartyInviteReply(long PartyKey, PartyInviteAnswer Response);

/// <summary>ExchangeAccept_RQ / ExchangeRefuse_RQ.</summary>
public sealed record ExchangeAnswer(long ExchangeId, int InviterKey);

/// <summary>ExchangeItemListModify_RQ.</summary>
public sealed record ExchangeModifyRequest(long ExchangeId, ExchangeModification Type, ExchangeItem Item, int GameCash);

/// <summary>ExchangeItemListLock_RQ.</summary>
public sealed record ExchangeLockRequest(long ExchangeId, bool Locked);

/// <summary>DuelCallOutAccept_RQ / DuelCallOutReject_RQ.</summary>
public sealed record DuelAnswer(int RoomKey, int ChallengerKey);

/// <summary>A (key, parts, type) target (SetPartyFocusTarget_RQ).</summary>
public sealed record FocusTargetRequest(int TargetKey, int PartsId, int TargetType, int InnerTargetKey, int InnerPartsId, int InnerTargetType);

/// <summary>
/// Lenient parsers of the client requests. <c>message</c> is the whole plain message including
/// the 2-byte opcode (the opcode itself is not checked, so a caller may route either candidate
/// opcode here). Every parser returns null unless the body is consumed exactly. Client layouts
/// are unobserved: the usmap layout is tried with our server widths first, then with the other
/// plausible widths (ints as i32 or varint, server id u16 or i32, ids i64 or varint).
/// The Encode* helpers build the primary layout (tests and scripted clients).
/// </summary>
public static class SocialRequests
{
    delegate T Read<T>(PacketReader r);

    static T? Try<T>(ReadOnlyMemory<byte> message, Read<T> read) where T : class
    {
        if (message.Length < 2) return null;
        try
        {
            var r = new PacketReader(message) { Offset = 2 };
            var value = read(r);
            return r.End ? value : null;
        }
        catch (Exception e) when (e is ProtocolException or ArgumentException or OverflowException) { return null; }
    }

    static T? First<T>(ReadOnlyMemory<byte> message, params Read<T>[] layouts) where T : class
    {
        foreach (var layout in layouts)
            if (Try(message, layout) is { } value) return value;
        return null;
    }

    sealed record Box<T>(T Value) where T : struct;
    static T? FirstValue<T>(ReadOnlyMemory<byte> message, params Read<T>[] layouts) where T : struct
    {
        foreach (var layout in layouts)
            if (Try(message, r => new Box<T>(layout(r))) is { } box) return box.Value;
        return null;
    }

    static int I32(PacketReader r) => r.I32();
    static int Var(PacketReader r) => checked((int)r.Varint());
    static int ServerU16(PacketReader r) => r.U16();
    static long Id64(PacketReader r) => r.I64();
    static long IdVar(PacketReader r) => r.Varint();

    static readonly Func<PacketReader, int>[] KeyWidths = [I32, Var];
    static readonly Func<PacketReader, int>[] ServerWidths = [ServerU16, I32];
    static readonly Func<PacketReader, long>[] IdWidths = [Id64, IdVar];

    static T? Each<T>(ReadOnlyMemory<byte> message, IEnumerable<Read<T>> layouts) where T : class => First(message, [.. layouts]);

    /// <summary>True for a request with no fields (PartyLeave_RQ, PartyDisband_RQ, PartyRecentMember_RQ).</summary>
    public static bool IsEmpty(ReadOnlyMemory<byte> message) => message.Length == 2;

    // ---------- Friends ----------

    /// <summary>FriendRequest_RQ / FriendBlock_RQ {server_id, nickname}.</summary>
    public static NamedTarget? ParseServerAndName(ReadOnlyMemory<byte> message) =>
        Each(message, ServerWidths.Select(s => (Read<NamedTarget>)(r => new NamedTarget(s(r), r.String()))));

    /// <summary>FriendRequestCancel/Reject/Accept_RQ, FriendDelete_RQ, PartySetLeader_RQ, PartyKickout_RQ, PartyJoin_RQ {i64}.</summary>
    public static long? ParseDbId(ReadOnlyMemory<byte> message) => FirstValue(message, [.. IdWidths.Select(f => (Read<long>)(r => f(r)))]);

    /// <summary>FriendBlockCancel_RQ {FriendBlockInfo}.</summary>
    public static FriendBlockInfo? ParseFriendBlockCancel(ReadOnlyMemory<byte> message) =>
        Each(message, from s in ServerWidths from id in IdWidths select (Read<FriendBlockInfo>)(r => new FriendBlockInfo(s(r), id(r), r.String())));

    /// <summary>FriendMemoUpdate_RQ {dbid, memo}.</summary>
    public static FriendMemoRequest? ParseFriendMemo(ReadOnlyMemory<byte> message) =>
        Each(message, IdWidths.Select(id => (Read<FriendMemoRequest>)(r => new FriendMemoRequest(id(r), r.String()))));

    // ---------- Party ----------

    /// <summary>PartyCreate_RQ {distribution, join type, special distribution, grade} (u8 enums).</summary>
    public static PartySettingsRequest? ParsePartyCreate(ReadOnlyMemory<byte> message) =>
        Try(message, r => new PartySettingsRequest(r.U8(), r.U8(), r.U8(), r.U8()));

    /// <summary>PartyRecommend_RQ: recommend type (i32) and optional candidate list (ignored for search).</summary>
    public static int? ParsePartyRecommendType(ReadOnlyMemory<byte> message) =>
        FirstValue(message, [.. KeyWidths.Select(f => (Read<int>)(r => f(r)))]);

    /// <summary>PartyInfoUpdate_RQ {distribution, special distribution, grade, join type}.</summary>
    public static PartySettingsRequest? ParsePartyInfoUpdate(ReadOnlyMemory<byte> message) =>
        Try(message, r =>
        {
            var distribution = r.U8();
            var special = r.U8();
            var grade = r.U8();
            return new PartySettingsRequest(distribution, r.U8(), special, grade);
        });

    /// <summary>PartyInvite_RQ {server_id, name} (same shape as FriendRequest_RQ).</summary>
    public static NamedTarget? ParsePartyInvite(ReadOnlyMemory<byte> message) => ParseServerAndName(message);

    /// <summary>PartyInviteCancel_RQ {target_nickname, target_server_id} (name first).</summary>
    public static NamedTarget? ParsePartyInviteCancel(ReadOnlyMemory<byte> message) =>
        Each(message, ServerWidths.Select(s => (Read<NamedTarget>)(r => { var name = r.String(); return new NamedTarget(s(r), name); })));

    /// <summary>PartyInviteResponse_RQ {array of {party_key i64, response u8}}.</summary>
    public static IReadOnlyList<PartyInviteReply>? ParsePartyInviteResponse(ReadOnlyMemory<byte> message) =>
        Each<IReadOnlyList<PartyInviteReply>>(message, IdWidths.Select(id => (Read<IReadOnlyList<PartyInviteReply>>)(r =>
        {
            var count = r.Varint();
            if (count > 64) throw new ProtocolException("Too many invite responses");
            var list = new List<PartyInviteReply>((int)count);
            for (var n = 0; n < count; n++) list.Add(new PartyInviteReply(id(r), (PartyInviteAnswer)r.U8()));
            return list;
        })));

    /// <summary>SetPartyFocusTarget_RQ {target_key, parts_id, target_type, {target_key, parts_id, target_type}}.</summary>
    public static FocusTargetRequest? ParseSetPartyFocusTarget(ReadOnlyMemory<byte> message) =>
        Each(message, KeyWidths.Select(k => (Read<FocusTargetRequest>)(r => new FocusTargetRequest(k(r), r.I32(), r.I32(), k(r), r.I32(), r.I32()))));

    // ---------- Exchange ----------

    /// <summary>ExchangeRequest_RQ {target_key}.</summary>
    public static int? ParseExchangeRequest(ReadOnlyMemory<byte> message) => FirstValue(message, [.. KeyWidths.Select(k => (Read<int>)(r => k(r)))]);

    /// <summary>ExchangeAccept_RQ / ExchangeRefuse_RQ {exchange_id, inviter_key}.</summary>
    public static ExchangeAnswer? ParseExchangeAnswer(ReadOnlyMemory<byte> message) =>
        Each(message, from id in IdWidths from k in KeyWidths select (Read<ExchangeAnswer>)(r => new ExchangeAnswer(id(r), k(r))));

    /// <summary>ExchangeCancel_RQ / ExchangeConfirm_RQ {exchange_id}.</summary>
    public static long? ParseExchangeId(ReadOnlyMemory<byte> message) => ParseDbId(message);

    /// <summary>ExchangeItemListModify_RQ {exchange_id, modification u8, {item_key i64, item_count i64}, game_cash}.</summary>
    public static ExchangeModifyRequest? ParseExchangeItemModify(ReadOnlyMemory<byte> message) =>
        Each(message, from id in IdWidths from cash in KeyWidths select (Read<ExchangeModifyRequest>)(r =>
            new ExchangeModifyRequest(id(r), (ExchangeModification)r.U8(), new ExchangeItem(r.I64(), r.I64()), cash(r))));

    /// <summary>ExchangeItemListLock_RQ {exchange_id, locked (packed bool)}.</summary>
    public static ExchangeLockRequest? ParseExchangeLock(ReadOnlyMemory<byte> message) =>
        Each(message, IdWidths.Select(id => (Read<ExchangeLockRequest>)(r => new ExchangeLockRequest(id(r), r.Bool()))));

    // ---------- Duel ----------

    /// <summary>DuelCallOut_RQ {accepter_key}.</summary>
    public static int? ParseDuelCallOut(ReadOnlyMemory<byte> message) => ParseExchangeRequest(message);

    /// <summary>DuelCallOutAccept_RQ / DuelCallOutReject_RQ {room_key, challenger_key}.</summary>
    public static DuelAnswer? ParseDuelAnswer(ReadOnlyMemory<byte> message) =>
        Each(message, KeyWidths.Select(k => (Read<DuelAnswer>)(r => new DuelAnswer(k(r), k(r)))));

    // ---------- Encoders of the primary layouts ----------

    public static byte[] EncodeServerAndName(ushort opcode, int serverId, string name) => PacketWriter.Opcode(opcode).U16(serverId).String(name).ToArray();
    public static byte[] EncodeDbId(ushort opcode, long dbId) => PacketWriter.Opcode(opcode).I64(dbId).ToArray();
    public static byte[] EncodeFriendBlockCancel(FriendBlockInfo b) => PacketWriter.Opcode(SocialOpcodes.FriendBlockCancelRq).U16(b.ServerId).I64(b.CharDbId).String(b.Nickname).ToArray();
    public static byte[] EncodeFriendMemo(long dbId, string memo) => PacketWriter.Opcode(SocialOpcodes.FriendMemoUpdateRq).I64(dbId).String(memo).ToArray();
    public static byte[] EncodePartyCreate(PartySettingsRequest s) =>
        PacketWriter.Opcode(SocialOpcodes.PartyCreateRq).U8(s.Distribution).U8(s.JoinType).U8(s.SpecialDistribution).U8(s.SpecialDistributionGrade).ToArray();
    public static byte[] EncodePartyInfoUpdate(PartySettingsRequest s) =>
        PacketWriter.Opcode(SocialOpcodes.PartyInfoUpdateRq).U8(s.Distribution).U8(s.SpecialDistribution).U8(s.SpecialDistributionGrade).U8(s.JoinType).ToArray();
    public static byte[] EncodePartyInviteCancel(string name, int serverId) => PacketWriter.Opcode(SocialOpcodes.PartyInviteCancelRq).String(name).U16(serverId).ToArray();
    public static byte[] EncodePartyInviteResponse(IReadOnlyList<PartyInviteReply> replies)
    {
        var w = PacketWriter.Opcode(SocialOpcodes.PartyInviteResponseRq).Varint(replies.Count);
        foreach (var reply in replies) w.I64(reply.PartyKey).U8((byte)reply.Response);
        return w.ToArray();
    }
    public static byte[] EncodeSetPartyFocusTarget(FocusTargetRequest f) =>
        PacketWriter.Opcode(SocialOpcodes.SetPartyFocusTargetRq).I32(f.TargetKey).I32(f.PartsId).I32(f.TargetType).I32(f.InnerTargetKey).I32(f.InnerPartsId).I32(f.InnerTargetType).ToArray();
    public static byte[] EncodeKey(ushort opcode, int key) => PacketWriter.Opcode(opcode).I32(key).ToArray();
    public static byte[] EncodeExchangeAnswer(ushort opcode, long exchangeId, int inviterKey) => PacketWriter.Opcode(opcode).I64(exchangeId).I32(inviterKey).ToArray();
    public static byte[] EncodeExchangeItemModify(ExchangeModifyRequest m) =>
        PacketWriter.Opcode(SocialOpcodes.ExchangeItemListModifyRq).I64(m.ExchangeId).U8((byte)m.Type).I64(m.Item.ItemKey).I64(m.Item.Count).I32(m.GameCash).ToArray();
    public static byte[] EncodeExchangeLock(long exchangeId, bool locked) => PacketWriter.Opcode(SocialOpcodes.ExchangeItemListLockRq).I64(exchangeId).Bool(locked).ToArray();
    public static byte[] EncodeDuelAnswer(ushort opcode, int roomKey, int challengerKey) => PacketWriter.Opcode(opcode).I32(roomKey).I32(challengerKey).ToArray();
}
