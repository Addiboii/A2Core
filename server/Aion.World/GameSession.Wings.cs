using Aion.Application.Gameplay;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Wing.dat from tools/data/wings.json (tools/wing_data.py, build 26262663): each wing's race and EquipStatList, and the
/// WingEnchantEffect stats per EffectGroup and enchant level (empty for 26262663: that table was not extracted from this
/// build, see tools/wing_data.py).
/// </summary>
public sealed class WingTables
{
    /// <summary>A wing: ERace (1 Light, 2 Dark), WingEnchantEffect group, EquipStatList.</summary>
    public sealed record Wing(int Id, int Race, string EffectGroup, IReadOnlyList<(string Stat, int Value)> Stats);

    sealed record WingFile([property: JsonPropertyName("wings")] Dictionary<string, WingRow> Wings,
        [property: JsonPropertyName("effects")] Dictionary<string, JsonElement[][][]> Effects);
    sealed record WingRow([property: JsonPropertyName("race")] int Race, [property: JsonPropertyName("effect_group")] string EffectGroup,
        [property: JsonPropertyName("stats")] JsonElement[][] Stats);

    readonly Dictionary<int, Wing> wings;
    readonly Dictionary<string, IReadOnlyList<(string Stat, int Value)>[]> effects;

    WingTables(WingFile file)
    {
        wings = file.Wings.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture),
            p => new Wing(int.Parse(p.Key, CultureInfo.InvariantCulture), p.Value.Race, p.Value.EffectGroup, Stats(p.Value.Stats)));
        effects = file.Effects.ToDictionary(p => p.Key, p => p.Value.Select(Stats).ToArray());
    }

    static IReadOnlyList<(string Stat, int Value)> Stats(JsonElement[][] list) => list.Select(s => (s[0].GetString()!, s[1].GetInt32())).ToList();

    static readonly Lazy<WingTables> embedded = new(() =>
    {
        using var stream = typeof(WingTables).Assembly.GetManifestResourceStream("wings.json") ?? throw new InvalidOperationException("Missing embedded game data wings.json");
        return new WingTables(JsonSerializer.Deserialize<WingFile>(stream) ?? throw new InvalidOperationException("Empty wings.json"));
    });

    public static WingTables Embedded => embedded.Value;
    public int Count => wings.Count;
    public Wing? Of(int id) => wings.GetValueOrDefault(id);

    /// <summary>Every wing of a race (Wing.dat PcRace), by id.</summary>
    public IReadOnlyList<int> OfRace(int race) => wings.Values.Where(w => w.Race == race).Select(w => w.Id).Order().ToList();

    /// <summary>A stat of a wing at an enchant level: its EquipStatList plus its effect group's stats at that level.</summary>
    public int Stat(int id, int enchant, string stat)
    {
        if (Of(id) is not { } wing) return 0;
        var total = wing.Stats.Where(s => s.Stat == stat).Sum(s => s.Value);
        if (effects.TryGetValue(wing.EffectGroup, out var levels) && enchant >= 0 && enchant < levels.Length)
            total += levels[enchant].Where(s => s.Stat == stat).Sum(s => s.Value);
        return total;
    }
}

// Wings (merge 2026-11-13): the worn wing and the wing skin are the Dell version's saved setting "wing" [wing, skin]
// (GameSession.Settings.cs) and its WingChange handler (GameSession.cs); game.character_wing_equips (migration 073) is kept but
// not used by the session. Wing.dat's tables above stay for the data checks.
public sealed partial class GameSession
{
    static WingTables WingData => WingTables.Embedded;

    /// <summary>The wing worn after a map load: the saved one while still registered, else the race's default wing for staff
    /// and ascended characters (grade 2+), else none.</summary>
    int WingAtLoad() => IsAdmin || (selected?.Ascension ?? 1) >= 2
        ? equippedWing != 0 && registeredWings.Contains(equippedWing) ? equippedWing : GamePackets.DefaultWing(profileRace) : 0;
}
