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
    static readonly Lazy<string> SeasonListHex = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("season-list.hex")
            ?? throw new InvalidOperationException("Missing embedded game data season-list.hex");
        return new StreamReader(stream).ReadToEnd().Trim();
    });

    static readonly Lazy<string> ContentsTicketsHex = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("contents-tickets.hex")
            ?? throw new InvalidOperationException("Missing embedded game data contents-tickets.hex");
        return new StreamReader(stream).ReadToEnd().Trim();
    });

    /// <summary>
    /// 0xe262 TrustLimitInfo_NT (catalogue 1371 + 0xdd07): the official body (identical in 30 entries but for the time, which
    /// is the entry's now in four places; 0x1a5a167... is a fixed update time). PROVISIONAL: the trust step and limits are the
    /// official account's, not tracked.
    /// </summary>
    static byte[] TrustLimitInfo(long now)
    {
        var t = Convert.ToHexString(BitConverter.GetBytes(now)).ToLowerInvariant();
        return Observed(0xe262, "0704020200000000000000000065cd1d00000000" + t + "03030000000000000000ffffffffffffffff" + t
            + "04040000000000000000ffffffffffffffff" + t + "05050000000000000000ffffffffffffffff" + t + "607167a5a101000005000000");
    }

    static byte[] Observed(ushort opcode, string hex) => [.. PacketWriter.Opcode(opcode).ToArray(), .. Convert.FromHexString(hex)];

    /// <summary>
    /// The first LoadComplete after a select (OBSERVED phase in 11 official entries: after 0x3623, before MyCharAppear_NT; again
    /// only after a relog): BMShopTutorialInfo_NT, ArenaMatchingInfo_NT (0x400a, catalogue 273 + 0x3ef9: the matching servers
    /// per contents type, one per race), MessageCardInitData_NT, 0xffab.
    /// </summary>
    List<byte[]> EntryLoadConstants() =>
    [
        Observed(0xe251, "00"),
        ArenaMatchingInfo(),
        Observed(0xe200, "00002392ffffffffff000000"),
        Observed(0xffab, "0000000000000000c0c62d0000000000"),
    ];

    /// <summary>
    /// ArenaMatchingInfo_NT 0x400a: one contents type (1) with its two matching servers, a Light and a Dark one. OFFICIAL (rec
    /// 20261106-105448 #610, a character of Dark server 2316): `0a40 01 01 02 2405 01 0c09 02` = 1 type {1: [(1316, Light),
    /// (2316, Dark)]}, the server ids u16 (they were varints here until 2026-11-12: 1204 went out as `b4 09`, read as 2484). The
    /// servers: this world and its pair's other-race world (four worlds), else this host's server for both races.
    /// </summary>
    byte[] ArenaMatchingInfo()
    {
        var (light, dark) = options.Worlds is { } worlds ? worlds.ArenaPair(ServerId) : (ServerId, ServerId);
        return PacketWriter.Opcode(0x400a).Varint(1).U8(1).Varint(2).U16(light).U8(1).U16(dark).U8(2).ToArray();
    }

    /// <summary>After MyCharAppear_NT of the first load (OBSERVED): WorldChatFreeChance_NT (its time), FieldEventInfo_NT 0x6000 (the
    /// Shugo festival events running now, "00" when none: GameSession.Systems2.cs), 0x570f.</summary>
    List<byte[]> EntryAfterAppearConstants(long now) =>
    [
        PacketWriter.Opcode(0xe223).I64(now).I32(0).ToArray(),
        FieldEventInfoAtEntry(now),
        Observed(0x570f, "000000"),
    ];

    /// <summary>The observed constants of the select burst (none depends on the character but the times).</summary>
    List<byte[]> EntryConstants(long now, byte[] daevanionPoints) =>
    [
        Observed(0xe263, "023f005300"),            // TradeServerInfo_NT
        Observed(0x5682, "00002300"),              // PrevSeasonMissionInfo_NT
        GodstoneList(),                            // GodstoneList_NT (OBSERVED "00" when none; GameSession.Godstones.cs)
        AgitDecoList(),                            // AgitDecoList_NT 0x8aaf (OBSERVED right after 0x5633; GameSession.Pantheon.cs)
        PresetInfo(),                              // PresetInfo_NT 0xe28c (OBSERVED right after 0x8aaf in 16 entries; it went with the load)
        daevanionPoints,                           // DaevanionPointUpdate_NT 0xe22e: used points per type (OBSERVED "0501000002..." with none)
        // DaevanionBoardInfo_NT 0xe226 follows here: the character's boards (GameSession.Daevanion.cs).
        Observed(0xe224, "02010202"),              // ContentsControl_NT
        // ArenaRanking_NT: Single/Team/Strategy, OBSERVED "0301dc05000000000000000000000002dc05...03dc05..." (1500 each) for a new
        // character; the stored ratings and counts since feature/rank-system (GameSession.Ranking.cs).
        ArenaRanking(),
        SubscribeList(now),                        // SubscribeList_NT (free mode: every benefit, GameSession.Subscriptions.cs)
        Observed(0xe33e, "00"),                    // ? (0xe33e)
        // AccountCurrencyLimitStatus_NT (0xffae, catalogue 1533 + 0xf9b1): reward counts per dungeon category {1: 0, 2: 0}, now
        // (OBSERVED after 0xe33e in all 10 entries; 0xffab AbyssPointUpdate_NT and 0xffa7 InitKinaLimitStatus_NT fit the same base).
        PacketWriter.Opcode(0xffae).Varint(2).U8(1).I32(0).U8(2).I32(0).I64(now).ToArray(),
        WaveDungeonInfoAtEntry(),                  // WaveDungeonInfo_NT 0x6103: the daily dungeon records ("00" with none; GameSession.DailyDungeons.cs)
        Observed(0x56b6, "00"),                    // AccountContentsInfoList_NT
        Observed(0xe25c, "014b01"),                // ContentsFeatureOnOffListByAdmin_NT
        // OBSERVED at every official entry (37 sessions, the auto checker 2026-11-07): three empty lists we never sent.
        Observed(0x572d, "00"),                    // ? (0x572d)
        BookmarkList(),                            // 0x9330 bookmarks (GameSession.Bookmarks.cs; "00" when none, OBSERVED)
        // ContentsTickets_NT (0x610b, catalogue 584 + 0x5ec3): 73 entry tickets {mask u8 | id i32 | bit0 reset time i64 | bit2 count
        // varint ...}; the official body of a character with nothing used (OBSERVED after 0x9330, tools/data/contents-tickets.hex).
        // The counts this character used are replaced (GameSession.Tickets.cs).
        ContentsTicketsBody(now),
        Observed(0x9331, "00"),                    // ? (0x9331)
        // 0x8d53 the season list (34 entries: Season_Main_Live, ArenaStrategy_Season, ...): the official body, identical in all
        // 10 recordings (tools/data/season-list.hex). We sent a cut 38-byte prefix announcing 34 entries with only one. The main
        // seasons' rows follow the schedule (GameSession.Ranking.cs: identical to the observed body in season 1).
        SeasonSchedule(now),
        PacketWriter.Opcode(0xffa7).Zeros(8).I64(now).ToArray(),   // InitKinaLimitStatus_NT (0xffa7): Kina acquired 0, now (OBSERVED, 8 entries)
        TrustLimitInfo(now),                       // TrustLimitInfo_NT (OBSERVED right after 0xffa7)
        // 0xffa5 {i64, now} (OBSERVED after 0xe256 in 10 entries, 0 or 1530: the same shape as the Kina limit status; CANDIDATE).
        PacketWriter.Opcode(0xffa5).Zeros(8).I64(now).ToArray(),
    ];
}
