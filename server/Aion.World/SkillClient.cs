using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// The player skills as the client data tables describe them (tools/skill_data.py -> tools/data/skills-client.json, OFFICIAL
/// client data; field meanings with their evidence rows in the tool's docstring): per skill its hit steps (target filter, effect
/// rows, projectile), the effect values per skill level (SkillEffectLv), the abnormals with their per-level values
/// (SkillAbnormalEffectLv), charge steps (SkillCharge). Loaded once, read-only, shared by every session.
/// </summary>
public sealed class SkillClient
{
    /// <summary>
    /// A SkillEffectFilter row: the area of a hit step. Shape Single / Self / Circle (Radius) / Arc (Radius, full Angle) /
    /// Rectangle (Length forward, Width); Forward / Side the offset of the shape (cm, EffectRangeValues[1] / [0]); Height the
    /// vertical reach; Max the most targets (TargetCountMax); NeedTarget: centred on the aimed target. CountCaster
    /// (bTargetCountIncludeCaster): a friendly area counts the caster among its targets (17120000's party heal: caster + 3).
    /// </summary>
    public sealed record Filter(int Id, string Shape, double Side, double Forward, double Radius, double Angle, double Length, double Width,
        double Height, int Min, int Max, bool NeedTarget, bool Enemy, bool Friend, string? Only, bool CountCaster = false)
    {
        public bool IsArea => Shape is "Circle" or "Arc" or "Rectangle";
    }

    public sealed record Condition(string Type, IReadOnlyList<string> Values, bool Expression);

    /// <summary>One SkillEffect row: its id (what 0x3804 carries), ESkillEffectType, level-1 values and its level group.</summary>
    public sealed record Effect(int Id, string Type, IReadOnlyList<string> Values, string? LevelGroup, IReadOnlyList<Condition> Conditions)
    {
        /// <summary>The ObjectType condition (PC / NPC): this row applies to monsters only when it is not "PC only".</summary>
        public bool AppliesToNpc => !Conditions.Any(c => c.Type == "ObjectType" && c.Values.Count > 0
            && (c.Expression ? c.Values[0] != "NPC" : c.Values[0] == "NPC"));
    }

    public sealed record Projectile(int Id, string Type, int Speed, int FixMinMs, int FixMaxMs);

    /// <summary>A hit moment of the cast (Skill.SkillEffectTimeDataList): its area, its effect rows, its projectile.</summary>
    public sealed record Step(int Index, Filter? Filter, IReadOnlyList<Effect> Effects, Projectile? Projectile);

    /// <summary>SkillCharge: the shortest hold, and per charge level its hold time (ms) and the skill fired.</summary>
    public sealed record Charge(int MinMs, int AddLimitMs, bool Movable, IReadOnlyList<(int Ms, int Skill)> Steps);

    public sealed record Skill(int Id, string Type, int LevelMax, string Target, string Disposition, string DamageType, string Element,
        double Range, int CastMs, int CooldownMs, int Mp, Filter? TargetFilter, IReadOnlyList<Step> Steps, Charge? Charge)
    {
        public IEnumerable<Effect> Effects => Steps.SelectMany(s => s.Effects);
        public bool HasDamage => Effects.Any(e => e.Type == "Damage");
        /// <summary>The widest area of its steps (the most targets one step may hit), null for single-target skills.</summary>
        public Filter? Area => Steps.Select(s => s.Filter).Where(f => f is { IsArea: true, Enemy: true }).MaxBy(f => f!.Max);
    }

    public sealed record AbnormalEffect(int Id, string Type, IReadOnlyList<string> Values, string? LevelGroup);

    public sealed record Abnormal(int Id, string Type, string EffectType, int Group, int OverlapCount, string OverlapTime, string Replace,
        int LevelMax, IReadOnlyList<AbnormalEffect> Effects);

    readonly Dictionary<int, Skill> skills = [];
    readonly Dictionary<int, Abnormal> abnormals = [];
    readonly Dictionary<string, (int[] Idx, Dictionary<int, string[]> Levels)> effectLevels = [];
    readonly Dictionary<string, (int[] Idx, Dictionary<int, string[]> Levels)> abnormalLevels = [];

    static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        _ => "",
    };

    static string[] Texts(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(Text).ToArray() : [];
    static int I(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    static double D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static int Key(string name) => int.Parse(name, CultureInfo.InvariantCulture);

    static Dictionary<string, (int[], Dictionary<int, string[]>)> LevelTable(JsonElement e)
    {
        var all = new Dictionary<string, (int[], Dictionary<int, string[]>)>();
        foreach (var g in e.EnumerateObject())
        {
            var idx = g.Value.GetProperty("idx").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            all[g.Name] = (idx, g.Value.GetProperty("lv").EnumerateObject().ToDictionary(l => Key(l.Name), l => Texts(l.Value)));
        }
        return all;
    }

    public SkillClient(JsonElement root)
    {
        var filters = new Dictionary<int, Filter>();
        foreach (var f in root.GetProperty("filters").EnumerateObject())
        {
            var v = f.Value;
            var off = v.TryGetProperty("off", out var o) ? o.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [0, 0, 0, 0];
            filters[Key(f.Name)] = new Filter(Key(f.Name), S(v, "shape"), off[0], off[1], D(v, "r"), D(v, "angle"), D(v, "len"), D(v, "w"), D(v, "h"),
                I(v, "min"), I(v, "max"), B(v, "need_target"), B(v, "enemy"), B(v, "friend"), v.TryGetProperty("only", out var only) ? only.GetString() : null,
                B(v, "count_caster"));
        }
        var projectiles = root.GetProperty("projectiles").EnumerateObject().ToDictionary(p => Key(p.Name), p =>
        {
            var fix = p.Value.GetProperty("fix_ms");
            return new Projectile(Key(p.Name), S(p.Value, "type"), I(p.Value, "speed"), fix[0].GetInt32(), fix[1].GetInt32());
        });
        var groups = root.GetProperty("groups").EnumerateObject().ToDictionary(g => Key(g.Name), g => (IReadOnlyList<Effect>)g.Value.EnumerateArray()
            .Select(e => new Effect(I(e, "id"), S(e, "type"), Texts(e.GetProperty("values")), e.TryGetProperty("lv", out var lv) ? lv.GetString() : null,
                e.TryGetProperty("cond", out var c) ? c.EnumerateArray().Select(x => new Condition(Text(x[0]), Texts(x[1]), x[2].ValueKind == JsonValueKind.True)).ToArray() : []))
            .ToArray());
        foreach (var p in root.GetProperty("skills").EnumerateObject())
        {
            var s = p.Value;
            var steps = new List<Step>();
            foreach (var st in s.GetProperty("steps").EnumerateArray())
                steps.Add(new Step(steps.Count, filters.GetValueOrDefault(I(st, "filter")), groups.GetValueOrDefault(I(st, "group")) ?? [],
                    projectiles.GetValueOrDefault(I(st, "projectile"))));
            Charge? charge = null;
            if (s.TryGetProperty("charge", out var ch))
                charge = new Charge(I(ch, "min_ms"), I(ch, "add_limit_ms"), B(ch, "movable"),
                    ch.GetProperty("steps").EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToArray());
            var mp = s.TryGetProperty("cost", out var cost) ? I(cost, "Mp") : 0;
            skills[Key(p.Name)] = new Skill(Key(p.Name), S(s, "type"), Math.Max(1, I(s, "lv_max")), S(s, "target"), S(s, "disposition"), S(s, "damage_type"),
                S(s, "element"), D(s, "range"), I(s, "cast_ms"), I(s, "cooldown_ms"), mp, filters.GetValueOrDefault(I(s, "target_filter")), steps, charge);
        }
        foreach (var p in root.GetProperty("abnormals").EnumerateObject())
        {
            var a = p.Value;
            abnormals[Key(p.Name)] = new Abnormal(Key(p.Name), S(a, "type"), S(a, "effect_type"), I(a, "group"), I(a, "overlap_count"), S(a, "overlap_time"),
                S(a, "replace"), Math.Max(1, I(a, "lv_max")), a.GetProperty("effects").EnumerateArray()
                    .Select(e => new AbnormalEffect(I(e, "id"), S(e, "type"), Texts(e.GetProperty("values")), e.TryGetProperty("lv", out var lv) ? lv.GetString() : null)).ToArray());
        }
        effectLevels = LevelTable(root.GetProperty("effect_levels"));
        abnormalLevels = LevelTable(root.GetProperty("abnormal_levels"));
    }

    static readonly Lazy<SkillClient> embedded = new(() =>
    {
        using var stream = typeof(SkillClient).Assembly.GetManifestResourceStream("skills-client.json")
            ?? throw new InvalidOperationException("Missing embedded game data skills-client.json");
        using var doc = JsonDocument.Parse(stream);
        return new SkillClient(doc.RootElement);
    });

    public static SkillClient Embedded => embedded.Value;
    public int Count => skills.Count;
    public Skill? Of(int skill) => skills.GetValueOrDefault(skill);
    public IEnumerable<Skill> All => skills.Values;
    public Abnormal? AbnormalOf(int abnormal) => abnormals.GetValueOrDefault(abnormal);

    static IReadOnlyList<string> At(IReadOnlyList<string> baseValues, string? group, int level,
        Dictionary<string, (int[] Idx, Dictionary<int, string[]> Levels)> table)
    {
        if (level <= 1 || group is null || !table.TryGetValue(group, out var g) || g.Levels.Count == 0) return baseValues;
        // A level past the table's last row keeps the last row (levels are learned up to the skill's SkillLvMax).
        var row = g.Levels.TryGetValue(level, out var exact) ? exact : g.Levels[g.Levels.Keys.Where(k => k <= level).DefaultIfEmpty(g.Levels.Keys.Min()).Max()];
        var values = baseValues.ToArray();
        var width = Math.Max(values.Length, g.Idx.Length == 0 ? 0 : g.Idx.Max() + 1);
        if (width > values.Length) Array.Resize(ref values, width);
        for (var i = 0; i < g.Idx.Length && i < row.Length; i++) values[g.Idx[i]] = row[i];
        return values;
    }

    /// <summary>An effect row's values at a skill level (SkillEffectLv replaces the level-1 values; OFFICIAL client data).</summary>
    public IReadOnlyList<string> ValuesAt(Effect effect, int level) => At(effect.Values, effect.LevelGroup, level, effectLevels);

    /// <summary>An abnormal effect row's values at an abnormal level (SkillAbnormalEffectLv).</summary>
    public IReadOnlyList<string> ValuesAt(AbnormalEffect effect, int level) => At(effect.Values, effect.LevelGroup, level, abnormalLevels);

    public static int Int(IReadOnlyList<string> values, int i) =>
        i < values.Count && double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : 0;

    /// <summary>
    /// A Damage row at a skill level: flat [0,1] and attack ratio [2,3] (1/10000), PvP flat [21,22] and ratio [23,24]
    /// (15090000 level 40: 2038 + 9250/10000 of attack; PvP 3632 + 7152/10000). [4] Split: the row's damage is divided by it
    /// (OFFICIAL, recordings 20261107-012837/013918: the level-1 Sorcerer's 15040000 rows of flat 114 with [4] 5 hit 23-25 each,
    /// the Bittercold Wind pulses 15280002 flat 238 with [4] 7 hit 35-44; rows with [4] 1 hit their whole flat: 15090000 79 -> 76).
    /// </summary>
    public (int FlatMin, int FlatMax, int RatioMin, int RatioMax, int PvpFlatMin, int PvpFlatMax, int PvpRatioMin, int PvpRatioMax, int Split) DamageAt(Effect effect, int level)
    {
        var v = ValuesAt(effect, level);
        return (Int(v, 0), Int(v, 1), Int(v, 2), Int(v, 3), Int(v, 21), Int(v, 22), Int(v, 23), Int(v, 24), Math.Max(1, Int(v, 4)));
    }

    /// <summary>
    /// A damage-over-time (Dot_NormalCalc) row at an abnormal level: the tick interval [0] ms and per tick flat [3,4] plus attack
    /// ratio [5,6] (1/10000); PvP flat [11,12], ratio [13,14] (140500002: 1000 ms, 43 + 4500/10000). (Hot rows have another
    /// layout: [0] interval, [1,2] flat, [3] ratio.)
    /// </summary>
    public (int IntervalMs, int FlatMin, int FlatMax, int RatioMin, int RatioMax)? TickAt(AbnormalEffect effect, int level)
    {
        if (effect.Type != "Dot_NormalCalc") return null;
        var v = ValuesAt(effect, level);
        return (Int(v, 0), Int(v, 3), Int(v, 4), Int(v, 5), Int(v, 6));
    }

    /// <summary>
    /// What a hit knows when the effect rows' conditions (SkillEffect.SkillEffectConditionList) are weighed: is this the aimed
    /// target, how many targets the cast gathered, a back attack, the target's / caster's abnormals (by id, by group, by effect
    /// type), the target's HP share (1/10000), whether the target is a player (ObjectType PC: heals and buffs on players).
    /// </summary>
    public sealed record RowContext(bool MainTarget = true, int Gathered = 1, bool Back = false, Func<int, bool>? TargetHas = null,
        Func<int, bool>? TargetHasGroup = null, Func<string, bool>? TargetHasEffectType = null, Func<int, bool>? CasterHas = null,
        Func<int, bool>? CasterHasGroup = null, int TargetHpRatio = 10_000, Random? Rng = null, bool TargetPc = false)
    {
        public static readonly RowContext Default = new();
    }

    /// <summary>
    /// An effect row applies when each of its conditions is met as its expression says (OFFICIAL client rows; e.g. 12100000's
    /// two Damage rows IsMainTarget true / false, 13130000's rows by the stacks of 130000011 on the target, 11010040's rows by
    /// GatherCount 1-4: the damage of the row for the number of targets hit, 13060000's back attack row). Abnormal-stack counts are
    /// 1 when present (our abnormals do not stack, CANDIDATE); a condition the server does not model counts as not met.
    /// </summary>
    public static bool RowApplies(Step step, Effect effect, RowContext c)
    {
        foreach (var cond in effect.Conditions)
        {
            var id = cond.Values.Count > 0 && int.TryParse(cond.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v0) ? v0 : 0;
            var n = cond.Values.Count > 1 && int.TryParse(cond.Values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v1) ? v1 : 0;
            bool Stacks(bool has) => n == 0 ? has : has && n == 1;
            var met = cond.Type switch
            {
                "ObjectType" => cond.Values.Count > 0 && cond.Values[0] == (c.TargetPc ? "PC" : "NPC"),
                "IsMainTarget" => c.MainTarget,
                "GatherCount" => c.Gathered == id || c.Gathered > id && id == step.Effects
                    .SelectMany(e => e.Conditions).Where(x => x.Type == "GatherCount")
                    .Select(x => int.TryParse(x.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0).DefaultIfEmpty(0).Max(),
                "CalcBackAttack" => c.Back,
                "CalcFrontAttack" => !c.Back,
                "Target_HasAbnormalId" or "Target_HasAbnormalId_ByCaster" => Stacks(c.TargetHas?.Invoke(id) == true),
                "Target_HasAbnormalGroupId" or "Target_HasAbnormalGroupId_ByCaster" => Stacks(c.TargetHasGroup?.Invoke(id) == true),
                "Target_HasAbnormalEffectType" => c.TargetHasEffectType?.Invoke(cond.Values.Count > 0 ? cond.Values[0] : "") == true,
                "Caster_HasAbnormalId" => Stacks(c.CasterHas?.Invoke(id) == true),
                "Caster_HasAbnormalGroupId" => Stacks(c.CasterHasGroup?.Invoke(id) == true),
                "Target_MaxHPRatio_Lower" => c.TargetHpRatio <= id,
                "Target_MaxHPRatio_Upper" => c.TargetHpRatio >= id,
                "Probability" => c.Rng is { } r && r.Next(10_000) < id,
                _ => false,
            };
            if (met != cond.Expression) return false;
        }
        return true;
    }

    /// <summary>The abnormal level a skill effect applies at a skill level (the Abnormal* row's [3], grown by SkillEffectLv).</summary>
    public int AbnormalLevelAt(Effect effect, int level) => Math.Max(1, Int(ValuesAt(effect, level), 3));
}
