using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// What gear does beyond its own stats, from the client tables (tools/gear_effects_data.py -> tools/data/gear-effects.json,
/// OFFICIAL client data): the wings' worn and owned effects (Wing.dat, WingEnchantEffect), the godstones' on-hit skills
/// (the ItemPassive abnormal's TriggerSkillByAttackAll row and the system skill it casts), the equipment sets (ItemSet,
/// ItemSetEffect) and the sub-stat skills (ItemEquipSubstatSkill). Loaded once, read-only.
/// </summary>
public sealed class GearEffects
{
    /// <summary>A wing: its owned effect group (WingEnchantEffect), its enchant group ("None": not enchantable), the worn stats.</summary>
    public sealed record Wing(int Id, string EffectGroup, string EnchantGroup, IReadOnlyList<(string Stat, int Value)> Worn);

    /// <summary>A TriggerSkillByAttackAll row: the skill cast on a hit, its chance (1/10000), its cooldown (ms), the hit types.</summary>
    public sealed record Trigger(int Abnormal, int Skill, int Chance, int CooldownMs, IReadOnlyList<string> HitTypes);

    /// <summary>One SkillEffect row of a proc skill: id (what 0x3804 carries), type, values, its Probability condition (1/10000).</summary>
    public sealed record ProcEffect(int Id, string Type, IReadOnlyList<string> Values, int Chance)
    {
        public int Int(int i) => SkillClient.Int(Values, i);
    }

    public sealed record ProcSkill(int Id, string DamageType, IReadOnlyList<ProcEffect> Effects);

    public sealed record AbnormalEffect(int Id, string Type, IReadOnlyList<string> Values)
    {
        public int Int(int i) => SkillClient.Int(Values, i);
    }

    public sealed record Abnormal(int Id, string Type, int Group, IReadOnlyList<AbnormalEffect> Effects)
    {
        /// <summary>The StatChange rows as stats (values[0] the EStat name, values[1] the amount).</summary>
        public IEnumerable<(string Stat, int Value)> Stats =>
            Effects.Where(e => e.Type == "StatChange" && e.Values.Count > 1).Select(e => (e.Values[0], e.Int(1)));
    }

    /// <summary>An equipment set: its pieces (item ids) and per number of pieces worn its stats and abnormal.</summary>
    public sealed record ItemSet(string Name, string Type, IReadOnlySet<int> Items, IReadOnlyList<(int Pieces, IReadOnlyList<(string Stat, int Value)> Stats, int Abnormal)> Steps);

    /// <summary>An ItemEquipSubstatSkill row: the class, the pick weight, the skill and its levels (base, per enchant, max).</summary>
    public sealed record SubSkill(string Class, int Weight, int Skill, int Base, int PerEnchant, int Max);

    /// <summary>An item's sub-stat skill roll: the group, the chance of each pick (1/10000) and the most picks.</summary>
    public sealed record ItemSubSkills(string Group, int Chance, int CountMax);

    readonly Dictionary<int, Wing> wings = [];
    readonly Dictionary<string, List<IReadOnlyList<(string, int)>>> wingEffects = [];
    readonly Dictionary<int, Trigger> triggers = [];
    readonly Dictionary<int, ProcSkill> procSkills = [];
    readonly Dictionary<int, Abnormal> abnormals = [];
    readonly List<ItemSet> sets = [];
    readonly Dictionary<int, List<ItemSet>> setsByItem = [];
    readonly Dictionary<string, List<SubSkill>> subSkills = [];
    readonly Dictionary<int, ItemSubSkills> itemSubSkills = [];

    static readonly Lazy<GearEffects> embedded = new(() =>
    {
        using var stream = typeof(GearEffects).Assembly.GetManifestResourceStream("gear-effects.json")
            ?? throw new InvalidOperationException("Missing embedded game data gear-effects.json");
        using var doc = JsonDocument.Parse(stream);
        return new GearEffects(doc.RootElement);
    });

    public static GearEffects Embedded => embedded.Value;

    static int Id(string name) => int.Parse(name, CultureInfo.InvariantCulture);
    static List<(string, int)> Stats(JsonElement list) => list.EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetInt32())).ToList();
    static List<string> Strings(JsonElement list) => list.EnumerateArray().Select(v => v.GetString() ?? "").ToList();

    GearEffects(JsonElement root)
    {
        foreach (var w in root.GetProperty("wings").EnumerateObject())
            wings[Id(w.Name)] = new Wing(Id(w.Name), w.Value[0].GetString()!, w.Value[1].GetString()!, Stats(w.Value[2]));
        foreach (var g in root.GetProperty("wing_effects").EnumerateObject())
            wingEffects[g.Name] = g.Value.EnumerateArray().Select(l => (IReadOnlyList<(string, int)>)Stats(l)).ToList();
        foreach (var t in root.GetProperty("triggers").EnumerateObject())
            triggers[Id(t.Name)] = new Trigger(Id(t.Name), t.Value[0].GetInt32(), t.Value[1].GetInt32(), t.Value[2].GetInt32(),
                t.Value[3].GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        foreach (var s in root.GetProperty("proc_skills").EnumerateObject())
            procSkills[Id(s.Name)] = new ProcSkill(Id(s.Name), s.Value[0].GetString()!,
                s.Value[1].EnumerateArray().Select(e => new ProcEffect(e[0].GetInt32(), e[1].GetString()!, Strings(e[2]), e[3].GetInt32())).ToList());
        foreach (var a in root.GetProperty("abnormals").EnumerateObject())
            abnormals[Id(a.Name)] = new Abnormal(Id(a.Name), a.Value[0].GetString()!, a.Value[1].GetInt32(),
                a.Value[2].EnumerateArray().Select(e => new AbnormalEffect(e[0].GetInt32(), e[1].GetString()!, Strings(e[2]))).ToList());
        foreach (var s in root.GetProperty("sets").EnumerateObject())
        {
            var set = new ItemSet(s.Name, s.Value[0].GetString()!, s.Value[1].EnumerateArray().Select(i => i.GetInt32()).ToHashSet(),
                s.Value[2].EnumerateArray().Select(e => (e[0].GetInt32(), (IReadOnlyList<(string, int)>)Stats(e[1]), e[2].GetInt32())).ToList());
            sets.Add(set);
            foreach (var item in set.Items) (setsByItem.TryGetValue(item, out var list) ? list : setsByItem[item] = []).Add(set);
        }
        foreach (var g in root.GetProperty("sub_skills").EnumerateObject())
            subSkills[g.Name] = g.Value.EnumerateArray().Select(r => new SubSkill(r[0].GetString()!, r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32(),
                r[4].GetInt32(), r[5].GetInt32())).ToList();
        foreach (var i in root.GetProperty("item_sub_skills").EnumerateObject())
            itemSubSkills[Id(i.Name)] = new ItemSubSkills(i.Value[0].GetString()!, i.Value[1].GetInt32(), i.Value[2].GetInt32());
    }

    public int WingCount => wings.Count;
    public IEnumerable<int> WingIds => wings.Keys;
    public Wing? WingOf(int id) => wings.GetValueOrDefault(id);

    /// <summary>The owned effect of a wing at an enchant level (WingEnchantEffect of its EffectGroup; the last level past the top).</summary>
    public IReadOnlyList<(string Stat, int Value)> WingOwned(int id, int level = 0) =>
        WingOf(id) is { } w && wingEffects.TryGetValue(w.EffectGroup, out var levels) && levels.Count > 0
            ? levels[Math.Clamp(level, 0, levels.Count - 1)] : [];

    public Trigger? TriggerOf(int abnormal) => triggers.GetValueOrDefault(abnormal);
    public ProcSkill? ProcSkillOf(int skill) => procSkills.GetValueOrDefault(skill);
    public Abnormal? AbnormalOf(int id) => abnormals.GetValueOrDefault(id);
    public IReadOnlyList<ItemSet> Sets => sets;
    public IReadOnlyList<ItemSet> SetsOf(int itemId) => setsByItem.GetValueOrDefault(itemId) ?? [];
    public IReadOnlyList<SubSkill> SubSkillGroup(string group) => subSkills.GetValueOrDefault(group) ?? [];
    public ItemSubSkills? SubSkillsOf(int itemId) => itemSubSkills.GetValueOrDefault(itemId);
}
