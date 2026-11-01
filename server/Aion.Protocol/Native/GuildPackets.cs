namespace Aion.Protocol.Native;

/// <summary>Legion (guild) opcodes: catalogue 0x3875+ mapped to wire 0x8a00+ (Global client).</summary>
public static class GuildOpcodes
{
    public const ushort Base = 0x8a00;
    public const ushort JoinRequestedNt = 0x8a12, JoinConfirmRq = 0x8a13, JoinConfirmRs = 0x8a14, JoinConfirmNt = 0x8a15,
        JoinCancelRq = 0x8a16, JoinCancelRs = 0x8a17, JoinCancelNt = 0x8a18,
        RecvJoinRequestsRq = 0x8a19, RecvJoinRequestsRs = 0x8a1a, RecvJoinRequestsNt = 0x8a1b,
        SendJoinRequestsRq = 0x8a1c, SendJoinRequestsRs = 0x8a1d,
        SetNoticeRq = 0x8a27, SetNoticeRs = 0x8a28, SetNoticeNt = 0x8a29,
        SetMemberGradeRq = 0x8a2a, SetMemberGradeRs = 0x8a2b, SetMemberGradeNt = 0x8a2c,
        SetAuthorityRq = 0x8a2d, SetAuthorityRs = 0x8a2e, SetAuthorityNt = 0x8a2f,
        DelegateMasterRq = 0x8a36, DelegateMasterRs = 0x8a37, DelegateMasterNt = 0x8a38,
        LevelNt = 0x8a32, DonationInfoRq = 0x8a3a, DonationInfoRs = 0x8a3b, DonationRq = 0x8a3c, DonationRs = 0x8a3d,
        AttendanceInfoRq = 0x8a3e, AttendanceInfoRs = 0x8a3f, AttendanceRq = 0x8a40, AttendanceRs = 0x8a41,
        AttendanceRewardRq = 0x8a42, AttendanceRewardRs = 0x8a43,
        AgitInfoRq = 0x8a44, AgitInfoRs = 0x8a45, AgitInfoNt = 0x8a46, AgitContractRq = 0x8a47, AgitContractRs = 0x8a48,
        UpdateGuildWalletNt = 0x8a9f, StorageInfoRq = 0x8aa0, StorageInfoRs = 0x8aa1,
        StorageItemModificationListNt = 0x8aa2, StorageSlotExtendedNt = 0x8aa3, StorageReservedSlotUpdatedNt = 0x8aa4,
        StorageHistoryRq = 0x8aa5, StorageHistoryRs = 0x8aa6,
        StorageItemStoreRq = 0x8aa7, StorageItemStoreRs = 0x8aa8, StorageItemTakeOutRq = 0x8aa9, StorageItemTakeOutRs = 0x8aaa,
        StorageItemSortRq = 0x8aab, StorageItemSortRs = 0x8aac, StorageSlotExtendRq = 0x8aad, StorageSlotExtendRs = 0x8aae,
        AgitDecoListNt = 0x8aaf, AgitDecoEquipRq = 0x8ab3, AgitDecoEquipRs = 0x8ab4, AgitDecoUnEquipRq = 0x8ab5, AgitDecoUnEquipRs = 0x8ab6,
        RelationRq = 0x8a65, RelationRs = 0x8a66, RelationNt = 0x8a67, RelationCancelRq = 0x8a68, RelationCancelRs = 0x8a69, RelationCancelNt = 0x8a6a,
        RelationConfirmRq = 0x8a6b, RelationConfirmRs = 0x8a6c,
        UnionCreateRq = 0x8a6d, UnionCreateRs = 0x8a6e, UnionCreateNt = 0x8a6f, UnionDisbandRq = 0x8a70, UnionDisbandRs = 0x8a71, UnionDisbandNt = 0x8a72,
        UnionInfoRq = 0x8a73, UnionInfoRs = 0x8a74, UnionJoinRq = 0x8a78, UnionJoinRs = 0x8a79, UnionJoinNt = 0x8a7a, UnionLeaveRq = 0x8a7c, UnionLeaveRs = 0x8a7d;
    public const int RelationAlly = 0, RelationHostile = 1;
    public const int WalletKinaType = 0;
}
