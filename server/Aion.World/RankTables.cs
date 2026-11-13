using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Season ranking data of the client tables (tools/rank_data.py -> tools/data/rank.json): the live seasons (SeasonSchedule
/// Season_Main_Live, the table's KST dates: season 1 runs 2026-09-30 07:00 to 2026-12-16 07:00 UTC = Wednesday 16:00 KST, OBSERVED
/// as the official Abyss tier buff's expiry; its rewards until 2027-03-10),
/// the ranked contents per season (SeasonContents: grade groups, the grade shown, reward groups), SeasonRanking (MinRankingPoint,
/// UpdatePeriod 60 min), the grades (SeasonRankingGrade) and the rewards (SeasonRankingReward + SeasonRankingRewardItem); the reward
/// buffs' stats per level (SkillAbnormalEffectLv), the weekly (mid-season) rewards and the Subjugation point factors (GlobalSetting).
/// </summary>
public sealed class RankTables
{
    public sealed record Season([property: JsonPropertyName("group")] string Group, [property: JsonPropertyName("no")] int No,
        [property: JsonPropertyName("start")] long Start, [property: JsonPropertyName("end")] long End,
        [property: JsonPropertyName("reward_start")] long RewardStart, [property: JsonPropertyName("reward_end")] long RewardEnd);
    public sealed record Contents([property: JsonPropertyName("group")] string Group, [property: JsonPropertyName("no")] int No,
        [property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("display")] string Display,
        [property: JsonPropertyName("grade_group")] string GradeGroup, [property: JsonPropertyName("class_grade_group")] string ClassGradeGroup,
        [property: JsonPropertyName("grade_info")] string GradeInfo, [property: JsonPropertyName("reward_common")] string RewardCommon,
        [property: JsonPropertyName("reward_class")] string RewardClass)
    {
        /// <summary>A ranked content (it has a grade group); SeasonMission and ItemSuccession are season contents without a ranking.</summary>
        public bool Ranked => GradeGroup != "" || ClassGradeGroup != "";
        /// <summary>ESeasonRankingGradeType Class: the grade shown (grade_display, the nameplate) is the class list's.</summary>
        public bool ShowsClassGrade => Display == "Class";
    }
    public sealed record Ranking([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("min_point")] long MinPoint,
        [property: JsonPropertyName("update_min")] int UpdateMinutes);
    public sealed record Grade([property: JsonPropertyName("grade")] int Value, [property: JsonPropertyName("rank_max")] int RankMax,
        [property: JsonPropertyName("pct_max")] int PercentMax, [property: JsonPropertyName("need")] long Need);
    public sealed record Reward([property: JsonPropertyName("class")] int Class, [property: JsonPropertyName("grade")] int Grade,
        [property: JsonPropertyName("light")] List<long[]> Light, [property: JsonPropertyName("dark")] List<long[]> Dark,
        [property: JsonPropertyName("light_buffs")] List<int[]> LightBuffs, [property: JsonPropertyName("dark_buffs")] List<int[]> DarkBuffs);

    /// <summary>A reward buff's abnormal (SkillAbnormal 20100 "Abyss Tier Rewards" ...): its group and its StatChange stats per level.</summary>
    public sealed record BuffInfo([property: JsonPropertyName("group")] int Group,
        [property: JsonPropertyName("levels")] Dictionary<string, Dictionary<string, int>> Levels);
    /// <summary>GlobalSetting's mid-season (weekly) ranking rewards and the weekly reset (EDayOfWeek, ms of the day in KST).</summary>
    public sealed record WeeklySettings([property: JsonPropertyName("day")] int Day, [property: JsonPropertyName("time_ms")] long TimeMs,
        [property: JsonPropertyName("common_use")] bool CommonUse, [property: JsonPropertyName("common_group")] string CommonGroup,
        [property: JsonPropertyName("class_use")] bool ClassUse, [property: JsonPropertyName("class_group")] string ClassGroup);

    sealed class File
    {
        [JsonPropertyName("buffs")] public Dictionary<string, BuffInfo> Buffs { get; set; } = [];
        [JsonPropertyName("weekly")] public WeeklySettings? Weekly { get; set; }
        [JsonPropertyName("suppression")] public Dictionary<string, double> Suppression { get; set; } = [];
        [JsonPropertyName("seasons")] public List<Season> Seasons { get; set; } = [];
        [JsonPropertyName("contents")] public List<Contents> Contents { get; set; } = [];
        [JsonPropertyName("ranking")] public List<Ranking> Ranking { get; set; } = [];
        [JsonPropertyName("grades")] public Dictionary<string, List<Grade>> Grades { get; set; } = [];
        [JsonPropertyName("grade_names")] public Dictionary<string, Dictionary<string, string>> GradeNames { get; set; } = [];
        [JsonPropertyName("rewards")] public Dictionary<string, List<Reward>> Rewards { get; set; } = [];
    }

    public const string LiveGroup = "Season_Main_Live";
    readonly File file;
    RankTables(File file) => this.file = file;

    static readonly Lazy<RankTables> embedded = new(() =>
    {
        using var stream = typeof(RankTables).Assembly.GetManifestResourceStream("rank.json") ?? throw new InvalidOperationException("rank.json is not embedded");
        return new RankTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("rank.json"));
    });
    public static RankTables Embedded => embedded.Value;

    public IEnumerable<Season> LiveSeasons => file.Seasons.Where(s => s.Group == LiveGroup).OrderBy(s => s.No);
    /// <summary>The live season running at <paramref name="now"/> (Unix ms), or null between or after the seasons.</summary>
    public Season? SeasonAt(long now) => LiveSeasons.FirstOrDefault(s => now >= s.Start && now < s.End);
    public Season? LiveSeason(int no) => LiveSeasons.FirstOrDefault(s => s.No == no);
    /// <summary>The ended live season whose reward period holds <paramref name="now"/> (its results and rewards), or null.</summary>
    public Season? RewardSeasonAt(long now) => LiveSeasons.FirstOrDefault(s => now >= s.RewardStart && now < s.RewardEnd);
    /// <summary>SeasonContents of a live season and ESeasonContentsType.</summary>
    public Contents? ContentsOf(int seasonNo, int type) => file.Contents.FirstOrDefault(c => c.Group == LiveGroup && c.No == seasonNo && c.Type == type);
    public IEnumerable<Contents> RankedContents(int seasonNo) => file.Contents.Where(c => c.Group == LiveGroup && c.No == seasonNo && c.Ranked);
    /// <summary>SeasonRanking MinRankingPoint (Abyss 1500, the others 1).</summary>
    public long MinPoint(int type) => file.Ranking.FirstOrDefault(r => r.Type == type)?.MinPoint ?? 1;
    /// <summary>SeasonRanking UpdatePeriod in minutes (60 for every type).</summary>
    public int UpdateMinutes(int type) => Math.Max(1, file.Ranking.FirstOrDefault(r => r.Type == type)?.UpdateMinutes ?? 60);

    /// <summary>
    /// The grade of rank <paramref name="rank"/> (of <paramref name="total"/>) with <paramref name="point"/> points in a grade group: the
    /// first grade whose RankMax (0 = any; RankPercentMax: within the top percent) the rank reaches and whose NeedRankingPoint the
    /// points reach. OBSERVED on every entry of the 8 official lists of recording 20261106-115817 (boss challenge class list: rank 1
    /// with 833,591 points grade 4, rank 2 with 482,850 grade 6, rank 101 with 591,820 grade 8; the user's rank 1364 grade 11).
    /// 0 when the group is empty or nothing fits.
    /// </summary>
    public int GradeOf(string group, int rank, long point, int total)
    {
        if (group == "" || rank <= 0 || !file.Grades.TryGetValue(group, out var grades)) return 0;
        foreach (var g in grades)
        {
            var rankOk = g.PercentMax > 0 ? (long)rank * 100 <= (long)total * g.PercentMax : g.RankMax == 0 || rank <= g.RankMax;
            if (rankOk && point >= g.Need) return g.Value;
        }
        return 0;
    }

    /// <summary>SeasonRankingGradeInfo: the L10N key of a grade's name (Title_rank_grade_abyss_19 "Trainee").</summary>
    public string GradeName(string infoGroup, int grade) =>
        file.GradeNames.GetValueOrDefault(infoGroup)?.GetValueOrDefault(grade.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? "";

    /// <summary>The reward row of a group for a class (ECharacterClass; rows with class None fit every class) and grade.</summary>
    public Reward? RewardOf(string group, int cls, int grade) =>
        group == "" || grade <= 0 ? null : file.Rewards.GetValueOrDefault(group)?.FirstOrDefault(r => r.Grade == grade && (r.Class == cls || r.Class == 0));

    /// <summary>The reward buffs of a row for the character's race (Light / Dark buff lists): (abnormal, level).</summary>
    public static List<(int Abnormal, int Level)> RewardBuffs(Reward? reward, int race) =>
        reward is null ? [] : (race == 2 ? reward.DarkBuffs : reward.LightBuffs).Where(b => b.Length >= 2).Select(b => (b[0], b[1])).ToList();

    /// <summary>A reward buff's stats at a level (SkillAbnormalEffectLv; PvPAmplifyDamage 200 at level 6), empty when unknown.</summary>
    public IReadOnlyDictionary<string, int> BuffStats(int abnormal, int level) =>
        file.Buffs.GetValueOrDefault(abnormal.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Levels
            .GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
    public int BuffGroup(int abnormal) => file.Buffs.GetValueOrDefault(abnormal.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Group ?? abnormal;

    /// <summary>
    /// The reward buffs that exclude each other (SkillAbnormalString desc_effect, OFFICIAL client text): "Only the highest buff rewards
    /// out of the Abyss, Arena of Solitude, and Arena of Cooperation are applied" (20100-20102), "out of Nightmare and Subjugation"
    /// (20103, 20104), "out of the Ascension Trial and Transcendence" (20105, 20106).
    /// </summary>
    public static readonly int[][] BuffFamilies = [[20100, 20101, 20102], [20103, 20104], [20105, 20106]];

    public WeeklySettings Weekly => file.Weekly ?? new WeeklySettings(3, 16 * 3_600_000L, false, "", false, "");

    /// <summary>Korea's offset: the client tables' dates and reset times are KST wall time.</summary>
    public const long KstMs = 9 * 3_600_000L;
    public const long WeekMs = 7 * 86_400_000L;

    /// <summary>The weekly reset at or before <paramref name="now"/> (Unix ms): GlobalSetting weekly_reset_day 3 (Wednesday) at
    /// weekly_quest_reset_time 16:00 KST = Wednesday 07:00 UTC (the shops' weekly reset 2026-11-11 07:00 UTC, OBSERVED).</summary>
    public long WeekStartAt(long now)
    {
        var w = Weekly;
        var kst = now + KstMs - w.TimeMs;                                    // KST wall time shifted so the reset falls at midnight
        var day = Math.Floor(kst / 86_400_000.0);
        var weekday = ((long)day % 7 + 7 + 4) % 7;                            // 1970-01-01 was a Thursday (4)
        var back = (weekday - w.Day + 7) % 7;
        return ((long)day - back) * 86_400_000L + w.TimeMs - KstMs;
    }

    /// <summary>GlobalSetting suppression_rank_point_* (basic_score 100; easy 1 ... hell 25).</summary>
    public double SuppressionFactor(string key) => file.Suppression.GetValueOrDefault(key);

    /// <summary>The reward items of a row for the character's race (1 Elyos = Light, 2 Asmodian = Dark): (item id, count).</summary>
    public static List<(int Item, long Count)> RewardItems(Reward? reward, int race) =>
        reward is null ? [] : (race == 2 ? reward.Dark : reward.Light).Where(i => i.Length >= 2).Select(i => ((int)i[0], i[1])).ToList();
}

/// <summary>One character on a season ranking list as computed at an update: overall and class rank, the grades, the changes.</summary>
public sealed record SeasonEntry(SeasonRankingRow Row, byte Class, int Rank, int ClassRank, int Grade, int ClassGrade, int Display,
    int RankChange, int ClassRankChange, int PrevSeasonRank);

/// <summary>
/// A season ranking as of one update (SeasonRanking UpdatePeriod: 60 minutes): every ranked character in the official order, their
/// ranks in the whole list and in their class list, the grades (RankTables.GradeOf) and the rank changes since the update before.
/// Order (OBSERVED, the 8 official lists): the best clear's points first (boss challenge, Transcendence, Ascension trial: the extra
/// JSON's best_clear_point), then the ranking point, both descending (Abyss and arena: the point alone). A character needs
/// MinRankingPoint (Abyss 1500); the whole list ranks at most 10,000 (OBSERVED total 10000 in the Abyss and boss lists).
/// </summary>
public sealed class SeasonSnapshot
{
    public const int OverallMax = 10_000;
    public int Type { get; }
    public int Season { get; }
    public long TakenAt { get; }
    public long NextUpdate { get; }
    public IReadOnlyList<SeasonEntry> Entries { get; }
    readonly Dictionary<Guid, SeasonEntry> byCharacter;
    readonly Dictionary<byte, List<SeasonEntry>> byClass;

    SeasonSnapshot(int type, int season, long takenAt, long next, List<SeasonEntry> entries)
    {
        (Type, Season, TakenAt, NextUpdate, Entries) = (type, season, takenAt, next, entries);
        byCharacter = entries.ToDictionary(e => e.Row.Character);
        byClass = entries.GroupBy(e => e.Class).ToDictionary(g => g.Key, g => g.OrderBy(e => e.ClassRank).ToList());
    }

    /// <summary>The list of a class (ECharacterClass; 0 = all) in rank order.</summary>
    public IReadOnlyList<SeasonEntry> List(byte cls) => cls == 0 ? Entries : byClass.GetValueOrDefault(cls) ?? (IReadOnlyList<SeasonEntry>)[];
    public SeasonEntry? Of(Guid character) => byCharacter.GetValueOrDefault(character);

    /// <summary>The next update after <paramref name="now"/>: the next multiple of the update period (CANDIDATE alignment: the
    /// official server's updates fall at :13:02 past the hour, OBSERVED next_update_time 2026-11-06 13:13:02.218 UTC).</summary>
    public static long NextUpdateAfter(long now, int minutes)
    {
        var period = minutes * 60_000L;
        return (now / period + 1) * period;
    }

    public static SeasonSnapshot Build(int type, int season, RankTables.Contents? contents, IEnumerable<SeasonRankingRow> rows, Func<SeasonRankingRow, byte> classOf,
        long now, long next, SeasonSnapshot? previous, IReadOnlyDictionary<Guid, int>? prevSeasonRanks = null, long minPoint = 1)
    {
        var ordered = rows.Where(r => r.Point >= minPoint).OrderByDescending(r => r.BestPoint).ThenByDescending(r => r.Point).ThenBy(r => r.WireId)
            .Take(OverallMax).Select(r => (Row: r, Class: classOf(r))).ToList();
        var classTotals = ordered.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Count());
        var classSeen = new Dictionary<byte, int>();
        var entries = new List<SeasonEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (row, cls) = ordered[i];
            var rank = i + 1;
            var classRank = classSeen[cls] = classSeen.GetValueOrDefault(cls) + 1;
            var grade = contents is null ? 0 : RankTables.Embedded.GradeOf(contents.GradeGroup, rank, row.Point, ordered.Count);
            var classGrade = contents is null ? 0 : RankTables.Embedded.GradeOf(contents.ClassGradeGroup, classRank, row.Point, classTotals[cls]);
            var display = contents is { ShowsClassGrade: true } ? classGrade : grade;
            var before = previous?.Of(row.Character);
            entries.Add(new SeasonEntry(row, cls, rank, classRank, grade, classGrade, display,
                before is null ? 0 : before.Rank - rank, before is null ? 0 : before.ClassRank - classRank, prevSeasonRanks?.GetValueOrDefault(row.Character) ?? 0));
        }
        return new SeasonSnapshot(type, season, now, next, entries);
    }

    /// <summary>The final results of an ended season for every ranked character (game.season_ranking_results).</summary>
    public List<SeasonResultRow> Results() =>
        Entries.Select(e => new SeasonResultRow(e.Row.Character, Type, Season, e.Class, e.Rank, Entries.Count, e.ClassRank, List(e.Class).Count,
            e.Row.Point, e.Row.Extra, e.Grade, e.ClassGrade, e.Display, false)).ToList();
}

/// <summary>
/// The host's season ranking lists (SharedWorld.Rankings): one snapshot per ESeasonContentsType and season, rebuilt from the store
/// at its update time (thread-safe; the store calls run outside the lock). The previous snapshot gives the rank changes. Also the
/// seasons whose final results were written, and the Abyss maps' fallen rankers (WorldMapAbyssRankerInfo_RS).
/// </summary>
public sealed class SeasonRankingCache
{
    readonly object gate = new();
    readonly Dictionary<(int Type, int Season), SeasonSnapshot> snapshots = [];
    readonly HashSet<int> finalized = [];
    readonly HashSet<long> weeksSettled = [];
    readonly List<(int Map, byte Race, Vector3d Position, long Expire)> fallen = [];

    public SeasonSnapshot? Current(int type, int season, long now)
    {
        lock (gate) return snapshots.TryGetValue((type, season), out var s) && now < s.NextUpdate ? s : null;
    }

    public SeasonSnapshot? Latest(int type, int season)
    {
        lock (gate) return snapshots.GetValueOrDefault((type, season));
    }

    public void Store(SeasonSnapshot snapshot)
    {
        lock (gate)
            if (!snapshots.TryGetValue((snapshot.Type, snapshot.Season), out var held) || held.TakenAt <= snapshot.TakenAt) snapshots[(snapshot.Type, snapshot.Season)] = snapshot;
    }

    public bool Finalized(int season) { lock (gate) return finalized.Contains(season); }
    public void MarkFinalized(int season) { lock (gate) finalized.Add(season); }
    /// <summary>A weekly reset (Unix ms) whose rewards this host paid or found paid (the store's settlement row is what counts).</summary>
    public bool WeekSettled(long reset) { lock (gate) return weeksSettled.Contains(reset); }
    public void MarkWeekSettled(long reset) { lock (gate) weeksSettled.Add(reset); }

    /// <summary>A high-ranked player fell on an Abyss map: shown on the world map until <paramref name="expire"/>.</summary>
    public void Fallen(int map, byte race, Vector3d position, long expire)
    {
        lock (gate)
        {
            fallen.RemoveAll(f => f.Expire <= expire - 3_600_000);
            fallen.Add((map, race, position, expire));
        }
    }

    public List<(byte Race, Vector3d Position, long Expire)> FallenOn(int map, long now)
    {
        lock (gate) return fallen.Where(f => f.Map == map && f.Expire > now).Select(f => (f.Race, f.Position, f.Expire)).ToList();
    }

    /// <summary>Checks only: forget every snapshot (each check builds its lists afresh).</summary>
    public void Clear()
    {
        lock (gate) { snapshots.Clear(); finalized.Clear(); fallen.Clear(); weeksSettled.Clear(); }
    }
}
