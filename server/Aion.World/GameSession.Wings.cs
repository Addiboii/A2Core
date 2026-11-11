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

// Wings. The equipped wing is the stored choice (migration 073) when it is still owned, else the race's default wing for
// staff and ascended characters (implied, never stored), else none. Owned = registered (game.character_wings) or that
// implied default, and of the character's race (Wing.dat PcRace). Packets: WingList_NT 0x5672, WingChange_RQ/RS/NT
// 0x5674-0x5676 (usmap DevPacketData_GameServer_WingChange_RQ {_is_wing_skin bool, _wing_id int}, _RS {_result, _is_wing_skin,
// _wing_id}, _NT {_key, _is_wing_skin, _wing_id}; the RQ OBSERVED 7 bytes live 2026-10-15, the replies CANDIDATE layouts).
public sealed partial class GameSession
{
    static WingTables WingData => WingTables.Embedded;

    /// <summary>The stored wing choice (0: the default rule).</summary>
    int equippedWing;

    /// <summary>Staff and ascended characters (grade 2+) own their race's default wing without a stored row.</summary>
    bool DefaultWingImplied => IsAdmin || (selected?.Ascension ?? 1) >= 2;

    bool OwnsWing(int id) => WingData.Of(id) is { } wing && wing.Race == profileRace
        && (registeredWings.Contains(id) || DefaultWingImplied && id == GamePackets.DefaultWing(profileRace));

    /// <summary>The wing worn after a map load: the stored one while owned, else the default rule.</summary>
    int WingAtLoad() => equippedWing != 0 && OwnsWing(equippedWing) ? equippedWing
        : DefaultWingImplied ? GamePackets.DefaultWing(profileRace) : 0;

    /// <summary>A stat from the worn wing. Enchant level 0: no wing of build 26262663 has an EnchantGroup (Wing.dat), so none can be enchanted.</summary>
    int WingStat(string stat) => wingId == 0 ? 0 : WingData.Stat(wingId, 0, stat);

    /// <summary>
    /// 0x5674 WingChange_RQ: an owned wing becomes the worn one (stored; the default wing clears the stored row) -> 0x5675
    /// result 0, 0x5676 to the player and those nearby (OURS: as TitleChange_NT), the stats. Not owned, or not stored: 0x5675 result 1. A skin request
    /// (is_wing_skin) is answered as before and changes neither the worn wing nor the store.
    /// </summary>
    async Task<List<byte[]>> WingChangeAsync(byte[] plain, CancellationToken ct)
    {
        var skin = (plain[2] & 1) != 0;
        var wanted = BitConverter.ToInt32(plain, 3);
        if (!OwnsWing(wanted)) return [GamePackets.WingChangeReply(wanted, skin, 1)];
        if (skin) return [GamePackets.WingChangeReply(wanted, skin), GamePackets.WingChangeNotice(playerKey, wanted, skin)];
        var stored = registeredWings.Contains(wanted) ? wanted : 0;
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetWingAsync(character, stored, ct) ? [[]] : []);
            if (saved.Count == 0) return [GamePackets.WingChangeReply(wanted, skin, 1)];
        }
        equippedWing = stored;
        wingId = wanted;
        var notice = GamePackets.WingChangeNotice(playerKey, wanted, skin);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        return [GamePackets.WingChangeReply(wanted, skin), notice, .. RefreshStats()];
    }
}
