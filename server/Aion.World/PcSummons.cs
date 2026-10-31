using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Player summon skills from tools/data/pc-summons.json (tools/pc_summons.py): a class skill whose SkillEffect.dat Spawn row
/// summons an NpcData PC_Summon (the Spiritmaster's Fire/Water/Wind/Earth spirits), the summon's template and its attacks.
/// </summary>
public static class PcSummons
{
    public sealed record SummonSkill([property: JsonPropertyName("npc")] int Npc, [property: JsonPropertyName("group")] string Group);

    public sealed record SummonTemplate(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("walk_speed")] double WalkSpeed,
        [property: JsonPropertyName("run_speed")] double RunSpeed,
        [property: JsonPropertyName("radius")] int Radius,
        [property: JsonPropertyName("skills")] int[] Skills,
        [property: JsonPropertyName("sub_type")] string SubType);

    sealed record SummonFile(
        [property: JsonPropertyName("skills")] Dictionary<string, SummonSkill> Skills,
        [property: JsonPropertyName("templates")] Dictionary<string, SummonTemplate> Templates,
        [property: JsonPropertyName("npc_skills")] Dictionary<string, NpcSkill> NpcSkills);

    static readonly Lazy<SummonFile> file = new(() =>
    {
        using var stream = typeof(PcSummons).Assembly.GetManifestResourceStream("pc-summons.json")
            ?? throw new InvalidOperationException("Missing embedded game data pc-summons.json");
        return JsonSerializer.Deserialize<SummonFile>(stream) ?? throw new InvalidOperationException("Empty game data pc-summons.json");
    });

    static string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The summon a class skill places, with its template: null for other skills and for summons that neither move nor attack (traps, markers).</summary>
    public static (SummonSkill Skill, SummonTemplate Template)? For(int skillId) =>
        file.Value.Skills.GetValueOrDefault(Id(skillId)) is { } skill && file.Value.Templates.GetValueOrDefault(Id(skill.Npc)) is { Skills.Length: > 0 } template
            ? (skill, template) : null;

    /// <summary>A summon's damaging attack (Skill.dat + its SkillEffect damage row), or null.</summary>
    public static NpcSkill? Skill(int skillId) =>
        file.Value.NpcSkills.GetValueOrDefault(Id(skillId)) is { Effect: > 0 } skill ? skill : null;
}
