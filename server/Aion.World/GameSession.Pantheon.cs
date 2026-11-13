using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// The Pantheon (the client's AgitDeco: artworks, small and large statues placed in the character's hall; "placing it in the
// Pantheon grants the effect contained within"). Tables AgitDecoItem / AgitDecoSlot (tools/data/pantheon.json). Live opcodes in
// the legion run (catalogue + 0x518b, as GuildDescUpdate_NT 0x8a33): AgitDecoList_NT 0x8aaf OBSERVED at every official entry
// (after GodstoneList_NT 0x5633): {decos [{id i32, slot u8}]}, slot 0 = owned but not placed, 1-12 frames, 13-16 small
// statues, 17 the large statue ("af8a00" with none; 115817: 14 decos, 13 placed). The rest is CANDIDATE (usmap, unrecorded):
// - a GetDeco item [deco Name] adds the deco: AgitDecoAdd_NT 0x8ab0 {id}; one already owned is refused (AgitDeco_Fail_AlreadyHave).
// - AgitDecoEquip_RQ 0x8ab3 {id, slot u8} -> RS 0x8ab4 {result u16, id, slot} then AgitDecoUpdate_NT 0x8ab1 {[{id, slot}]}
//   with every deco whose slot changed (a deco in the slot goes back to 0). The slot's DecoType must be the deco's, its
//   NeedLevel reached.
// - AgitDecoUnEquip_RQ 0x8ab5 {id} -> RS 0x8ab6 {result u16, id} then 0x8ab1 {id, 0}.
// Stored as setting "agit_decos" ([[id, slot], ...] in acquisition order). The decos' AbnormalId effects are not applied yet.
public sealed partial class GameSession
{
    public const ushort AgitDecoListNt = 0x8aaf, AgitDecoAddNt = 0x8ab0, AgitDecoUpdateNt = 0x8ab1, AgitDecoEquipRq = 0x8ab3, AgitDecoEquipRs = 0x8ab4,
        AgitDecoUnEquipRq = 0x8ab5, AgitDecoUnEquipRs = 0x8ab6;

    public sealed record DecoInfo(int Id, string Name, string Type, int Abnormal, string Race);
    sealed record PantheonData(Dictionary<int, DecoInfo> Decos, Dictionary<string, int> ByName, Dictionary<int, (string Type, int Level)> Slots);

    static readonly Lazy<PantheonData> Pantheon = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("pantheon.json") ?? throw new InvalidOperationException("Missing embedded game data pantheon.json");
        var root = JsonDocument.Parse(stream).RootElement;
        var decos = root.GetProperty("decos").EnumerateObject().Select(p => new DecoInfo(int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
            p.Value[0].GetString()!, p.Value[1].GetString()!, p.Value[2].GetInt32(), p.Value[3].GetString()!)).ToDictionary(d => d.Id);
        return new PantheonData(decos, decos.Values.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase),
            root.GetProperty("slots").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                p => (p.Value[0].GetString()!, p.Value[1].GetInt32())));
    });

    /// <summary>The character's decorations in acquisition order: id and slot (0 = not placed).</summary>
    readonly List<(int Id, byte Slot)> agitDecos = [];
    public IReadOnlyList<(int Id, byte Slot)> AgitDecosForChecks => agitDecos;

    void LoadPantheon(IReadOnlyDictionary<string, JsonElement> settings)
    {
        agitDecos.Clear();
        try
        {
            if (settings.TryGetValue("agit_decos", out var saved) && saved.Deserialize<int[][]>() is { } rows)
                foreach (var r in rows) if (r is [var id, var slot] && Pantheon.Value.Decos.ContainsKey(id) && agitDecos.All(d => d.Id != id)) agitDecos.Add((id, (byte)Math.Clamp(slot, 0, 255)));
        }
        catch (JsonException) { }
    }

    Task SavePantheonAsync(CancellationToken ct) => SaveSettingAsync("agit_decos", agitDecos.Select(d => new[] { d.Id, (int)d.Slot }).ToArray(), ct);

    public static byte[] AgitDecoListPacket(IReadOnlyList<(int Id, byte Slot)> decos)
    {
        var w = PacketWriter.Opcode(AgitDecoListNt).Varint(decos.Count);
        foreach (var (id, slot) in decos) w.I32(id).U8(slot);
        return w.ToArray();
    }

    byte[] AgitDecoList() => AgitDecoListPacket(agitDecos);

    static byte[] AgitDecoUpdate(IReadOnlyList<(int Id, byte Slot)> changed)
    {
        var w = PacketWriter.Opcode(AgitDecoUpdateNt).Varint(changed.Count);
        foreach (var (id, slot) in changed) w.I32(id).U8(slot);
        return w.ToArray();
    }

    /// <summary>A GetDeco item: the decoration joins the hall's collection (not placed), the item used up.</summary>
    async Task<List<byte[]>?> UseDecoItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var name, ..] || !Pantheon.Value.ByName.TryGetValue(name, out var id) || agitDecos.Any(d => d.Id == id)) return null;
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        agitDecos.Add((id, 0));
        await SavePantheonAsync(ct);
        return [.. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. Used(row, reply), PacketWriter.Opcode(AgitDecoAddNt).I32(id).ToArray()];
    }

    async Task<List<byte[]>> AgitDecoEquipAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 7) return [];
        var id = BitConverter.ToInt32(plain, 2);
        var slot = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(AgitDecoEquipRs).U16(result).I32(id).U8(slot).ToArray();
        var index = agitDecos.FindIndex(d => d.Id == id);
        if (index < 0 || !Pantheon.Value.Decos.TryGetValue(id, out var deco)) return [Reply(EResult.AgitDeco_InvalidDecoItem)];
        if (!Pantheon.Value.Slots.TryGetValue(slot, out var slotInfo)) return [Reply(EResult.AgitDeco_InvalidDecoSlot)];
        if (!string.Equals(slotInfo.Type, deco.Type, StringComparison.OrdinalIgnoreCase)) return [Reply(EResult.AgitDeco_InvalidDecoType)];
        if (Level < slotInfo.Level) return [Reply(EResult.AgitDeco_NotEnoughCharLevel)];
        if (agitDecos[index].Slot == slot) return [Reply(EResult.AgitDeco_AlreadyEquipDeco)];
        var changed = new List<(int, byte)>();
        var occupant = agitDecos.FindIndex(d => d.Slot == slot);
        if (occupant >= 0)
        {
            agitDecos[occupant] = (agitDecos[occupant].Id, 0);
            changed.Add(agitDecos[occupant]);
        }
        agitDecos[index] = (id, slot);
        changed.Add(agitDecos[index]);
        await SavePantheonAsync(ct);
        return [Reply(0), AgitDecoUpdate(changed)];
    }

    async Task<List<byte[]>> AgitDecoUnEquipAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var id = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(AgitDecoUnEquipRs).U16(result).I32(id).ToArray();
        var index = agitDecos.FindIndex(d => d.Id == id);
        if (index < 0) return [Reply(EResult.AgitDeco_InvalidDecoItem)];
        if (agitDecos[index].Slot == 0) return [Reply(EResult.AgitDeco_Fail_UnEquip)];
        agitDecos[index] = (id, 0);
        await SavePantheonAsync(ct);
        return [Reply(0), AgitDecoUpdate([agitDecos[index]])];
    }
}
