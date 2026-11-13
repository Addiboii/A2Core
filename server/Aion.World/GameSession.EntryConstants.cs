using Aion.Protocol.Native;

namespace Aion.World;

// The list and state messages of the world entry, in the official order. OBSERVED order: our recordings' 34 world entries
// (2026-10-13 to 11-06, client 2.0.6; the shape of 24, the others lack the level-gated ones): 0x8d53 0x8d58 0x8aaf 0x9330
// 0x610b 0x8d37 0x9331 0xffae [0x5712] [0xe30b] 0xffa5 0xffa7 0xe262 [0x3656] 0x572d, then after the load 0xe251 0x400a 0xe200
// 0xffab 0xe223 0x6000 0x570f; the older constants (capture 20261013-144836) sit between them as the 234912 entry has them.
// The official server sends the part up to 0x572d before PrepareEnterMap 0x3621 and the rest after 0x3623; ours sends the
// whole block after 0x3623 (OURS: our entry works that way; moving it before the load is untested with a client).
// Bodies are the forms of a character with nothing yet (the level-1 and level-9 entries) unless noted. Names from the catalogue
// runs and the usmap; the 0xff.. and 0x9331/0x572d messages have no name in our mapping.
// Not sent: 0x5712 and CharCombatPowerUpdate_NT 0x3656 (only at level 23+, their values unknown).
public sealed partial class GameSession
{
    static byte[] Observed(ushort opcode, string hex) => [.. PacketWriter.Opcode(opcode).ToArray(), .. Convert.FromHexString(hex)];

    /// <summary>OURS: MonolithList_NT from this level (recorded absent at levels 1 and 9, present at 23, 44, 45; CANDIDATE: the level-10 unlocks).</summary>
    public const int MonolithListLevel = 10;

    /// <summary>
    /// MonolithList_NT 0xe30b {list of {monolith id i32, resonate count i32}}: three monoliths by race, OBSERVED in all 27 entries
    /// at level 23+ (Light 1001, 1002, 3001; Dark 2001, 2002, 4001). The counts are per character there (0-560); we keep no
    /// resonance, so 0 (OURS).
    /// </summary>
    static byte[] MonolithList(int race)
    {
        int[] ids = race == 2 ? [2001, 2002, 4001] : [1001, 1002, 3001];
        var w = PacketWriter.Opcode(0xe30b).Varint(ids.Length);
        foreach (var id in ids) w.I32(id).I32(0);
        return w.ToArray();
    }

    /// <summary>
    /// TrustLimitInfo_NT 0xe262: flags (07), the Kina limits by EContentsKinaLimitType {type u8, used i64, limit i64, time i64},
    /// the update time i64 and the trust step i32. The form of every 2026-11-05/06 entry (21): type 2 limited to 500,000,000,
    /// types 3-5 unlimited (-1), nothing used, step 5; the 2026-10 and some 11-04 accounts had flags 03, step 1 and type 2 at 0
    /// (per account: OURS, the permissive one, as we enforce no Kina limit). CANDIDATE: the times (the server's clock).
    /// </summary>
    public static byte[] TrustLimitInfo(long now)
    {
        var w = PacketWriter.Opcode(0xe262).U8(0x07).Varint(4);
        foreach (var type in new byte[] { 2, 3, 4, 5 })
            w.U8(type).U8(type).I64(0).I64(type == 2 ? 500_000_000 : -1).I64(now);
        return w.I64(now).I32(5).ToArray();
    }

    /// <summary>
    /// ArenaMatchingInfo_NT 0x400a, by race: Light 010102230501290502 (the level-1 entry 2026-10-28, 234912), Dark 010102630901650902
    /// (the level-1 entries 2026-11-04 183516 and the Dark characters at 45). OBSERVED; their meaning is not decoded.
    /// </summary>
    static byte[] ArenaMatchingInfo(int race) => Observed(0x400a, race == 2 ? "010102630901650902" : "010102230501290502");

    /// <summary>The world entry's lists and state messages (see the file remarks), after the load reply.</summary>
    List<byte[]> EntryLists(long now)
    {
        var race = profileRace == 2 ? 2 : 1;
        var replies = new List<byte[]>
        {
            Observed(0xe263, "023f005300"),            // TradeServerInfo_NT
            SeasonSchedule,                            // SeasonSchedule_NT 0x8d53 (GameSession.SeasonSchedule.cs)
            Observed(0x8d58, "00"),                    // MyPrevSeasonRanking_NT
            Observed(0x5682, "00002300"),              // PrevSeasonMissionInfo_NT
        };
        // GodstoneList_NT: the owned godstones, else the empty list.
        var godstoneList = EconomyAtEntry().ToList();
        replies.AddRange(godstoneList.Count > 0 ? godstoneList : [Observed(0x5633, "00")]);
        replies.AddRange(
        [
            Observed(0x8aaf, "00"),                    // AgitDecoList_NT
            Observed(0xe22e, "05010000020000030000040000050000"),   // DaevanionPointUpdate_NT: five point types at 0
            Observed(0xe226, "00"),                    // DaevanionBoardInfo_NT
            BookmarkList(),                            // 0x9330 right before 0x610b (OBSERVED 34 of 34; GameSession.Bookmarks.cs)
        ]);
        if (Store is not null) replies.Add(ContentTicketList(now));   // 0x610b (GameSession.ContentTickets.cs)
        replies.AddRange(CraftListsAtEntry());         // 0x8d7e, 0x8d37, 0x8d8c (GameSession.Craft.cs)
        replies.AddRange(
        [
            Observed(0xe224, "02010202"),              // ContentsControl_NT
            Observed(0xe32a, "0301dc05000000000000000000000002dc05000000000000000000000003dc050000000000000000000000"),   // ArenaRanking_NT: Single/Team/Strategy at 1500
            Observed(0x9331, "00"),                    // ? (0x9331, after BookmarkedContentList_NT)
            Observed(0xe33b, "00"),                    // SubscribeList_NT
            Observed(0xe33e, "00"),                    // ? (0xe33e)
            PacketWriter.Opcode(0xffae).U8(0).I64(now).ToArray(),   // ? (0xffae): none, a time (OBSERVED 00 + the server time at level 1)
            Observed(0x6103, "00"),                    // ? (0x6103)
        ]);
        if (level >= MonolithListLevel) replies.Add(MonolithList(race));
        replies.AddRange(
        [
            Observed(0x56b6, "00"),                    // AccountContentsInfoList_NT
            PacketWriter.Opcode(0xffa5).I64(0).I64(now).ToArray(),  // ? (0xffa5): 0, a time (OBSERVED at level 1)
            PacketWriter.Opcode(0xffa7).I64(0).I64(now).ToArray(),  // ? (0xffa7): 0, a time (OBSERVED at level 1)
            TrustLimitInfo(now),                       // TrustLimitInfo_NT 0xe262
            Observed(0xe25c, "014b01"),                // ContentsFeatureOnOffListByAdmin_NT
            Observed(0x572d, "00"),                    // ? (0x572d; 00 in all 34)
            Observed(0xe251, "00"),                    // BMShopTutorialInfo_NT
            ArenaMatchingInfo(race),                   // ArenaMatchingInfo_NT 0x400a
            Observed(0xe200, "00002392ffffffffff000000"),   // MessageCardInitData_NT
            // ? (0xffab): i64 0, i64 3000000 (OBSERVED 2.0.6, 31 of 31; the first value 0 for the new characters).
            PacketWriter.Opcode(0xffab).I64(0).I64(3_000_000).ToArray(),
            PacketWriter.Opcode(0xe223).I64(now).I32(0).ToArray(),   // WorldChatFreeChance_NT (observed: a time then four zero bytes)
            Observed(0x6000, "00"),                    // ? (0x6000)
            Observed(0x570f, "000000"),                // MonsterCubeRewardList_RS (the usmap position, 0x47 above the old base; 0x6108 is DungeonClearKillNpc_NT)
        ]);
        return replies;
    }
}
