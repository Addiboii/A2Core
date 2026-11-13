using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Rank (the client's Ranking window; SeasonRanking.dat: BossChallenge, PartyChallenge, Abyss, ArenaSingle/Team, Awaken,
// Conqueror, Suppression, all UpdatePeriod 60 min). The 0x8d run (usmap 0x398e + 0x53c5):
//   SeasonSchedule_NT 0x8d53 (entry: the season of each ESeasonContentsType, OBSERVED identical in every entry)
//   RetrieveLatestSeasonRanking_RQ 0x8d54 {ESeasonContentsType u8, ECharacterClass u8 (0 all)} -> RS 0x8d55
//   RetrieveMyLatestSeasonRanking_RQ 0x8d56 {type, class} -> RS 0x8d57 (OBSERVED: the client asks {Abyss, 0} at every entry)
//   MyPrevSeasonRanking_NT 0x8d58 (entry: OBSERVED "00"), TakePrevSeasonRankingReward_RQ 0x8d59 {type} -> RS 0x8d5a
// SeasonRankingData OBSERVED (8 official lists of 376-500 entries, recording 20261106-115817, and the user's own boss challenge
// entry #175066; every body parses to the byte): rank i32 | dbid i64 | nickname str | guild str | guild emblem u16 | level i32 |
// conqueror level i32 | ECharacterClass u8 | point i64 | extra display JSON str | rank change i32 | previous season rank i32 |
// grade i32 | grade display i32. List RS: u16 0 | type | class | entries varint | next update i64 | total ranked i32. My RS:
// result u16 | type | class | entry | total i32 | next update i64; nobody ranked = the OBSERVED 403e (SeasonRanking_NoRanker) form.
//
// The rules (feature/rank-system, docs: a2core-log/rank-system-2026-11-12.md), OBSERVED in those lists unless marked:
// - Seasons: SeasonSchedule Season_Main_Live (season 1: 2026-09-30 07:00 to 2026-12-16 07:00 UTC = Wednesday 16:00 KST, the table's
//   KST dates: OBSERVED as the Abyss tier buff's expiry, fix/rank-rewards); points
//   count per season (game.season_ranking_points, migration 094); at the season's end the lists are final (results written at the
//   first entry after it, game.season_ranking_results) and their rewards can be taken during the reward period.
// - The lists update every UpdatePeriod (60 min): the ranks, the changes and next_update_time come from that update
//   (SharedWorld.Rankings). The order: the best clear's points, then the ranking point (SeasonSnapshot).
// - Grades: RankTables.GradeOf (first grade whose RankMax and NeedRankingPoint the entry reaches); the class list's grade is
//   the class grade group's, the whole list's the common group's (0 when the season has none: S1 boss challenge, OBSERVED grade 0
//   with grade display 11); grade_display is the class grade when DisplayRankingGrade is Class (every S1 ranking).
// - Extra JSON per type as official: BossChallenge {"boss_group_id","best_clear_point_level","best_clear_point_time",
//   "best_clear_point"}, PartyChallenge {"dungeon_info":[{"dungeon_group_id","best_clear_level","best_clear_point_time",
//   "best_clear_point"}]}, Awaken {"dungeon_id","dungeon_difficulty","best_clear_point_time","best_clear_point"}, Abyss
//   {"kill_count","death_count","assist_count"}, ArenaSingle/Team {"play_count","win_count","lose_count"}.
// - Points: boss challenge clear = 4000 x (tier - 1) + 300 x (level + 1) - seconds (OBSERVED on 230 clears); Ascension trial
//   clear = 7900 - 5 x seconds at difficulty 4, 4620 - 3 x seconds at difficulty 3 (OBSERVED; the other difficulties CANDIDATE);
//   Transcendence clear ~ 1000 x level + 600 - 0.12 x level x seconds (CANDIDATE fit of 493 clears: the official score adds
//   monster/boss/bonus parts the tables do not give); the ranking point is the season's sum of clear points (CANDIDATE: OBSERVED
//   sums far above the best clear). Abyss: the season's Abyss Points (CANDIDATE), MinRankingPoint 1500. Arena: a rating from 1500
//   (OBSERVED start in ArenaRanking_NT), Elo K 32 per match (CANDIDATE).
// Conqueror and Suppression: no points kept, their lists are empty. Suppression (Subjugation) ranks from season 2 (SeasonContents
// 209/308, buff 20104; points GameSession.SuppressionClearPoints, no Subjugation dungeon yet); Conqueror has a SeasonRanking row and
// grades by rank only (ranking_grade_conqueror 1-10/11-30/31-60/61-100) but no SeasonContents row in any season and every official
// list entry shows conqueror level 0 (4,000 entries of recording 20261106-115817), so no Conqueror ranking runs (OBSERVED).
// The reward buffs and the weekly Abyss rewards: GameSession.RankRewards.cs (fix/rank-rewards).
public sealed partial class GameSession
{
    public const byte SeasonContentsAbyss = 1, SeasonContentsPartyChallenge = 4, SeasonContentsArenaSingle = 5, SeasonContentsArenaTeam = 6,
        SeasonContentsAwaken = 21;
    public const int SeasonRankingListMax = 500;
    const int SeasonNoRanker = 0x3e40;   // EResult SeasonRanking_NoRanker (15936), OBSERVED as `403e` in the empty reply
    public const ushort SeasonScheduleNt = 0x8d53, MyPrevSeasonRankingNt = 0x8d58, ArenaRankingNt = 0xe32a, RankingPointUpdateNt = 0xe273;
    /// <summary>ArenaRanking_NT's start rating (OBSERVED 1500 for Single, Team and Strategy at every entry).</summary>
    public const long ArenaStartRating = 1500;
    /// <summary>Elo factor of an arena match (CANDIDATE: official ratings 1167-2501 after 11-118 matches).</summary>
    public const int ArenaRatingFactor = 32;
    /// <summary>A fallen Abyss ranker stays on the world map this long (CANDIDATE).</summary>
    public const long FallenRankerMs = 600_000;
    /// <summary>Abyss display grades shown as rankers on the world map: 1-8, the class ranks 1-100 the PvP rank buffs name (CANDIDATE).</summary>
    public const int FallenRankerGrade = 8;
    static RankTables Rank => RankTables.Embedded;

    static readonly Dictionary<string, byte> ClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Novice"] = 1, ["Gladiator"] = 2, ["Templar"] = 3, ["Ranger"] = 4, ["Assassin"] = 5, ["Elementalist"] = 6, ["Sorcerer"] = 7,
        ["Cleric"] = 8, ["Chanter"] = 9, ["Fighter"] = 12,
    };

    byte ClassOf(int creationId, int gender) => data.Profile(creationId, gender) is { } p && ClassIds.TryGetValue(p.Class, out var id) ? id : (byte)0;
    byte MyClass => ClassOf(character?.CreationId ?? 0, character?.Gender ?? 0);

    // The character's season state: the current live season's points (loaded at the entry, saved at the tick), the ended season's
    // results during its reward period, the Abyss grade others see.
    int seasonNo;
    readonly Dictionary<int, SeasonPointRow> seasonPoints = [];
    readonly Dictionary<(int Type, int Season), SeasonPointRow> seasonPointsDirty = [];
    List<SeasonResultRow> prevSeasonResults = [];
    int prevSeasonNo;
    int abyssGradeShown = OfficialAbyssRankingGrade;

    /// <summary>The character's Abyss grade as others see it (CharAppear _abyss_ranking_grade; 19 Trainee unranked, OBSERVED).</summary>
    public int AbyssGradeShown => abyssGradeShown;
    /// <summary>The character's ranking points of a type in the current season (0 when none).</summary>
    public long SeasonPointOf(int type) => seasonPoints.GetValueOrDefault(type)?.Point ?? 0;
    public SeasonPointRow? SeasonRowOf(int type) => seasonPoints.GetValueOrDefault(type);
    public IReadOnlyList<SeasonResultRow> PrevSeasonResults => prevSeasonResults;

    /// <summary>World entry: the season's points, the ended season finalized (its results written once) and this character's results,
    /// the Abyss grade. A store without the tables (migration 094 not applied) leaves everything empty.</summary>
    async Task LoadSeasonRankingAsync(INativeCharacterStore store, Guid character, long now, CancellationToken ct)
    {
        seasonPoints.Clear();
        seasonPointsDirty.Clear();
        prevSeasonResults = [];
        prevSeasonNo = 0;
        abyssGradeShown = OfficialAbyssRankingGrade;
        (rankBuffCheckAt, weeklyCheckAt) = (0, 0);   // the reward buffs and the week's rewards at the first tick (GameSession.RankRewards.cs)
        seasonNo = Rank.SeasonAt(now)?.No ?? 0;
        try
        {
            if (seasonNo > 0)
                foreach (var row in await store.SeasonPointsAsync(character, seasonNo, ct)) seasonPoints[row.ContentsType] = row;
            if (Rank.RewardSeasonAt(now) is { } ended)
            {
                await FinalizeSeasonAsync(store, ended, ct);
                prevSeasonNo = ended.No;
                prevSeasonResults = (await store.SeasonResultsAsync(ended.No, 0, character, ct)).ToList();
            }
            if (seasonNo > 0 && await SnapshotAsync(SeasonContentsAbyss, seasonNo, now, ct) is { } abyss && abyss.Of(character) is { Display: > 0 } mine)
                abyssGradeShown = mine.Display;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"season ranking not loaded: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The final results of an ended season, written once for every ranked type (at the first entry after its end).</summary>
    async Task FinalizeSeasonAsync(INativeCharacterStore store, RankTables.Season ended, CancellationToken ct)
    {
        if (World?.Rankings.Finalized(ended.No) == true) return;
        foreach (var contents in Rank.RankedContents(ended.No))
        {
            if ((await store.SeasonResultsAsync(ended.No, contents.Type, null, ct)).Count > 0) continue;
            var rows = await store.SeasonRankingAsync(contents.Type, ended.No, 20_000, ct);
            if (rows.Count == 0) continue;
            var final = SeasonSnapshot.Build(contents.Type, ended.No, contents, rows, r => ClassOf(r.CreationId, r.Gender), ended.End, ended.End, null, null, Rank.MinPoint(contents.Type));
            var written = await store.SaveSeasonResultsAsync(final.Results(), ct);
            Note($"season {ended.No} type {contents.Type}: {written} final results written");
        }
        World?.Rankings.MarkFinalized(ended.No);
    }

    /// <summary>A season ranking as of its last update (rebuilt from the store when the update time passed).</summary>
    async Task<SeasonSnapshot?> SnapshotAsync(int type, int season, long now, CancellationToken ct)
    {
        if (Store is not { } store || season <= 0) return null;
        var cache = World?.Rankings;
        if (cache?.Current(type, season, now) is { } held) return held;
        var rows = await StoredValue(() => store.SeasonRankingAsync(type, season, 20_000, ct), (IReadOnlyList<SeasonRankingRow>)[]);
        var prevRanks = season > 1
            ? (await StoredValue(() => store.SeasonResultsAsync(season - 1, type, null, ct), (IReadOnlyList<SeasonResultRow>)[])).ToDictionary(r => r.Character, r => r.Rank)
            : null;
        var snapshot = SeasonSnapshot.Build(type, season, Rank.ContentsOf(season, type), rows, r => ClassOf(r.CreationId, r.Gender), now,
            SeasonSnapshot.NextUpdateAfter(now, Rank.UpdateMinutes(type)), cache?.Latest(type, season), prevRanks, Rank.MinPoint(type));
        cache?.Store(snapshot);
        if (type == SeasonContentsAbyss && CharacterId is { } me) abyssGradeShown = snapshot.Of(me) is { Display: > 0 } mine ? mine.Display : OfficialAbyssRankingGrade;
        return snapshot;
    }

    async Task<List<byte[]>?> RankingRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var opcode = Opcode(plain);
        if (opcode == TakePrevSeasonRankingRewardRq) return await TakeSeasonRewardAsync(plain, now, ct);
        if (opcode is not (LatestSeasonRankingRq or MySeasonRankingRq) || plain.Length < 4) return null;
        var type = plain[2];
        var cls = plain[3];
        var season = Rank.SeasonAt(now);
        var snapshot = season is not null && Rank.ContentsOf(season.No, type) is { Ranked: true } ? await SnapshotAsync(type, season.No, now, ct) : null;
        var next = snapshot?.NextUpdate ?? SeasonSnapshot.NextUpdateAfter(now, Rank.UpdateMinutes(type));
        var list = snapshot?.List(cls) ?? [];
        if (opcode == LatestSeasonRankingRq)
        {
            var w = PacketWriter.Opcode(LatestSeasonRankingRs).U16(0).U8(type).U8(cls).Varint(Math.Min(list.Count, SeasonRankingListMax));
            for (var i = 0; i < list.Count && i < SeasonRankingListMax; i++) WriteSeasonEntry(w, list[i], cls);
            return [w.I64(next).I32(list.Count).ToArray()];
        }
        var mine = CharacterId is { } me ? snapshot?.Of(me) : null;
        if (mine is null || cls != 0 && mine.Class != cls)
        {
            var body = (byte[])SeasonRankingNone.Clone();
            body[2] = type;
            body[3] = cls;
            return [PacketWriter.Opcode(MySeasonRankingRs).Raw(body).I64(next).ToArray()];
        }
        var my = PacketWriter.Opcode(MySeasonRankingRs).U16(0).U8(type).U8(cls);
        WriteSeasonEntry(my, mine, cls);
        return [my.I32(list.Count).I64(next).ToArray()];
    }

    /// <summary>One SeasonRankingData in the observed layout: the whole list's rank, change and grade for class 0, else the class list's.</summary>
    public static void WriteSeasonEntry(PacketWriter w, SeasonEntry e, byte cls) =>
        WriteSeasonEntry(w, cls == 0 ? e.Rank : e.ClassRank, e.Row, e.Class, cls == 0 ? e.RankChange : e.ClassRankChange, e.PrevSeasonRank,
            cls == 0 ? e.Grade : e.ClassGrade, e.Display);

    public static void WriteSeasonEntry(PacketWriter w, int rank, SeasonRankingRow row, byte cls, int change = 0, int prevSeasonRank = 0, int grade = 0, int display = 0) =>
        w.I32(rank).I64(row.WireId).String(row.Name).String(row.Guild).U16((ushort)row.GuildEmblem).I32(row.Level).I32(0).U8(cls).I64(row.Point).String(row.Extra)
            .I32(change).I32(prevSeasonRank).I32(grade).I32(display);

    // ---------------------------------------------------------------- points

    /// <summary>The live season of <paramref name="now"/>: a new season starts the points afresh (the old season's rows stay stored).</summary>
    void EnsureSeason(long now)
    {
        var current = Rank.SeasonAt(now)?.No ?? 0;
        if (current == seasonNo) return;
        seasonNo = current;
        seasonPoints.Clear();
    }

    void SetSeasonPoints(int type, long point, long best, string extra)
    {
        EnsureSeason(lastNow == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastNow);
        if (seasonNo <= 0) return;   // between seasons nothing counts (CANDIDATE)
        var row = new SeasonPointRow(type, seasonNo, point, best, extra);
        seasonPoints[type] = row;
        seasonPointsDirty[(type, seasonNo)] = row;
    }

    /// <summary>A clear of a ranked content: its points added to the season's sum; its extra JSON kept when it is the best clear.</summary>
    void AddClearPoints(int type, long points, string extra)
    {
        EnsureSeason(lastNow == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastNow);
        var row = seasonPoints.GetValueOrDefault(type);
        var best = row?.BestPoint ?? 0;
        SetSeasonPoints(type, (row?.Point ?? 0) + points, Math.Max(best, points), points >= best || row is null ? extra : row.Extra);
        Note($"season points: type {type} +{points} = {(row?.Point ?? 0) + points}");
    }

    /// <summary>Session tick: changed season points saved.</summary>
    async Task FlushSeasonPointsAsync(CancellationToken ct)
    {
        if (seasonPointsDirty.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var rows = seasonPointsDirty.Values.ToList();
        seasonPointsDirty.Clear();
        foreach (var row in rows) await Stored(async () => await store.SaveSeasonPointsAsync(character, row, ct) ? [[]] : []);
    }

    /// <summary>OBSERVED (230 boss challenge clears of the official lists): best_clear_point + best_clear_point_time = 4000 x (tier - 1)
    /// + 300 x (level + 1): group 3001 (tier 3) level 1 8600 ... level 10 11300, 2001/2002 (tier 2) level 10 7300, 1002 (tier 1) level 6
    /// 2100. At least 1 (CANDIDATE floor).</summary>
    public static long BossClearPoints(int tier, int level, int seconds) => Math.Max(1, 4000L * (Math.Max(1, tier) - 1) + 300L * (level + 1) - Math.Max(0, seconds));

    /// <summary>Ascension trial clear points: OBSERVED 7900 - 5 x seconds at difficulty 4 (rank 1: 7595 in 61 s), 4620 - 3 x seconds at
    /// difficulty 3 (67 clears); difficulties 1-2 (1540 x d - d x seconds) and 5+ (7900 + 3280 x (d - 4) - 5 x seconds) CANDIDATE.</summary>
    public static long AwakenClearPoints(int difficulty, int seconds)
    {
        seconds = Math.Max(0, seconds);
        var points = difficulty switch
        {
            >= 4 => 7900L + 3280L * (difficulty - 4) - 5L * seconds,
            3 => 4620L - 3L * seconds,
            _ => 1540L * Math.Max(1, difficulty) - (long)Math.Max(1, difficulty) * seconds,
        };
        return Math.Max(1, points);
    }

    /// <summary>Transcendence clear points (CANDIDATE: a fit of the official levels 2-4, 2470-4639 points; the official score adds
    /// rank, time, monster, boss and bonus scores the tables do not give).</summary>
    public static long PartyClearPoints(int level, int seconds) => Math.Max(1000L * level / 2, (long)Math.Round(1000.0 * level + 600 - 0.12 * level * Math.Max(0, seconds)));

    // ---------------------------------------------------------------- Abyss

    static (long Kills, long Deaths, long Assists) AbyssCounts(string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(extra);
            long Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Get("kill_count"), Get("death_count"), Get("assist_count"));
        }
        catch (JsonException) { return (0, 0, 0); }
    }

    static string AbyssExtra(long kills, long deaths, long assists) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"kill_count\":{kills},\"death_count\":{deaths},\"assist_count\":{assists}}}");

    /// <summary>The Abyss ranking: Abyss Points earned (the ranking point, uncapped: CANDIDATE), kills and deaths (the extra JSON);
    /// RankingPointUpdate_NT {ERankingPointType AbyssRankingPoint 0, point i64} when the point changed (usmap, CANDIDATE opcode 0xe273
    /// in the 0xe2 run of BossChallengeDungeonEnter_RQ 0xe234).</summary>
    void AbyssRanking(long points, int kills, int deaths)
    {
        var row = seasonPoints.GetValueOrDefault(SeasonContentsAbyss);
        var (k, d, a) = AbyssCounts(row?.Extra);
        SetSeasonPoints(SeasonContentsAbyss, (row?.Point ?? 0) + Math.Max(0, points), 0, AbyssExtra(k + kills, d + deaths, a));
        if (points > 0 && seasonNo > 0) pendingAchievementPackets.Add(RankingPointUpdate());
    }

    byte[] RankingPointUpdate() => PacketWriter.Opcode(RankingPointUpdateNt).U8(0).I64(SeasonPointOf(SeasonContentsAbyss)).ToArray();

    /// <summary>This player fell to the other race on an Abyss map: the death counts; a high Abyss grade shows on the world map.</summary>
    internal void AbyssDied(long now)
    {
        AbyssRanking(0, 0, 1);
        if (abyssGradeShown is > 0 and <= FallenRankerGrade && (position ?? worldPlayer?.Position) is { } at)
            World?.Rankings.Fallen(worldMap, (byte)(profileRace == 2 ? 2 : 1), at, now + FallenRankerMs);
    }

    /// <summary>WorldMapAbyssRankerInfo_RS {result u16, map i32, rankers varint {ERace u8, pos f32 x3, expire i64}} (usmap): the
    /// high-ranked players fallen on this map lately ("Show location of high-ranked player deaths in the Abyss"; CANDIDATE).</summary>
    byte[] AbyssRankerInfo(long now)
    {
        var fallen = World?.Rankings.FallenOn(worldMap, now) ?? [];
        var w = PacketWriter.Opcode(WorldMapAbyssRankerInfoRs).U16(0).I32(worldMap).Varint(fallen.Count);
        foreach (var (race, at, expire) in fallen) w.U8(race).F32((float)at.X).F32((float)at.Y).F32((float)at.Z).I64(expire);
        return w.ToArray();
    }

    // ---------------------------------------------------------------- arena

    /// <summary>EMatchingType of a Matching.dat id (Single 1, Team 2, Strategy 3) and its season ranking (ArenaSingle 5, ArenaTeam 6;
    /// Strategy none).</summary>
    static (byte Matching, int Season) ArenaKind(int matchingId) => Systems2Tables.Embedded.MatchingOf(matchingId)?.Type switch
    {
        "Single" => (1, SeasonContentsArenaSingle),
        "Team" => (2, SeasonContentsArenaTeam),
        "Strategy" => (3, 0),
        _ => (0, 0),
    };

    static (long Play, long Win, long Lose) ArenaCounts(string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(extra);
            long Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Get("play_count"), Get("win_count"), Get("lose_count"));
        }
        catch (JsonException) { return (0, 0, 0); }
    }

    /// <summary>The arena rating of a type (ArenaSingle 5 / ArenaTeam 6): 1500 before the first match.</summary>
    public long ArenaRating(int type) => seasonPoints.GetValueOrDefault(type)?.Point ?? ArenaStartRating;

    /// <summary>Elo change of one match: <paramref name="score"/> 1 won, 0.5 draw, 0 lost against the other team's mean rating.</summary>
    public static int ArenaRatingChange(long mine, double opponents, double score) =>
        (int)Math.Round(ArenaRatingFactor * (score - 1 / (1 + Math.Pow(10, (opponents - mine) / 400.0))));

    /// <summary>A finished arena match of this player: the rating, the counts and ArenaRanking_NT again (CANDIDATE: when the official
    /// server sends it after a match).</summary>
    internal void ArenaRated(int matchingId, double score, double opponents)
    {
        var (_, type) = ArenaKind(matchingId);
        if (type == 0) return;
        var rating = ArenaRating(type);
        var (play, win, lose) = ArenaCounts(seasonPoints.GetValueOrDefault(type)?.Extra);
        var next = Math.Max(1, rating + ArenaRatingChange(rating, opponents, score));
        SetSeasonPoints(type, next, 0, string.Create(CultureInfo.InvariantCulture,
            $"{{\"play_count\":{play + 1},\"win_count\":{win + (score >= 1 ? 1 : 0)},\"lose_count\":{lose + (score <= 0 ? 1 : 0)}}}"));
        pendingAchievementPackets.Add(ArenaRanking());
    }

    /// <summary>ArenaRanking_NT {count, {EMatchingType u8, ranking point i64, win, lose, draw, quit, serial win varint}}: Single, Team
    /// and Strategy (OBSERVED at every entry as 1500 and zeros for a new character; the counts' width CANDIDATE varint, 0 is one byte).</summary>
    public byte[] ArenaRanking()
    {
        var w = PacketWriter.Opcode(ArenaRankingNt).Varint(3);
        foreach (var (matching, type) in new (byte, int)[] { (1, SeasonContentsArenaSingle), (2, SeasonContentsArenaTeam), (3, 0) })
        {
            var row = type == 0 ? null : seasonPoints.GetValueOrDefault(type);
            var (play, win, lose) = ArenaCounts(row?.Extra);
            w.U8(matching).I64(row?.Point ?? ArenaStartRating).Varint(win).Varint(lose).Varint(Math.Max(0, play - win - lose)).Varint(0).Varint(0);
        }
        return w.ToArray();
    }

    // ---------------------------------------------------------------- the ended season

    /// <summary>MyPrevSeasonRanking_NT {count, {type u8, class u8, rank, total i32, point i64, extra str, reward taken bool, grade,
    /// grade display i32}} (usmap; OBSERVED "00" with none): the ended season's results during its reward period, the class list's
    /// when the season shows class grades (CANDIDATE which rank and the reward flag's sense).</summary>
    public byte[] MyPrevSeasonRanking()
    {
        var w = PacketWriter.Opcode(MyPrevSeasonRankingNt).Varint(prevSeasonResults.Count);
        foreach (var r in prevSeasonResults)
        {
            var byClass = Rank.ContentsOf(r.Season, r.ContentsType) is not { ShowsClassGrade: false };
            w.U8((byte)r.ContentsType).U8(byClass ? r.Class : (byte)0).I32(byClass ? r.ClassRank : r.Rank).I32(byClass ? r.ClassTotal : r.Total).I64(r.Point)
                .String(r.Extra).Bool(r.RewardTaken).I32(byClass ? r.ClassGrade : r.Grade).I32(r.GradeDisplay);
        }
        return w.ToArray();
    }

    /// <summary>The season reward items of a result: the common group by the whole list's grade and the class group by the class
    /// grade (SeasonRankingReward, Light for Elyos / Dark for Asmodians). The rows' buffs follow the current season's grades instead
    /// (GameSession.RankRewards.cs).</summary>
    public static List<(int Item, long Count)> SeasonRewardItems(SeasonResultRow result, int race)
    {
        var contents = Rank.ContentsOf(result.Season, result.ContentsType);
        if (contents is null) return [];
        return [.. RankTables.RewardItems(Rank.RewardOf(contents.RewardCommon, 0, result.Grade), race),
            .. RankTables.RewardItems(Rank.RewardOf(contents.RewardClass, result.Class, result.ClassGrade), race)];
    }

    /// <summary>TakePrevSeasonRankingReward_RQ {type u8} -> RS {result u16, type u8, has reward item bool} (usmap; result width as
    /// the other replies, CANDIDATE): during the ended season's reward period, once per type; then MyPrevSeasonRanking_NT again.</summary>
    async Task<List<byte[]>> TakeSeasonRewardAsync(byte[] plain, long now, CancellationToken ct)
    {
        var type = plain.Length >= 3 ? plain[2] : (byte)0;
        byte[] Reply(int result, bool items) => PacketWriter.Opcode(TakePrevSeasonRankingRewardRs).U16(result).U8(type).Bool(items).ToArray();
        if (Rank.RewardSeasonAt(now) is not { } season || season.No != prevSeasonNo) return [Reply(EResult.SeasonRanking_NoRewardableTime, false)];
        var index = prevSeasonResults.FindIndex(r => r.ContentsType == type);
        if (index < 0 || prevSeasonResults[index].RewardTaken) return [Reply(EResult.SeasonRanking_NoRewardToTake, false)];
        var result = prevSeasonResults[index];
        if (Store is not { } store || CharacterId is not { } character || !await StoredValue(() => store.TakeSeasonRewardAsync(character, season.No, type, ct), false))
            return [Reply(EResult.SeasonRanking_NoRewardToTake, false)];
        prevSeasonResults[index] = result with { RewardTaken = true };
        var items = SeasonRewardItems(result, profileRace);
        if (items.Count > 0) pendingRewards.Add(items);
        Note($"season {season.No} reward type {type}: grade {result.Grade}/{result.ClassGrade}, {items.Count} items");
        return [Reply(0, items.Count > 0), MyPrevSeasonRanking()];
    }

    // ---------------------------------------------------------------- the season list

    /// <summary>SeasonSchedule_NT: the OBSERVED list (tools/data/season-list.hex, identical in all 10 entries) with the main seasons'
    /// rows (latest Season_Main_Live) following the schedule: latest = the live season of now, previous = the one before (season 1:
    /// Season_Main 1, as OBSERVED), next = the one after ("" 0 after the last). In season 1 this is the observed body byte for byte.</summary>
    byte[] SeasonSchedule(long now)
    {
        var official = Convert.FromHexString(SeasonListHex.Value);
        if (Rank.SeasonAt(now) is not { } season) return PacketWriter.Opcode(SeasonScheduleNt).Raw(official).ToArray();
        var r = new PacketReader(official);
        var count = (int)r.Varint();
        var w = PacketWriter.Opcode(SeasonScheduleNt).Varint(count);
        for (var i = 0; i < count; i++)
        {
            var key = r.U8();
            var (latest, latestNo, prev, prevNo, next, nextNo) = (r.String(), r.I32(), r.String(), r.I32(), r.String(), r.I32());
            if (latest == RankTables.LiveGroup && prev != "")
            {
                (latestNo, prevNo) = (season.No, season.No > 1 ? season.No - 1 : 1);
                prev = season.No > 1 ? RankTables.LiveGroup : "Season_Main";
                (next, nextNo) = Rank.LiveSeason(season.No + 1) is { } after ? (RankTables.LiveGroup, after.No) : ("", 0);
            }
            w.U8(key).String(latest).I32(latestNo).String(prev).I32(prevNo).String(next).I32(nextNo);
        }
        return w.ToArray();
    }

    // ---------------------------------------------------------------- clears (Transcendence, Ascension trial)

    /// <summary>The season type a cleared dungeon scores in, its level and the extra JSON of a clear (see the notes above), or null.</summary>
    static (byte Type, int Level, string Group)? SeasonClearOf(DungeonInfo room, int dungeonId)
    {
        if (room.Type == "Awaken") return (SeasonContentsAwaken, Math.Clamp(dungeonId % 10, 1, 9), "");
        if (Tickets.PartyGroupOf(dungeonId) is { } group)
            for (var level = 1; level <= 9; level++)
                if (Tickets.PartyLevel(group, level) is { } l && l.Dungeon == dungeonId) return (SeasonContentsPartyChallenge, level, group);
        return null;
    }

    public const ushort AwakenDungeonClearNt = 0xe33f, PartyDungeonClearNt = 0x9250;

    /// <summary>The clear notice of an Ascension trial (AwakenDungeonClear_NT {result u16, remaining s i32, deaths i32, clear point i32})
    /// or a Transcendence level (PartyDungeonClear_NT {level u8, clear s i32, EClearRank u8, rank point i64, rank score, time score,
    /// monster score, boss score, bonus score i32, EPartyChallengeRankReason u8}). Usmap fields, never recorded: CANDIDATE (rank S,
    /// the point as the rank point and score, Success).</summary>
    static byte[]? ClearNotice((byte Type, int Level, string Group) clear, DungeonInfo room, long points, int seconds, int deaths) =>
        clear.Type == SeasonContentsAwaken
            ? PacketWriter.Opcode(AwakenDungeonClearNt).U16(0).I32(Math.Max(0, (room.Limit > 0 ? room.Limit : room.Progress) - seconds)).I32(deaths).I32((int)points).ToArray()
            : PacketWriter.Opcode(PartyDungeonClearNt).U8((byte)clear.Level).I32(seconds).U8(1).I64(points).I32((int)points).I32(0).I32(0).I32(0).I32(0).U8(1).ToArray();

    /// <summary>A Transcendence or Ascension trial clear: its clear notice and its points in the season ranking.</summary>
    void RecordSeasonClear(DungeonInfo room, int dungeonId, long elapsedMs, int deaths)
    {
        if (SeasonClearOf(room, dungeonId) is not { } clear) return;
        var seconds = (int)Math.Max(0, elapsedMs / 1000);
        var points = clear.Type == SeasonContentsAwaken ? AwakenClearPoints(clear.Level, seconds) : PartyClearPoints(clear.Level, seconds);
        var notice = ClearNotice(clear, room, points, seconds, deaths);
        var extra = clear.Type == SeasonContentsAwaken
            ? JsonSerializer.Serialize(new { dungeon_id = dungeonId, dungeon_difficulty = clear.Level, best_clear_point_time = seconds, best_clear_point = points })
            : JsonSerializer.Serialize(new { dungeon_info = new[] { new { dungeon_group_id = PartyGroupNumber(clear.Group), best_clear_level = clear.Level,
                best_clear_point_time = seconds, best_clear_point = points } } });
        _ = InvokeAsync(_ =>
        {
            AddClearPoints(clear.Type, points, extra);
            return (true, notice is null ? new List<byte[]>() : [notice]);
        });
    }

    internal void RecordSeasonClearForChecks(int dungeonId, long elapsedMs, int deaths) =>
        RecordSeasonClear(MapEventData.Dungeon(dungeonId)!, dungeonId, elapsedMs, deaths);

    /// <summary>Checks only: the shared world's ranking lists forgotten (each check builds its own).</summary>
    internal void ClearRankingsForChecks() => World?.Rankings.Clear();

    /// <summary>Checks only: the world entry's season loading at another time (the checks' entries run before season 1).</summary>
    internal Task LoadSeasonRankingForChecksAsync(long now) =>
        Store is { } store && CharacterId is { } character ? LoadSeasonRankingAsync(store, character, now, CancellationToken.None) : Task.CompletedTask;

    /// <summary>Checks only: this player fell in the Abyss (the PvP kill path), on its own loop.</summary>
    internal void AbyssDiedForChecks(long now) => AbyssDied(now);

    /// <summary>Checks only: this player's appearance as others get it (0x3645).</summary>
    internal byte[] OtherAppearanceForChecks() => OtherAppearance();

    internal byte[] SeasonScheduleForChecks(long now) => SeasonSchedule(now);
    internal static string SeasonListHexForChecks => SeasonListHex.Value;
    internal void ArenaRatedForChecks(int matchingId, double score, double opponents) => ArenaRated(matchingId, score, opponents);
    internal int CreationIdForChecks => character?.CreationId ?? 0;
    internal int GenderForChecks => character?.Gender ?? 0;

    /// <summary>DungeonGroup.dat ids of the party challenge groups (PartyDungeonLevel Group): the official extra's dungeon_group_id (Arkanis 1006 OBSERVED).</summary>
    static readonly Dictionary<string, int> PartyGroupIds = new()
    {
        ["DeusCenter_G_Group"] = 1005, ["Arkanis_G_Group"] = 1006, ["SunkenTemple_G_Group"] = 1010, ["MirrorHeart_G_Group"] = 1011, ["FafniteGully_G_Group"] = 1015,
    };

    static int PartyGroupNumber(string group) => PartyGroupIds.GetValueOrDefault(group);
}
