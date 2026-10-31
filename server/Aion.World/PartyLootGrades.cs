namespace Aion.World;

/// <summary>EItemGrade (usmap) inferred from decompose package names when Item.dat grade is not embedded in items.json.</summary>
public static class PartyLootGrades
{
    public static int Grade(int templateId)
    {
        if (DecomposeTables.Embedded.Of(templateId) is not { Groups: { Length: > 0 } groups }) return 1;
        var name = groups[0];
        if (name.Contains("Mythic", StringComparison.OrdinalIgnoreCase)) return 6;
        if (name.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Contains("Unique", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Contains("Legend", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains("Rare", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }
}
