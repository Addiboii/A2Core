using System.Text.Json;

namespace Aion.World;

/// <summary>Roll random substats for a new equipment instance (Item.dat SubStats tables in items.json).</summary>
public static class ItemRoller
{
    /// <summary>Stat name → rolled value; empty when the template has no substat table.</summary>
    public static IReadOnlyDictionary<string, int> Roll(GameData data, int templateId, Random rng)
    {
        var table = data.SubstatRolls(templateId);
        if (table.Count == 0) return new Dictionary<string, int>();
        var result = new Dictionary<string, int>();
        var variable = new List<(string Stat, int Min, int Max, int Weight)>();
        foreach (var row in table)
        {
            if (row.Min == row.Max)
                result[row.Stat] = row.Min;
            else
                variable.Add(row);
        }
        if (variable.Count > 0)
        {
            var picked = WeightedPick(variable, rng);
            if (picked is { } pick)
            {
                var value = pick.Min == pick.Max ? pick.Min : rng.Next(Math.Min(pick.Min, pick.Max), Math.Max(pick.Min, pick.Max) + 1);
                result[pick.Stat] = value;
            }
        }
        return result;
    }

    public static JsonElement RollJson(GameData data, int templateId, Random rng)
    {
        var rolled = Roll(data, templateId, rng);
        return rolled.Count == 0 ? JsonSerializer.SerializeToElement(new Dictionary<string, int>())
            : JsonSerializer.SerializeToElement(rolled);
    }

    static (string Stat, int Min, int Max)? WeightedPick(IReadOnlyList<(string Stat, int Min, int Max, int Weight)> table, Random rng)
    {
        var total = table.Sum(r => Math.Max(0, r.Weight));
        if (total <= 0) return null;
        var roll = rng.Next(total);
        foreach (var row in table)
        {
            if (row.Weight <= 0) continue;
            if (roll < row.Weight) return (row.Stat, row.Min, row.Max);
            roll -= row.Weight;
        }
        var last = table.LastOrDefault(r => r.Weight > 0);
        return last.Weight <= 0 ? null : (last.Stat, last.Min, last.Max);
    }
}
