using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// ElementalSummon.dat rows (tools/data/elemental-summons.json): NpcList per elemental group drives
/// 0xe239 _elemental_group_type (EElementalSummonGroupType).
/// </summary>
public static class ElementalSummons
{
    sealed record Row(
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("deactivate_skill")] int DeactivateSkill,
        [property: JsonPropertyName("npcs")] int[] Npcs);

    sealed record File(
        [property: JsonPropertyName("enum")] Dictionary<string, int> Enum,
        [property: JsonPropertyName("by_npc")] Dictionary<string, int> ByNpc,
        [property: JsonPropertyName("rows")] Row[] Rows);

    static readonly Lazy<File> file = new(() =>
    {
        using var stream = typeof(ElementalSummons).Assembly.GetManifestResourceStream("elemental-summons.json")
            ?? throw new InvalidOperationException("Missing embedded game data elemental-summons.json");
        return JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty game data elemental-summons.json");
    });

    static string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>EElementalSummonGroupType for a spirit NpcData id; 0 when unknown.</summary>
    public static byte GroupTypeForNpc(int npcId) =>
        file.Value.ByNpc.GetValueOrDefault(Id(npcId)) is int value and > 0 and <= byte.MaxValue ? (byte)value : (byte)0;

    /// <summary>Named group -> enum byte (Skill.dat CanUseWhenElementalSummonSpawned strings).</summary>
    public static byte GroupType(string group) =>
        group.Length > 0 && file.Value.Enum.TryGetValue(group, out var value) && value is > 0 and <= byte.MaxValue ? (byte)value : (byte)0;

    public static byte GroupTypeForDismissSkill(int skillId)
    {
        foreach (var row in file.Value.Rows)
            if (row.DeactivateSkill == skillId) return GroupType(row.Group);
        return 0;
    }
}
