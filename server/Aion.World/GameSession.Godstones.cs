using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Godstones (the client's GodstoneData, GodstoneGacha, GodstoneCombine; tools/data/enhance.json). Godstones are not items: the
// character holds counts per godstone id (setting "godstones"), listed at entry with GodstoneList_NT 0x5633 (OBSERVED
// 20261106-105448: "01 c1693001 01000000" = one entry {id i32 19950017, count i32 1}; "00" when none). Live opcodes in the 0x56 run
// (catalogue + 0x1ebe):
// - an item with use effect GetGodstone [GodstoneGacha group] rolls the group's GodstoneProb and adds one godstone:
//   GodstoneUpdate_NT 0x5634 {EGodstoneModifyReason u8 (1 UseItem), [{id, count now}]} (usmap, CANDIDATE: never recorded).
// - ItemEnchantGodstone_RQ 0x5635 {item uid i64, slot u8, godstone id i32} -> RS 0x5636 {result u16, uid, slot, id} (usmap,
//   CANDIDATE): a weapon godstone into a weapon slot (1-based, up to Item.dat GodStoneSlotCount), for GlobalSetting
//   godstone_cost_money_count 10000 Kina; the godstone count drops (0x5634 reason 2 Enchant), a godstone already in the slot is
//   replaced (CANDIDATE: lost), the item record carries it (upgrades.godstones) and others see BroadcastItemEnchantGodstone_NT
//   0x5637 (OBSERVED 115817: key varint, item id i32, EVisualEquipSlot u8, slot u8, godstone id i32).
// - CombineGodstone_RQ 0x5638 {materials map: id i32 -> count i32} -> RS 0x5639 {result u16, material grade u8, reward ids
//   [i32], updates [{id, count}]} (usmap, CANDIDATE): godstone_combine_material_count_max (4) godstones of one grade, at most
//   godstone_combine_max_grade (Unique), give one roll of GodstoneCombine group = that grade.
// The godstone's effect (OptAbnormalId: an ItemPassive abnormal with an on-hit skill) is in GameSession.GearEffects.cs.
public sealed partial class GameSession
{
    public const ushort GodstoneListNt = 0x5633, GodstoneUpdateNt = 0x5634, ItemEnchantGodstoneRq = 0x5635, ItemEnchantGodstoneRs = 0x5636,
        BroadcastItemEnchantGodstoneNt = 0x5637, CombineGodstoneRq = 0x5638, CombineGodstoneRs = 0x5639;
    const byte GodstoneUseItem = 1, GodstoneEnchant = 2;
    public const long GodstoneEnchantKina = 10_000;     // GlobalSetting godstone_cost_money_count
    public const int GodstoneCombineCount = 4;          // GlobalSetting godstone_combine_material_count_max
    static readonly string[] GodstoneGrades = ["Rare", "Legend", "Unique"];   // up to godstone_combine_max_grade

    /// <summary>The character's godstones: id -> count (setting "godstones").</summary>
    readonly SortedDictionary<int, int> godstones = [];

    public IReadOnlyDictionary<int, int> GodstonesForChecks => godstones;

    /// <summary>The enhancement settings at character load: godstones and the succession counters.</summary>
    void LoadEnhanceSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        godstones.Clear();
        try
        {
            if (settings.TryGetValue("godstones", out var g) && g.Deserialize<Dictionary<int, int>>() is { } saved)
                foreach (var (id, count) in saved) if (count > 0 && Enhance.GodstoneOf(id) is not null) godstones[id] = count;
            successionCounts = settings.TryGetValue("succession_count", out var s) && s.Deserialize<int[]>() is { Length: 2 } counts ? counts : [0, 0];
        }
        catch (JsonException) { successionCounts = [0, 0]; }
    }

    byte[] GodstoneList() => GodstoneListPacket(godstones);

    public static byte[] GodstoneListPacket(IReadOnlyDictionary<int, int> held)
    {
        var w = PacketWriter.Opcode(GodstoneListNt).Varint(held.Count);
        foreach (var (id, count) in held.OrderBy(p => p.Key)) w.I32(id).I32(count);
        return w.ToArray();
    }

    public static byte[] GodstoneBroadcast(int key, int itemId, byte visual, byte slot, int godstone) =>
        PacketWriter.Opcode(BroadcastItemEnchantGodstoneNt).Varint(key).I32(itemId).U8(visual).U8(slot).I32(godstone).ToArray();

    static byte[] GodstoneUpdate(byte reason, IEnumerable<(int Id, int Count)> updates)
    {
        var list = updates.ToList();
        var w = PacketWriter.Opcode(GodstoneUpdateNt).U8(reason).Varint(list.Count);
        foreach (var (id, count) in list) w.I32(id).I32(count);
        return w.ToArray();
    }

    Task SaveGodstonesAsync(CancellationToken ct) => SaveSettingAsync("godstones", godstones.ToDictionary(p => p.Key, p => p.Value), ct);

    /// <summary>An item's socketed godstones by slot (upgrades.godstones, 3 entries, 0 = empty).</summary>
    static IReadOnlyList<int> WireGodstones(ItemState item)
    {
        var ids = new int[3];
        if (item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty("godstones", out var list) && list.ValueKind == JsonValueKind.Array)
            for (var i = 0; i < 3 && i < list.GetArrayLength(); i++) ids[i] = list[i].TryGetInt32(out var id) ? id : 0;
        return ids;
    }

    /// <summary>The worn items' godstones by visual slot (the equipment records others see).</summary>
    Dictionary<int, IReadOnlyList<int>> VisualGodstones()
    {
        var result = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var row in storedItems.Values.Where(i => i.Location == "equipment" && i.Slot is { } s && VisualSlot.ContainsKey(s)))
            result[VisualSlot[row.Slot!.Value]] = WireGodstones(row);
        return result;
    }

    /// <summary>A GetGodstone item: one godstone of its gacha group, the item used up.</summary>
    async Task<List<byte[]>?> UseGodstoneItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var group, ..] || Enhance.GodstoneGacha(group) is not { Count: > 0 } pool) return null;
        var (id, _) = EnhanceTables.Pick(pool, p => p.Weight, rng);
        if (Enhance.GodstoneOf(id) is null) return null;
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        godstones[id] = godstones.GetValueOrDefault(id) + 1;
        await SaveGodstonesAsync(ct);
        return [.. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. Used(row, reply), GodstoneUpdate(GodstoneUseItem, [(id, godstones[id])])];
    }

    async Task<List<byte[]>> EnchantGodstoneAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 15) return [];
        var key = BitConverter.ToInt64(plain, 2);
        var slot = plain[10];
        var id = BitConverter.ToInt32(plain, 11);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemEnchantGodstoneRs).U16(result).I64(key).U8(slot).I32(id).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment"))
            return [Reply(EResult.Item_Enchant_Godstone_InvalidData)];
        if (Enhance.GodstoneOf(id) is not { } stone) return [Reply(EResult.Item_Enchant_Godstone_InvalidData)];
        if (godstones.GetValueOrDefault(id) < 1) return [Reply(EResult.Item_Enchant_Godstone_NotEnoughGodstone)];
        if (ItemDB.Embedded.Of(row.TemplateId) is not { } gear || slot < 1 || slot > Math.Min(3, gear.GodstoneSlots)) return [Reply(EResult.Item_Enchant_Godstone_InvalidSlot)];
        // GodstoneData Category is EEquipCategory::Weapon for all 51 godstones: only weapons take them.
        if (stone.Category == "Weapon" && StatPools.KindOf(gear.Group) != "weapon") return [Reply(EResult.Item_Enchant_Godstone_CategoryNotMatch)];
        if (CostStacks(KinaItemId, GodstoneEnchantKina, row.InstanceId) is not { } kina) return [Reply(EResult.Item_Enchant_Godstone_NotEnoughCost)];
        var sockets = WireGodstones(row).ToArray();
        sockets[slot - 1] = id;
        var done = await Stored(async () =>
        {
            foreach (var (item, revision, count) in kina) if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
            return await store.SetItemUpgradeAsync(character, row.InstanceId, "godstones", JsonSerializer.Serialize(sockets), ct) ? [[]] : [];
        });
        if (done.Count == 0) return [Reply(EResult.Item_Enchant_Godstone_InternalError)];
        if (--godstones[id] <= 0) godstones.Remove(id);
        await SaveGodstonesAsync(ct);
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemEnchant);
        replies.Add(GodstoneUpdate(GodstoneEnchant, [(id, godstones.GetValueOrDefault(id))]));
        replies.Add(Reply(0));
        if (row.Location == "equipment" && inventory.TryGetValue(key, out var worn) && VisualSlot.TryGetValue(worn.Slot, out var visual))
        {
            var notice = GodstoneBroadcast(playerKey, row.TemplateId, (byte)visual, slot, id);
            worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
            replies.Add(notice);
            // The worn weapon's new godstone passive (and the one it replaced gone): GameSession.GearEffects.cs.
            gearState = null;
            replies.AddRange(GearChanges());
        }
        return replies;
    }

    async Task<List<byte[]>> CombineGodstonesAsync(byte[] plain, CancellationToken ct)
    {
        var materials = new Dictionary<int, int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            for (var n = r.Varint(); n > 0; n--)
            {
                var id = r.I32();
                materials[id] = materials.GetValueOrDefault(id) + r.I32();
            }
        }
        catch (ProtocolException) { return []; }
        byte grade = 0;
        byte[] Reply(int result, IReadOnlyList<int>? rewards = null, IReadOnlyList<(int, int)>? updates = null)
        {
            var w = PacketWriter.Opcode(CombineGodstoneRs).U16(result).U8(grade).Varint(rewards?.Count ?? 0);
            foreach (var id in rewards ?? []) w.I32(id);
            w.Varint(updates?.Count ?? 0);
            foreach (var (id, count) in updates ?? []) w.I32(id).I32(count);
            return w.ToArray();
        }
        if (materials.Count == 0 || materials.Values.Any(c => c < 1) || materials.Values.Sum() != GodstoneCombineCount) return [Reply(EResult.Godstone_Combine_InvalidCount)];
        var stones = materials.Keys.Select(Enhance.GodstoneOf).ToList();
        if (stones.Any(s => s is null)) return [Reply(EResult.Godstone_Combine_InvalidData)];
        if (stones.Any(s => !s!.Combinable)) return [Reply(EResult.Godstone_Combine_InvalidMeterial)];
        if (stones.Select(s => s!.Grade).Distinct().Count() != 1) return [Reply(EResult.Godstone_Combine_GradeIsNotSame)];
        var materialGrade = stones[0]!.Grade;
        grade = (byte)(Array.IndexOf(["None", "Common", "Rare", "Legend", "Unique", "Epic"], materialGrade) is var g and >= 0 ? g : 0);   // EItemGrade
        if (!GodstoneGrades.Contains(materialGrade) || Enhance.GodstoneCombine(materialGrade) is not { Count: > 0 } pool) return [Reply(EResult.Godstone_Combine_OverCombineMaxGrade)];
        if (materials.Any(m => godstones.GetValueOrDefault(m.Key) < m.Value)) return [Reply(EResult.Godstone_Combine_NotEnoughMaterial)];
        var (reward, _) = EnhanceTables.Pick(pool, p => p.Weight, rng);
        foreach (var (id, count) in materials) if ((godstones[id] -= count) <= 0) godstones.Remove(id);
        godstones[reward] = godstones.GetValueOrDefault(reward) + 1;
        await SaveGodstonesAsync(ct);
        var changed = materials.Keys.Append(reward).Distinct().Select(id => (id, godstones.GetValueOrDefault(id))).ToList();
        return [Reply(0, [reward], changed)];
    }
}
