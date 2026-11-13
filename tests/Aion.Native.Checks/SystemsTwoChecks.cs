using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Community and PvP menus (feature/systems-2): the official legion info and member list bytes (rec 20261107-210132), the legion
/// management flow on stored characters (create, notice, emblem, attendance, donation, level up, applications, grades, delegation),
/// friend limits and result codes, mail limits, the expedition (party recruit / join), the week's Abyss Points, the arena and
/// battlefield matching tables and the Shugo festival schedule.
/// </summary>
static class SystemsTwoChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
    static ItemState Kina(long amount, long wire) =>
        new(Guid.NewGuid(), "b", GameSession.KinaItemId, amount, "bag", null, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    // GuildInfo_RS #1927 of rec 20261107-210132 (the legion "Adventalis": 90 members, master Axaeona, level 1, emblem 2, Manual).
    const string OfficialGuildInfo = "038a00000a416476656e74616c697307417861656f6e615a80010000000000000000e80300000000000002002e204368696c6c20e280a2204e6f20507265737375726520e280a220507665526169642d666f637573656420e299a58a01416476656e74616c6973204020446973636f7264206964202f58565767686b714b66472020202d3e205577550d0a0d0a48656c6c6f20416476656e747320210d0a46616d696c79206c6567696f6e204665656c206672656520746f20696e766974652065616368206f7468657220746f207468652064756e67656f6e2f6162797373204b72750d0a0d0a02000200000324df510da10100000000000000000000000000000000000000000000000000000300fe1f000001dc07000002000200000101000000000000000000000000";
    const string OfficialIntro = "204368696c6c20e280a2204e6f20507265737375726520e280a220507665526169642d666f637573656420e299a5";
    const string OfficialNotice = "416476656e74616c6973204020446973636f7264206964202f58565767686b714b66472020202d3e205577550d0a0d0a48656c6c6f20416476656e747320210d0a46616d696c79206c6567696f6e204665656c206672656520746f20696e766974652065616368206f7468657220746f207468652064756e67656f6e2f6162797373204b72750d0a0d0a";
    // GuildMemberInfo_RS #1928 of the same recording, its first 8 members (one shared online-bool byte) with the count set to 8.
    const string OfficialMembers8 = "058a00000800fce10b0000000000c08f020000000b0913000000084368656f6e6e6d612d0000000249e75db1b7a1010000600400002409000094c101000000000000ff530900000000000967020000000c09100000000942726973696f6e61632d000000020b62639ea1010000000000005b0a0000e20f02000000000000fc380b00000000001a83020000000c090f000000054c656467651500000002770d3145a101000000000000ef000000643400000000000000d3210b00000000005487020000000c09200000000941706f7068656e69612d00000002c8a4adb7a1010000200400009808000030a6010000000000009e030b0000000000088e020000000c090b00000005417a72616d2d000000020b3efc92a1010000000000000d050000530b0100000000000228750800000000004c95020000000c092400000007417861656f6e612d000000000120c6abbab7a1010000e90300005b0a000068dc010000000000028cf90a00000000005d9b020000000c09140000000847697261666666752d00000002144265657020626f6f702049276d2061204e6f6f625716ccb7a1010000e1170000a90b000056ed020000000000002aae0b0000000000569e020000000c09180000000c495061636b537069726974732d000000021d324151a101000000000000fd0600007061010000000000";

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        await Mail(storedWorldSession, now, check);
        await Friends(storedWorldSession, now, check);
        await Legion(storedWorldSession, now, check);
        await Expedition(storedWorldSession, now, check);
        await Abyss(storedWorldSession, now, check);
        ArenaAndBattlefield(check);
        await ShugoFestival(storedWorldSession, now, check);
    }

    /// <summary>Mail: at most post_load_count posts, none expired.</summary>
    public static async Task Mail(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var mailStore = new FakeStore();
        var mailer = await storedWorldSession("Mailer", mailStore);
        for (var i = 0; i < 105; i++)
            mailStore.Posts.Add(new PostRecord(1000 + i, 1, 1, 17, "", false, false, DateTimeOffset.FromUnixTimeMilliseconds(now - 1000 + i), DateTimeOffset.FromUnixTimeMilliseconds(now + 86_400_000), [], 0));
        mailStore.Posts.Add(new PostRecord(2000, 1, 1, 17, "", false, false, DateTimeOffset.FromUnixTimeMilliseconds(now - 5000), DateTimeOffset.FromUnixTimeMilliseconds(now - 1), [], 0));
        var mailbox = await mailer.HandleAsync(new PacketWriter().U16(GameSession.PostLoadRq).U8(1).U8(0).I64(0).I64(0).ToArray(), now);
        check(BitConverter.ToInt32(mailbox[0], 5) == 105 && mailbox[0][13] == 100 && mailbox[0].Length > 14,
            "LoadPost_RS: 105 live posts counted, 100 shown (post_load_count), the expired one left out");
        mailer.ExitSharedWorld();
    }

    /// <summary>Friends: the client's FriendRequest_* result codes.</summary>
    public static async Task Friends(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var friendStore = new FakeStore();
        var friend = await storedWorldSession("Befriender", friendStore);
        var unknown = await friend.HandleAsync(new PacketWriter().U16(SocialOpcodes.FriendRequestRq).I32(1204).String("Nobody Here").ToArray(), now);
        check(Op(unknown[0]) == SocialOpcodes.FriendRequestRs && BitConverter.ToUInt16(unknown[0], 2) == EResult.FriendRequest_NotFoundCharacter,
            "a friend request to an unknown name: FriendRequest_NotFoundCharacter (8321)");
        friend.ExitSharedWorld();
    }

    /// <summary>Legion: official bytes (rec 20261107-210132) and the management flow.</summary>
    public static async Task Legion(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var adventalis = new GuildView(1, "Adventalis", 1, 0, 2, 2, "", Encoding.UTF8.GetString(Convert.FromHexString(OfficialIntro)),
            Encoding.UTF8.GetString(Convert.FromHexString(OfficialNotice)), DateTimeOffset.FromUnixTimeMilliseconds(0x1a10d51df24),
            [new GuildMember(Guid.NewGuid(), "Axaeona", 45, 0, " ", 36, epoch), .. Enumerable.Range(0, 89).Select(i => new GuildMember(Guid.NewGuid(), $"m{i}", 45, 2, "", 1, epoch))]);
        check(GameSession.GuildInfoBody(0, adventalis, (false, true, 0, 0, 3), [(0, 8190), (1, 2012), (2, 512)], 0).SequenceEqual(Convert.FromHexString(OfficialGuildInfo)),
            "GuildInfo_RS rebuilt byte for byte as official (#1927: emblem u16, max exp 1000 from GuildData, can attend today, 3 donations, one EssenceGuild wallet)");
        GameSession.GuildMemberWire W(long account, long dbid, int pc, string name, int level, byte grade, string comment, bool online, long logout, int subzone, int equip, long cp) =>
            new(account, dbid, pc, name, level, grade, comment, online, logout, subzone, equip, cp);
        var eight = new[]
        {
            W(778748, 651614571085336512, 19, "Cheonnma", 45, 2, "", true, 1794083216871, 1120, 2340, 115092),
            W(611327, 651896046062036745, 16, "Brisionac", 45, 2, "", false, 1793658675723, 0, 2651, 135138),
            W(735484, 651896046062043930, 15, "Ledge", 21, 2, "", false, 1792162205047, 0, 239, 13412),
            W(729555, 651896046062045012, 32, "Apophenia", 45, 2, "", true, 1794082972872, 1056, 2200, 108080),
            W(721822, 651896046062046728, 11, "Azram", 45, 2, "", false, 1793467366923, 0, 1293, 68435),
            W(554280, 651896046062048588, 36, "Axaeona", 45, 0, " ", false, 1794083826630, 1001, 2651, 121960),
            W(719244, 651896046062050141, 20, "Girafffu", 45, 2, "Beep boop I'm a Noob", true, 1794084968023, 6113, 2985, 191830),
            W(765482, 651896046062050902, 24, "IPackSpirits", 45, 2, "", false, 1792364589597, 0, 1789, 90480),
        };
        check(GameSession.GuildMemberInfoBody(0, eight).SequenceEqual(Convert.FromHexString(OfficialMembers8)),
            "GuildMemberInfo_RS rebuilt byte for byte as official (#1928: presence mask bit 1 = message, online bools packed eight to a byte)");
        check(Systems2Tables.Embedded.GuildLevelForExp(999) == 1 && Systems2Tables.Embedded.GuildLevelForExp(1000) == 2 && Systems2Tables.Embedded.GuildLevelForExp(767_500) == 20
              && Systems2Tables.Embedded.GuildLevelOf(2).Stats.Any(s => s.Stat == "HPMax" && s.Value == 4), "GuildData: level 2 at 1000 exp, level 20 the last; level 2's HPMax buff 4");

        // The management flow on a stored character.
        var store = new FakeStore();
        var master = await storedWorldSession("Legate", store);
        store.Items.Add(Kina(2_000_000, 7001));
        await master.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), now);
        long KinaLeft() => store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity);
        var created = await master.HandleAsync(new PacketWriter().U16(0x8a00).String("Dawnguard").U8(2).String("").U16(3).String("hi").ToArray(), now);
        check(Op(created[0]) == 0x8a01 && Res(created[0]) == 0 && store.GuildRow is { Emblem: 3, JoinRule: 2, Intro: "hi" } && KinaLeft() == 1_900_000,
            "GuildCreate_RQ with a u16 emblem: created (Manual), 100000 Kina (GlobalSetting guild_create_gold)");
        var notice = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetNoticeRq).String("Raid at 8").ToArray(), now);
        check(Op(notice[0]) == GameSession.GuildSetNoticeRs && Res(notice[0]) == 0 && notice.Any(b => Op(b) == GameSession.GuildSetNoticeNt) && store.GuildRow!.Notice == "Raid at 8",
            "GuildSetNotice_RQ: stored, RS 0 and the members' GuildSetNotice_NT");
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetNoticeRq).String(new string('x', 161)).ToArray(), now))[0]) == EResult.Guild_SetNoticeInvalidLength,
            "a notice over 160 characters (l10n_guild_notice_max_length) is refused");
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetEmblemRq).I32(99).ToArray(), now))[0]) == EResult.Guild_SetEmblemInvalidID,
            "an emblem GuildEmblem.dat does not have is refused");
        var emblem = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetEmblemRq).I32(5).ToArray(), now);
        check(Res(emblem[0]) == 0 && store.GuildRow!.Emblem == 5 && emblem.Any(b => Op(b) == GameSession.GuildSetEmblemNt) && emblem.Any(b => Op(b) == GameSession.GuildDescUpdateNt),
            "emblem 5 (unlocked at level 1): stored, GuildSetEmblemID_NT and the tag (0x8a33)");
        var info = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAttendanceInfoRq).ToArray(), now);
        check(Op(info[0]) == GameSession.GuildAttendanceInfoRs && Res(info[0]) == 0 && info[0][6] == 1, "GuildAttendanceInfo_RS: can attend today");
        var attended = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAttendanceRq).ToArray(), now);
        check(Res(attended[0]) == 0 && store.GuildRow!.Exp == GameSession.GuildAttendanceExp
              && Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAttendanceRq).ToArray(), now))[0]) == EResult.Guild_AttendanceAlreadyDone,
            "GuildAttendance_RQ once a day: legion exp; again refused (Guild_AttendanceAlreadyDone)");
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAttendanceRewardRq).ToArray(), now))[0]) == 0
              && Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAttendanceRewardRq).ToArray(), now))[0]) == EResult.Guild_AttendanceRewardAlreadyDone,
            "the attendance reward once after attending");
        var donated = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildDonationRq).U8(2).ToArray(), now);
        check(Op(donated[0]) == GameSession.GuildDonationRs && Res(donated[0]) == 0 && donated[0][5] == 2 && KinaLeft() == 1_800_000 && store.GuildRow!.Exp == 110,
            "GuildDonation_RQ level 2: 100000 Kina for 100 legion exp, two donations left today");
        store.GuildRow = store.GuildRow! with { Exp = 995 };
        var levelUp = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildDonationRq).U8(1).ToArray(), now);
        check(Res(levelUp[0]) == 0 && store.GuildRow!.Level == 2 && levelUp.Any(b => Op(b) == GameSession.GuildLevelNt && b[2] == 2),
            "a donation past 1000 exp raises the legion to level 2 (GuildData AccumulateExp) with GuildLevel_NT");
        await master.HandleAsync(new PacketWriter().U16(GameSession.GuildDonationRq).U8(1).ToArray(), now);
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildDonationRq).U8(1).ToArray(), now))[0]) == EResult.Guild_DonationFinishedToday,
            "a fourth donation in a day is refused (three a day)");
        // An application to this legion (filed by a character elsewhere), answered by the master.
        var applicant = Guid.NewGuid();
        store.LegionApplications.Add(new GuildApplication(applicant, store.GuildRow!.Id, "Squire", 30, 2, "let me in", epoch, epoch));
        var recv = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildRecvJoinRequestsRq).ToArray(), now);
        check(Op(recv[0]) == GameSession.GuildRecvJoinRequestsRs && Res(recv[0]) == 0 && recv[0][4] == 1 && recv[0].AsSpan().IndexOf("Squire"u8) > 0,
            "GuildRecvJoinRequests_RS lists the application");
        var confirmed = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildJoinConfirmRq).String("Squire").Bool(true).ToArray(), now);
        check(Res(confirmed[0]) == 0 && store.GuildRow!.Members.Any(m => m.Name == "Squire" && m.Grade == 2) && confirmed.Any(b => Op(b) == GameSession.GuildJoinNt),
            "GuildJoinConfirm_RQ accept: the applicant joins as a member; GuildJoin_NT");
        var graded = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetGradeRq).String("Squire").U8(1).ToArray(), now);
        check(Res(graded[0]) == 0 && store.GuildRow!.Members.Single(m => m.Name == "Squire").Grade == 1 && graded.Any(b => Op(b) == GameSession.GuildSetGradeNt),
            "GuildSetMemberGrade_RQ: Squire becomes an officer (GuildSetMemberGrade_NT)");
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetGradeRq).String("Squire").U8(0).ToArray(), now))[0]) == EResult.Guild_SetGradeGuildMaster,
            "nobody is made master by a grade change");
        var members = await master.HandleAsync(new PacketWriter().U16(0x8a04).ToArray(), now);
        check(Op(members[0]) == 0x8a05 && members[0][4] == 2 && members[0][5] == 0, "GuildMemberInfo_RS: two members, the first without a message (mask 0)");
        var authority = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetAuthorityRq).Bool(false).Varint(1).U8(1).I32(2012).ToArray(), now);
        check(Res(authority[0]) == 0 && store.LegionAuthority[1] == 2012 && authority.Any(b => Op(b) == GameSession.GuildSetAuthorityNt),
            "GuildSetAuthority_RQ: the officers' authority as the official legion's (2012)");
        var delegated = await master.HandleAsync(new PacketWriter().U16(GameSession.GuildDelegateRq).String("Squire").ToArray(), now);
        check(Res(delegated[0]) == 0 && store.GuildRow!.Members.Single(m => m.Name == "Squire").Grade == 0 && store.GuildRow.Members.Single(m => m.Name == "Legate").Grade == 1,
            "GuildDelegateMaster_RQ: Squire is master, Legate an officer");
        check(Res((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildSetAuthorityRq).Bool(true).Varint(0).ToArray(), now))[0]) == EResult.Guild_CannotGrantAuth,
            "an officer cannot set the authority");
        check((await master.HandleAsync(new PacketWriter().U16(GameSession.GuildAgitInfoRq).ToArray(), now))[0].SequenceEqual(Convert.FromHexString("458a0000000000000000000000000000")),
            "GuildAgitInfo_RS: no hall, the OBSERVED bytes (#1933)");
        master.ExitSharedWorld();

        // Applying to a Manual legion from outside: GuildJoin_RS join result 1 (Requested), listed, withdrawn.
        var outsider = new FakeStore();
        var seeker = await storedWorldSession("Seeker", outsider);
        outsider.OtherGuilds.Add(new GuildView(9, "Wardens", 3, 7000, 1, 2, "", "we raid", "", epoch, [new GuildMember(Guid.NewGuid(), "Warden", 45, 0, "", 1, epoch)]));
        var none = await seeker.HandleAsync(new PacketWriter().U16(GameSession.GuildSendJoinRequestsRq).ToArray(), now);
        check(none[0].SequenceEqual(Convert.FromHexString("1d8a000000")), "GuildSendJoinRequests_RS with no application: the OBSERVED 1d8a 0000 00");
        var applied = await seeker.HandleAsync(new PacketWriter().U16(0x8a0f).I64(9).U8(2).String("hello").String("").ToArray(), now);
        check(Op(applied[0]) == 0x8a10 && Res(applied[0]) == 0 && applied[0][12] == 1 && outsider.LegionApplications.Single().GuildId == 9,
            "GuildJoin_RQ to a Manual legion files an application (GuildJoin_RS join result 1 Requested)");
        var listed = await seeker.HandleAsync(new PacketWriter().U16(GameSession.GuildSendJoinRequestsRq).ToArray(), now);
        check(listed[0][4] == 1 && listed[0].AsSpan().IndexOf("Wardens"u8) > 0, "GuildSendJoinRequests_RS lists it");
        var recommended = await seeker.HandleAsync(new PacketWriter().U16(GameSession.GuildRecommendedListRq).U8(0).ToArray(), now);
        check(Op(recommended[0]) == GameSession.GuildRecommendedListRs && recommended[0][4] == 1, "GuildRecommendedList_RS: the legions taking members");
        check(Res((await seeker.HandleAsync(new PacketWriter().U16(GameSession.GuildJoinCancelRq).I64(9).Bool(false).ToArray(), now))[0]) == 0 && outsider.LegionApplications.Count == 0,
            "GuildJoinCancel_RQ withdraws it");
        seeker.ExitSharedWorld();
    }

    /// <summary>Expedition: recruit, join by party key, cancel, recommendations.</summary>
    public static async Task Expedition(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var leaderStore = new FakeStore();
        var leader = await storedWorldSession("Captain", leaderStore);
        var joiner = await storedWorldSession("Joiner", new FakeStore());
        var recruit = await leader.HandleAsync(new PacketWriter().U16(GameSession.PartyRecruitRq).ToArray(), now);
        check(Op(recruit[0]) == GameSession.PartyRecruitRs && Res(recruit[0]) == 0 && BitConverter.ToInt64(recruit[0], 4) == now + GameSession.PartyRecruitMs
              && recruit.Any(b => Op(b) == SocialOpcodes.PartyInfoNt) && recruit.Any(b => Op(b) == GameSession.PartyRecruitNt),
            "PartyRecruit_RQ alone: a party is made and opened to joiners for 10 minutes (PartyRecruit_RS, PartyInfo_NT, PartyRecruit_NT)");
        var partyKey = BitConverter.ToInt64(recruit.First(b => Op(b) == SocialOpcodes.PartyInfoNt), 2);
        var joined = await joiner.HandleAsync(new PacketWriter().U16(GameSession.PartyJoinRq).I64(partyKey).ToArray(), now);
        check(Op(joined[0]) == GameSession.PartyJoinRs && Res(joined[0]) == 0 && joined.Any(b => Op(b) == SocialOpcodes.PartyInfoNt),
            "PartyJoin_RQ with the recruiting party's key: joined (PartyInfo_NT)");
        check(Res((await joiner.HandleAsync(new PacketWriter().U16(GameSession.PartyRecruitRq).ToArray(), now))[0]) != 0, "only the leader recruits");
        check(Res((await leader.HandleAsync(new PacketWriter().U16(GameSession.PartyRecruitCancelRq).ToArray(), now))[0]) == 0, "PartyRecruitCancel_RQ closes the recruitment");
        var recommend = await leader.HandleAsync(new PacketWriter().U16(SocialOpcodes.PartyRecommendRq).U8(1).Varint(0).ToArray(), now);
        check(Op(recommend[0]) == SocialOpcodes.PartyRecommendRs && Res(recommend[0]) == 0 && recommend[0][4] == 1, "PartyRecommend_RS (Normal): answered");
        leader.ExitSharedWorld();
        joiner.ExitSharedWorld();
    }

    /// <summary>Abyss Points of the week (AbyssPointUpdate_NT) and the week boundary.</summary>
    public static async Task Abyss(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var warriorStore = new FakeStore();
        var warrior = await storedWorldSession("Warrior", warriorStore);
        var ap = warrior.AbyssPointsEarned(300);
        check(ap.Single().SequenceEqual(new PacketWriter().U16(GameSession.AbyssPointUpdateNt).I64(300).I64(3_000_000).ToArray()),
            "AbyssPointUpdate_NT {300, 3000000} after 300 Abyss Points (the OBSERVED entry form 0 / 3000000)");
        await warrior.TickAsync(now + 100);
        check(warriorStore.Settings.TryGetValue("abyss_point_week", out var week) && week.GetProperty("acc").GetInt64() == 300, "the week's count is saved at the next tick");
        check(GameSession.AbyssWeekOf(new DateTimeOffset(2026, 11, 11, 7, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()) == new DateOnly(2026, 11, 11)
              && GameSession.AbyssWeekOf(new DateTimeOffset(2026, 11, 11, 6, 59, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()) == new DateOnly(2026, 11, 4),
            "the Abyss week turns on Wednesday 16:00 KST = 07:00 UTC (season_mission_reset_day 3, season_mission_reset_time; fix/rank-rewards)");
        warrior.ExitSharedWorld();
    }

    /// <summary>Arena and battlefield: Matching / RewardArena / MatchingSchedule / MatchingContents tables.</summary>
    public static void ArenaAndBattlefield(Action<bool, string> check)
    {
        check(GameSession.ArenaTeamSize(1) == 1 && GameSession.ArenaTeamSize(2) == 5 && GameSession.ArenaTeamSize(3) == 10 && GameSession.ArenaTeamSize(4) == 0,
            "Matching.dat team sizes: Single 1, Team 5, Strategy (battlefield) 10; the hidden party matching closed");
        var strategyWin = GameSession.ArenaRewardItems(3, won: true);
        check(strategyWin.Where(r => r.Item == GameSession.AbyssPointItemId).Sum(r => r.Count) == 4000 && strategyWin.Where(r => r.Item == 930100013).Sum(r => r.Count) == 3
              && GameSession.ArenaRewardItems(1, won: false).SequenceEqual([(GameSession.AbyssPointItemId, 300L)]),
            "RewardArena.dat: a battlefield win pays 1000 + 3000 Abyss Points and 1 + 2 Medal2; a lost 1v1 300 Abyss Points");
        var monday = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        check(GameSession.MatchingOpen(3, monday + 12 * 3_600_000) && !GameSession.MatchingOpen(3, monday + 15 * 3_600_000) && GameSession.MatchingOpen(1, monday + 15 * 3_600_000),
            "MatchingSchedule group 100: the battlefield opens 11:00-14:00 and 19:00-21:00; the arena always");
        check(Systems2Tables.Embedded.ContentsOf(55) is { Type: "CarMode", Map: 62, Lives: 0, RespawnMs: 10_000 }, "MatchingContents 55: CarMode on map 62, unlimited lives, 10 s respawn");
    }

    /// <summary>Shugo festival: the EventSchedule hours, the official FieldEventInfo / FieldEventChanged / entrance bytes, the entry window.</summary>
    public static async Task ShugoFestival(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        static long Ms(int y, int mo, int d, int h, int mi, int s = 0) => new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero).ToUnixTimeMilliseconds();
        static long Hex64(string hex) => BitConverter.ToInt64(Convert.FromHexString(hex));
        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        var racing = Systems2Tables.Embedded.FieldEventOf(110301)!;
        var jump = Systems2Tables.Embedded.FieldEventOf(210101)!;
        // Thursday 2026-11-12 14:00 KST = 05:00 UTC: open, 3 minutes to enter, then the game.
        var (prepare, open, start, end) = GameSession.FieldEventState(racing, Ms(2026, 11, 12, 5, 1));
        check(racing.Kind == "ShugoFesta" && racing.Map == 500039 && prepare == GameSession.FieldEventPrepare && open == Ms(2026, 11, 12, 5, 0)
              && start == Ms(2026, 11, 12, 5, 3) && end == Ms(2026, 11, 12, 5, 8, 12)
              && GameSession.FieldEventState(racing, Ms(2026, 11, 12, 5, 3)).State == GameSession.FieldEventProgress
              && GameSession.FieldEventState(racing, Ms(2026, 11, 12, 5, 8, 12)).State == GameSession.FieldEventEnd,
            "racing (FieldEvent 110301, EventSchedule 1103) on 2026-11-12: opens 05:00 UTC (14:00 KST), entry until 05:03, races 312 s (300 + 12 OBSERVED), then over");
        check(GameSession.FieldEventState(jump, Ms(2026, 11, 15, 14, 59, 59)).Open == Ms(2026, 11, 15, 14, 0)
              && GameSession.FieldEventState(jump, Ms(2026, 11, 15, 14, 59, 59)).State == GameSession.FieldEventEnd
              && GameSession.FieldEventState(jump, Ms(2026, 11, 15, 15, 0)) == (GameSession.FieldEventPrepare, Ms(2026, 11, 15, 15, 0), Ms(2026, 11, 15, 15, 3), Ms(2026, 11, 15, 15, 5, 55))
              && GameSession.FieldEventState(jump, Ms(2026, 11, 14, 15, 0, 30)).Open == Ms(2026, 11, 14, 15, 0)       // 00:00 KST Sunday
              && GameSession.FieldEventNextOpen(jump, Ms(2026, 11, 15, 23, 30)) == Ms(2026, 11, 16, 0, 0),
            "Jump Jump (210101) on Sunday 2026-11-15 and across the KST day change: every full hour, 175 s after the 180 s entry window");
        check(GameSession.FestaEventsOf(2).Select(e => e.Id).SequenceEqual([210301, 210501, 210101, 210401, 210801, 210701, 210201, 210601, 210901])
              && GameSession.FestaEventsOf(1).Count == 9 && GameSession.FestaEventsOf(1).Take(4).Select(e => e.Id).SequenceEqual([110301, 110201, 110701, 110801]),
            "the festival lists the race's 9 ShugoFesta events in the official order (rec 20261106-115817; Elyos 3, 2, 7, 8 from rec 20261107-010735)");

        // Official bytes (rec 20261106-115817 #17090, #35839, #70114; rec 20261107-010735 #34; rec 20261106-105448 #617).
        var racingDark = Systems2Tables.Embedded.FieldEventOf(210301)!;
        long o1 = Hex64("06e8bab0a1010000"), s1 = Hex64("7fa9bdb0a1010000"), e1 = Hex64("3f6cc2b0a1010000");
        check(Hex(GameSession.FieldEventChanged(racingDark, (GameSession.FieldEventPrepare, o1, o1 + 180_000, 0))) == "016003fdea0c0106e8bab0a101000006e8bab0a101000026a7bdb0a1010000"
              && Hex(GameSession.FieldEventChanged(racingDark, (GameSession.FieldEventProgress, o1, s1, e1))) == "016003fdea0c0206e8bab0a10100007fa9bdb0a10100003f6cc2b0a1010000"
              && Hex(GameSession.FieldEventChanged(racingDark, (GameSession.FieldEventEnd, o1, s1, e1))) == "016000fdea0c008075d52abb030000",
            "FieldEventChanged_NT 0x6001 byte for byte: Prepare (open, open, open + 180 s), Progress (open, start, end), gone (mask 0, 2100-01-01 KST)");
        long o2 = Hex64("c50285b3a1010000"), s2 = Hex64("3cc487b3a1010000");
        var light = new[] { (110301, "fc868cb3a1010000"), (110201, "1c588cb3a1010000"), (110701, "1c588cb3a1010000"), (110801, "1c588cb3a1010000") }
            .Select(x => (Systems2Tables.Embedded.FieldEventOf(x.Item1)!, (GameSession.FieldEventProgress, o2, s2, Hex64(x.Item2)))).ToList();
        check(Hex(GameSession.FieldEventInfo(light)) == "00600403dddd0602c50285b3a10100003cc487b3a1010000fc868cb3a101000003f9dc0602c50285b3a10100003cc487b3a10100001c588cb3a101000003ede00602c50285b3a10100003cc487b3a10100001c588cb3a101000003d1e10602c50285b3a10100003cc487b3a10100001c588cb3a1010000"
              && Hex(GameSession.FieldEventInfo(1, Ms(2026, 11, 12, 5, 30))) == "006000"
              && GameSession.FieldEventInfo(2, Ms(2026, 11, 12, 5, 4))[2] == 9 && GameSession.FieldEventInfo(1, Ms(2026, 11, 12, 5, 7))[2] == 4,
            "FieldEventInfo_NT 0x6000 byte for byte (4 Elyos games still running at 01:07:36), 00 outside the festival, 9 during it");

        // A session: the changes at the open, the start and the ends; entry refused outside the 3-minute window.
        var store = new FakeStore();
        var player = await storedWorldSession("Festagoer", store);
        var next = GameSession.FieldEventNextOpen(racing, now);
        var opened = (await player.TickAsync(next + 1_000)).Where(b => Op(b) == GameSession.FieldEventChangedNt).ToList();
        var race = opened.Count > 0 ? ((opened[0][3] & 0x7f) | (opened[0][4] & 0x7f) << 7 | opened[0][5] << 14) / 100_000 : 0;
        var eventId = race == 2 ? 210301 : 110301;
        check(opened.Count == 9 && opened.All(b => b[2] == 3 && b[6] == GameSession.FieldEventPrepare && BitConverter.ToInt64(b, 7) == next && BitConverter.ToInt64(b, 23) == next + 180_000)
              && (await player.TickAsync(next + 2_000)).All(b => Op(b) != GameSession.FieldEventChangedNt),
            "at the full hour the session tells the 9 festival games opening (Prepare until +180 s), once");
        var started = (await player.TickAsync(next + 181_000)).Where(b => Op(b) == GameSession.FieldEventChangedNt).ToList();
        check(started.Count == 9 && started.All(b => b[6] == GameSession.FieldEventProgress && BitConverter.ToInt64(b, 15) == next + 180_000),
            "at +180 s the 9 games start (Progress)");
        var late = await player.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(eventId).ToArray(), next + 182_000);
        check(late.Count == 1 && Op(late[0]) == GameSession.FieldEventEntranceRs && Res(late[0]) == EResult.Telpo_InvalidFieldEventState,
            "FieldEventEntranceTeleport_RQ 0x6007 after the entry window: refused (the client's 'admission period has ended')");
        var gone = (await player.TickAsync(next + 600_000)).Where(b => Op(b) == GameSession.FieldEventChangedNt).ToList();
        check(gone.Count == 9 && gone.All(b => b[2] == 0 && b.Length == 15) && GameSession.FieldEventInfo(race, next + 600_000).Length == 3,
            "after the games end each one is reported gone (mask 0) and the entry list is empty");
        var early = await player.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(eventId).ToArray(), next + 3_000_000);
        check(early.Count == 1 && Res(early[0]) == EResult.Telpo_InvalidFieldEventState, "entry refused before the next hour's window opens");
        await player.TickAsync(next + 3_600_000 + 500);
        var inWindow = await player.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(eventId).ToArray(), next + 3_600_000 + 1_000);
        check(inWindow.Count > 0 && Op(inWindow[0]) == GameSession.FieldEventEntranceRs && Res(inWindow[0]) != EResult.Telpo_InvalidFieldEventState
              && inWindow[0].AsSpan(4).SequenceEqual(new PacketWriter().Varint(eventId).ToArray()),
            "entry allowed during the next hour's window; the reply carries the event id as a varint (OBSERVED 0x6008 0000 d5ef0c)");
        player.ExitSharedWorld();
    }
}
