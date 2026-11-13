using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Achievement data (tools/data/achievements.json, tools/achievement_data.py): objectives, steps, rewards, groups, attendance boards.</summary>
public sealed class AchievementTables
{
    public sealed record Entry(
        [property: JsonPropertyName("g")] string Group,
        [property: JsonPropertyName("t")] string Type,
        [property: JsonPropertyName("v")] string[] Values,
        [property: JsonPropertyName("s")] int[][] Steps,
        [property: JsonPropertyName("u")] int Unlock,
        [property: JsonPropertyName("c")] int Reset,
        [property: JsonPropertyName("n")] int[]? Npcs = null,
        [property: JsonPropertyName("i")] int[]? Items = null,
        [property: JsonPropertyName("e")] int[]? Envs = null,
        [property: JsonPropertyName("r")] Dictionary<string, long[][]>? Rewards = null,
        [property: JsonPropertyName("p")] Dictionary<string, int>? PointsByStep = null)
    {
        /// <summary>Battle pass exp for completing the step (AchievementReward.RewardPoint).</summary>
        public int Points(int step) => PointsByStep?.GetValueOrDefault(step.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? 0;
        public int Id { get; set; }
        /// <summary>The count that completes the last step.</summary>
        public int Target => Steps.Length == 0 ? 1 : Steps[^1][1];
        public bool Has(string value) => Values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        public int StepCount(int step) => Steps.FirstOrDefault(s => s[0] == step)?[1] ?? int.MaxValue;
        public IReadOnlyList<(int ItemId, long Count)> StepRewards(int step) =>
            Rewards?.GetValueOrDefault(step.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList() ?? [];
    }
    public sealed record Group([property: JsonPropertyName("i")] int Id, [property: JsonPropertyName("r")] int Race, [property: JsonPropertyName("c")] string Category);
    public sealed record Board([property: JsonPropertyName("a")] int Achievement, [property: JsonPropertyName("r")] int Race, [property: JsonPropertyName("d")] int Days,
        [property: JsonPropertyName("s")] string Season, [property: JsonPropertyName("always")] bool Always,
        [property: JsonPropertyName("from")] long FromMs = 0, [property: JsonPropertyName("to")] long ToMs = 0)
    {
        public int Id { get; set; }
    }
    public sealed record PassGroup([property: JsonPropertyName("n")] string Name, [property: JsonPropertyName("r")] int Race, [property: JsonPropertyName("from")] long FromMs,
        [property: JsonPropertyName("to")] long ToMs, [property: JsonPropertyName("limit")] int Limit, [property: JsonPropertyName("passes")] long[][] Passes,
        [property: JsonPropertyName("missions")] string? Missions)
    {
        public int Id { get; set; }
    }
    sealed record File([property: JsonPropertyName("achievements")] Dictionary<string, Entry> Achievements,
        [property: JsonPropertyName("groups")] Dictionary<string, Group> Groups,
        [property: JsonPropertyName("group_rewards")] Dictionary<string, long[][]> GroupRewards,
        [property: JsonPropertyName("attendance")] Dictionary<string, Board> Attendance,
        [property: JsonPropertyName("battlepass")] Dictionary<string, PassGroup>? BattlePass = null);

    readonly File file;
    readonly Dictionary<int, Entry> byId = [];
    readonly Dictionary<string, List<Entry>> byType = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Group> groupsById = [];
    readonly Dictionary<int, List<Entry>> byGroupId = [];

    AchievementTables(File file)
    {
        this.file = file;
        foreach (var (key, entry) in file.Achievements)
        {
            entry.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
            byId[entry.Id] = entry;
            byType.TryAdd(entry.Type, []);
            byType[entry.Type].Add(entry);
            if (file.Groups.TryGetValue(entry.Group, out var group))
            {
                byGroupId.TryAdd(group.Id, []);
                byGroupId[group.Id].Add(entry);
            }
        }
        foreach (var group in file.Groups.Values) groupsById.TryAdd(group.Id, group);
        foreach (var (key, board) in file.Attendance) board.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, group) in file.BattlePass ?? []) group.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
    }
    public IEnumerable<PassGroup> PassGroups => file.BattlePass?.Values ?? Enumerable.Empty<PassGroup>();

    static readonly Lazy<AchievementTables> embedded = new(() =>
    {
        using var stream = typeof(AchievementTables).Assembly.GetManifestResourceStream("achievements.json") ?? throw new InvalidOperationException("achievements.json is not embedded");
        return new AchievementTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("achievements.json"));
    });
    public static AchievementTables Embedded => embedded.Value;

    public Entry? Of(int id) => byId.GetValueOrDefault(id);
    public IReadOnlyList<Entry> OfType(string type) => byType.GetValueOrDefault(type) ?? [];
    public Group? GroupOf(Entry entry) => file.Groups.GetValueOrDefault(entry.Group);
    public Group? GroupById(int id) => groupsById.GetValueOrDefault(id);
    public IReadOnlyList<Entry> InGroup(int groupId) => byGroupId.GetValueOrDefault(groupId) ?? [];
    public IEnumerable<Entry> All => byId.Values;
    public IReadOnlyList<long[]> GroupRewards(int groupId) => file.GroupRewards.GetValueOrDefault(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [];
    public IEnumerable<Board> Boards => file.Attendance.Values;

    /// <summary>The attendance boards open now for a race: the dated ones in their window, else the undated ones (OURS fallback).</summary>
    public List<Board> OpenBoards(int race, long nowMs)
    {
        var dated = Boards.Where(b => b.Race is 0 || b.Race == race).Where(b => b.FromMs > 0 && b.FromMs <= nowMs && nowMs <= b.ToMs).ToList();
        return dated.Count > 0 ? dated : Boards.Where(b => (b.Race is 0 || b.Race == race) && b.Always).ToList();
    }
}

// Achievements (the official sends the full list at world entry: AchievementList_NT 0x5683 = 39 691 bytes for a level-4
// Light character, 2327 entries, OBSERVED in capture 20261013-144836 #466; AchievementCountUpdate_NT 0x5684 ×42 as it
// played). Common_AchievementState = {count i32, complete time i64, rewarded step u8} (usmap; the count update bytes
// 01 | id | 02000000 | time | 00 confirm it). The listed set (OBSERVED): every achievement of a group of the character's
// race or of all races in the categories Unlock, Achievement and SeasonMissionUnlock (not Onboarding), plus the open
// attendance boards' achievements, the active battle pass's and the active season missions' (the last two are not
// modelled yet). The group reward states map is sent empty (CANDIDATE: its bool-array packing is not decoded).
// Counting (OURS, by EAchievementObjectiveType): PCLevel, ClearQuest/ClearQuestType/ClearQuestExplorationType, KillNpc/KillNpcAll, ClearDungeon/
// ClearDungeonType/ClearDungeonGroup/ClearDungeonBySingle, ClearMapEvent, UseEnvObjSpawnerName, UseItem/UseUsableItemType,
// CraftCategorySuccess, GatherSuccess/GatherCategorySuccess, CraftMasteryLevel/GatherMasteryLevel, EquipItemLevel,
// DecomposeItem, SoulbindTry, LoginCharacter, Attendance, EngraveTeleportArtifact, JoinParty, GuildJoin, AddFriend,
// StorageStore, CloseNPC, ClearAchievement. Rewards: TakeAchievementReward 0x5685/0x5686 {id, step}, group rewards
// 0x5687/0x5688 {group, step}, the batch 0x5689/0x568a; Attendance_RQ/RS 0xe34d/0xe34e checks
// in once a day; CloseNpcAchievement 0xe32f/0xe330, CloseEnvObjAchievement 0xe331/0xe332. Opcodes in the anchored runs,
// bodies from the usmap: CANDIDATE except the two observed.
public sealed partial class GameSession
{
    public const ushort AchievementListNt = 0x5683, AchievementCountUpdateNt = 0x5684, TakeAchievementRewardRq = 0x5685, TakeAchievementRewardRs = 0x5686,
        TakeAchievementGroupRewardRq = 0x5687, TakeAchievementGroupRewardRs = 0x5688, TakeAchievementRewardBatchRq = 0x5689, TakeAchievementRewardBatchRs = 0x568a,
        // The 0xe3 block's live offset is +0xa762 (ArenaRanking_NT 0xe32a OBSERVED): Attendance_RQ/RS 0xe34d/0xe34e (official
        // reply "4ee30000"; the client sends 0xe34d at every login and on the attendance button, and with the old 0xe2e6 it was
        // never answered, so the attendance window never opened), CloseNpc/CloseEnvObjAchievement 0xe32f-0xe332.
        AttendanceRq = 0xe34d, AttendanceRs = 0xe34e, CloseNpcAchievementRq = 0xe32f, CloseNpcAchievementRs = 0xe330, CloseEnvObjAchievementRq = 0xe331, CloseEnvObjAchievementRs = 0xe332;
    const byte IncreaseAchievementReward = 2;   // EIncreaseReason (OBSERVED 2026-11-04 162844 #1508, next to 0x568a)
    static AchievementTables Achievements => AchievementTables.Embedded;
    static readonly HashSet<string> ListedCategories = new(StringComparer.OrdinalIgnoreCase) { "Achievement", "Unlock", "SeasonMissionUnlock" };

    sealed class AchievementState
    {
        public int Count;
        public long CompletedMs;
        public int RewardedStep;
        public long UpdatedMs;
    }

    readonly Dictionary<int, AchievementState> achievements = [];
    readonly HashSet<int> achievementDirty = [];
    readonly HashSet<(int Group, int Step)> groupRewardsTaken = [];
    long achievementFlushAt;

    public int AchievementCount(int id) => achievements.GetValueOrDefault(id)?.Count ?? 0;
    /// <summary>Checks only: an achievement's count (progress the checks cannot play through).</summary>
    public void SetAchievementForChecks(int id, int count) => (achievements.TryGetValue(id, out var st) ? st : achievements[id] = new AchievementState()).Count = count;
    /// <summary>Changed states not yet written (checks).</summary>
    public int PendingAchievementWrites => achievementDirty.Count;
    public long AchievementFlushAt => achievementFlushAt;

    void LoadAchievements(IReadOnlyList<AchievementRecord> stored, IReadOnlyList<(int Group, int Step)> groupTaken, long now)
    {
        achievements.Clear();
        achievementDirty.Clear();
        groupRewardsTaken.Clear();
        groupRewardsTaken.UnionWith(groupTaken);
        foreach (var record in stored)
        {
            var state = new AchievementState { Count = record.Count, CompletedMs = record.CompletedAt?.ToUnixTimeMilliseconds() ?? 0, RewardedStep = record.RewardedStep, UpdatedMs = record.UpdatedAt.ToUnixTimeMilliseconds() };
            // Daily and weekly missions start over after their cycle (UTC days; weeks from Monday).
            if (Achievements.Of(record.Id) is { Reset: 1 or 2 } entry && CycleStart(entry.Reset, now) > state.UpdatedMs)
                state = new AchievementState { UpdatedMs = now };
            achievements[record.Id] = state;
        }
    }

    static long CycleStart(int reset, long nowMs)
    {
        var day = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime.Date;
        if (reset == 2) day = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    /// <summary>Whether the character's list carries this achievement (race, category, open attendance boards).</summary>
    bool Listed(AchievementTables.Entry entry, long now)
    {
        if (Achievements.GroupOf(entry) is not { } group || (group.Race != 0 && group.Race != profileRace)) return false;
        // Season groups (SeasonMissionUnlock, SeasonMission): only the current season's (server-rules.xml <season current>). OBSERVED
        // 2026-11-07 (capture 012837): the official list has every Group_Season_G1_* achievement (Unlock 113, missions 65) and none of
        // seasons 2-4; ours listed the unlocks of all seasons (279 too many) and no mission.
        if (group.Category is "SeasonMissionUnlock" or "SeasonMission")
            return entry.Group.Contains($"_Season_{ServerRules.Embedded.CurrentSeason}_", StringComparison.Ordinal);
        if (ListedCategories.Contains(group.Category)) return true;
        if (group.Category == "BattlePass") return PassOfMission(entry, now) is not null;
        return group.Category == "Attendance" && Achievements.OpenBoards(profileRace, now).Any(b => b.Achievement == entry.Id);
    }

    // The i64 is the completion time, but for an attendance board the last check-in: OBSERVED (official AchievementList_NT,
    // 115817): board 520330100 count 11, time 2026-11-06 10:55 (that day's check-in), never completed. With 0 there the client
    // took today's check-in as not done: the window opened again with red dots after every relog (live 2026-11-07).
    PacketWriter WriteState(PacketWriter w, int id, AchievementState? state, bool update = false) =>
        w.I32(id).I32(state?.Count ?? 0).I64(state is { Count: > 0 } && (IsAttendanceBoard(id) || update && state.CompletedMs == 0) ? state.UpdatedMs : state?.CompletedMs ?? 0)
            .U8((byte)(state?.RewardedStep ?? 0));

    bool IsAttendanceBoard(int id) =>
        (UnlistedBoards.GetValueOrDefault(profileRace) ?? []).Contains(id)
        || Achievements.Of(id) is { } entry && Achievements.GroupOf(entry) is { Category: "Attendance" };

    /// <summary>
    /// AchievementList_NT at world entry: the listed achievements with their states, then the group reward states (usmap map
    /// group i32 -> {step reward states [bool]}, the bools packed across the groups). OBSERVED (capture 105448 #329): every
    /// Achievement-category group of the race, one flag per group reward step. Before 2026-11-06 we sent no group: the client
    /// counted the completed achievements (44/1229) but its Achievements window listed nothing.
    /// </summary>
    byte[] AchievementList(long now)
    {
        var listed = Achievements.All.Where(e => Listed(e, now)).ToList();
        var extra = UnlistedBoards.GetValueOrDefault(profileRace) ?? [];
        var w = PacketWriter.Opcode(AchievementListNt).Varint((uint)(listed.Count + extra.Length));
        foreach (var entry in listed) WriteState(w, entry.Id, achievements.GetValueOrDefault(entry.Id));
        foreach (var id in extra) WriteState(w, id, achievements.GetValueOrDefault(id));
        var groups = listed.Select(e => Achievements.GroupOf(e)).OfType<AchievementTables.Group>()
            .Where(g => g.Category == "Achievement" && Achievements.GroupRewards(g.Id).Count > 0).DistinctBy(g => g.Id).OrderBy(g => g.Id).ToList();
        w.Varint((uint)groups.Count);
        foreach (var group in groups)
        {
            var steps = Achievements.GroupRewards(group.Id).Count;
            w.I32(group.Id).Varint((uint)steps);
            for (var step = 1; step <= steps; step++) w.Bool(groupRewardsTaken.Contains((group.Id, step)));
        }
        return w.ToArray();
    }

    byte[] AchievementUpdate(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(AchievementCountUpdateNt).Varint((uint)ids.Count);
        // A count update carries the time of the change also before completion (OBSERVED: every Empyrean Trace pickup's 0x5684 of
        // 420100700 "Collect Empyrean Trace: Altgard", e.g. count 2 of 5 with that moment's time, rec 105448 #17554; 2026-11-11).
        foreach (var id in ids) WriteState(w, id, achievements.GetValueOrDefault(id), update: true);
        return w.ToArray();
    }

    /// <summary>Counts <paramref name="add"/> on every listed achievement of the type that <paramref name="match"/> accepts (null: all of the type).</summary>
    List<byte[]> Achieve(string type, int add = 1, Func<AchievementTables.Entry, bool>? match = null, long now = 0) =>
        Progress(type, match, state => state.Count + add, now);

    /// <summary>Sets a value (level, item level): the count becomes the value when higher.</summary>
    List<byte[]> AchieveSet(string type, int value, Func<AchievementTables.Entry, bool>? match = null, long now = 0) =>
        Progress(type, match, state => Math.Max(state.Count, value), now);

    List<byte[]> Progress(string type, Func<AchievementTables.Entry, bool>? match, Func<AchievementState, int> next, long now, int depth = 0)
    {
        if (now == 0) now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = new List<int>();
        var completed = new List<int>();
        var passExp = new List<byte[]>();
        foreach (var entry in Achievements.OfType(type))
        {
            if (match is not null && !match(entry)) continue;
            if (!Listed(entry, now)) continue;
            if (entry.Unlock != 0 && (achievements.GetValueOrDefault(entry.Unlock)?.CompletedMs ?? 0) == 0) continue;
            if (!achievements.TryGetValue(entry.Id, out var state)) achievements[entry.Id] = state = new AchievementState();
            if (state.CompletedMs != 0 && state.Count >= entry.Target) continue;
            var value = Math.Min(entry.Target, next(state));
            if (value == state.Count) continue;
            state.Count = value;
            state.UpdatedMs = now;
            var stepsBefore = entry.Steps.Count(st => st[1] <= state.Count);
            if (value >= entry.Target) { state.CompletedMs = now; completed.Add(entry.Id); }
            achievementDirty.Add(entry.Id);
            changed.Add(entry.Id);
            // A battle pass mission step done: its exp to the pass.
            for (var step = stepsBefore + 1; step <= entry.Steps.Length && entry.Steps[step - 1][1] <= value; step++)
                passExp.AddRange(BattlePassExp(entry, entry.Steps[step - 1][0], now));
        }
        if (changed.Count == 0) return [];
        var replies = new List<byte[]> { AchievementUpdate(changed) };
        replies.AddRange(passExp);
        // Completing one counts for the ClearAchievement objectives naming it (one level deep).
        if (depth == 0 && completed.Count > 0)
            replies.AddRange(Progress("ClearAchievement", e => completed.Any(id => e.Has(id.ToString(System.Globalization.CultureInfo.InvariantCulture))), s => s.Count + 1, now, 1));
        return replies;
    }

    // ---------- The hooks ----------

    List<byte[]> AchieveKill(int npcId) => [.. Achieve("KillNpc", 1, e => e.Npcs?.Contains(npcId) == true), .. Achieve("KillNpcAll")];

    /// <summary>A rewarded quest. ClearQuestExplorationType values: [exploration type] or [type, map] (Group_L_Adventure_Garrison
    /// "OccupationTerritory 1010" counts to 15 = the 15 OccupationTerritory quests with AcquireMapId 1010).</summary>
    List<byte[]> AchieveQuest(string code, string? type, string? exploration = null, int map = 0) =>
        [.. Achieve("ClearQuest", 1, e => e.Has(code)), .. Achieve("ClearQuestType", 1, e => e.Values.Length == 0 || (type is not null && e.Has(type))),
         .. (exploration is null ? new List<byte[]>() : Achieve("ClearQuestExplorationType", 1, e => e.Values.Length > 0 && string.Equals(e.Values[0], exploration, StringComparison.OrdinalIgnoreCase)
                && (e.Values.Length < 2 || e.Values[1].Length == 0 || e.Values[1] == map.ToString(System.Globalization.CultureInfo.InvariantCulture))))];

    /// <summary>A clear with its time and deaths: the limit achievements (ObjValues [dungeon names..., limit]).</summary>
    List<byte[]> AchieveDungeonClearLimits(DungeonInfo room, long elapsedMs, int deaths) =>
        [.. Achieve("ClearDungeonLimitTime", 1, e => e.Values.Length >= 2 && e.Values[..^1].Any(v => string.Equals(v, room.Name, StringComparison.OrdinalIgnoreCase)) && long.TryParse(e.Values[^1], out var limit) && elapsedMs <= limit * 1000),
         .. Achieve("ClearDungeonLimitDeath", 1, e => e.Values.Length >= 2 && e.Values[..^1].Any(v => string.Equals(v, room.Name, StringComparison.OrdinalIgnoreCase)) && int.TryParse(e.Values[^1], out var most) && deaths <= most),
         .. Achieve("ClearDungeonLimitPartyDeath", 1, e => e.Values.Length >= 2 && e.Values[..^1].Any(v => string.Equals(v, room.Name, StringComparison.OrdinalIgnoreCase)) && int.TryParse(e.Values[^1], out var most) && deaths <= most)];

    List<byte[]> AchieveDungeonClear(DungeonInfo room, string mapEvent, int playersInCopy) =>
        [.. Achieve("ClearDungeon", 1, e => e.Has(room.Name)),
         .. Achieve("ClearDungeonType", 1, e => e.Values.Length > 0 && e.Has(room.Type)),
         .. Achieve("ClearDungeonGroup", 1, e => e.Has(room.Group)),
         .. (playersInCopy <= 1 ? Achieve("ClearDungeonBySingle", 1, e => e.Has(room.Name)) : []),
         .. Achieve("ClearMapEvent", 1, e => e.Has(mapEvent))];

    List<byte[]> AchieveItemUse(int itemId, string effect) =>
        [.. Achieve("UseItem", 1, e => e.Items?.Contains(itemId) == true), .. Achieve("UseUsableItemType", 1, e => e.Has(effect))];

    List<byte[]> AchieveCraft(string worktable, int masteryLevel) =>
        [.. Achieve("CraftCategorySuccess", 1, e => e.Values.Length == 0 || e.Has(worktable)), .. AchieveSet("CraftMasteryLevel", masteryLevel)];

    long elapsedAchieveAt;

    /// <summary>An enchant reached a level: EnchantLevelItem [level], EnchantLevelEquipCategory [category, level], EnchantLevelItemGrade [level, ..., grade] (category and grade unmatched, OURS).</summary>
    List<byte[]> AchieveEnchant(int level) =>
        [.. Achieve("EnchantLevelItem", 1, e => e.Values.Length >= 1 && int.TryParse(e.Values[0], out var need) && level >= need),
         // OBSERVED (144631): every successful enchant counts 1 on 443100600 (EnchantLevelEquipCategory, values ["1", "30"],
         // steps 10..1000 enchants), so these count enchant successes, not a level reached (we required level >= 30: never).
         // PROVISIONAL: the values (equip category, item level?) are not checked.
         .. Achieve("EnchantLevelEquipCategory", 1),
         .. Achieve("EnchantLevelItemGrade", 1)];

    /// <summary>Items that arrived in the bag: GetItem/AcquireItem by id (quantities), GetItemEquip for equipment (grade unmatched), GetItemMisc/Usable by the id range (OURS).</summary>
    List<byte[]> AchieveItemsGained(IReadOnlyList<(int ItemId, long Count)> gained)
    {
        if (gained.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var (itemId, count) in gained)
        {
            var n = (int)Math.Min(count, int.MaxValue);
            replies.AddRange(Achieve("GetItem", n, e => e.Items?.Contains(itemId) == true));
            replies.AddRange(Achieve("AcquireItem", n, e => e.Items?.Contains(itemId) == true));
            if (itemId < 500_000_000) replies.AddRange(Achieve("GetItemEquip", n));
            else if (itemId < 600_000_000) replies.AddRange(Achieve("GetItemUsable", n));
            else if (itemId < 900_000_000) replies.AddRange(Achieve("GetItemMisc", n));
        }
        return replies;
    }

    List<byte[]> AchieveGather(int masteryLevel) =>
        [.. Achieve("GatherSuccess"), .. Achieve("GatherCategorySuccess"), .. AchieveSet("GatherMasteryLevel", masteryLevel)];

    // ---------- Rewards ----------

    /// <summary>
    /// 0x5685 TakeAchievementReward_RQ {id i32, step u8}: every unclaimed step up to the requested one (an attendance board
    /// claimed on day three after days one and two were never claimed pays all three). The steps come from the character's
    /// own progress (RewardedStep, Count), each taken once (TakeStepAsync: the store only moves rewarded_step forward); the
    /// client's step only bounds the claim (OURS). The reply and its order are unchanged: 0x5686 first, then per step the
    /// state update and the inventory change.
    /// </summary>
    async Task<List<byte[]>> TakeAchievementRewardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var id = BitConverter.ToInt32(plain, 2);
        var step = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(TakeAchievementRewardRs).U16(result).I32(id).U8(step).ToArray();
        var replies = new List<byte[]>();
        var last = Math.Min((int)step, Achievements.Of(id)?.Steps.Length ?? 0);
        for (var s = (achievements.GetValueOrDefault(id)?.RewardedStep ?? 0) + 1; s <= last; s++)
        {
            if (await TakeStepAsync(id, s, ct) is not { } taken) break;
            replies.AddRange(taken);
        }
        return replies.Count == 0 ? [Reply(1)] : [Reply(0), .. replies];
    }

    /// <summary>
    /// Takes one step's reward; null when not claimable. Returns the state update and the inventory change, or nothing when
    /// send is false (the caller sends one inventory change for several steps).
    /// </summary>
    async Task<List<byte[]>?> TakeStepAsync(int id, int step, CancellationToken ct, bool send = true)
    {
        if (Achievements.Of(id) is not { } entry || !achievements.TryGetValue(id, out var state) || state.RewardedStep >= step || state.Count < entry.StepCount(step)) return null;
        if (PaidPassLocked(id)) return null;   // the paid pass's levels need a purchase (none is sold)
        var items = entry.StepRewards(step);
        if (Store is { } store && CharacterId is { } character)
        {
            var ok = await Stored(async () => await store.TakeAchievementRewardAsync(character, id, step, items, ct) ? [[]] : []);
            if (ok.Count == 0) return null;
        }
        state.RewardedStep = step;
        achievementDirty.Add(id);
        if (!send) return [];
        return [AchievementUpdate([id]), .. items.Count > 0 ? await InventoryChangesAsync(ct, IncreaseAchievementReward, 0) : []];
    }

    /// <summary>0x5687 TakeAchievementGroupReward_RQ {group i32, step u8}: the group's reward at that step once enough of its achievements are complete.</summary>
    async Task<List<byte[]>> TakeGroupRewardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var groupId = BitConverter.ToInt32(plain, 2);
        var step = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(TakeAchievementGroupRewardRs).U16(result).I32(groupId).U8(step).ToArray();
        var taken = await TakeGroupStepAsync(groupId, step, ct);
        return taken is null ? [Reply(1)] : [Reply(0), .. taken];
    }

    /// <summary>
    /// The achievement steps reached in a group: what its reward thresholds count. Group_L_Growth_Accomplish has 10 achievements
    /// with 67 steps and thresholds 11..68, so finished achievements (at most 10) never reached the first one while the client,
    /// counting steps, showed the reward as ready (live 2026-11-06: collect-all took nothing). Single-step groups count the same
    /// either way (the official claim of Group_D_Adventure_Imprint step 1, capture 2026-11-06).
    /// </summary>
    int CompletedInGroup(int groupId) => Achievements.InGroup(groupId)
        .Sum(e => achievements.GetValueOrDefault(e.Id) is { } state ? e.Steps.Count(st => st[1] <= state.Count) : 0);

    async Task<List<byte[]>?> TakeGroupStepAsync(int groupId, int step, CancellationToken ct)
    {
        var rewards = Achievements.GroupRewards(groupId);
        if (step < 1 || step > rewards.Count || groupRewardsTaken.Contains((groupId, step))) return null;
        var row = rewards[step - 1];
        if (row.Length < 3 || CompletedInGroup(groupId) < row[0]) return null;
        var items = new List<(int, long)> { ((int)row[1], row[2]) };
        if (Store is { } store && CharacterId is { } character)
        {
            var ok = await Stored(async () => await store.TakeGroupRewardAsync(character, groupId, step, items, ct) ? [[]] : []);
            if (ok.Count == 0) return null;
        }
        groupRewardsTaken.Add((groupId, step));
        return await InventoryChangesAsync(ct, IncreaseAchievementReward, 0);
    }

    /// <summary>0x5689 TakeAchievementRewardBatch_RQ {category u8, group i32, only character}: every claimable step of the group (or category).</summary>
    async Task<List<byte[]>> TakeRewardBatchAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 8) return [];
        var category = plain[2];
        var groupId = BitConverter.ToInt32(plain, 3);
        var only = plain[7] != 0;
        var replies = new List<byte[]>();
        var results = new List<(int Group, List<byte> GroupSteps, Dictionary<int, int> Steps)>();
        var groups = groupId != 0 ? [groupId] : Achievements.All.Select(e => Achievements.GroupOf(e)).OfType<AchievementTables.Group>().Where(g => (g.Race == 0 || g.Race == profileRace) && CategoryOrdinal(g.Category) == category).Select(g => g.Id).Distinct().ToList();
        foreach (var gid in groups)
        {
            var steps = new Dictionary<int, int>();
            var groupSteps = new List<byte>();
            foreach (var entry in Achievements.InGroup(gid))
                for (var step = (achievements.GetValueOrDefault(entry.Id)?.RewardedStep ?? 0) + 1; step <= entry.Steps.Length; step++)
                {
                    var taken = await TakeStepAsync(entry.Id, step, ct);
                    if (taken is null) break;
                    replies.AddRange(taken);
                    steps[entry.Id] = step;
                }
            for (var step = 1; step <= Achievements.GroupRewards(gid).Count; step++)
                if (await TakeGroupStepAsync(gid, step, ct) is { } taken) { replies.AddRange(taken); groupSteps.Add((byte)step); }
            if (steps.Count > 0 || groupSteps.Count > 0) results.Add((gid, groupSteps, steps));
        }
        // OBSERVED (official category-7 claims, captures 20261106-105448 / 20261107-014953): the item change, then 0x568a, and no
        // AchievementCountUpdate for the claimed achievements; the reply's taken steps are the client's new state. Our extra
        // 0x5684 (count, complete time 0, rewarded step) after it left the collection button on "collect" (live 2026-11-07).
        return [.. replies.Where(b => (b[0] | b[1] << 8) == 0x561b), TakeRewardBatchReply(category, groupId, only, results),
            .. replies.Where(b => (b[0] | b[1] << 8) is not (0x561b or AchievementCountUpdateNt))];
    }

    /// <summary>
    /// 0x568a TakeAchievementRewardBatch_RS (usmap): result, category, group, only character, more available (packed bools), then per
    /// group {group i32, taken group reward steps [u8], taken achievement steps map i32 -> u8}.
    /// </summary>
    public static byte[] TakeRewardBatchReply(byte category, int groupId, bool only, IReadOnlyList<(int Group, List<byte> GroupSteps, Dictionary<int, int> Steps)> results)
    {
        var w = PacketWriter.Opcode(TakeAchievementRewardBatchRs).U16(0).U8(category).I32(groupId).Bool(only).Bool(false).Varint((uint)results.Count);
        foreach (var (gid, groupSteps, steps) in results)
        {
            w.I32(gid).Varint((uint)groupSteps.Count);
            foreach (var step in groupSteps) w.U8(step);
            // _taken_achievement_steps is a map i32 -> u8. We wrote the step as i32 (3 bytes too many per entry): with several
            // rewards the client misread the rest and crashed (live 2026-11-06, collect-all in the Achievements window).
            w.Varint((uint)steps.Count);
            foreach (var (id, step) in steps) w.I32(id).U8((byte)Math.Clamp(step, 0, 255));
        }
        return w.ToArray();
    }

    static byte CategoryOrdinal(string category) => category switch
    {
        "Achievement" => 1, "Journal" => 2, "Unlock" => 3, "SeasonMission" => 4, "SeasonMissionExp" => 5, "BattlePass" => 6, "Attendance" => 7,
        "SeasonMissionUnlock" => 8, "Onboarding" => 9, "EventMission" => 10, "MembershipPass" => 11, _ => 0,
    };

    // ---------- Attendance and the close reports ----------

    /// <summary>0xe34d Attendance_RQ: today's check-in on every open board (one day per UTC day); always result 0, as official.</summary>
    List<byte[]> Attendance(long now)
    {
        var replies = new List<byte[]>();
        var today = CycleStart(1, now);
        foreach (var board in Achievements.OpenBoards(profileRace, now))
        {
            var state = achievements.GetValueOrDefault(board.Achievement);
            if (state is { UpdatedMs: > 0 } && state.UpdatedMs >= today && state.Count > 0) continue;
            replies.AddRange(Achieve("Attendance", 1, e => e.Id == board.Achievement, now));
        }
        // The boards our tables lack (UnlistedBoards): the day counted, marked taken (no known reward), so the client shows no
        // check-in or reward pending on them.
        foreach (var id in UnlistedBoards.GetValueOrDefault(profileRace) ?? [])
        {
            if (!achievements.TryGetValue(id, out var state)) achievements[id] = state = new AchievementState();
            if (state.UpdatedMs >= today && state.Count > 0) continue;
            state.Count++;
            state.RewardedStep = Math.Min(255, state.Count);
            state.UpdatedMs = now;
            achievementDirty.Add(id);
            replies.Add(AchievementUpdate([id]));
        }
        // OBSERVED: all 10 official Attendance_RS are "4ee30000" (result 0), the client's repeat requests (every click on the
        // attendance button) included. Our result 1 on a repeat (live 2026-11-07, 7 clicks) kept the window from opening.
        return [PacketWriter.Opcode(AttendanceRs).U16(0).ToArray(), .. replies];
    }

    /// <summary>
    /// Attendance boards the official login lists that our achievement tables lack (OBSERVED: official AchievementList_NT of
    /// 2026-11-06/07 carries 5x0320100, 5x0330100 (our boards 1022/1023) and 5x0350100, 5x0360100). Without them the client
    /// showed their check-in as pending: red dots in the attendance window with nothing to collect (live 2026-11-07).
    /// PROVISIONAL: their days and rewards are unknown; each day is counted and marked taken.
    /// </summary>
    static readonly Dictionary<int, int[]> UnlistedBoards = new() { [1] = [510350100, 510360100], [2] = [520350100, 520360100] };

    List<byte[]> CloseNpcAchievement(byte[] plain)
    {
        if (plain.Length < 6) return [];
        var key = BitConverter.ToInt32(plain, 2);
        var npcId = targets?.GetValueOrDefault(key)?.NpcId ?? 0;
        return [PacketWriter.Opcode(CloseNpcAchievementRs).U16(0).I32(key).ToArray(), .. npcId == 0 ? [] : Achieve("CloseNPC", 1, e => e.Npcs?.Contains(npcId) == true)];
    }

    List<byte[]> CloseEnvObjAchievement(byte[] plain)
    {
        if (plain.Length < 6) return [];
        var key = BitConverter.ToInt32(plain, 2);
        var index = key - WorldObjects.FirstKey;
        var objects = WorldObjects.ObjectsOn(worldMap);
        var name = index >= 0 && index < objects.Count ? objects[index].Name : null;
        return [PacketWriter.Opcode(CloseEnvObjAchievementRs).U16(0).I32(key).ToArray(), .. name is null ? [] : Achieve("CloseEnvObj", 1, e => e.Has(name))];
    }

    // ---------- Persistence ----------

    /// <summary>The changed states, written in one batch (each tick that has changes, at most every two seconds).</summary>
    internal Task FlushAchievementsForChecks(long now) => FlushAchievementsAsync(now, default, force: true);

    async Task FlushAchievementsAsync(long now, CancellationToken ct, bool force = false)
    {
        if (achievementDirty.Count == 0 || Store is not { } store || CharacterId is not { } character || (!force && now < achievementFlushAt)) return;
        achievementFlushAt = now + 2000;
        var records = achievementDirty.Select(id => achievements.TryGetValue(id, out var s)
                ? new AchievementRecord(id, s.Count, s.CompletedMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(s.CompletedMs), s.RewardedStep, DateTimeOffset.FromUnixTimeMilliseconds(s.UpdatedMs == 0 ? now : s.UpdatedMs))
                : null).OfType<AchievementRecord>().ToList();
        achievementDirty.Clear();
        await Stored(async () => await store.SaveAchievementsAsync(character, records, ct) ? [[]] : []);
    }
}
