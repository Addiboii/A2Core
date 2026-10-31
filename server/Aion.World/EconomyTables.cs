using System.Text.Json;

namespace Aion.World;

/// <summary>Reads one embedded tools/data file (tools/economy_data.py).</summary>
static class EmbeddedJson
{
    public static JsonElement Load(string name)
    {
        using var stream = typeof(EmbeddedJson).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded game data {name}");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    public static int Int(string key) => int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Godstones (tools/data/godstones.json from GodstoneData, GodstoneGacha, GodstoneCombine and Item.dat of build 26262663).
/// A godstone is a collection entry, not a bag item: a GetGodstone item adds one (its gacha group), a weapon slot takes
/// one, and a combine of same-grade stones rolls one from the material grade's pool (probabilities out of 100000000).
/// </summary>
public sealed class GodstoneTables
{
    public sealed record Stone(int Id, int Grade, int Abnormal, bool Combinable);

    readonly Dictionary<int, Stone> stones = [];
    readonly Dictionary<string, (int Id, int Prob)[]> gacha = [];
    readonly Dictionary<int, (int Id, int Prob)[]> combine = [];
    readonly Dictionary<int, string> items = [];
    readonly Dictionary<int, int> slots = [];

    /// <summary>Stones a combine takes (GlobalSetting godstone_combine_material_count_max; CANDIDATE: exactly that many).</summary>
    public int CombineCount { get; }
    /// <summary>Kina for socketing one stone (GlobalSetting godstone_cost_money_count; CANDIDATE use).</summary>
    public long SocketKina { get; }

    GodstoneTables(JsonElement root)
    {
        foreach (var p in root.GetProperty("stones").EnumerateObject())
            stones[EmbeddedJson.Int(p.Name)] = new Stone(EmbeddedJson.Int(p.Name), p.Value[0].GetInt32(), p.Value[1].GetInt32(), p.Value[2].GetInt32() == 1);
        foreach (var p in root.GetProperty("gacha").EnumerateObject())
            gacha[p.Name] = [.. p.Value.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32()))];
        foreach (var p in root.GetProperty("combine").EnumerateObject())
            combine[EmbeddedJson.Int(p.Name)] = [.. p.Value.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32()))];
        foreach (var p in root.GetProperty("items").EnumerateObject()) items[EmbeddedJson.Int(p.Name)] = p.Value.GetString()!;
        foreach (var p in root.GetProperty("slots").EnumerateObject()) slots[EmbeddedJson.Int(p.Name)] = p.Value.GetInt32();
        var settings = root.GetProperty("settings");
        CombineCount = settings.GetProperty("combine_count").GetInt32();
        SocketKina = settings.GetProperty("socket_kina").GetInt64();
    }

    static readonly Lazy<GodstoneTables> embedded = new(() => new GodstoneTables(EmbeddedJson.Load("godstones.json")));
    public static GodstoneTables Embedded => embedded.Value;

    public Stone? Of(int id) => stones.GetValueOrDefault(id);
    /// <summary>Godstone slots of an equipment template (Item.dat GodStoneSlotCount; 0: none).</summary>
    public int Slots(int templateId) => slots.GetValueOrDefault(templateId);
    public bool IsGodstoneItem(int templateId) => items.ContainsKey(templateId);

    static int Roll((int Id, int Prob)[] pool, int roll)
    {
        var total = pool.Sum(p => (long)p.Prob);
        if (total <= 0) return 0;
        var at = (long)roll * total / 100_000_000;
        foreach (var (id, prob) in pool)
        {
            if (at < prob) return id;
            at -= prob;
        }
        return pool[^1].Id;
    }

    /// <summary>The stone a GetGodstone item gives for a roll 0..99999999; 0 when the item is not one.</summary>
    public int FromItem(int templateId, int roll) => items.TryGetValue(templateId, out var group) && gacha.TryGetValue(group, out var pool) ? Roll(pool, roll) : 0;

    /// <summary>The stone a combine of <paramref name="grade"/> materials gives for a roll 0..99999999; 0 when that grade has no pool.</summary>
    public int Combine(int grade, int roll) => combine.TryGetValue(grade, out var pool) ? Roll(pool, roll) : 0;
    public bool CanCombine(int grade) => combine.ContainsKey(grade);
}

/// <summary>
/// Exceed enchanting (tools/data/exceed-enchant.json from ExceedEnchant, AdditionalStat and Item.dat of build 26262663):
/// after an item's enchant reaches its Enchant.dat maximum, ItemExceedEnchant raises upgrades.exceed through its group's
/// rows (success /10000, fail correction, fail penalty, Kina, costs); each level adds that row's AdditionalStat totals.
/// </summary>
public sealed class ExceedEnchantTables
{
    public sealed record Step(int Success, int FailCorrection, int FailPenalty, long Kina, IReadOnlyList<(int ItemId, int Count)> Costs);

    readonly Dictionary<int, string> items = [];
    readonly Dictionary<string, JsonElement[]> groups = [];

    ExceedEnchantTables(JsonElement root)
    {
        foreach (var p in root.GetProperty("items").EnumerateObject()) items[EmbeddedJson.Int(p.Name)] = p.Value.GetString()!;
        foreach (var p in root.GetProperty("groups").EnumerateObject()) groups[p.Name] = [.. p.Value.EnumerateArray()];
    }

    static readonly Lazy<ExceedEnchantTables> embedded = new(() => new ExceedEnchantTables(EmbeddedJson.Load("exceed-enchant.json")));
    public static ExceedEnchantTables Embedded => embedded.Value;

    JsonElement[]? Rows(int itemId) => items.TryGetValue(itemId, out var group) && groups.TryGetValue(group, out var rows) ? rows : null;

    /// <summary>The attempt from exceed level <paramref name="level"/>; null at the maximum or for an item without exceed.</summary>
    public Step? Next(int itemId, int level)
    {
        if (Rows(itemId) is not { } rows || level < 0 || level >= rows.Length || rows[level][0].GetInt32() <= 0) return null;
        var r = rows[level];
        return new Step(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt64(), [.. r[4].EnumerateArray().Select(c => (c[0].GetInt32(), c[1].GetInt32()))]);
    }

    /// <summary>Stat totals at an exceed level (AdditionalStat of that row).</summary>
    public IReadOnlyDictionary<string, int> Effect(int itemId, int level)
    {
        if (level <= 0 || Rows(itemId) is not { } rows) return new Dictionary<string, int>();
        return rows[Math.Min(level, rows.Length - 1)][5].EnumerateArray().ToDictionary(s => s[0].GetString()!, s => s[1].GetInt32());
    }
}

/// <summary>Item.dat trade and storage rules (tools/data/item-rules.json): broker categories, legion warehouse permission, binding.</summary>
public sealed class ItemRules
{
    readonly Dictionary<int, string> trade = [];
    readonly HashSet<int> guildStorage, bindOnAcquire, nonExchangeable;

    ItemRules(JsonElement root)
    {
        foreach (var p in root.GetProperty("trade").EnumerateObject()) trade[EmbeddedJson.Int(p.Name)] = p.Value.GetString()!;
        guildStorage = [.. root.GetProperty("guild_storage").EnumerateArray().Select(e => e.GetInt32())];
        bindOnAcquire = [.. root.GetProperty("bind_on_acquire").EnumerateArray().Select(e => e.GetInt32())];
        nonExchangeable = [.. root.GetProperty("non_exchangeable").EnumerateArray().Select(e => e.GetInt32())];
    }

    static readonly Lazy<ItemRules> embedded = new(() => new ItemRules(EmbeddedJson.Load("item-rules.json")));
    public static ItemRules Embedded => embedded.Value;

    /// <summary>Whether the broker takes the template (a TradeCategory and an exchangeable kind); binding is checked per instance.</summary>
    public bool Tradable(int templateId) => trade.ContainsKey(templateId);
    public string? TradeCategory(int templateId) => trade.GetValueOrDefault(templateId);
    public bool GuildStorable(int templateId) => guildStorage.Contains(templateId) && !bindOnAcquire.Contains(templateId) && !nonExchangeable.Contains(templateId);
    public bool BoundOnAcquire(int templateId) => bindOnAcquire.Contains(templateId);
}

/// <summary>Legion data (tools/data/guild.json): levels, grade authority, warehouse extensions and settings.</summary>
public sealed class GuildTables
{
    readonly (int Level, long Exp, int Members, int Officers)[] levels;
    readonly (long Cost, int Level)[] extensions;

    public IReadOnlyDictionary<int, int> GradeAuthority { get; }
    public int StorageSlots { get; }
    public int SlotsPerExtension { get; }
    public long StorageSortCooldownMs { get; }
    public int JoinRequestsSent { get; }
    public int JoinRequestsReceived { get; }
    public int HistoryDays { get; }
    public long AttendanceExp { get; }
    public int MaxExtensions => extensions.Length;

    GuildTables(JsonElement root)
    {
        levels = [.. root.GetProperty("levels").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt64(), r[2].GetInt32(), r[3].GetInt32()))];
        extensions = [.. root.GetProperty("storage_extensions").EnumerateArray().Select(r => (r[0].GetInt64(), r[1].GetInt32()))];
        GradeAuthority = root.GetProperty("grade_authority").EnumerateObject().ToDictionary(p => EmbeddedJson.Int(p.Name), p => p.Value.GetInt32());
        var s = root.GetProperty("settings");
        StorageSlots = s.GetProperty("storage_slots").GetInt32();
        SlotsPerExtension = s.GetProperty("storage_slots_per_extension").GetInt32();
        StorageSortCooldownMs = s.GetProperty("storage_sort_cooldown_ms").GetInt64();
        JoinRequestsSent = s.GetProperty("join_requests_sent").GetInt32();
        JoinRequestsReceived = s.GetProperty("join_requests_received").GetInt32();
        HistoryDays = s.GetProperty("history_days").GetInt32();
        AttendanceExp = s.GetProperty("attendance_exp").GetInt64();
    }

    static readonly Lazy<GuildTables> embedded = new(() => new GuildTables(EmbeddedJson.Load("guild.json")));
    public static GuildTables Embedded => embedded.Value;

    /// <summary>The level row for a legion level (the last row beyond the table).</summary>
    public (int Level, long Exp, int Members, int Officers) Level(int level) => levels[Math.Clamp(level, 1, levels.Length) - 1];
    public int Slots(int extensionCount) => StorageSlots + Math.Max(0, extensionCount) * SlotsPerExtension;
    /// <summary>The cost of extension number <paramref name="next"/> (1-based) and the legion level it needs; null past the last.</summary>
    public (long Cost, int Level)? Extension(int next) => next >= 1 && next <= extensions.Length ? extensions[next - 1] : null;
}
