using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Faction-correct box and chest rewards: no box gives an item of the other faction. From tools/data/box-race.json
/// (tools/box_race_data.py, the client's BoxReward / BoxClassReward / RewardPackage race and class rows):
/// <list type="number">
/// <item>The race row of a box group (BoxReward Race Light / Dark; 67 groups: field boss chests, gathering skin chests, the
/// Founders chests, the class equipment chests) and of a reward package (RewardPackage, the treasure chests
/// reward_manual_TreasureBox_*): the opener's row replaces the one boxes.json / loot.json kept (boxes.json had the Dark
/// row for both races, loot.json both races' packages in one table).</item>
/// <item>The class rows of a box group (BoxClassReward Class + Race): the opener's class and race row is added.</item>
/// <item>A safety net for every candidate (fixed item, selectable item, package entry, chest / cube table entry): an item
/// whose Item.dat ItemRace is the other faction becomes its twin of the opener's faction (same internal name with the race
/// token swapped, _L_ / _D_), or, without a twin, leaves the candidate list before the roll, so the remaining weights of a
/// package are renormalised (their sum is the new total) and an independent chance simply never hits.</item>
/// </list>
/// CANDIDATE: the official server's choice is not recorded for a race box (the 3 official box opens, rec 20261107-011529,
/// were race All chests: G_Box_PreBook_Vehicle_A_s_01_b, G_Box_Wishlist_A_01_b, G_Box_Sellect_PreBook_Vehicle_A_s_01_b);
/// the race rows and ItemRace are the client's own data (its tooltip lists the opener's row).
/// </summary>
public sealed class FactionRewards
{
    /// <summary>A box row: all items, one selected item, packages [name, Prob of 10000, times] and the most opened at once.</summary>
    public sealed record BoxRow(
        [property: JsonPropertyName("items")] int[][] Items,
        [property: JsonPropertyName("select")] int[][] Select,
        [property: JsonPropertyName("packages")] JsonElement[][] Packages,
        [property: JsonPropertyName("max")] int Max = 0);

    sealed record RaceFile(
        [property: JsonPropertyName("boxes")] Dictionary<string, Dictionary<string, BoxRow>> Boxes,
        [property: JsonPropertyName("class_boxes")] Dictionary<string, Dictionary<string, Dictionary<string, BoxRow>>> ClassBoxes,
        [property: JsonPropertyName("loot_packages")] Dictionary<string, Dictionary<string, JsonElement[][]>> LootPackages,
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages,
        [property: JsonPropertyName("twins")] Dictionary<string, int> Twins);

    readonly RaceFile file;
    FactionRewards(RaceFile file) => this.file = file;

    static readonly Lazy<FactionRewards> embedded = new(() =>
    {
        using var stream = typeof(FactionRewards).Assembly.GetManifestResourceStream("box-race.json")
            ?? throw new InvalidOperationException("Missing embedded game data box-race.json");
        return new FactionRewards(JsonSerializer.Deserialize<RaceFile>(stream) ?? throw new InvalidOperationException("Empty box-race.json"));
    });

    public static FactionRewards Embedded => embedded.Value;

    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>1 Light, 2 Dark; anything else counts as Light (a character always has a faction).</summary>
    public static int Faction(int race) => race == 2 ? 2 : 1;

    /// <summary>Whether a character of the race may own the item (Item.dat ItemRace All / None or the race's own).</summary>
    public static bool Allowed(int itemId, int race) => ItemRules.RaceAllows(itemId, Faction(race));

    /// <summary>The other faction's item with the same name (Wing_L_Gather_01a_f &lt;-&gt; Wing_D_Gather_01a_f), if the client has one.</summary>
    public int? Twin(int itemId) => file.Twins.TryGetValue(Key(itemId), out var twin) ? twin : null;

    /// <summary>The item a character of the race gets for a reward: the item, its faction twin, or null (no faction-correct item).</summary>
    public int? ForRace(int itemId, int race) =>
        Allowed(itemId, race) ? itemId : Twin(itemId) is { } twin && Allowed(twin, race) ? twin : null;

    /// <summary>The race row of a box group (BoxReward Race), null when the group has only an All row.</summary>
    public BoxRow? Box(string group, int race) =>
        file.Boxes.TryGetValue(group, out var rows) && rows.TryGetValue(Key(Faction(race)), out var row) ? row : null;

    /// <summary>The box groups with race rows (checks).</summary>
    public IEnumerable<string> RaceBoxGroups => file.Boxes.Keys;

    /// <summary>The class rows of a box group for the opener (BoxClassReward: race All and the opener's race, the opener's class).</summary>
    public IEnumerable<BoxRow> ClassBoxes(string group, int race, string? className)
    {
        if (className is null || !file.ClassBoxes.TryGetValue(group, out var races)) yield break;
        foreach (var raceKey in new[] { "0", Key(Faction(race)) })
            if (races.TryGetValue(raceKey, out var classes) && classes.TryGetValue(className, out var row))
                yield return row;
    }

    /// <summary>The box groups with class rows (checks).</summary>
    public IEnumerable<string> ClassBoxGroups => file.ClassBoxes.Keys;

    /// <summary>A reward table's packages for the race (RewardPackage race rows), null when the table has no race rows.</summary>
    public JsonElement[][]? LootPackages(string table, int race) =>
        file.LootPackages.TryGetValue(table, out var rows) ? rows.GetValueOrDefault(Key(Faction(race))) ?? [] : null;

    /// <summary>The reward tables with race rows (checks).</summary>
    public IEnumerable<string> RaceLootTables => file.LootPackages.Keys;

    /// <summary>A package's entries [item, weight, min, max] from the race file (newer client export), null when not listed there.</summary>
    public long[][]? Package(string name) => file.Packages.GetValueOrDefault(name);

    /// <summary>
    /// The item a candidate of a list gives a character of the race: <see cref="ForRace(int, int)"/>, except that an item of the other
    /// faction whose twin is itself in the list is left out (the list already carries the faction's own entry with its own odds).
    /// </summary>
    public int? ForRace(int itemId, int race, IReadOnlySet<int> listed) =>
        Allowed(itemId, race) ? itemId : Twin(itemId) is { } twin && Allowed(twin, race) && !listed.Contains(twin) ? twin : null;

    /// <summary>
    /// A package's entries for a character of the race: each item of the other faction replaced by its twin (or left out when the
    /// twin is listed too), entries without a faction-correct item left out with their weight, so the rest is renormalised by
    /// its own sum.
    /// </summary>
    public List<(int ItemId, long Weight, long Min, long Max)> ForRace(IEnumerable<long[]> entries, int race)
    {
        var rows = entries.Where(e => e.Length >= 4).ToList();
        var listed = rows.Select(e => (int)e[0]).ToHashSet();
        var list = new List<(int, long, long, long)>();
        foreach (var e in rows)
            if (ForRace((int)e[0], race, listed) is { } item)
                list.Add((item, e[1], e[2], e[3]));
        return list;
    }

    /// <summary>Fixed rewards for a character of the race: twins for the other faction's items, the ones without a faction-correct item dropped.</summary>
    public List<(int ItemId, long Count)> Filter(IEnumerable<(int ItemId, long Count)> rewards, int race)
    {
        var rows = rewards.ToList();
        var listed = rows.Select(r => r.ItemId).ToHashSet();
        return rows.Select(r => (Item: ForRace(r.ItemId, race, listed), r.Count)).Where(r => r.Item is not null).Select(r => (r.Item!.Value, r.Count)).ToList();
    }
}
