using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>A usable item's effect (Item.dat UseEffect): RewardBox, GetVehicle or GetVehicleSoul, GetDaevanionPoint, AutoUseWhenAcquire, values.</summary>
public sealed record ItemUse(string Effect, bool AutoUse, IReadOnlyList<string> Values);

/// <summary>An item's use timing (Item.dat): cooldown, cooldown group, cast time (0: instant).</summary>
public sealed record ItemCooldown(int CooldownMs, string Group, int CastMs);

/// <summary>One effect row of an item skill: ESkillEffectType and its EffectValueList.</summary>
public sealed record SkillEffectRow(string Type, IReadOnlyList<string> Values)
{
    public int Value(int index) => index < Values.Count && int.TryParse(Values[index], out var v) ? v : 0;
}

/// <summary>A buff (SkillAbnormal): its group (one active buff per group) and effect rows (id, EAbnormalEffectType, values).</summary>
public sealed record AbnormalInfo(int Group, IReadOnlyList<(int Id, string Type, IReadOnlyList<string> Values)> Effects);

/// <summary>A creature board's slot rules (VehicleCreatureSlot): open levels, roll cost, lock costs, option group per slot.</summary>
public sealed record CreatureSlotRule(
    [property: JsonPropertyName("open")] int[] Open,
    [property: JsonPropertyName("crystals")] int[] Crystals,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("gold")] int Gold,
    [property: JsonPropertyName("lock")] int[] Lock,
    [property: JsonPropertyName("lock_gold")] int[] LockGold,
    [property: JsonPropertyName("groups")] string[] Groups);

/// <summary>A creature board stat option (VehicleCreatureOption): grade, EStat id and name, value range, weight.</summary>
public sealed record CreatureOption(int Grade, int StatId, string Stat, int Min, int Max, int Prob);

/// <summary>A ride (VehicleList): name, the pet item granting it, ECreatureType, souls to acquire, stat group and speeds.</summary>
public sealed record RideInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("item")] int? Item,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("need")] int NeedSoul,
    [property: JsonPropertyName("crystal")] int Crystal,
    [property: JsonPropertyName("stat")] string? StatGroup,
    [property: JsonPropertyName("run")] double? Run,
    [property: JsonPropertyName("sprint")] double? Sprint);

/// <summary>
/// Reward boxes (chests) and pets from tools/data/boxes.json (tools/box_data.py: BoxReward,
/// RewardPackageItem, VehicleList, Item.dat). Opening a box gives all of its items, the selected one
/// of its select items, and for each package that hits (Prob of 10000) one package item by ItemProb
/// (of 100000000) with a count between its min and max. CANDIDATE reading of the tables; the
/// official server's roll is not observed.
/// </summary>
public sealed class RewardBoxes
{
    sealed record Box(
        [property: JsonPropertyName("items")] int[][] Items,
        [property: JsonPropertyName("select")] int[][] Select,
        [property: JsonPropertyName("packages")] JsonElement[][] Packages,
        [property: JsonPropertyName("max")] int Max);

    sealed record BoxFile(
        [property: JsonPropertyName("handled_use_effects")] string[]? HandledUseEffects,
        [property: JsonPropertyName("uses")] Dictionary<string, JsonElement[]> Uses,
        [property: JsonPropertyName("cool")] Dictionary<string, JsonElement[]> Cool,
        [property: JsonPropertyName("skills")] Dictionary<string, string[][]> Skills,
        [property: JsonPropertyName("ride_levels")] Dictionary<string, int> RideLevels,
        [property: JsonPropertyName("ride_stats")] Dictionary<string, Dictionary<string, Dictionary<string, int>>> RideStats,
        [property: JsonPropertyName("abnormals")] Dictionary<string, AbnormalFile> Abnormals,
        [property: JsonPropertyName("passives")] Dictionary<string, int[]> Passives,
        [property: JsonPropertyName("ride_board_exp")] Dictionary<string, int> RideBoardExp,
        [property: JsonPropertyName("creature_levels")] Dictionary<string, Dictionary<string, int[]>> CreatureLevels,
        [property: JsonPropertyName("creature_slots")] Dictionary<string, CreatureSlotRule> CreatureSlots,
        [property: JsonPropertyName("creature_options")] Dictionary<string, JsonElement[][]> CreatureOptions,
        [property: JsonPropertyName("boxes")] Dictionary<string, Box> Boxes,
        [property: JsonPropertyName("packages")] Dictionary<string, int[][]> Packages,
        [property: JsonPropertyName("rides")] Dictionary<string, RideInfo> Rides);

    sealed record AbnormalFile([property: JsonPropertyName("group")] int Group, [property: JsonPropertyName("effects")] JsonElement[][] Effects);

    readonly BoxFile file;

    public AbnormalInfo? Abnormal(int abnormalId) =>
        file.Abnormals.TryGetValue(Key(abnormalId), out var a)
            ? new AbnormalInfo(a.Group, a.Effects.Select(e => (e[0].GetInt32(), e[1].GetString()!, (IReadOnlyList<string>)e.Skip(2).Select(v => v.GetString()!).ToList())).ToList())
            : null;

    /// <summary>The abnormal ids of a SkillAbnormal group.</summary>
    public IEnumerable<int> AbnormalsInGroup(int group) =>
        file.Abnormals.Where(a => a.Value.Group == group && int.TryParse(a.Key, out _)).Select(a => int.Parse(a.Key, System.Globalization.CultureInfo.InvariantCulture)).Order();

    /// <summary>CANDIDATE: debuffs for dispel scrolls — negative StatChange or CC/Dot effect types in SkillAbnormalEffect rows.</summary>
    public bool Debuff(int abnormalId)
    {
        if (Abnormal(abnormalId) is not { } info) return false;
        foreach (var (_, type, values) in info.Effects)
        {
            if (type is "StatChange" && values.Count >= 2 && int.TryParse(values[1], out var stat) && stat < 0) return true;
            if (type.Contains("Dot_", StringComparison.Ordinal) || type is "Stun" or "Sleep" or "Fear" or "Silence" or "Bind" or "Stone" or "Paralysis" or "Blind")
                return true;
        }
        return false;
    }

    RewardBoxes(BoxFile file) => this.file = file;

    static readonly Lazy<RewardBoxes> embedded = new(() =>
    {
        using var stream = typeof(RewardBoxes).Assembly.GetManifestResourceStream("boxes.json")
            ?? throw new InvalidOperationException("Missing embedded game data boxes.json");
        return new RewardBoxes(JsonSerializer.Deserialize<BoxFile>(stream) ?? throw new InvalidOperationException("Empty boxes.json"));
    });

    public static RewardBoxes Embedded => embedded.Value;

    static readonly HashSet<string> defaultHandled =
    [
        "RewardBox", "GetVehicle", "GetVehicleSoul", "GetVehicleSoulCrystal", "Fix_VehicleCreatureLevel", "Skill",
        "Telpo_Home", "GetKina", "GetNonBindingKina", "GetTitle", "GetSkin", "GetSkinSet", "GetSocialAction",
        "GetDaevanionPoint", "GetQuest", "GetWing", "AscensionGrade",
    ];

    /// <summary>Whether the server has an item-use handler for this Item.dat UseEffect (boxes.json handled_use_effects).</summary>
    public bool ImplementsUseEffect(string effect)
    {
        if (file.HandledUseEffects is { Length: > 0 } handled)
            return handled.Contains(effect);
        return defaultHandled.Contains(effect);
    }

    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ItemUse? Use(int itemId) =>
        file.Uses.TryGetValue(Key(itemId), out var row) && row.Length >= 2
            ? new ItemUse(row[0].GetString()!, row[1].GetInt32() == 1, row.Skip(2).Select(v => v.GetString()!).ToList()) : null;

    public RideInfo? Ride(int rideId) => file.Rides.GetValueOrDefault(Key(rideId));

    /// <summary>The permanent abnormals a skill's Abnormal_Passive effects give (class passives, e.g. weapon equip permission).</summary>
    public IReadOnlyList<int> Passives(int skillId) => file.Passives.GetValueOrDefault(Key(skillId)) ?? [];

    /// <summary>Board exp when a ride reaches a level (VehicleLevel GetCreatureExp; level 1 = acquired).</summary>
    public int RideBoardExp(int level) => file.RideBoardExp.GetValueOrDefault(Key(level));

    /// <summary>A board level's exp to the next level and grade odds (of 10000, Common..Mythic); null above the top level.</summary>
    public (int NeedExp, int[] Odds)? CreatureLevel(int type, int level) =>
        file.CreatureLevels.TryGetValue(Key(type), out var levels) && levels.TryGetValue(Key(level), out var row) && row.Length >= 7 ? (row[0], row[1..7]) : null;

    public int CreatureMaxLevel(int type) => file.CreatureLevels.TryGetValue(Key(type), out var levels) && levels.Count > 0 ? levels.Keys.Max(int.Parse) : 1;

    public CreatureSlotRule? CreatureSlots(int type) => file.CreatureSlots.GetValueOrDefault(Key(type));

    public IReadOnlyList<CreatureOption> CreatureOptions(string group) =>
        file.CreatureOptions.TryGetValue(group, out var rows)
            ? rows.Select(r => new CreatureOption(r[0].GetInt32(), r[1].GetInt32(), r[2].GetString()!, r[3].GetInt32(), r[4].GetInt32(), r[5].GetInt32())).ToList() : [];

    public ItemCooldown Cooldown(int itemId) =>
        file.Cool.TryGetValue(Key(itemId), out var c) && c.Length >= 3 ? new ItemCooldown(c[0].GetInt32(), c[1].GetString()!, c[2].GetInt32()) : new ItemCooldown(0, "None", 0);

    /// <summary>The effect rows of an item skill (potions, food, scrolls).</summary>
    public IReadOnlyList<SkillEffectRow> SkillEffects(int skillId) =>
        file.Skills.TryGetValue(Key(skillId), out var rows) ? rows.Select(r => new SkillEffectRow(r[0], r.Skip(1).ToList())).ToList() : [];

    /// <summary>Souls a ride needs to go from this level to the next (VehicleLevel NeedSoulCount; 0 = top level).</summary>
    public int RideLevelCost(int level) => file.RideLevels.GetValueOrDefault(Key(level));

    public int RideMaxLevel => file.RideLevels.Count == 0 ? 1 : file.RideLevels.Keys.Max(int.Parse);

    /// <summary>The stats a ride gives at a level (VehicleStat by its StatGroupName).</summary>
    public IReadOnlyDictionary<string, int> RideStats(int rideId, int level) =>
        Ride(rideId)?.StatGroup is { } group && file.RideStats.TryGetValue(group, out var levels) && levels.TryGetValue(Key(level), out var stats)
            ? stats : new Dictionary<string, int>();

    /// <summary>The most boxes of a group opened at once (BoxReward MultiUseMax), or 0 for an unknown group.</summary>
    public int MaxOpen(string group) => file.Boxes.TryGetValue(group, out var box) ? box.Max : 0;

    /// <summary>The rewards of opening <paramref name="count"/> boxes of a group, merged by item; null for an unknown group.</summary>
    public List<(int ItemId, long Count)>? Roll(string group, int selectIndex, int count, Random rng)
    {
        if (!file.Boxes.TryGetValue(group, out var box)) return null;
        var rewards = new Dictionary<int, long>();
        void Add(int item, long n) => rewards[item] = rewards.GetValueOrDefault(item) + n;
        for (var open = 0; open < count; open++)
        {
            foreach (var item in box.Items) Add(item[0], item[1]);
            if (box.Select.Length > 0) { var chosen = box.Select[Math.Clamp(selectIndex, 0, box.Select.Length - 1)]; Add(chosen[0], chosen[1]); }
            foreach (var package in box.Packages)
            {
                var name = package[0].GetString()!;
                int probability = package[1].GetInt32(), times = package[2].GetInt32();
                if (!file.Packages.TryGetValue(name, out var entries) || entries.Length == 0) continue;
                for (var t = 0; t < times; t++)
                {
                    if (rng.Next(10_000) >= probability) continue;
                    var total = entries.Sum(e => (long)e[1]);
                    var pick = (long)(rng.NextDouble() * total);
                    foreach (var e in entries)
                    {
                        if ((pick -= e[1]) >= 0) continue;
                        Add(e[0], rng.Next(e[2], Math.Max(e[2], e[3]) + 1));
                        break;
                    }
                }
            }
        }
        return rewards.Select(r => (r.Key, r.Value)).ToList();
    }
}
