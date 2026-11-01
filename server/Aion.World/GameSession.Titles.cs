using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Title.dat from tools/data/titles.json (tools/title_data.py).</summary>
public sealed class TitleTables
{
    /// <summary>A title: race (0 all), ETitleEquipCategory (1 Attack, 2 Defense, 3 Etc, 0 none), the stats while equipped and the stats for owning it.</summary>
    public sealed record Title(int Id, int Race, int Category, IReadOnlyList<(string Stat, int Value)> EquipStats, IReadOnlyList<(string Stat, int Value)> OwnStats);

    sealed record TitleFile([property: JsonPropertyName("titles")] Dictionary<string, JsonElement[]> Titles);

    readonly Dictionary<int, Title> titles;

    TitleTables(TitleFile file) => titles = file.Titles.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p =>
        new Title(int.Parse(p.Key, CultureInfo.InvariantCulture), p.Value[0].GetInt32(), p.Value[1].GetInt32(), Stats(p.Value[2]), Stats(p.Value[3])));

    static IReadOnlyList<(string, int)> Stats(JsonElement list) => list.EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetInt32())).ToList();

    static readonly Lazy<TitleTables> embedded = new(() =>
    {
        using var stream = typeof(TitleTables).Assembly.GetManifestResourceStream("titles.json") ?? throw new InvalidOperationException("Missing embedded game data titles.json");
        return new TitleTables(JsonSerializer.Deserialize<TitleFile>(stream) ?? throw new InvalidOperationException("Empty titles.json"));
    });

    public static TitleTables Embedded => embedded.Value;
    public Title? Of(int id) => titles.GetValueOrDefault(id);
    public int Count => titles.Count;
}

// Titles. The 0x56 run (catalogue index + 0x54ca): TitleList_NT 0x5679 OBSERVED at the official world entry as `00 00` (no
// titles, no equipped map) for a fresh character; the requests and notices take the usmap fields (CANDIDATE layouts). A
// character owns titles (GetTitle items, the builder command AcquireTitle), equips one per ETitleEquipCategory (its
// EquipStatList applies) and shows one (TitleChange; CharState _title_id for others). Owned titles' StatList applies as well
// (the collection bonus; CANDIDATE: whether the official server sums them is not known).
public sealed partial class GameSession
{
    public const ushort TitleListNt = 0x5679, TitleChangeRq = 0x567a, TitleChangeRs = 0x567b, TitleChangeNt = 0x567c, TitleAcquireNt = 0x567d,
        TitleEquipRq = 0x567f, TitleEquipRs = 0x5680, TitleEquipChangeNt = 0x5681;
    static TitleTables Titles => TitleTables.Embedded;

    readonly SortedSet<int> titles = [];
    /// <summary>The equipped title per ETitleEquipCategory (1..3) and the shown one (0).</summary>
    readonly Dictionary<int, int> titleEquips = [];

    public IReadOnlyCollection<int> OwnedTitles => titles;
    public IReadOnlyDictionary<int, int> TitleEquips => titleEquips;
    public int ShownTitle => titleEquips.GetValueOrDefault(0);

    void LoadTitles(CharacterExtras extras)
    {
        titles.Clear();
        titles.UnionWith(extras.Titles);
        titleEquips.Clear();
        foreach (var (category, id) in extras.TitleEquips) titleEquips[category] = id;
    }

    /// <summary>0x5679 TitleList_NT: the owned ids, then the equipped map {category u8, title i32} (the shown title under 0).</summary>
    byte[] TitleList()
    {
        var w = PacketWriter.Opcode(TitleListNt).Varint(titles.Count);
        foreach (var id in titles) w.I32(id);
        w.Varint(titleEquips.Count);
        foreach (var (category, id) in titleEquips.OrderBy(p => p.Key)) w.U8((byte)category).I32(id);
        return w.ToArray();
    }

    /// <summary>A title acquired (an item, a command): stored, TitleAcquire_NT 0x567d, the owned stats. Empty when unknown, of the other race, or owned.</summary>
    public async Task<List<byte[]>> AcquireTitleAsync(int titleId, CancellationToken ct)
    {
        if (Titles.Of(titleId) is not { } title || title.Race != 0 && title.Race != profileRace || titles.Contains(titleId)) return [];
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.GrantTitleAsync(character, titleId, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
        }
        titles.Add(titleId);
        return [PacketWriter.Opcode(TitleAcquireNt).I32(titleId).ToArray(), .. RefreshStats()];
    }

    /// <summary>A GetTitle item [title id]: used up, the title acquired.</summary>
    async Task<List<byte[]>?> UseTitleItemAsync(INativeCharacterStore store, Guid character, ItemState row, int titleId, bool reply, CancellationToken ct)
    {
        if (Titles.Of(titleId) is not { } title || title.Race != 0 && title.Race != profileRace) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        if (titles.Contains(titleId)) return replies;
        replies.AddRange(await AcquireTitleAsync(titleId, ct));
        return replies;
    }

    /// <summary>
    /// 0x567f TitleEquip_RQ {equip slot u8 (ETitleEquipCategory), title i32}, the title window's Equip Effect (F): an owned title
    /// of that category (0 clears) -> 0x5680 {result u16, slot, title}, then 0x5681 TitleEquipChange_NT {slot u8, title i32}
    /// (catalogue index after TitleEquip_RS, usmap layout) and the stats. Like 0x567c after TitleChange, the notice is what
    /// moves the client's equipped-effect slot (AionEventParam_TitleEquipped); the reply alone left the window unchanged.
    /// </summary>
    async Task<List<byte[]>> TitleEquipAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        int category = plain[2], titleId = BitConverter.ToInt32(plain, 3);
        byte[] Reply(int result) => PacketWriter.Opcode(TitleEquipRs).U16(result).U8((byte)category).I32(titleId).ToArray();
        if (category is < 1 or > 3 || titleId != 0 && (!titles.Contains(titleId) || Titles.Of(titleId)?.Category != category)) return [Reply(1)];
        if (!await SaveTitleAsync(category, titleId, ct)) return [Reply(1)];
        return [Reply(0), PacketWriter.Opcode(TitleEquipChangeNt).U8((byte)category).I32(titleId).ToArray(), .. RefreshStats()];
    }

    /// <summary>0x567a TitleChange_RQ {title i32}: the shown title (0 none) -> 0x567b {result u16, title}; 0x567c {key, title} to those nearby.</summary>
    async Task<List<byte[]>> TitleChangeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var titleId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(TitleChangeRs).U16(result).I32(titleId).ToArray();
        if (titleId != 0 && !titles.Contains(titleId)) return [Reply(1)];
        if (!await SaveTitleAsync(0, titleId, ct)) return [Reply(1)];
        var shown = PacketWriter.Opcode(TitleChangeNt).Varint(playerKey).I32(titleId).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [shown]);
        return [Reply(0), shown];
    }

    async Task<bool> SaveTitleAsync(int category, int titleId, CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetTitleAsync(character, category, titleId, ct) ? [[]] : []);
            if (saved.Count == 0) return false;
        }
        if (titleId == 0) titleEquips.Remove(category);
        else titleEquips[category] = titleId;
        return true;
    }

    /// <summary>A stat from the equipped titles' EquipStatList and the owned titles' StatList.</summary>
    public int TitleStat(string stat) =>
        titleEquips.Where(p => p.Key != 0).Sum(p => Titles.Of(p.Value)?.EquipStats.Where(s => s.Stat == stat).Sum(s => s.Value) ?? 0)
        + titles.Sum(id => Titles.Of(id)?.OwnStats.Where(s => s.Stat == stat).Sum(s => s.Value) ?? 0);
}
