using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Client-table data of the community and PvP menus (tools/systems2_data.py -> tools/data/systems2.json): legion levels,
/// buffs, grade authority defaults, emblems and storage extensions; arena and battlefield matching; field events and the
/// Shugo festival schedule; Abyss artifacts, rifts, season ranking; Post.dat; the GlobalSetting values these use.
/// </summary>
public sealed class Systems2Tables
{
    public sealed record GuildLevel([property: JsonPropertyName("level")] int Level, [property: JsonPropertyName("accumulate_exp")] long AccumulateExp,
        [property: JsonPropertyName("max_members")] int MaxMembers, [property: JsonPropertyName("officers")] int Officers,
        [property: JsonPropertyName("stats")] List<GuildStat> Stats);
    public sealed record GuildStat([property: JsonPropertyName("stat")] string Stat, [property: JsonPropertyName("value")] int Value);
    public sealed record Emblem([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("unlock_level")] int UnlockLevel);
    public sealed record StorageExtension([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("cost")] long Cost,
        [property: JsonPropertyName("currency")] string Currency, [property: JsonPropertyName("level")] int Level);
    public sealed record Matching([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("teams")] int Teams, [property: JsonPropertyName("team_size")] int TeamSize,
        [property: JsonPropertyName("party_size")] int PartySize, [property: JsonPropertyName("hidden")] bool Hidden,
        [property: JsonPropertyName("reward_join")] string RewardJoin, [property: JsonPropertyName("reward_win")] string RewardWin,
        [property: JsonPropertyName("schedule_group")] int ScheduleGroup, [property: JsonPropertyName("contents")] List<MatchingPick> Contents);
    public sealed record MatchingPick([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("prob")] int Prob);
    public sealed record MatchingContents([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("values")] List<string> Values, [property: JsonPropertyName("dungeon")] int Dungeon,
        [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("play_ms")] long PlayMs, [property: JsonPropertyName("lives")] int Lives,
        [property: JsonPropertyName("respawn_ms")] long RespawnMs);
    public sealed record MatchingSchedule([property: JsonPropertyName("group")] int Group, [property: JsonPropertyName("day")] string Day,
        [property: JsonPropertyName("windows")] List<long[]> Windows);
    public sealed record RewardItem([property: JsonPropertyName("item")] string Item, [property: JsonPropertyName("count")] long Count);
    public sealed record FieldEvent([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("contents")] string Contents,
        [property: JsonPropertyName("value")] string Value, [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("entrance")] string Entrance,
        [property: JsonPropertyName("entrance_map")] int EntranceMap, [property: JsonPropertyName("progress_s")] int ProgressSeconds,
        [property: JsonPropertyName("ready_s")] int ReadySeconds, [property: JsonPropertyName("start_members")] int StartMembers,
        [property: JsonPropertyName("title")] string Title);
    public sealed record EventSchedule([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("days")] List<string> Days,
        [property: JsonPropertyName("times_ms")] List<long> TimesMs, [property: JsonPropertyName("events")] List<EventRef> Events);
    public sealed record EventRef([property: JsonPropertyName("category")] string Category, [property: JsonPropertyName("key")] string Key);
    public sealed record AbyssArtifact([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("event")] string Event, [property: JsonPropertyName("teleport")] string Teleport,
        [property: JsonPropertyName("cost_item")] string CostItem, [property: JsonPropertyName("cost_count")] int CostCount);
    public sealed record AbyssEvent([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("pre_end_s")] int PreEndSeconds);
    public sealed record Rift([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("map")] int Map,
        [property: JsonPropertyName("min_level")] int MinLevel, [property: JsonPropertyName("max_level")] int MaxLevel,
        [property: JsonPropertyName("life_s")] int LifeSeconds, [property: JsonPropertyName("costs")] List<RewardItem> Costs);
    public sealed record SeasonRanking([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("contents")] string Contents,
        [property: JsonPropertyName("min_point")] long MinPoint, [property: JsonPropertyName("update_s")] int UpdateSeconds);
    public sealed record Post([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("contents")] string Contents,
        [property: JsonPropertyName("tab")] string Tab, [property: JsonPropertyName("params")] List<string> Params);

    sealed class File
    {
        [JsonPropertyName("global")] public Dictionary<string, JsonElement> Global { get; set; } = [];
        [JsonPropertyName("guild_levels")] public List<GuildLevel> GuildLevels { get; set; } = [];
        [JsonPropertyName("guild_authority")] public Dictionary<string, int> GuildAuthority { get; set; } = [];
        [JsonPropertyName("guild_emblems")] public List<Emblem> GuildEmblems { get; set; } = [];
        [JsonPropertyName("guild_storage_extensions")] public List<StorageExtension> GuildStorageExtensions { get; set; } = [];
        [JsonPropertyName("matching")] public List<Matching> Matching { get; set; } = [];
        [JsonPropertyName("matching_contents")] public List<MatchingContents> MatchingContents { get; set; } = [];
        [JsonPropertyName("matching_schedule")] public List<MatchingSchedule> MatchingSchedule { get; set; } = [];
        [JsonPropertyName("arena_rewards")] public Dictionary<string, List<RewardItem>> ArenaRewards { get; set; } = [];
        [JsonPropertyName("field_events")] public List<FieldEvent> FieldEvents { get; set; } = [];
        [JsonPropertyName("event_schedules")] public List<EventSchedule> EventSchedules { get; set; } = [];
        [JsonPropertyName("abyss_artifacts")] public List<AbyssArtifact> AbyssArtifacts { get; set; } = [];
        [JsonPropertyName("abyss_events")] public List<AbyssEvent> AbyssEvents { get; set; } = [];
        [JsonPropertyName("rifts")] public List<Rift> Rifts { get; set; } = [];
        [JsonPropertyName("season_ranking")] public List<SeasonRanking> SeasonRanking { get; set; } = [];
        [JsonPropertyName("posts")] public List<Post> Posts { get; set; } = [];
        [JsonPropertyName("item_ids")] public Dictionary<string, int> ItemIds { get; set; } = [];
    }

    readonly File file;

    Systems2Tables(File file) => this.file = file;

    static readonly Lazy<Systems2Tables> embedded = new(() =>
    {
        using var stream = typeof(Systems2Tables).Assembly.GetManifestResourceStream("systems2.json") ?? throw new InvalidOperationException("systems2.json is not embedded");
        return new Systems2Tables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("systems2.json"));
    });

    public static Systems2Tables Embedded => embedded.Value;

    /// <summary>A GlobalSetting number (0 when absent).</summary>
    public long Global(string key) => file.Global.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public IReadOnlyList<GuildLevel> GuildLevels => file.GuildLevels;
    public GuildLevel GuildLevelOf(int level) => file.GuildLevels.FirstOrDefault(l => l.Level == level) ?? file.GuildLevels[0];
    /// <summary>The legion level its cumulative exp reaches: GuildData AccumulateExp is the exp a level fills (level 1: 1000).</summary>
    public int GuildLevelForExp(long exp)
    {
        var level = 1;
        foreach (var l in file.GuildLevels.OrderBy(l => l.Level))
            if (exp >= l.AccumulateExp && l.Level < file.GuildLevels.Max(x => x.Level)) level = l.Level + 1;
        return level;
    }
    /// <summary>GuildMemberGradeAuthority.dat: EGuildMemberGrade 0 Master, 1 Officer, 2 Member -> EGuildAuthority bit set.</summary>
    public IReadOnlyList<(byte Grade, int Bits)> DefaultAuthority =>
        [(0, file.GuildAuthority.GetValueOrDefault("Master")), (1, file.GuildAuthority.GetValueOrDefault("Officer")), (2, file.GuildAuthority.GetValueOrDefault("Member"))];
    public IReadOnlyList<Emblem> GuildEmblems => file.GuildEmblems;
    public IReadOnlyList<StorageExtension> GuildStorageExtensions => file.GuildStorageExtensions;
    public IReadOnlyList<Matching> Matchings => file.Matching;
    public Matching? MatchingOf(int id) => file.Matching.FirstOrDefault(m => m.Id == id);
    public MatchingContents? ContentsOf(int id) => file.MatchingContents.FirstOrDefault(c => c.Id == id);
    public IReadOnlyList<MatchingSchedule> MatchingSchedules => file.MatchingSchedule;
    public IReadOnlyList<RewardItem> ArenaReward(string name) => file.ArenaRewards.GetValueOrDefault(name) ?? [];
    public IReadOnlyList<FieldEvent> FieldEvents => file.FieldEvents;
    public FieldEvent? FieldEventOf(int id) => file.FieldEvents.FirstOrDefault(e => e.Id == id);
    public IReadOnlyList<EventSchedule> EventSchedules => file.EventSchedules;
    public IReadOnlyList<AbyssArtifact> AbyssArtifacts => file.AbyssArtifacts;
    public IReadOnlyList<AbyssEvent> AbyssEvents => file.AbyssEvents;
    public IReadOnlyList<Rift> Rifts => file.Rifts;
    public IReadOnlyList<SeasonRanking> SeasonRankings => file.SeasonRanking;
    public Post? PostOf(int id) => file.Posts.FirstOrDefault(p => p.Id == id);
    /// <summary>Item.dat id of an item name used by the rewards and costs (0 when unknown).</summary>
    public int ItemId(string name) => file.ItemIds.GetValueOrDefault(name);
}
