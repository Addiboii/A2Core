using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>ItemSetEffect.dat (tools/data/item-sets.json, tools/item_set_data.py).</summary>
public sealed class ItemSetTables
{
    sealed record Tier(int Pieces, IReadOnlyDictionary<string, int> If, IReadOnlyList<(string Stat, int Value)> Stats);

    sealed record SetDef(string Name, IReadOnlyList<Tier> Tiers);

    sealed record File(
        [property: JsonPropertyName("sets")] Dictionary<string, JsonElement> Sets,
        [property: JsonPropertyName("items")] Dictionary<string, string> Items,
        [property: JsonPropertyName("stat_map")] Dictionary<string, string>? StatMap);

    readonly Dictionary<string, SetDef> sets;
    readonly Dictionary<int, string> itemToSet;
    readonly Dictionary<string, string> statMap;

    ItemSetTables(File file)
    {
        statMap = file.StatMap ?? [];
        itemToSet = file.Items.ToDictionary(p => int.Parse(p.Key), p => p.Value);
        sets = file.Sets.ToDictionary(p => p.Key, p =>
        {
            var name = p.Value.GetProperty("name").GetString() ?? p.Key;
            var tiers = p.Value.GetProperty("tiers").EnumerateArray().Select(t =>
            {
                var pieces = t.GetProperty("pieces").GetInt32();
                var cond = t.TryGetProperty("if", out var c)
                    ? c.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32()) : (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
                var stats = t.GetProperty("stats").EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetInt32())).ToList();
                return new Tier(pieces, cond, stats);
            }).ToList();
            return new SetDef(name, tiers);
        });
    }

    static readonly Lazy<ItemSetTables> embedded = new(() =>
    {
        using var stream = typeof(ItemSetTables).Assembly.GetManifestResourceStream("item-sets.json")
            ?? throw new InvalidOperationException("Missing embedded game data item-sets.json");
        return new ItemSetTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty item-sets.json"));
    });

    public static ItemSetTables Embedded => embedded.Value;

    string MapStat(string stat) => statMap.GetValueOrDefault(stat, stat);

    /// <summary>Set bonuses that apply to <paramref name="stat"/> given equipped template ids and resource pools.</summary>
    public int Bonus(IReadOnlyList<int> equippedItemIds, int hp, int hpMax, int mp, int mpMax, string stat)
    {
        var mapped = MapStat(stat);
        var bySet = equippedItemIds.Where(itemToSet.ContainsKey).GroupBy(id => itemToSet[id]).ToDictionary(g => g.Key, g => g.Count());
        var total = 0;
        foreach (var (setId, count) in bySet)
        {
            if (!sets.TryGetValue(setId, out var set)) continue;
            foreach (var tier in set.Tiers.Where(t => count >= t.Pieces))
            {
                if (!TierApplies(tier, hp, hpMax, mp, mpMax)) continue;
                total += tier.Stats.Where(s => MapStat(s.Stat) == mapped).Sum(s => s.Value);
            }
        }
        return total;
    }

    static bool TierApplies(Tier tier, int hp, int hpMax, int mp, int mpMax)
    {
        if (tier.If.TryGetValue("hp_pct_min", out var hpMin) && hpMax > 0 && hp * 100 / hpMax < hpMin) return false;
        if (tier.If.TryGetValue("mp_pct_min", out var mpMin) && mpMax > 0 && mp * 100 / mpMax < mpMin) return false;
        return true;
    }
}
