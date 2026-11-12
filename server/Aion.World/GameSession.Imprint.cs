using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Manastone imprinting (ManastoneTables, tools/data/manastones.json from build 26262663). OBSERVED in the community recordings
// 2026-11-06 234912 (i49411-49595, five imprints on an equipped two-slot Magicbook) and 2026-11-04 163050 (i4553-5575, a four-slot
// item): choosing a stone sends nothing; each imprint answers 0x561d ItemEquipLevelChange_NT (the item was worn), 0x561b (the
// stone -1 and the Kinah, decrease reason 96 kItemImprintMagicStone; the item, update reason 2 kMagicStone, its slots and level),
// then 0x562a ItemImprint_RS {result u16 0, equip uid i64, stone id i32, stone uid i64, count, {stone i32, stat u16, grade u8}}.
// Every imprint replaces every slot with new rolls of the stone's group, each slot on its own; the item level is the item's
// own plus the enchant's plus the slots' grades (23 + grades: 26, 25, 27, 25, 26 in 234912).
// CANDIDATE: the request body (usmap ItemImprint_RQ {equip uid i64, stone id i32, stone uid i64}; encrypted in every recording);
// the refusal codes (EResult kItem_Imprint_*, read from the usmap of client 2.0.6); the stat update after the level change.
public sealed partial class GameSession
{
    public const ushort ItemImprintRq = 0x5629, ItemImprintRs = 0x562a;
    const byte DecreaseImprint = 96, UpdateMagicStone = 2;   // EDecreaseReason / EUpdateReason (OBSERVED i49416)
    const int ImprintFail = 0x034b, ImprintInternalError = 0x034e, ImprintNotImprintable = 0x034f, ImprintInvalidStone = 0x0350;
    static ManastoneTables Manastones => ManastoneTables.Embedded;

    /// <summary>The roll for one draw: 0..n-1 (replaceable in checks).</summary>
    public Func<int, int> ImprintRoll { get; set; } = Random.Shared.Next;

    /// <summary>The stored slots of an item (upgrades.magicstones = [[stone, stat, grade], ...]).</summary>
    static IReadOnlyList<(int Stone, int Stat, int Grade)> MagicStonesOf(ItemState item)
    {
        if (item.Upgrades.ValueKind != JsonValueKind.Object || !item.Upgrades.TryGetProperty("magicstones", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var slots = new List<(int, int, int)>();
        foreach (var s in list.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 3 && s[0].TryGetInt32(out var stone) && s[1].TryGetInt32(out var stat) && s[2].TryGetInt32(out var grade))
                slots.Add((stone, stat, grade));
        return slots;
    }

    /// <summary>What the equipped items' manastones add to a stat (by EStat name).</summary>
    int MagicStoneStat(string stat) => inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail?.MagicStones is { Count: > 0 })
        .SelectMany(i => i.Detail!.MagicStones!).Where(s => Manastones.StatName(s.Stat) == stat).Sum(s => Manastones.Amount(s.Stone, s.Stat, s.Grade));

    static byte[] ImprintReply(int result, long equip, int stone, long stoneUid, IReadOnlyList<(int Stone, int Stat, int Grade)> slots)
    {
        var w = PacketWriter.Opcode(ItemImprintRs).U16(result).I64(equip).I32(stone).I64(stoneUid).Varint((uint)slots.Count);
        foreach (var (id, stat, grade) in slots) w.I32(id).U16(stat).U8((byte)grade);
        return w.ToArray();
    }

    /// <summary>0x5629 {equip uid i64, stone id i32, stone uid i64}: every stone slot of the item rolled anew for one stone and the Kinah.</summary>
    async Task<List<byte[]>> ImprintAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 22) return [];
        var equipKey = BitConverter.ToInt64(plain, 2);
        var stoneId = BitConverter.ToInt32(plain, 10);
        var stoneKey = BitConverter.ToInt64(plain, 14);
        byte[] Refuse(int result) => ImprintReply(result, equipKey, stoneId, stoneKey, []);
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(equipKey, out var item) || item.Location is not ("bag" or "equipment"))
            return [Refuse(ImprintInternalError)];
        if (Manastones.Item(item.TemplateId) is not { } slots) return [Refuse(ImprintNotImprintable)];
        if (!storedItems.TryGetValue(stoneKey, out var stone) || stone.TemplateId != stoneId || stone.Location != "bag" || stone.Quantity < 1
            || Manastones.Stone(stoneId) is not { } kind || !slots.Groups.Contains(kind.EquipGroup))
            return [Refuse(ImprintInvalidStone)];
        // Costs: the stone itself and the Kinah from the largest stacks; nothing the client sent is counted.
        var costs = new List<(Guid, long, int)> { (stone.InstanceId, stone.Revision, 1) };
        var kina = slots.Kina;
        foreach (var stack in storedItems.Values.Where(i => i.TemplateId == KinaItemId && i.Location == "bag").OrderByDescending(i => i.Quantity))
        {
            if (kina <= 0) break;
            var n = (int)Math.Min(stack.Quantity, kina);
            costs.Add((stack.InstanceId, stack.Revision, n));
            kina -= n;
        }
        if (kina > 0) return [Refuse(0x0107)];   // kItem_Common_Fail_NotEnoughCountMoney
        var rolled = Enumerable.Range(0, slots.Count).Select(_ => Manastones.Roll(kind.Group, ImprintRoll)).Select(r => (stoneId, r.Stat, r.Grade)).ToList();
        var saved = await Stored(async () => await store.ImprintItemAsync(character, item.InstanceId, item.Revision, costs,
            rolled.Select(r => new MagicStoneSlot(r.stoneId, r.Stat, r.Grade)).ToList(), ct) ? [[]] : []);
        if (saved.Count == 0) return [Refuse(ImprintFail)];
        var worn = item.Location == "equipment";
        var changes = await InventoryChangesAsync(ct, 0, DecreaseImprint, UpdateMagicStone);
        var replies = new List<byte[]>();
        if (worn)
        {
            var level = EquipItemLevel();
            replies.Add(GamePackets.ItemEquipLevelChange(level, level));
        }
        replies.AddRange(changes);
        replies.Add(ImprintReply(0, equipKey, stoneId, stoneKey, rolled));
        if (worn) replies.AddRange(RefreshStats());
        return replies;
    }
}
