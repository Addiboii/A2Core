using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Skin.dat and SkinSet.dat from tools/data/skins.json (tools/skin_data.py).</summary>
public sealed class SkinTables
{
    sealed record SkinFile(
        [property: JsonPropertyName("skins")] Dictionary<string, int> Skins,
        [property: JsonPropertyName("sets")] Dictionary<string, int[]> Sets,
        [property: JsonPropertyName("categories")] Dictionary<string, string> Categories);

    readonly SkinFile file;
    SkinTables(SkinFile file) => this.file = file;

    static readonly Lazy<SkinTables> embedded = new(() =>
    {
        using var stream = typeof(SkinTables).Assembly.GetManifestResourceStream("skins.json") ?? throw new InvalidOperationException("Missing embedded game data skins.json");
        return new SkinTables(JsonSerializer.Deserialize<SkinFile>(stream) ?? throw new InvalidOperationException("Empty skins.json"));
    });

    public static SkinTables Embedded => embedded.Value;
    /// <summary>A skin by its Skin.dat name (GetSkin items), or 0.</summary>
    public int Of(string name) => file.Skins.GetValueOrDefault(name);
    /// <summary>The skins of a set by its SkinSet.dat name (GetSkinSet items).</summary>
    public IReadOnlyList<int> Set(string name) => file.Sets.GetValueOrDefault(name) ?? [];
    public bool Known(int skinId) => file.Categories.ContainsKey(skinId.ToString(CultureInfo.InvariantCulture));
    public string Category(int skinId) => file.Categories.GetValueOrDefault(skinId.ToString(CultureInfo.InvariantCulture)) ?? "None";
    public int Count => file.Skins.Count;
}

// Skins (appearance items). The 0x8d run (catalogue index + 0x89d1): SkinInfo_NT 0x8d5b OBSERVED at the official world
// entry as `00 00 00 01 00 00` (no skins, no points, no pure-skin equips, preset 1, no preset or visual slot infos);
// SkinUpdate_NT 0x8d5c and SkinEquip_RQ/RS 0x8d5e/0x8d5f take the usmap fields (CANDIDATE layouts). GetSkin / GetSkinSet
// items add skins to the collection by Skin.dat / SkinSet.dat name; an equipped skin shows as the EquipBroad _skin_id of
// its visual slot for the player and those nearby (ItemEquipChange on a change). Skin points, presets, dyes and the
// pure-skin (no item) display are not modelled.
public sealed partial class GameSession
{
    public const ushort SkinInfoNt = 0x8d5b, SkinUpdateNt = 0x8d5c, SkinEquipRq = 0x8d5e, SkinEquipRs = 0x8d5f;
    static SkinTables Skins => SkinTables.Embedded;

    /// <summary>The collection: skin id -> count.</summary>
    readonly Dictionary<int, int> skins = [];
    /// <summary>Equipped skins by ESkinEquipSlot.</summary>
    readonly Dictionary<int, int> skinEquips = [];
    /// <summary>ESkinEquipSlot -> EVisualEquipSlot (the weapons share MainHand; off-hand weapons and the shield have no visual slot).</summary>
    static readonly Dictionary<int, int> SkinVisualSlot = new()
    {
        [1] = 2, [2] = 3, [3] = 4, [4] = 5, [5] = 6, [6] = 7, [7] = 8, [8] = 1, [10] = 1, [11] = 1, [13] = 1, [14] = 1, [15] = 1, [17] = 1, [18] = 1,
        [20] = 11, [21] = 10, [22] = 9, [23] = 12, [24] = 13, [25] = 14, [27] = 15,
    };

    public IReadOnlyDictionary<int, int> OwnedSkins => skins;
    public IReadOnlyDictionary<int, int> SkinEquips => skinEquips;

    void LoadSkins(CharacterExtras extras)
    {
        skins.Clear();
        foreach (var (id, count) in extras.Skins) skins[id] = count;
        skinEquips.Clear();
        foreach (var (slot, id) in extras.SkinEquips) skinEquips[slot] = id;
    }

    /// <summary>0x8d5b SkinInfo_NT: the collection {skin i32, {count i32, instances i32}}, no points, no pure-skin equips, preset 1, no preset or visual slot infos.</summary>
    byte[] SkinInfo()
    {
        var w = PacketWriter.Opcode(SkinInfoNt).Varint(skins.Count);
        foreach (var (id, count) in skins.OrderBy(p => p.Key)) w.I32(id).I32(count).I32(0);
        return w.Varint(0).Varint(0).U8(1).Varint(0).Varint(0).ToArray();
    }

    /// <summary>0x8d5c SkinUpdate_NT: the changed counts {skin i32 -> count i32}, no points.</summary>
    byte[] SkinUpdate(IReadOnlyCollection<int> ids)
    {
        var w = PacketWriter.Opcode(SkinUpdateNt).Varint(ids.Count);
        foreach (var id in ids) w.I32(id).I32(skins.GetValueOrDefault(id));
        return w.Varint(0).ToArray();
    }

    /// <summary>The skin shown in a visual slot (0 none).</summary>
    int SkinIn(int visualSlot) => skinEquips.Where(p => SkinVisualSlot.GetValueOrDefault(p.Key) == visualSlot).Select(p => p.Value).FirstOrDefault();

    /// <summary>The visual equips with their skins: the 15 EVisualEquipSlot records (others' appearance) or the worn ones (the own).</summary>
    IReadOnlyList<EquipBroad> VisualEquips(bool all)
    {
        var items = VisualItems();
        var slots = all ? Enumerable.Range(1, 15) : items.Keys.Order();
        return slots.Select(slot =>
        {
            var item = items.GetValueOrDefault(slot);
            return new EquipBroad(item, VisualSlot: (byte)slot, SkinId: item == 0 ? 0 : SkinIn(slot), Godstones: item == 0 ? [] : [0, 0, 0]);
        }).ToList();
    }

    /// <summary>Skins added to the collection (an item, a command): stored, SkinUpdate_NT 0x8d5c. Empty when none is known.</summary>
    public async Task<List<byte[]>> AcquireSkinsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var known = ids.Where(Skins.Known).Distinct().ToList();
        if (known.Count == 0) return [];
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.GrantSkinsAsync(character, known, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
        }
        foreach (var id in known) skins[id] = skins.GetValueOrDefault(id) + 1;
        return [SkinUpdate(known)];
    }

    /// <summary>A GetSkin [skin name] or GetSkinSet [set name] item: used up, the skins acquired.</summary>
    async Task<List<byte[]>?> UseSkinItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        var ids = use.Effect == "GetSkinSet" ? Skins.Set(use.Values[0]) : Skins.Of(use.Values[0]) is var one and > 0 ? [one] : [];
        if (ids.Count == 0) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.AddRange(await AcquireSkinsAsync(ids, ct));
        return replies;
    }

    /// <summary>
    /// 0x8d5e SkinEquip_RQ: {ESkinEquipSlot u8 -> SkinEquipInfo {slot u8, skin dbid i64, skin i32}}; skin 0 clears the slot.
    /// 0x8d5f {result u16, the map as sent, no visual slot infos}; the changed visual slots go to those nearby (ItemEquipChange).
    /// </summary>
    async Task<List<byte[]>> SkinEquipAsync(byte[] plain, CancellationToken ct)
    {
        var changes = new Dictionary<int, int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var count = (int)r.Varint();
            for (var i = 0; i < count && i < 32; i++)
            {
                int slot = r.U8();
                r.U8();
                r.I64();
                changes[slot] = r.I32();
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(SkinEquipRs).U16(result).Varint(changes.Count);
            foreach (var (slot, skin) in changes) w.U8((byte)slot).U8((byte)slot).I64(0).I32(skin);
            return w.Varint(0).ToArray();
        }
        if (changes.Count == 0 || changes.Any(c => c.Key is < 1 or > 27 || c.Value != 0 && !skins.ContainsKey(c.Value))) return [Reply(1)];
        var before = VisualEquips(true);
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkinEquipsAsync(character, changes, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        foreach (var (slot, skin) in changes)
            if (skin == 0) skinEquips.Remove(slot);
            else skinEquips[slot] = skin;
        var after = VisualEquips(true);
        var replies = new List<byte[]> { Reply(0) };
        var changed = before.Zip(after).Where(p => p.First.SkinId != p.Second.SkinId).Select(p => (p.First.VisualSlot, p.First, p.Second)).ToList();
        if (changed.Count > 0)
        {
            var shown = PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), changed);
            worldPlayer?.Map?.Broadcast(worldPlayer, [shown]);
            replies.Add(shown);
        }
        return replies;
    }
}
