using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Rank rewards while a season runs (fix/rank-rewards, 2026-11-13; notes a2core-log/rank-rewards-2026-11-13.md).
//
// Reward buffs. SeasonRankingReward rows carry Light/DarkRewardBuffList (SkillAbnormal 20100 "Abyss Tier Rewards", 20101/20102 the
// arenas, 20103 Nightmare, 20104 Subjugation, 20105/20106 Ascension/Transcendence; StatChange rows per level from
// SkillAbnormalEffectLv: level 6 = PvP damage boost / tolerance / Abyss PvE boost 2.00%). The client text (Ranking window,
// UI_SEASON_POPUP_REWARD_DESC_*): "Buffs are applied based on current Class Rank. If there are multiple buffs of the same type, only
// the highest-Grade buff of that type will be applied. Buffs currently applied may change when Ranks and Grades are updated."
// OBSERVED (recordings 20261106-105448 ... 20261107-212241: 397 official 0x3645 with abnormal 20100, 2 own-update 0x382b):
//   - the level is the class grade's row of the season's class reward group: Abyss class grade g -> 20100 level 21 - g (g 2-18),
//     21 for g 1; grade 19 (Trainee) has no buff (every one of the 397 samples fits: grade 15 -> 6, 18 -> 3, 7 -> 14 ...);
//   - Common_Abnormal mask 0x11 (caster = the player, range base), duration = the time left, expire = the season's end
//     2026-12-16 07:00:00.05 UTC (the table's 16:00 KST);
//   - at the hourly ranking update (12:13:03, 21:13:04) the buff is applied again: 0x382b with the same uid and level, the new
//     duration, show_msg 1 - to the player and those around.
//   Applied here at the first tick in the world (0x382a, CANDIDATE: the own entry was never recorded with a buff) and at every
//   update of the lists (SeasonSnapshot.NextUpdate); removed (0x382c reason 7, CANDIDATE) when no grade gives it any more; at the
//   season's end it runs out (reason 1). Kept through death (CANDIDATE). The families ("Only the highest buff rewards out of the
//   Abyss, Arena of Solitude, and Arena of Cooperation are applied", RankTables.BuffFamilies) keep the highest level of each.
//
// Weekly Abyss rewards. GlobalSetting midseason_class_ranking_reward_use true / midseason_class_ranking_reward_group_abyss
// Weekly_Reward_Class_Abyss (class grades 1-4: transformation candy Candy_Polymorph_L/D_04/01/03/02_01_b), midseason_ranking_reward_use
// false (Weekly_Reward_Abyss by the whole list's grade, not used). Post.dat 26 "Abyss Class Rank Reward has been sent." (25 for the
// whole list), EPostSaveTab Reward, "Valid within 7 days of sending" (STR_WEEKLY_RANKING_REWARD_VALID_TIME). At the weekly reset
// (weekly_reset_day 3 = Wednesday, 16:00 KST = 07:00 UTC) the Abyss class list as of that moment pays the week that ended (only
// resets inside a live season; the season's last week too). CANDIDATE: no recording spans a Wednesday reset (the reset time, that the
// season's last week pays, the empty msg_params). Settlement safety: game.season_weekly_settlements (migration 096) - the store
// writes the week's row and its mails in one transaction and does nothing when the row exists, so a week pays once after restarts
// and when several sessions reach the reset at once; any session's tick (or the next entry) settles it, and as nobody earns points
// while offline the list is the one at the reset.
public sealed partial class GameSession
{
    public const int WeeklyRewardPost = 25, WeeklyClassRewardPost = 26, WeeklyRewardValidDays = 7;
    /// <summary>How long a failed settlement or buff refresh (store error, not in the world yet) waits before it is tried again.</summary>
    const long RankRetryMs = 60_000;
    long rankBuffCheckAt;
    long weeklyCheckAt;

    /// <summary>
    /// The reward buffs of a player's current entries (one per ranked content): the class reward group's row of the class grade and the
    /// common group's row of the whole list's grade (race's list), the highest level per abnormal, then per family the highest level
    /// (equal levels: the lower abnormal id, CANDIDATE).
    /// </summary>
    public static List<(int Abnormal, int Level)> RankBuffsFor(IEnumerable<(RankTables.Contents Contents, SeasonEntry Entry)> entries, int race)
    {
        var all = new List<(int Abnormal, int Level)>();
        foreach (var (contents, entry) in entries)
        {
            all.AddRange(RankTables.RewardBuffs(Rank.RewardOf(contents.RewardClass, entry.Class, entry.ClassGrade), race));
            all.AddRange(RankTables.RewardBuffs(Rank.RewardOf(contents.RewardCommon, 0, entry.Grade), race));
        }
        var best = all.Where(b => b.Level > 0).GroupBy(b => b.Abnormal).Select(g => (Abnormal: g.Key, Level: g.Max(b => b.Level))).ToList();
        return best.Where(b => RankTables.BuffFamilies.FirstOrDefault(f => f.Contains(b.Abnormal)) is not { } family
                || !best.Any(o => o.Abnormal != b.Abnormal && family.Contains(o.Abnormal) && (o.Level > b.Level || o.Level == b.Level && o.Abnormal < b.Abnormal)))
            .OrderBy(b => b.Abnormal).ToList();
    }

    /// <summary>The session tick's rank work: the week's rewards when a weekly reset passed, the reward buffs at each list update.</summary>
    async Task<List<byte[]>> RankRewardsTickAsync(long now, CancellationToken ct)
    {
        if (Store is null || CharacterId is null) return [];
        if (now >= weeklyCheckAt) await SettleWeekAsync(now, ct);
        return resources is not null && now >= rankBuffCheckAt ? await RefreshRankBuffsAsync(now, ct) : [];
    }

    /// <summary>
    /// The reward buffs as the current lists give them: new ones added (0x382a), the kept ones applied again with the time left and
    /// their level now (0x382b, OBSERVED at each hourly update), the ones no grade gives any more removed.
    /// </summary>
    async Task<List<byte[]>> RefreshRankBuffsAsync(long now, CancellationToken ct)
    {
        rankBuffCheckAt = now + RankRetryMs;
        if (CharacterId is not { } me) return [];
        var season = Rank.SeasonAt(now);
        var entries = new List<(RankTables.Contents, SeasonEntry)>();
        var next = SeasonSnapshot.NextUpdateAfter(now, 60);
        try
        {
            if (season is not null)
                foreach (var contents in Rank.RankedContents(season.No))
                {
                    if (await SnapshotAsync(contents.Type, season.No, now, ct) is not { } snapshot) continue;
                    next = Math.Min(next, snapshot.NextUpdate);
                    if (snapshot.Of(me) is { } entry) entries.Add((contents, entry));
                }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"rank buffs not refreshed: {e.GetType().Name}: {e.Message}");
            return [];
        }
        rankBuffCheckAt = season is null ? now + RankRetryMs : Math.Min(next, season.End);
        var wanted = season is null ? new List<(int Abnormal, int Level)>() : RankBuffsFor(entries, profileRace);
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        var before = buffs.Where(b => b.Rank).Select(b => (b.AbnormalId, b.Level)).OrderBy(b => b.AbnormalId).ToList();
        replies.AddRange(RemoveBuffs(buffs.Where(b => b.Rank && !wanted.Any(w => w.Abnormal == b.AbnormalId)).ToList(), ExpireEtc));
        foreach (var (abnormal, level) in wanted)
        {
            var stats = Rank.BuffStats(abnormal, level);
            AddBuff(abnormal, 1, 0, now, replies, setup: b =>
            {
                b.Rank = true;
                b.Level = level;
                b.DurationMs = Math.Max(1, season!.End - now);
                b.ExpireAt = season.End;
                b.Stats.Clear();
                foreach (var (stat, value) in stats) b.Stats[stat] = value;
            });
        }
        if (!before.SequenceEqual(wanted.Select(w => (w.Abnormal, w.Level)))) replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>The rank reward buffs the player has: (abnormal, level, expire time).</summary>
    public IReadOnlyList<(int Abnormal, int Level, long Expire)> RankBuffs => buffs.Where(b => b.Rank).Select(b => (b.AbnormalId, b.Level, b.ExpireAt)).ToList();

    /// <summary>
    /// The weekly reset at or before <paramref name="now"/> pays the week that ended there, once: the Abyss list of the season running
    /// then (as of the reset), the weekly reward rows of the class grade (and of the whole list's grade when GlobalSetting turns it on),
    /// one mail per reward; online receivers get RecvPost_NT at once.
    /// </summary>
    async Task SettleWeekAsync(long now, CancellationToken ct)
    {
        var reset = Rank.WeekStartAt(now);
        var cache = World?.Rankings;
        weeklyCheckAt = now + RankRetryMs;
        if (cache?.WeekSettled(reset) == true || Store is not { } store) { weeklyCheckAt = reset + RankTables.WeekMs; return; }
        var season = Rank.SeasonAt(reset - 1);
        var contents = season is null ? null : Rank.ContentsOf(season.No, SeasonContentsAbyss);
        var weekly = Rank.Weekly;
        if (season is null || contents is not { Ranked: true } || !weekly.ClassUse && !weekly.CommonUse)
        {
            cache?.MarkWeekSettled(reset);
            weeklyCheckAt = reset + RankTables.WeekMs;
            return;
        }
        try
        {
            var rows = await store.SeasonRankingAsync(SeasonContentsAbyss, season.No, 20_000, ct);
            var list = SeasonSnapshot.Build(SeasonContentsAbyss, season.No, contents, rows, r => ClassOf(r.CreationId, r.Gender), reset, reset, null, null,
                Rank.MinPoint(SeasonContentsAbyss));
            var mails = WeeklyRewardMails(list, weekly, r => data.Profile(r.CreationId, r.Gender)?.Race ?? 1);
            var sent = await store.SettleWeeklyRankingAsync(SeasonContentsAbyss, season.No, DateTimeOffset.FromUnixTimeMilliseconds(reset), mails,
                TimeSpan.FromDays(WeeklyRewardValidDays), ct);
            cache?.MarkWeekSettled(reset);
            weeklyCheckAt = reset + RankTables.WeekMs;
            if (sent is null) return;   // settled before (another session, an earlier run)
            Note($"weekly Abyss rewards of the week to {DateTimeOffset.FromUnixTimeMilliseconds(reset):u}: {sent.Count} mails");
            foreach (var (character, post) in sent)
                if (World?.FindOnline(character) is { } online)
                    online.Send(WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"weekly rewards not settled: {e.GetType().Name}: {e.Message}");   // tried again after RankRetryMs
        }
    }

    /// <summary>The weekly reward mails of a list: Post.dat 26 with the class grade's Weekly_Reward_Class_Abyss items, Post.dat 25 with the
    /// whole list's Weekly_Reward_Abyss items (only when GlobalSetting turns them on), the receiver's race's items.</summary>
    public static List<WeeklyRewardMail> WeeklyRewardMails(SeasonSnapshot list, RankTables.WeeklySettings weekly, Func<SeasonRankingRow, int> raceOf)
    {
        var mails = new List<WeeklyRewardMail>();
        foreach (var e in list.Entries)
        {
            var race = raceOf(e.Row);
            if (weekly.ClassUse && RankTables.RewardItems(Rank.RewardOf(weekly.ClassGroup, e.Class, e.ClassGrade), race) is { Count: > 0 } classItems)
                mails.Add(new WeeklyRewardMail(e.Row.Character, WeeklyClassRewardPost, classItems.Select(i => (i.Item, i.Count)).ToList()));
            if (weekly.CommonUse && RankTables.RewardItems(Rank.RewardOf(weekly.CommonGroup, 0, e.Grade), race) is { Count: > 0 } items)
                mails.Add(new WeeklyRewardMail(e.Row.Character, WeeklyRewardPost, items.Select(i => (i.Item, i.Count)).ToList()));
        }
        return mails;
    }

    /// <summary>Subjugation (Suppression) rank points of a clear: GlobalSetting suppression_rank_point_basic_score (100) x the
    /// difficulty's factor (easy 1, normal 6, hard 10, extreme 15, despair 20, hell 25). CANDIDATE: the client says the points also
    /// depend on the clear time and the deaths left, which no table gives; no Subjugation dungeon runs here yet (it ranks from season 2).</summary>
    public static long SuppressionClearPoints(string difficulty) =>
        (long)Math.Round(Rank.SuppressionFactor("basic_score") * Rank.SuppressionFactor(difficulty.ToLowerInvariant()));

    internal Task<List<byte[]>> RankRewardsTickForChecksAsync(long now) => RankRewardsTickAsync(now, CancellationToken.None);
    internal void ResetRankRewardsForChecks() => (rankBuffCheckAt, weeklyCheckAt) = (0, 0);
}
