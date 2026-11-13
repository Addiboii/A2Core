using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// The duty quests' client data (tools/duty_data.py -> tools/data/duty.json): the mission board pool (206 DutyMission quests),
/// the 44 scroll groups (577 DutyScroll quests; which quests a group draws is a CANDIDATE rule, the client has no list), the
/// re-draw prices, the per-type limits (QuestType.dat) and the random reward packages (RewardPackage / RewardPackageItem).
/// </summary>
public sealed class DutyTables
{
    public sealed record TypeRule([property: JsonPropertyName("get_limit")] int GetLimit, [property: JsonPropertyName("limit_group")] int LimitGroup,
        [property: JsonPropertyName("complete_limit")] int CompleteLimit, [property: JsonPropertyName("duplicate")] bool Duplicate,
        [property: JsonPropertyName("reroll")] bool Reroll, [property: JsonPropertyName("random_reward")] bool RandomReward);
    public sealed record Redraw([property: JsonPropertyName("max")] int Max, [property: JsonPropertyName("costs")] long[][] Costs);
    public sealed record Mission([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("race")] string Race,
        [property: JsonPropertyName("level")] int Level, [property: JsonPropertyName("grade")] string Grade);
    public sealed record Group([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("cost")] string Cost,
        [property: JsonPropertyName("quests")] string[] Quests);

    sealed class File
    {
        [JsonPropertyName("board_size")] public int BoardSize { get; set; } = 15;
        [JsonPropertyName("reset_kst_ms")] public long ResetKstMs { get; set; } = 57_600_000;
        [JsonPropertyName("types")] public Dictionary<string, TypeRule> Types { get; set; } = [];
        [JsonPropertyName("redraw")] public Dictionary<string, Redraw> Redraw { get; set; } = [];
        [JsonPropertyName("missions")] public List<Mission> Missions { get; set; } = [];
        [JsonPropertyName("groups")] public Dictionary<string, Group> Groups { get; set; } = [];
        [JsonPropertyName("scroll_items")] public Dictionary<string, string> ScrollItems { get; set; } = [];
        // package -> [slot package, prob (of 100,000,000), count]; slot -> [item, weight, count min, count max, enchant]
        [JsonPropertyName("packages")] public Dictionary<string, JsonElement[][]> Packages { get; set; } = [];
        [JsonPropertyName("slots")] public Dictionary<string, long[][]> Slots { get; set; } = [];
    }

    static readonly Lazy<DutyTables> embedded = new(() =>
    {
        using var stream = typeof(DutyTables).Assembly.GetManifestResourceStream("duty.json") ?? throw new InvalidOperationException("duty.json is not embedded");
        return new DutyTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("duty.json"));
    });

    public static DutyTables Embedded => embedded.Value;

    readonly File file;
    readonly Dictionary<string, string> groupOf = new(StringComparer.OrdinalIgnoreCase);
    DutyTables(File file)
    {
        this.file = file;
        foreach (var (name, group) in file.Groups)
            foreach (var code in group.Quests) groupOf.TryAdd(code, name);
    }

    /// <summary>GlobalSetting dutymission_quest_journal_acquired_count: missions a board draws (15).</summary>
    public int BoardSize => file.BoardSize;
    /// <summary>The daily reset in KST ms after midnight (weekly_quest_reset_time 16:00 KST = 07:00 UTC).</summary>
    public long ResetKstMs => file.ResetKstMs;
    public IReadOnlyList<Mission> Missions => file.Missions;
    public IReadOnlyDictionary<string, Group> Groups => file.Groups;
    public TypeRule? Rule(string? type) => type is null ? null : file.Types.GetValueOrDefault(type);
    public Redraw? RedrawOf(string? type) => type is null ? null : file.Redraw.GetValueOrDefault(type);
    /// <summary>The first group drawing a quest (a scroll quest given before its group was stored).</summary>
    public string? GroupOf(string code) => groupOf.GetValueOrDefault(code);
    /// <summary>Item.dat UseEffect GetDutyQuest: the scroll's group.</summary>
    public string? ScrollGroup(int itemId) => file.ScrollItems.GetValueOrDefault(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static bool IsDuty(string? type) => type is "DutyMission" or "DutyScroll";

    /// <summary>
    /// The random rewards a quest offers (QuestReward RandomReward package): one item per slot, drawn by the slot's ItemProb weights
    /// with a count between its min and max, carrying the slot's Prob (OBSERVED form: 5 items, _prob 100M/50M/33M/25M/10M).
    /// </summary>
    public DutyReward[] Roll(string? package, Random rng)
    {
        if (package is null || !file.Packages.TryGetValue(package, out var slots)) return [];
        var rolled = new List<DutyReward>();
        foreach (var slot in slots)
        {
            if (slot.Length < 2 || slot[0].GetString() is not { } name || !file.Slots.TryGetValue(name, out var candidates) || candidates.Length == 0) continue;
            var total = candidates.Sum(c => Math.Max(0, c[1]));
            if (total <= 0) continue;
            var pick = rng.NextInt64(total);
            var chosen = candidates[^1];
            foreach (var c in candidates)
            {
                if (pick < Math.Max(0, c[1])) { chosen = c; break; }
                pick -= Math.Max(0, c[1]);
            }
            var count = chosen[2] >= chosen[3] ? chosen[2] : chosen[2] + rng.NextInt64(chosen[3] - chosen[2] + 1);
            rolled.Add(new DutyReward((int)chosen[0], Math.Max(1, count), (byte)Math.Clamp(chosen[4], 0, 255), slot[1].GetInt64()));
        }
        return [.. rolled];
    }

    public const long ProbScale = 100_000_000;

    /// <summary>
    /// The offered items a turn-in pays: each with its _prob of 100,000,000 (the first slot always). CANDIDATE reading of the
    /// independent per-slot chance (the slots' Prob do not add up to 100M); no official turn-in of a duty quest was recorded.
    /// </summary>
    public static List<int> Paid(IReadOnlyList<DutyReward> offered, Random rng)
    {
        var paid = new List<int>();
        for (var i = 0; i < offered.Count; i++)
            if (offered[i].Prob >= ProbScale || rng.NextInt64(ProbScale) < offered[i].Prob) paid.Add(i);
        return paid;
    }

    /// <summary>The start (ms) of the duty day holding <paramref name="nowMs"/>: the last 16:00 KST (07:00 UTC).</summary>
    public long CycleOf(long nowMs)
    {
        const long day = 86_400_000L;
        var shifted = nowMs + RankTables.KstMs - ResetKstMs;
        var days = shifted >= 0 ? shifted / day : (shifted - day + 1) / day;
        return days * day + ResetKstMs - RankTables.KstMs;
    }

    /// <summary>The next daily reset after <paramref name="nowMs"/>.</summary>
    public long NextReset(long nowMs) => CycleOf(nowMs) + 86_400_000L;
}

/// <summary>An offered random reward item (Common_RewardItemWithProb).</summary>
public sealed record DutyReward(int Item, long Count, byte Enchant, long Prob);

/// <summary>A mission board entry (Common_DutyMissionQuest): EQuestStatus 1 Acquirable, 2 accepted (its quest uid), 4 rewarded.</summary>
public sealed record DutyEntry(int Uid, int QuestId, int QuestUid, byte Status, DutyReward[] Rewards);

/// <summary>A held duty quest's own data: re-draws done, the scroll group it came from, the offered random rewards.</summary>
public sealed record DutyQuestExtra(int Roll, string? Group, DutyReward[] Rewards);

/// <summary>
/// One character's mission board (DutyMissionQuestJournalStatus_NT) and duty quest data, kept in the character settings
/// "duty_board" and "duty_quests".
/// OBSERVED (rec 20261106-115817 #173793 at the world entry, the same body at three logins of 2026-11-07): refreshed 3, rewarded 0,
/// 19 missions = 15 offered (status 1, uids 91-105 drawn together, one of them accepted since) + 4 older accepted ones (status 2,
/// uids 52-76), expire and weekly expire both 2026-11-01 07:00 UTC (in the past: the board is not redrawn at the login); a board
/// without missions answers {0, rewarded, [], now + 5 s, [], now + 5 s} (rec 20261106-145442 #6635, 20261107-011529 #19481).
/// </summary>
public sealed class DutyState
{
    public int Refreshed { get; set; }
    public int Rewarded { get; set; }
    /// <summary>The duty day (DutyTables.CycleOf) the counters belong to; 0 before the first board.</summary>
    public long Cycle { get; set; }
    public long Expire { get; set; }
    public long WeeklyExpire { get; set; }
    public int NextUid { get; set; } = 1;
    public List<DutyEntry> Board { get; } = [];
    public Dictionary<string, DutyQuestExtra> Quests { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A new duty day: counters back to 0 (the board is redrawn by the caller).</summary>
    public bool StartCycle(long cycle)
    {
        if (Cycle == cycle) return false;
        (Cycle, Refreshed, Rewarded) = (cycle, 0, 0);
        return true;
    }

    /// <summary>
    /// Draws the board: accepted missions (status 2, still held) stay; the others are replaced by up to BoardSize missions of the
    /// race the level allows, not held, each with its random rewards rolled. Returns the uids dropped. OURS/CANDIDATE: uniform draw
    /// (the official 15 held 13 Common + 2 Rare of the 89/9/5 Common/Rare/Legend pool, as a uniform draw would).
    /// </summary>
    public List<int> Draw(DutyTables tables, QuestBook book, int race, int level, Func<string, bool> held, Random rng, long now)
    {
        var dropped = Board.Where(e => !(e.Status == 2 && book.ById.TryGetValue(e.QuestId, out var c) && held(c))).Select(e => e.Uid).ToList();
        Board.RemoveAll(e => dropped.Contains(e.Uid));
        var kept = Board.Select(e => e.QuestId).ToHashSet();
        var pool = Eligible(tables, book, race, level).Where(m => !held(m.Code) && !kept.Contains(book.Find(m.Code)!.Id)).ToList();
        for (var i = pool.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }
        foreach (var mission in pool.Take(tables.BoardSize).OrderBy(m => m.Code, StringComparer.Ordinal))
        {
            var quest = book.Find(mission.Code)!;
            Board.Add(new DutyEntry(NextUid++, quest.Id, 0, 1, tables.Roll(quest.Rewards?.Random, rng)));
        }
        // A board without missions: the OBSERVED short expiry (now + 5 s); else the next daily reset (CANDIDATE: weekly = daily, as recorded).
        Expire = WeeklyExpire = Eligible(tables, book, race, level).Any() ? tables.NextReset(now) : now + 5_000;
        return dropped;
    }

    public static IEnumerable<DutyTables.Mission> Eligible(DutyTables tables, QuestBook book, int race, int level) =>
        tables.Missions.Where(m => book.Find(m.Code) is { } q && QuestBook.RaceMatches(q, race) && level >= q.Level && q.Steps.Length > 0);

    // ---------- storage (character settings, JSON) ----------

    static long[][] Pack(IEnumerable<DutyReward> rewards) => rewards.Select(r => new[] { r.Item, r.Count, r.Enchant, r.Prob }).ToArray();
    static DutyReward[] Unpack(JsonElement e) => e.ValueKind != JsonValueKind.Array ? [] :
        e.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 4)
            .Select(r => new DutyReward(r[0].GetInt32(), r[1].GetInt64(), (byte)r[2].GetInt32(), r[3].GetInt64())).ToArray();

    public JsonElement BoardJson() => JsonSerializer.SerializeToElement(new
    {
        refreshed = Refreshed, rewarded = Rewarded, cycle = Cycle, expire = Expire, weekly = WeeklyExpire, next = NextUid,
        board = Board.Select(e => new object[] { e.Uid, e.QuestId, e.QuestUid, e.Status, Pack(e.Rewards) }),
    });

    public JsonElement QuestsJson() => JsonSerializer.SerializeToElement(Quests.ToDictionary(q => q.Key, q => new object?[] { q.Value.Roll, q.Value.Group, Pack(q.Value.Rewards) }));

    static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    public static DutyState Load(IReadOnlyDictionary<string, JsonElement> settings)
    {
        var state = new DutyState();
        try
        {
            if (settings.TryGetValue(DutyBoardSetting, out var b) && b.ValueKind == JsonValueKind.Object)
            {
                (state.Refreshed, state.Rewarded) = ((int)Long(b, "refreshed"), (int)Long(b, "rewarded"));
                (state.Cycle, state.Expire, state.WeeklyExpire) = (Long(b, "cycle"), Long(b, "expire"), Long(b, "weekly"));
                state.NextUid = (int)Math.Max(1, Long(b, "next"));
                if (b.TryGetProperty("board", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var e in list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 5))
                        state.Board.Add(new DutyEntry(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), (byte)e[3].GetInt32(), Unpack(e[4])));
            }
            if (settings.TryGetValue(DutyQuestsSetting, out var q) && q.ValueKind == JsonValueKind.Object)
                foreach (var p in q.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 3))
                    state.Quests[p.Name] = new DutyQuestExtra(p.Value[0].GetInt32(), p.Value[1].ValueKind == JsonValueKind.String ? p.Value[1].GetString() : null, Unpack(p.Value[2]));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException) { return new DutyState(); }
        return state;
    }

    public const string DutyBoardSetting = "duty_board", DutyQuestsSetting = "duty_quests";
}
