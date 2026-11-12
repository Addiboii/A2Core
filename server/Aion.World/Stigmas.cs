using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Stigma rules from tools/data/stigmas.json (tools/stigmas.py, client build 26262663). OBSERVED, read from the tables:
/// SkillAcquireData Stigma rows (per class 13 skills, levels 1-20 at 1/2/4/8 shards, NeedCharacterLevel 22, learning needs
/// AscensionGrade_3), Exp.dat BonusStigmaPoint and StigmaSkillContextSlotMax by level, QuickSlotData's QuickSlot_Stigma_1..4
/// (wire slots 20-23: SlotId - 1, OBSERVED 2026-11-05), and each skill's Skill.dat cast data and SkillEffect Damage rows. CANDIDATE readings (see the builder):
/// a row's cost buys that level, BonusStigmaPoint is the running total of a level, slot N opens where the slot maximum reaches N.
/// </summary>
public sealed class StigmaTables
{
    sealed record ClassEntry(
        [property: JsonPropertyName("skills")] int[] Skills,
        // [skill level, CostStigmaPoint, NeedCharacterLevel, NeedAscensionGrade number]
        [property: JsonPropertyName("ladder")] int[][] Ladder,
        [property: JsonPropertyName("max_level")] int MaxLevel);

    sealed record File(
        [property: JsonPropertyName("classes")] Dictionary<string, ClassEntry> Classes,
        [property: JsonPropertyName("shards")] Dictionary<string, int> Shards,
        [property: JsonPropertyName("slot_max")] Dictionary<string, int> SlotMax,
        [property: JsonPropertyName("slots")] int[] Slots,
        [property: JsonPropertyName("variants")] Dictionary<string, int[][]>? Variants,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDefinition> Skills,
        [property: JsonPropertyName("projectile_speeds")] Dictionary<string, int>? ProjectileSpeeds = null);

    /// <summary>One level of a stigma: its shard cost, the character level and the ascension grade it needs.</summary>
    public readonly record struct Step(int Cost, int NeedLevel, int Ascension);

    readonly File file;
    readonly Dictionary<int, string> owner = [];
    readonly Dictionary<int, int> variantBase = [];

    StigmaTables(File file)
    {
        this.file = file;
        foreach (var (cls, entry) in file.Classes)
            foreach (var skill in entry.Skills) owner[skill] = cls;
        foreach (var (stigma, steps) in file.Variants ?? [])
            foreach (var step in steps) variantBase[step[1]] = int.Parse(stigma, CultureInfo.InvariantCulture);
    }

    static readonly Lazy<StigmaTables> embedded = new(() =>
    {
        using var stream = typeof(StigmaTables).Assembly.GetManifestResourceStream("stigmas.json")
            ?? throw new InvalidOperationException("Missing embedded game data stigmas.json");
        return new StigmaTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("stigmas.json"));
    });

    public static StigmaTables Embedded => embedded.Value;

    static string Key(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether a skill is a stigma of any class.</summary>
    public bool IsStigma(int skillId) => owner.ContainsKey(skillId);

    /// <summary>The class whose SkillAcquireData Stigma rows name the skill, or null.</summary>
    public string? ClassOf(int skillId) => owner.GetValueOrDefault(skillId);

    public IReadOnlyList<int> SkillsOf(string className) => file.Classes.GetValueOrDefault(className)?.Skills ?? [];

    public int MaxLevel(string className) => file.Classes.GetValueOrDefault(className)?.MaxLevel ?? 0;

    /// <summary>Level <paramref name="level"/> of a stigma for its own class; null for another class's skill or a level without a row.</summary>
    public Step? StepOf(string className, int skillId, int level)
    {
        if (ClassOf(skillId) != className || file.Classes.GetValueOrDefault(className) is not { } entry) return null;
        return entry.Ladder.FirstOrDefault(row => row.Length >= 4 && row[0] == level) is { } row ? new Step(row[1], row[2], row[3]) : null;
    }

    /// <summary>The shards spent to reach a stigma's level (levels without a row count 0: a saved stigma is never re-priced up).</summary>
    public int Spent(int skillId, int level) =>
        ClassOf(skillId) is { } cls ? Enumerable.Range(1, Math.Max(0, level)).Sum(l => StepOf(cls, skillId, l)?.Cost ?? 0) : 0;

    /// <summary>Exp.dat BonusStigmaPoint at a character level (the shards a character of that level has from levels).</summary>
    public int Shards(int characterLevel) => file.Shards.GetValueOrDefault(Key(characterLevel));

    /// <summary>Exp.dat StigmaSkillContextSlotMax at a character level, at most the number of stigma quick slots.</summary>
    public int OpenSlots(int characterLevel) => Math.Min(file.SlotMax.GetValueOrDefault(Key(characterLevel)), file.Slots.Length);

    /// <summary>The stigma quick slots (QuickSlotData QuickSlot_Stigma_1..N).</summary>
    public IReadOnlyList<int> Slots => file.Slots;

    public bool IsStigmaSlot(int quickSlot) => Array.IndexOf(file.Slots, quickSlot) >= 0;

    /// <summary>Whether a stigma quick slot is open at a character level (slot N open once the slot maximum reaches N, CANDIDATE).</summary>
    public bool SlotOpen(int quickSlot, int characterLevel) => Array.IndexOf(file.Slots, quickSlot) is >= 0 and var index && index < OpenSlots(characterLevel);

    /// <summary>
    /// The skill the client casts for a stigma at a level: from level 5 a variant (id + 10 at 5-9, + 20 at 10-14, + 30 at 15-19,
    /// + 40 at 20; OBSERVED rule, community recordings 2026-11-05/06), else 0 (the stigma itself).
    /// </summary>
    public int Variant(int stigma, int level) =>
        file.Variants?.GetValueOrDefault(Key(stigma)) is { } steps ? steps.Where(s => s[0] <= level).Select(s => s[1]).LastOrDefault() : 0;

    /// <summary>A stigma's variant steps: from which stigma level which skill is cast (its parts, SpecializedSkillParts.dat Slot_1..4).</summary>
    public IReadOnlyList<(int From, int Skill)> VariantSteps(int stigma) =>
        file.Variants?.GetValueOrDefault(Key(stigma)) is { } steps ? steps.Select(s => (s[0], s[1])).ToList() : [];

    /// <summary>The stigma a variant belongs to, or null for any other skill.</summary>
    public int? StigmaOfVariant(int skillId) => variantBase.TryGetValue(skillId, out var stigma) ? stigma : null;

    /// <summary>Cast data of a stigma skill (or a chain step it offers), as class-skills.json has it for the class skills.</summary>
    public SkillDefinition? Skill(int skillId) => file.Skills.GetValueOrDefault(Key(skillId));

    public int ProjectileSpeed(int projectileId) => file.ProjectileSpeeds?.GetValueOrDefault(Key(projectileId)) ?? 0;
}
