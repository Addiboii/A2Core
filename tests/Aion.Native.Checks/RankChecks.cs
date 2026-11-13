using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// feature/rank-system: the season ranking rules against the official lists (recording 20261106-115817: grades, order, clear points),
/// the season schedule, the hourly lists with rank changes, the Abyss ranking and grade, arena ratings, the ended season's results
/// and rewards (migration 094 rows in the fake store).
/// </summary>
static class RankChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static int Res(byte[] b) => BitConverter.ToUInt16(b, 2);
    static long Ms(int year, int month, int day, int hour = 0) => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static SeasonRankingRow Row(int wire, string name, long point, long best = 0, int creation = 1) =>
        new(Guid.NewGuid(), wire, name, 45, creation, 1, point, "{}", best);

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var rank = RankTables.Embedded;

        // ---------- Seasons (SeasonSchedule Season_Main_Live, KST ticks: fix/rank-rewards) ----------
        check(rank.SeasonAt(Ms(2026, 11, 12))?.No == 1 && rank.SeasonAt(Ms(2026, 9, 30, 6)) is null && rank.SeasonAt(Ms(2026, 9, 30, 7))?.No == 1
              && rank.SeasonAt(Ms(2026, 12, 16, 6))?.No == 1 && rank.SeasonAt(Ms(2026, 12, 16, 7))?.No == 2 && rank.LiveSeason(1)!.End == Ms(2026, 12, 16, 7)
              && rank.RewardSeasonAt(Ms(2026, 12, 20))?.No == 1 && rank.RewardSeasonAt(Ms(2026, 11, 12)) is null,
            "Season_Main_Live 1 runs 2026-09-30 07:00 to 2026-12-16 07:00 UTC (the table's 16:00 KST; OBSERVED: the Abyss tier buff expires 2026-12-16 07:00 UTC), its rewards in season 2");
        check(rank.RankedContents(1).Select(c => c.Type).Order().SequenceEqual([1, 3, 4, 5, 6, 21]) && rank.ContentsOf(1, 3) is { ShowsClassGrade: true, GradeGroup: "", ClassGradeGroup: "ranking_grade_class_bosschallenge_S1" }
              && rank.MinPoint(1) == 1500 && rank.MinPoint(3) == 1 && rank.UpdateMinutes(5) == 60,
            "season 1 ranks Abyss, boss challenge, Transcendence, both arenas and the Ascension trial; class grades shown; Abyss needs 1500 points; hourly");

        // ---------- Grades (OBSERVED: every entry of the official lists) ----------
        check(rank.GradeOf("ranking_grade_class_bosschallenge_S1", 1, 833_591, 2166) == 4 && rank.GradeOf("ranking_grade_class_bosschallenge_S1", 2, 482_850, 2166) == 6
              && rank.GradeOf("ranking_grade_class_bosschallenge_S1", 4, 650_127, 2166) == 5 && rank.GradeOf("ranking_grade_class_bosschallenge_S1", 101, 591_820, 2166) == 8
              && rank.GradeOf("ranking_grade_class_bosschallenge_S1", 1364, 10_485, 2166) == 11 && rank.GradeOf("", 9954, 10_485, 10000) == 0,
            "boss challenge class grades as official: rank 1 with 833,591 -> 4, rank 2 with 482,850 -> 6, rank 101 -> 8, the user's rank 1364 -> 11; no common group -> 0");
        check(rank.GradeOf("ranking_grade_class_abyss_S1", 1, 25_002_989, 1711) == 1 && rank.GradeOf("ranking_grade_class_abyss_S1", 2, 4_941_030, 1711) == 5
              && rank.GradeOf("ranking_grade_class_arena_S1", 1, 2501, 910) == 1 && rank.GradeOf("ranking_grade_class_arena_S1", 228, 1499, 910) == 10
              && rank.GradeOf("ranking_grade_class_awaken_S1", 21, 253_518, 2047) == 6,
            "Abyss, arena and Ascension class grades as official (Abyss rank 2 with 4.9M -> 5, arena rank 228 with 1499 -> 10, Ascension rank 21 -> 6)");
        check(rank.GradeName("ranking_grade_info_abyss", 19) == "Title_rank_grade_abyss_19" && rank.GradeName("ranking_grade_info_main", 1) == "Title_rank_grade_main_1",
            "SeasonRankingGradeInfo names the grades (abyss 19 Trainee, main 1 Challenger 1)");

        // ---------- Clear points (OBSERVED formulas) ----------
        check(GameSession.BossClearPoints(3, 10, 19) == 11281 && GameSession.BossClearPoints(3, 9, 123) == 10877 && GameSession.BossClearPoints(2, 10, 0) == 7300
              && GameSession.BossClearPoints(1, 6, 201) == 1899,
            "boss challenge clear points = 4000 x (tier - 1) + 300 x (level + 1) - seconds (official 11281 in 19 s, the user's 1899 at level 6 in 201 s)");
        check(GameSession.AwakenClearPoints(4, 61) == 7595 && GameSession.AwakenClearPoints(4, 121) == 7295 && GameSession.AwakenClearPoints(3, 183) == 4071,
            "Ascension trial clear points: 7900 - 5 x s at difficulty 4, 4620 - 3 x s at difficulty 3 (official 7595 / 7295 / 4071)");
        check(GameSession.PartyClearPoints(4, 335) is > 4400 and < 4700 && GameSession.PartyClearPoints(2, 400) is > 2400 and < 2600,
            "Transcendence clear points fall in the official ranges (level 4 ~4558 in 335 s, level 2 ~2470-2534)");

        // ---------- A list as of an update: order, ranks, grades, rank changes ----------
        var contents = rank.ContentsOf(1, 3);
        var a = Row(10, "A", 833_591, 11_281, 1);
        var b = Row(11, "B", 482_850, 11_258, 1);
        var c = Row(12, "C", 785_162, 11_252, 1);
        var d = Row(13, "D", 650_127, 11_252, 1);
        var first = SeasonSnapshot.Build(3, 1, contents, [d, c, b, a], r => 4, 1000, 2000, null);
        check(first.Entries.Select(e => e.Row.Name).SequenceEqual(["A", "B", "C", "D"]) && first.Entries.Select(e => e.ClassGrade).SequenceEqual([4, 6, 4, 5])
              && first.Entries.All(e => e.Grade == 0 && e.Display == e.ClassGrade) && first.List(4).Count == 4 && first.List(2).Count == 0,
            "the list orders by the best clear, then the point (official ranks 1-4: 11281, 11258, 11252 x2 with 785,162 before 650,127); grade 0 overall, the class grade shown");
        var later = SeasonSnapshot.Build(3, 1, contents, [d with { BestPoint = 11_300 }, c, b, a], r => 4, 2000, 3000, first);
        check(later.Of(d.Character) is { Rank: 1, RankChange: 3 } && later.Of(a.Character) is { Rank: 2, RankChange: -1 },
            "the next update's rank changes (D 4 -> 1: +3, A 1 -> 2: -1, as the official -7 / -2 of a falling entry)");
        var abyss = SeasonSnapshot.Build(1, 1, rank.ContentsOf(1, 1), [Row(1, "Low", 1499), Row(2, "High", 25_002_989)], r => 4, 0, 1, null, minPoint: rank.MinPoint(1));
        check(abyss.Entries.Count == 1 && abyss.Entries[0] is { Grade: 1, ClassGrade: 1, Display: 1 },
            "the Abyss list needs MinRankingPoint 1500; its common (ranking_grade_abyss_S2) and class grades");
        check(SeasonSnapshot.NextUpdateAfter(Ms(2026, 11, 12, 10) + 1, 60) == Ms(2026, 11, 12, 11), "the next update on the next full hour");

        // ---------- The season list (SeasonSchedule_NT) ----------
        var store = new FakeStore();
        var player = await storedWorldSession("RankPlayer", store);
        player.ClearRankingsForChecks();
        var entry = await storedWorldSession("RankEntry", new FakeStore());
        var schedule = player.SeasonScheduleForChecks(Ms(2026, 11, 12));
        var official = Convert.FromHexString("538d" + GameSession.SeasonListHexForChecks);
        var season2 = player.SeasonScheduleForChecks(Ms(2027, 1, 5));
        var s2 = new PacketReader(season2) { Offset = 2 };
        s2.Varint(); s2.U8();
        check(schedule.SequenceEqual(official) && (s2.String(), s2.I32(), s2.String(), s2.I32(), s2.String(), s2.I32()) == ("Season_Main_Live", 2, "Season_Main_Live", 1, "Season_Main_Live", 3),
            "SeasonSchedule_NT: in season 1 the official body byte for byte; in season 2 the Abyss row reads latest 2, previous 1, next 3");

        // ---------- The player's points: Abyss, arena, saved at the tick, the Abyss grade others see ----------
        var st = Ms(2026, 11, 12, 10);
        await player.TickAsync(st);
        var ap = player.AbyssPointsEarned(2_200_000);
        var abyssTick = await player.TickAsync(st + 1);
        check(ap.Count == 1 && player.SeasonPointOf(GameSession.SeasonContentsAbyss) == 2_200_000
              && abyssTick.Any(p => p.SequenceEqual(new PacketWriter().U16(GameSession.RankingPointUpdateNt).U8(0).I64(2_200_000).ToArray()))
              && store.SeasonPoints.Single(p => p.ContentsType == 1) is { Season: 1, Point: 2_200_000, Extra: "{\"kill_count\":0,\"death_count\":0,\"assist_count\":0}" },
            "Abyss Points count in the season's Abyss ranking: RankingPointUpdate_NT {AbyssRankingPoint, 2,200,000} and the row saved at the tick");
        var abyssList = await player.HandleAsync(new PacketWriter().U16(GameSession.MySeasonRankingRq).U8(1).U8(0).ToArray(), st + 3_600_000);
        check(Res(abyssList[0]) == 0 && player.AbyssGradeShown == 8,
            "at the next update the player is on the Abyss list and shows its Abyss class grade (8: 2.1M points, 3-Star Officer... rank <= 100) instead of 19 (Trainee)");
        var appearance = player.OtherAppearanceForChecks();
        check(appearance.AsSpan().IndexOf(BitConverter.GetBytes(player.AbyssGradeShown)) > 0, "CharAppear_NT carries the Abyss grade (_abyss_ranking_grade)");
        player.AbyssDiedForChecks(st + 3_600_001);
        var fallen = await player.HandleAsync(new PacketWriter().U16(GameSession.WorldMapAbyssRankerInfoRq).ToArray(), st + 3_600_002);
        check(fallen[0].Length == 2 + 2 + 4 + 1 + 21 && fallen[0][8] == 1,
            $"a fallen high-ranked player shows on WorldMapAbyssRankerInfo_RS (race, position, expiry) {Convert.ToHexString(fallen.FirstOrDefault() ?? [])} grade {player.AbyssGradeShown}");

        check(player.ArenaRanking().SequenceEqual(Convert.FromHexString("2ae30301dc05000000000000000000000002dc05000000000000000000000003dc050000000000000000000000")),
            "ArenaRanking_NT before any match: the OBSERVED entry bytes (1500 each)");
        check(GameSession.ArenaRatingChange(1500, 1500, 1) == 16 && GameSession.ArenaRatingChange(1500, 1500, 0) == -16 && GameSession.ArenaRatingChange(1500, 1500, 0.5) == 0,
            "an even arena match moves the rating by 16 (Elo K 32)");
        player.ArenaRatedForChecks(1, 1, 1500);
        var arenaTick = await player.TickAsync(st + 3_600_003);
        check(player.ArenaRating(GameSession.SeasonContentsArenaSingle) == 1516 && arenaTick.Any(p => Op(p) == GameSession.ArenaRankingNt && BitConverter.ToInt64(p, 4) == 1516)
              && store.SeasonPoints.Single(p => p.ContentsType == 5).Extra == "{\"play_count\":1,\"win_count\":1,\"lose_count\":0}",
            "a won 1v1: rating 1516, ArenaRanking_NT again, the official arena extra JSON (play/win/lose)");

        // ---------- fix/rank-rewards: the reward buffs follow the class grade (OBSERVED: Abyss class grade g -> 20100 level 21 - g) ----------
        var appliedBuffs = player.RankBuffs;
        var addBuff = arenaTick.FirstOrDefault(p => Op(p) == 0x382a && p.AsSpan().IndexOf(BitConverter.GetBytes(20100)) > 0);
        check(appliedBuffs.Count == 1 && appliedBuffs[0] == (20100, 13, Ms(2026, 12, 16, 7)) && addBuff is not null
              && player.CombatStat("PvPAmplifyDamage") >= rank.BuffStats(20100, 13)["PvPAmplifyDamage"] && rank.BuffStats(20100, 6)["PvPDecreaseDamage"] == 200,
            $"at the list update the Abyss class grade 8 gives 'Abyss Tier Rewards' 20100 level 13 until the season's end (0x382a), its PvP stats from SkillAbnormalEffectLv: {string.Join(",", appliedBuffs)}");
        var buffUpdate = await player.RankRewardsTickForChecksAsync(st + 2 * 3_600_000 + 5);
        var change = buffUpdate.FirstOrDefault(p => Op(p) == 0x382b);
        var at = change is null ? -1 : change.AsSpan().IndexOf(BitConverter.GetBytes(20100));
        static int MaskOf(byte[] p) { var i = 2; while (p[i++] >= 0x80) { } return p[i]; }   // after the key varint
        check(change is not null && at > 0 && BitConverter.ToInt64(change, at + 4) == Ms(2026, 12, 16, 7) - (st + 2 * 3_600_000 + 5)
              && BitConverter.ToInt64(change, at + 12) == Ms(2026, 12, 16, 7) && MaskOf(change) == 0x11 && player.RankBuffs.Count == 1,
            "at the next hourly update the buff is applied again: 0x382b, same uid, mask 0x11, duration = time left, expire = season end (OBSERVED 12:13:03 / 21:13:04)");
        var abyssRow = store.SeasonPoints.Single(p => p.ContentsType == 1);
        store.SeasonPoints.Remove(abyssRow);
        store.SeasonPoints.Add(abyssRow with { Point = 100 });
        var buffGone = await player.RankRewardsTickForChecksAsync(st + 3 * 3_600_000 + 5);
        check(player.RankBuffs.Count == 0 && buffGone.Any(p => Op(p) == 0x382c && p[^1] == 7),
            "below MinRankingPoint the player leaves the Abyss list and the buff is removed (0x382c, reason 7)");
        store.SeasonPoints.Remove(store.SeasonPoints.Single(p => p.ContentsType == 1));
        store.SeasonPoints.Add(abyssRow);
        static SeasonEntry Entry(byte cls, int grade, int classGrade) => new(Row(1, "E", 1), cls, 1, 1, grade, classGrade, classGrade, 0, 0, 0);
        var abyssS1 = rank.ContentsOf(1, 1)!;
        check(GameSession.RankBuffsFor([(abyssS1, Entry(2, 0, 15))], 1).SequenceEqual([(20100, 6)]) && GameSession.RankBuffsFor([(abyssS1, Entry(2, 0, 18))], 2).SequenceEqual([(20100, 3)])
              && GameSession.RankBuffsFor([(abyssS1, Entry(2, 0, 1))], 1).SequenceEqual([(20100, 21)]) && GameSession.RankBuffsFor([(abyssS1, Entry(2, 1, 19))], 1).Count == 0
              && GameSession.RankBuffsFor([(rank.ContentsOf(1, 3)!, Entry(2, 0, 1))], 1).Count == 0,
            "the buff level per Abyss class grade as every official 0x3645 (15 -> 6, 18 -> 3, 1 -> 21; 19 Trainee none); season 1's other rankings give none");
        check(GameSession.RankBuffsFor([(abyssS1, Entry(2, 0, 15)), (rank.ContentsOf(2, 5)!, Entry(2, 0, 1))], 1).SequenceEqual([(20101, 21)])
              && GameSession.RankBuffsFor([(abyssS1, Entry(2, 0, 1)), (rank.ContentsOf(2, 5)!, Entry(2, 0, 5))], 1).SequenceEqual([(20100, 21)])
              && GameSession.RankBuffsFor([(rank.ContentsOf(3, 20)!, Entry(2, 0, 3))], 1).SequenceEqual([(20104, 17)]),
            "only the highest of the Abyss / arena buffs applies (client text); Subjugation's 20104 from season 3");

        // ---------- fix/rank-rewards: the weekly Abyss class rewards, once per week ----------
        check(rank.WeekStartAt(Ms(2026, 11, 13)) == Ms(2026, 11, 11, 7) && rank.WeekStartAt(Ms(2026, 11, 11, 7)) == Ms(2026, 11, 11, 7)
              && rank.WeekStartAt(Ms(2026, 11, 11, 7) - 1) == Ms(2026, 11, 4, 7) && rank.Weekly is { ClassUse: true, CommonUse: false, ClassGroup: "Weekly_Reward_Class_Abyss", Day: 3 },
            "the weekly reset: Wednesday 16:00 KST = 07:00 UTC (weekly_reset_day 3, weekly_quest_reset_time); GlobalSetting pays the class rewards only");
        var weeklyStore = new FakeStore();
        var weeklyPlayer = await storedWorldSession("RankWeekly", weeklyStore);
        weeklyPlayer.ClearRankingsForChecks();
        weeklyStore.SeasonPoints.Add(new SeasonPointRow(1, 1, 7_000_000, 0, "{}"));
        var rival = Row(8, "Top", 21_000_000) with { CreationId = weeklyPlayer.CreationIdForChecks, Gender = weeklyPlayer.GenderForChecks };
        weeklyStore.Rankings.Add(rival);
        weeklyStore.Rankings.Add(Row(9, "Low", 2_000) with { CreationId = weeklyPlayer.CreationIdForChecks, Gender = weeklyPlayer.GenderForChecks });
        var reset = Ms(2026, 11, 18, 7);
        weeklyPlayer.ResetRankRewardsForChecks();
        await weeklyPlayer.RankRewardsTickForChecksAsync(reset + 5_000);
        var mine = weeklyStore.WeeklyMails.Where(m => m.Character == weeklyStore.Character).Select(m => m.Post).ToList();
        var top = weeklyStore.WeeklyMails.Where(m => m.Character == rival.Character).Select(m => m.Post).ToList();
        bool Fits(PostRecord p, int grade) => p is { DataId: GameSession.WeeklyClassRewardPost, Tab: 2 } && p.Attachments.Count == 1
            && rank.Weekly.ClassGroup is var g && RankTables.RewardItems(rank.RewardOf(g, 2, grade), 1).Concat(RankTables.RewardItems(rank.RewardOf(g, 2, grade), 2))
                .Any(i => i.Item == p.Attachments[0].TemplateId)
            && (p.Expire - p.Sent).TotalDays is > 6.99 and < 7.01;
        check(weeklyStore.WeeklyMails.Count == 2 && mine.Count == 1 && Fits(mine[0], 4) && top.Count == 1 && Fits(top[0], 1)
              && weeklyStore.WeeklySettled.Contains((1, DateTimeOffset.FromUnixTimeMilliseconds(reset))),
            $"the week to 2026-11-18 07:00 pays the Abyss class grades 1-4 (rank 1 with 21M: grade 1; the player, rank 2 with 7M: grade 4) a transformation candy by mail (Post 26, Reward tab, 7 days); grade 18 nothing ({weeklyStore.WeeklyMails.Count} mails)");
        var notice = await weeklyPlayer.TickAsync(reset + 6_000);
        check(notice.Any(p => Op(p) == GameSession.PostRecvNt), "the online receiver gets RecvPost_NT at once");
        weeklyPlayer.ClearRankingsForChecks();   // as after a restart: the host's memory is empty, the store's settlement row stays
        weeklyPlayer.ResetRankRewardsForChecks();
        await weeklyPlayer.RankRewardsTickForChecksAsync(reset + 60_000);
        var other = await storedWorldSession("RankWeekly2", weeklyStore);
        other.ResetRankRewardsForChecks();
        await other.RankRewardsTickForChecksAsync(reset + 61_000);
        check(weeklyStore.WeeklyMails.Count == 2, "the same week again (after a restart, from another session) pays nothing: the settlement row is kept");
        weeklyPlayer.ResetRankRewardsForChecks();
        await weeklyPlayer.RankRewardsTickForChecksAsync(Ms(2026, 9, 30, 8));
        check(weeklyStore.WeeklyMails.Count == 2 && !weeklyStore.WeeklySettled.Any(w => w.WeekEnd == DateTimeOffset.FromUnixTimeMilliseconds(Ms(2026, 9, 30, 7))),
            "the reset that starts season 1 pays nothing (no live season before it)");
        check(GameSession.SuppressionClearPoints("Hell") == 2500 && GameSession.SuppressionClearPoints("easy") == 100,
            "Subjugation rank points: basic 100 x the difficulty factor (GlobalSetting; time and deaths CANDIDATE)");
        weeklyPlayer.ExitSharedWorld();
        other.ExitSharedWorld();

        // ---------- MyCharAppear's Abyss ranking point (mask bit 16) ----------
        var plain = GamePackets.MyCharAppear(new MyCharacterAppear { Key = 1, Name = "x", CustomizingData = [] });
        var pointed = GamePackets.MyCharAppear(new MyCharacterAppear { Key = 1, Name = "x", CustomizingData = [], AbyssRankingPoint = 2000 });
        check(pointed.Length == plain.Length + 8 && (BitConverter.ToUInt32(pointed, 3) & 1u << 16) != 0 && (BitConverter.ToUInt32(plain, 3) & 1u << 16) == 0,
            "MyCharAppear_NT: the Abyss ranking point as mask bit 16 (8 bytes), none without points (as every official entry)");

        // ---------- The ended season: results once, MyPrevSeasonRanking_NT, the reward ----------
        player.ClearRankingsForChecks();
        store.SeasonPoints.Add(new SeasonPointRow(3, 1, 1_200_000, 11_300, "{\"boss_group_id\":3001}"));
        store.Rankings.Add(Row(7, "Rival", 900_000, 11_000) with { CreationId = player.CreationIdForChecks, Gender = player.GenderForChecks });
        var rewardTime = Ms(2026, 12, 20);
        await player.LoadSeasonRankingForChecksAsync(rewardTime);
        var results = store.SeasonResults.Where(r => r.ContentsType == 3 && r.Season == 1).ToList();
        var mineResult = player.PrevSeasonResults.SingleOrDefault(r => r.ContentsType == 3);
        check(results.Count == 2 && mineResult is { Rank: 1, ClassRank: 1, ClassTotal: 2, ClassGrade: 1, GradeDisplay: 1, RewardTaken: false },
            "after season 1 the boss challenge results are written once (rank 1 with 1.2M points: class grade 1)");
        var prevList = player.MyPrevSeasonRanking();
        check(prevList.Length > 3 && prevList[2] == player.PrevSeasonResults.Count && prevList[3] == player.PrevSeasonResults[0].ContentsType
              && player.PrevSeasonResults.Select(r => r.ContentsType).SequenceEqual([1, 3, 5]),
            "MyPrevSeasonRanking_NT lists the season's results (Abyss, boss challenge, arena)");
        check(RankTables.RewardItems(rank.RewardOf("Reward_Season_Class_BossChallenge_G1", 2, 1), 1).Select(i => i.Item).SequenceEqual([532520012, 536010014])
              && RankTables.RewardItems(rank.RewardOf("Reward_Season_Class_BossChallenge_G1", 2, 1), 2)[0].Item == 532520034
              && rank.RewardOf("Reward_Season_Class_Abyss_G1", 2, 1)!.LightBuffs.Single().SequenceEqual([20100, 21]),
            "SeasonRankingReward: boss challenge grade 1 = Title_L_e_Season_012_b + skin (Elyos), Title_D_e_Season_012_b (Asmodian); Abyss grade 1 buff 20100 level 21");
        var items = GameSession.SeasonRewardItems(mineResult!, 1);
        var take = await player.HandleAsync(new PacketWriter().U16(GameSession.TakePrevSeasonRankingRewardRq).U8(3).ToArray(), rewardTime + 1);
        var again = await player.HandleAsync(new PacketWriter().U16(GameSession.TakePrevSeasonRankingRewardRq).U8(3).ToArray(), rewardTime + 2);
        var early = await entry.HandleAsync(new PacketWriter().U16(GameSession.TakePrevSeasonRankingRewardRq).U8(3).ToArray(), Ms(2026, 11, 12));
        check(items.Count == RankTables.RewardItems(rank.RewardOf("Reward_Season_Class_BossChallenge_G1", mineResult!.Class, 1), 1).Count
              && Res(take[0]) == 0 && take[0][4] == 3 && take[0][5] == (items.Count > 0 ? 1 : 0) && Op(take[1]) == GameSession.MyPrevSeasonRankingNt
              && Res(again[0]) == EResult.SeasonRanking_NoRewardToTake && store.SeasonResults.Single(r => r.Character == store.Character && r.ContentsType == 3).RewardTaken
              && Res(early[0]) == EResult.SeasonRanking_NoRewardableTime,
            "TakePrevSeasonRankingReward: the class grade's reward once; again NoRewardToTake; outside the reward period NoRewardableTime");
        player.ExitSharedWorld();
        entry.ExitSharedWorld();

        // ---------- Migration 094 ----------
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "database", "migrations"))) root = root.Parent;
        var migration = root is null ? "" : File.ReadAllText(Path.Combine(root.FullName, "database", "migrations", "094_season_rankings.sql"));
        check(migration.Contains("CREATE TABLE game.season_ranking_points") && migration.Contains("CREATE TABLE game.season_ranking_results")
              && migration.Contains("PRIMARY KEY (character_id, contents_type, season_no)")
              && migration.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON game.season_ranking_points, game.season_ranking_results TO aion_app")
              && migration.Contains("FROM game.character_boss_challenges b") && migration.Contains("s.name = 'season_points'"),
            "migration 094: season points and results per character, type and season; the boss challenge records and season_points settings kept as season 1");
        var weeklyMigration = root is null ? "" : File.ReadAllText(Path.Combine(root.FullName, "database", "migrations", "096_weekly_ranking_rewards.sql"));
        check(weeklyMigration.Contains("CREATE TABLE game.season_weekly_settlements") && weeklyMigration.Contains("PRIMARY KEY (contents_type, week_end)")
              && weeklyMigration.Contains("GRANT SELECT, INSERT, UPDATE ON game.season_weekly_settlements TO aion_app"),
            "migration 096: one settlement row per ranking type and weekly reset (a week pays once)");
    }
}
