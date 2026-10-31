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
        [property: JsonPropertyName("from")] long FromMs = 0, [property: JsonPropertyName("to")] long ToMs = 0,
        [property: JsonPropertyName("sg")] string? SeasonGroup = null, [property: JsonPropertyName("sn")] int SeasonNo = 0,
        [property: JsonPropertyName("sgid")] int SeasonGroupId = 0)
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
// Counting (OURS, by EAchievementObjectiveType): PCLevel, ClearQuest/ClearQuestType, KillNpc/KillNpcAll, ClearDungeon/
// ClearDungeonType/ClearDungeonGroup/ClearDungeonBySingle, ClearMapEvent, UseEnvObjSpawnerName, UseItem/UseUsableItemType,
// CraftCategorySuccess, GatherSuccess/GatherCategorySuccess, CraftMasteryLevel/GatherMasteryLevel, EquipItemLevel,
// DecomposeItem, SoulbindTry, LoginCharacter, Attendance, EngraveTeleportArtifact, JoinParty, GuildJoin, AddFriend,
// StorageStore, CloseNPC, ClearAchievement. Rewards: TakeAchievementReward 0x5685/0x5686 {id, step}, group rewards
// 0x5687/0x5688 {group, step}, the batch 0x5689/0x568a; Attendance_RQ/RS 0xe34d/0xe34e (usmap index 1503/1504 + E3Base 0xdd6e;
// the older 0xe2e6/0xe2e7 E2 run is not what the live client sends) checks in once a day; CloseNpcAchievement 0xe2c8/0xe2c9,
// CloseEnvObjAchievement 0xe2ca/0xe2cb. Opcodes in the anchored runs,
// bodies from the usmap: CANDIDATE except the two observed.
public sealed partial class GameSession
{
    public const ushort AchievementListNt = 0x5683, AchievementCountUpdateNt = 0x5684, TakeAchievementRewardRq = 0x5685, TakeAchievementRewardRs = 0x5686,
        TakeAchievementGroupRewardRq = 0x5687, TakeAchievementGroupRewardRs = 0x5688, TakeAchievementRewardBatchRq = 0x5689, TakeAchievementRewardBatchRs = 0x568a,
        AttendanceRq = SocialOpcodes.E3Base + 1503, AttendanceRs = SocialOpcodes.E3Base + 1504, SeasonScheduleNt = 0x8d53, AccountContentsInfoListNt = 0x56b6, CloseNpcAchievementRq = 0xe2c8, CloseNpcAchievementRs = 0xe2c9, CloseEnvObjAchievementRq = 0xe2ca, CloseEnvObjAchievementRs = 0xe2cb;
    const byte IncreaseAchievementReward = 26;   // EIncreaseReason (CANDIDATE value)
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
            if (Achievements.Of(record.Id) is { Reset: 1 or 2 } entry && ContentResetSchedule.AchievementCycleStartMs(entry.Reset, now) > state.UpdatedMs)
                state = new AchievementState { UpdatedMs = now };
            achievements[record.Id] = state;
        }
    }

    static long CycleStart(int reset, long nowMs) => ContentResetSchedule.AchievementCycleStartMs(reset, nowMs);

    /// <summary>Whether the character's list carries this achievement (race, category, open attendance boards).</summary>
    bool Listed(AchievementTables.Entry entry, long now)
    {
        if (Achievements.GroupOf(entry) is not { } group || (group.Race != 0 && group.Race != profileRace)) return false;
        if (ListedCategories.Contains(group.Category)) return true;
        if (SeasonMissionListed(entry, now)) return true;
        if (group.Category == "BattlePass") return PassOfMission(entry, now) is not null;
        return group.Category == "Attendance" && Achievements.OpenBoards(profileRace, now).Any(b => b.Achievement == entry.Id);
    }

    static PacketWriter WriteState(PacketWriter w, int id, AchievementState? state) =>
        w.I32(id).I32(state?.Count ?? 0).I64(state?.CompletedMs ?? 0).U8((byte)(state?.RewardedStep ?? 0));

    /// <summary>AchievementList_NT at world entry: the listed achievements with their states, no group reward states.</summary>
    byte[] AchievementList(long now)
    {
        var listed = Achievements.All.Where(e => Listed(e, now)).ToList();
        var w = PacketWriter.Opcode(AchievementListNt).Varint((uint)listed.Count);
        foreach (var entry in listed) WriteState(w, entry.Id, achievements.GetValueOrDefault(entry.Id));
        return w.Varint(0).ToArray();
    }

    byte[] AchievementUpdate(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(AchievementCountUpdateNt).Varint((uint)ids.Count);
        foreach (var id in ids) WriteState(w, id, achievements.GetValueOrDefault(id));
        return w.ToArray();
    }

    /// <summary>Every open attendance board achievement (same set as AccountContentsInfoList_NT).</summary>
    IReadOnlyList<int> AttendanceAchievementIds(long now) =>
        LatestAttendanceSeasons(profileRace, now).Select(e => e.Board.Achievement).ToList();

    /// <summary>After an attendance reward claim, sync every board's persisted state — partial 0x5684 makes other tabs' red dots return.</summary>
    IReadOnlyList<int> AchievementUpdateIds(int id, long now) =>
        Achievements.Boards.Any(b => b.Achievement == id) ? AttendanceAchievementIds(now) : [id];

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

    List<byte[]> AchieveQuest(string code, string? type) =>
        [.. Achieve("ClearQuest", 1, e => e.Has(code)), .. Achieve("ClearQuestType", 1, e => e.Values.Length == 0 || (type is not null && e.Has(type)))];

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
         .. Achieve("EnchantLevelEquipCategory", 1, e => e.Values.Length >= 2 && int.TryParse(e.Values[1], out var need) && level >= need),
         .. Achieve("EnchantLevelItemGrade", 1, e => e.Values.Length >= 1 && int.TryParse(e.Values[0], out var need) && level >= need)];

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

    /// <summary>0x5685 TakeAchievementReward_RQ {id i32, step u8}: the step's items once the step's count is reached and not yet rewarded.</summary>
    async Task<List<byte[]>> TakeAchievementRewardAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var id = BitConverter.ToInt32(plain, 2);
        var step = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(TakeAchievementRewardRs).U16(result).I32(id).U8(step).ToArray();
        var taken = await TakeStepAsync(id, step, now, ct);
        if (taken is null) return [Reply(1)];
        await FlushAchievementsAsync(now, ct, force: true);
        if (Achievements.Boards.Any(b => b.Achievement == id)) taken.Add(AccountContentsInfoList(now));
        return [.. taken, Reply(0)];
    }

    /// <summary>Takes one step's reward; null when not claimable. Returns the state update and the inventory change.</summary>
    async Task<List<byte[]>?> TakeStepAsync(int id, int step, long now, CancellationToken ct)
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
        return [AchievementUpdate(AchievementUpdateIds(id, now)), .. items.Count > 0 ? await InventoryChangesAsync(ct, IncreaseAchievementReward, 0) : []];
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

    int CompletedInGroup(int groupId) => Achievements.InGroup(groupId).Count(e => (achievements.GetValueOrDefault(e.Id)?.CompletedMs ?? 0) != 0);

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
    async Task<List<byte[]>> TakeRewardBatchAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 8) return [];
        var category = plain[2];
        var groupId = BitConverter.ToInt32(plain, 3);
        var only = plain[7] != 0;
        var replies = new List<byte[]>();
        var results = new List<(int Group, List<byte> GroupSteps, Dictionary<int, int> Steps)>();
        var attendanceAchievements = Achievements.Boards.Select(b => b.Achievement).ToHashSet();
        var refreshAccountContents = false;
        var groups = groupId != 0 ? [groupId] : Achievements.All.Select(e => Achievements.GroupOf(e)).OfType<AchievementTables.Group>().Where(g => (g.Race == 0 || g.Race == profileRace) && CategoryOrdinal(g.Category) == category).Select(g => g.Id).Distinct().ToList();
        foreach (var gid in groups)
        {
            var steps = new Dictionary<int, int>();
            var groupSteps = new List<byte>();
            foreach (var entry in Achievements.InGroup(gid))
                for (var step = (achievements.GetValueOrDefault(entry.Id)?.RewardedStep ?? 0) + 1; step <= entry.Steps.Length; step++)
                {
                    var taken = await TakeStepAsync(entry.Id, step, now, ct);
                    if (taken is null) break;
                    if (attendanceAchievements.Contains(entry.Id)) refreshAccountContents = true;
                    replies.AddRange(taken);
                    steps[entry.Id] = step;
                }
            for (var step = 1; step <= Achievements.GroupRewards(gid).Count; step++)
                if (await TakeGroupStepAsync(gid, step, ct) is { } taken) { replies.AddRange(taken); groupSteps.Add((byte)step); }
            if (steps.Count > 0 || groupSteps.Count > 0) results.Add((gid, groupSteps, steps));
        }
        var takeResults = results.Select(r => (r.Group, (IReadOnlyList<byte>)r.GroupSteps, (IReadOnlyDictionary<int, int>)r.Steps)).ToList();
        var batchResult = (ushort)(takeResults.Count > 0 ? 0 : 1);
        if (takeResults.Count > 0) await FlushAchievementsAsync(now, ct, force: true);
        var batch = new List<byte[]>();
        batch.AddRange(replies);
        batch.Add(RewardBatchReply(category, groupId, only, takeResults, batchResult));
        if (refreshAccountContents) batch.Add(AccountContentsInfoList(now));
        return batch;
    }

    /// <summary>
    /// TakeAchievementRewardBatch_RS (usmap): {result, category u8, group i32, only bool, more bool,
    /// [Common_AchievementRewardBatchTakeResult {group i32, [group step u8], map achievement i32 -> step u8}]}.
    /// The map value is a ByteProperty: an i32 there shifts every later group and the client reads garbage counts.
    /// </summary>
    public static byte[] RewardBatchReply(byte category, int groupId, bool only, IReadOnlyList<(int Group, IReadOnlyList<byte> GroupSteps, IReadOnlyDictionary<int, int> Steps)> results, ushort result = 0)
    {
        var w = PacketWriter.Opcode(TakeAchievementRewardBatchRs).U16(result).U8(category).I32(groupId).Bool(only).Bool(false).Varint((uint)results.Count);
        foreach (var (gid, groupSteps, steps) in results)
        {
            w.I32(gid).Varint((uint)groupSteps.Count);
            foreach (var s in groupSteps) w.U8(s);
            w.Varint((uint)steps.Count);
            foreach (var (id, step) in steps) w.I32(id).U8((byte)step);
        }
        return w.ToArray();
    }

    static byte CategoryOrdinal(string category) => category switch
    {
        "Achievement" => 1, "Journal" => 2, "Unlock" => 3, "SeasonMission" => 4, "SeasonMissionExp" => 5, "BattlePass" => 6, "Attendance" => 7,
        "SeasonMissionUnlock" => 8, "Onboarding" => 9, "EventMission" => 10, "MembershipPass" => 11, _ => 0,
    };

    // ---------- Attendance and the close reports ----------

    /// <summary>usmap ESeasonContentsType of the attendance boards (Attendance1..Attendance5 = 22..26).</summary>
    static byte? AttendanceSeasonType(string season) => season.Length == 11 && season.StartsWith("Attendance", StringComparison.Ordinal) && season[10] is >= '1' and <= '5' ? (byte)(21 + season[10] - '0') : null;

    /// <summary>
    /// SeasonSchedule_NT 0x8d53 (catalogue 0x398e + 0x53c5, the run of NpcFirstAggro_NT 0x398d = 0x8d52 OBSERVED):
    /// {map ESeasonContentsType u8 -> Common_SeasonSchedule {latest group name, latest season no, prev group, prev no,
    /// next group, next no}} (usmap). The attendance window lists the Attendance.dat rows whose SeasonId (SeasonSchedule.dat
    /// group name + number) is the latest season of its contents type, so without this map it has no board and does not
    /// open. Sent with the open attendance boards' seasons (the latest window per type); prev/next are that type's boards
    /// of the race just before and after it in time (empty when none).
    /// </summary>
    byte[] SeasonSchedule(long now) => SeasonSchedule(profileRace, now);

    /// <summary>The latest open board per attendance ESeasonContentsType of the race, by type.</summary>
    internal static List<(byte Type, AchievementTables.Board Board)> LatestAttendanceSeasons(int race, long now) =>
        Achievements.OpenBoards(race, now)
            .Where(b => !string.IsNullOrEmpty(b.SeasonGroup) && AttendanceSeasonType(b.Season) is not null)
            .GroupBy(b => AttendanceSeasonType(b.Season)!.Value)
            .Select(g => (Type: g.Key, Board: g.OrderByDescending(b => b.FromMs).First()))
            .OrderBy(e => e.Type).ToList();

    internal static byte[] SeasonSchedule(int race, long now)
    {
        var latest = LatestAttendanceSeasons(race, now);
        var w = PacketWriter.Opcode(SeasonScheduleNt).Varint((uint)latest.Count);
        foreach (var (type, board) in latest)
        {
            var sameType = Achievements.Boards
                .Where(b => (b.Race is 0 || b.Race == race) && AttendanceSeasonType(b.Season) == type && !string.IsNullOrEmpty(b.SeasonGroup)
                            && (b.SeasonGroup != board.SeasonGroup || b.SeasonNo != board.SeasonNo))
                .ToList();
            var prev = sameType.Where(b => b.FromMs < board.FromMs).MaxBy(b => b.FromMs);
            var next = sameType.Where(b => b.FromMs > board.FromMs).MinBy(b => b.FromMs);
            w.U8(type).String(board.SeasonGroup!).I32(board.SeasonNo)
                .String(prev?.SeasonGroup ?? "").I32(prev?.SeasonNo ?? 0)
                .String(next?.SeasonGroup ?? "").I32(next?.SeasonNo ?? 0);
        }
        return w.ToArray();
    }

    /// <summary>
    /// AccountContentsInfoList_NT 0x56b6 (catalogue 0x37f8 + 0x1ebe in the anchored 0x56 run; OBSERVED `00` for a character
    /// that had not checked in): [Common_AccountContentsInfo {season key {ESeasonContentsType u8, season group id i32
    /// (SeasonScheduleGroup.dat), season no i32}, server id i32, char dbid i64, value1 i32, value2 i32}] (usmap). Client
    /// RedDot_Attendance / Attendance_Check compare value1 and value2 to the current game day (16:00 KST / 07:00 UTC), like
    /// guild attendance_date and attendance_reward_date — not achievement counts. value1 is the last check-in day; value2 is
    /// that same day once every step reward through the current count is claimed, else 0. Achievement progress stays on 0x5683/0x5684.
    /// </summary>
    byte[] AccountContentsInfoList(long now) => AccountContentsInfoList(profileRace, options.WorldId, selected?.WireId ?? 0, now,
        id =>
        {
            var s = achievements.GetValueOrDefault(id);
            return AttendanceAccountValues(s?.Count ?? 0, s?.RewardedStep ?? 0, s?.UpdatedMs ?? 0, now);
        });

    /// <summary>Maps stored attendance achievement state to AccountContentsInfo value1/value2 game-day keys.</summary>
    internal static (int CheckDay, int RewardDay) AttendanceAccountValues(int count, int rewardedStep, long updatedMs, long now)
    {
        if (count <= 0) return (0, 0);
        var todayStart = CycleStart(1, now);
        var checkDay = updatedMs >= todayStart ? ContentResetSchedule.GameDayKey(now) : ContentResetSchedule.GameDayKey(updatedMs);
        var rewardDay = AttendanceRewardsSynced(count, rewardedStep) ? checkDay : 0;
        return (checkDay, rewardDay);
    }

    /// <summary>Every step reward through the current attendance count is taken (rewardedStep is the highest claimed step).</summary>
    internal static bool AttendanceRewardsSynced(int count, int rewardedStep) => count > 0 && rewardedStep >= count;

    internal static byte[] AccountContentsInfoList(int race, int serverId, long charDbId, long now, Func<int, (int CheckDay, int RewardDay)> state)
    {
        var keyed = LatestAttendanceSeasons(race, now).Where(e => e.Board.SeasonGroupId != 0).ToList();
        var w = PacketWriter.Opcode(AccountContentsInfoListNt).Varint((uint)keyed.Count);
        foreach (var (type, board) in keyed)
        {
            var (checkDay, rewardDay) = state(board.Achievement);
            w.U8(type).I32(board.SeasonGroupId).I32(board.SeasonNo).I32(serverId).I64(charDbId).I32(checkDay).I32(rewardDay);
        }
        return w.ToArray();
    }

    /// <summary>0xe34d Attendance_RQ (empty body): today's check-in on every open board (one day per UTC day). Attendance_RS is opcode + _result u16 0; the client also expects AccountContentsInfoList_NT 0x56b6 on every reply (check-in or refresh). New check-ins add achievement updates before 0x56b6.</summary>
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
        return [PacketWriter.Opcode(AttendanceRs).U16(0).ToArray(), .. replies, AccountContentsInfoList(now)];
    }

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
        MarkClosedEnvObject(name);
        return [PacketWriter.Opcode(CloseEnvObjAchievementRs).U16(0).I32(key).ToArray(), .. name is null ? [] : Achieve("CloseEnvObj", 1, e => e.Has(name))];
    }

    // ---------- Persistence ----------

    /// <summary>The changed states, written in one batch (each tick that has changes, at most every two seconds).</summary>
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
