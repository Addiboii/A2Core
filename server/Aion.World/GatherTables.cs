using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Essence Extraction and flight values from the client's own tables (tools/gather_tables.py -> tools/data/gather-tables.json):
/// GatherMastery (100 levels: exp, Beginner/Intermediate grade, skill and specialized points), the gather skill tree
/// (GatherSkill + GatherSkillEffect: per level the point cost, grade, prior skill, chance and effect), the material grade
/// groups (GatherSourceMaterial GroupKey 1-4 = c/r/l/u) by the node's first material, and the GlobalSetting gather / flight values.
/// </summary>
public sealed class GatherTables
{
    /// <summary>One level of a gather skill (GatherSkillEffect). Prob is /10000; Grade 0 Beginner, 1 Intermediate.</summary>
    public sealed record Effect(int Id, int Skill, int Level, int SkillPoints, int SpecializedPoints, int Grade, int Prob, string Type,
        IReadOnlyList<int> Values, IReadOnlyList<(int Skill, int Level)> Prior);
    /// <summary>A gather skill (GatherSkill): EGatherSkillCategory (Common, Metal, Jewelry, Tree, Herb, Food, Od) and display order.</summary>
    public sealed record Skill(int Id, string Category, string Condition, int Order);
    /// <summary>A grade group of a material (GatherSourceMaterial): GroupKey 1 c, 2 r, 3 l, 4 u.</summary>
    public sealed record Grade(int Key, int Item, int Count, IReadOnlyList<(int Item, int Count)> Add, int Gauge);
    public sealed record Material(string Category, IReadOnlyList<Grade> Grades);
    public sealed record MasteryLevel(long NeedExp, int Grade, int SkillPoints, int SpecializedPoints);

    readonly Dictionary<int, Skill> skills;
    readonly Dictionary<(int Skill, int Level), Effect> effects;
    readonly Dictionary<int, Material> materials;
    readonly Dictionary<string, double> settings;
    public IReadOnlyList<MasteryLevel> Mastery { get; }

    GatherTables(JsonElement root)
    {
        Mastery = root.GetProperty("mastery").EnumerateArray()
            .Select(m => new MasteryLevel(m[0].GetInt64(), m[1].GetInt32(), m[2].GetInt32(), m[3].GetInt32())).ToList();
        skills = root.GetProperty("skills").EnumerateObject().Select(p => new Skill(int.Parse(p.Name, CultureInfo.InvariantCulture),
            p.Value.GetProperty("category").GetString()!, p.Value.GetProperty("condition").GetString()!, p.Value.GetProperty("order").GetInt32()))
            .ToDictionary(s => s.Id);
        effects = root.GetProperty("effects").EnumerateArray().Select(e => new Effect(e.GetProperty("id").GetInt32(), e.GetProperty("skill").GetInt32(),
                e.GetProperty("level").GetInt32(), e.GetProperty("sp").GetInt32(), e.GetProperty("spec").GetInt32(), e.GetProperty("grade").GetInt32(),
                e.GetProperty("prob").GetInt32(), e.GetProperty("type").GetString()!, e.GetProperty("values").EnumerateArray().Select(v => v.GetInt32()).ToList(),
                e.GetProperty("prior").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList()))
            .ToDictionary(e => (e.Skill, e.Level));
        materials = root.GetProperty("materials").EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture),
            p => new Material(p.Value.GetProperty("category").GetString()!, p.Value.GetProperty("grades").EnumerateArray()
                .Select(g => new Grade(g[0].GetInt32(), g[1].GetInt32(), g[2].GetInt32(),
                    g[3].EnumerateArray().Select(a => (a[0].GetInt32(), a[1].GetInt32())).ToList(), g[4].GetInt32())).ToList()));
        settings = root.GetProperty("settings").EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
            .ToDictionary(p => p.Name, p => p.Value.GetDouble());
    }

    static readonly Lazy<GatherTables> embedded = new(() =>
    {
        using var stream = typeof(GatherTables).Assembly.GetManifestResourceStream("gather-tables.json")
            ?? throw new InvalidOperationException("Missing embedded game data gather-tables.json");
        using var doc = JsonDocument.Parse(stream);
        return new GatherTables(doc.RootElement);
    });

    public static GatherTables Embedded => embedded.Value;

    public IEnumerable<Skill> Skills => skills.Values.OrderBy(s => s.Id);
    public Skill? SkillOf(int id) => skills.GetValueOrDefault(id);
    public Effect? EffectOf(int skill, int level) => effects.GetValueOrDefault((skill, level));
    public int MaxLevel(int skill) => effects.Keys.Where(k => k.Skill == skill).Select(k => k.Level).DefaultIfEmpty(0).Max();
    /// <summary>The skill of a category in display order (1 minimap, 2 MultiplyReward, 3 DiscoveryAddProb; Common: 1 SuccessGaugeAdd,
    /// 2 FailGaugeMinus, 3 AllCriticalRateUp).</summary>
    public Skill? SkillIn(string category, int order) => skills.Values.FirstOrDefault(s => s.Category == category && s.Order == order);
    /// <summary>The grade groups of a node, by its first (GroupKey 1) material (the material tools/world_objects.py puts in the node).</summary>
    public Material? MaterialOf(int firstItem) => materials.GetValueOrDefault(firstItem);
    public double Setting(string name, double fallback) => settings.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>The exp a mastery level needs to reach the next (0 at 50, the Beginner cap that a grade quest lifts, and at 100).</summary>
    public long NeedExp(int level) => level >= 1 && level <= Mastery.Count ? Mastery[level - 1].NeedExp : 0;
    /// <summary>EGatherMasteryGrade of a mastery level: 0 Beginner (1-50), 1 Intermediate (51-100).</summary>
    public int GradeAt(int level) => Mastery.Count == 0 ? 0 : Mastery[Math.Clamp(level, 1, Mastery.Count) - 1].Grade;
    /// <summary>The skill and specialized points earned up to a mastery level (95 and 14 at 100). OFFICIAL: a row's points come on
    /// REACHING its level (Level is 1-based: row 1 gives nothing, row 100 gives 1 + 2; the official mastery-100 character had spent
    /// exactly the sum of rows 1..100 and had 0 / 0 left), so the first specialized point comes at level 10.</summary>
    /// <param name="specializedFrom">0 = official. Else the level at which the first specialized point (the level-10 one) is given
    /// instead (server-rules.xml gather specialized_from_level, OURS); every later point and the total stay official.</param>
    public (int Skill, int Specialized) PointsAt(int level, int specializedFrom = 0)
    {
        int sp = 0, spec = 0;
        for (var i = 0; i < Math.Min(level, Mastery.Count); i++) { sp += Mastery[i].SkillPoints; spec += Mastery[i].SpecializedPoints; }
        if (specializedFrom > 0 && FirstSpecialized() is { Level: > 0 } first && specializedFrom != first.Level)
            spec += (level >= specializedFrom ? first.Points : 0) - (level >= first.Level ? first.Points : 0);
        return (sp, spec);
    }

    /// <summary>The first mastery level that gives specialized points and how many (OFFICIAL: level 10, 1 point).</summary>
    public (int Level, int Points) FirstSpecialized()
    {
        for (var i = 0; i < Mastery.Count; i++)
            if (Mastery[i].SpecializedPoints > 0) return (i + 1, Mastery[i].SpecializedPoints);
        return (0, 0);
    }
    /// <summary>The points a skill at a level has cost (the sum of its levels' costs).</summary>
    public (int Skill, int Specialized) CostTo(int skill, int level)
    {
        int sp = 0, spec = 0;
        for (var l = 1; l <= level; l++)
            if (EffectOf(skill, l) is { } e) { sp += e.SkillPoints; spec += e.SpecializedPoints; }
        return (sp, spec);
    }
}
