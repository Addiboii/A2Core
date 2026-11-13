using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Arcana (the client's Arcana window: card items, EEquipSlot kArcana 41-50, Item.dat EquipCategory Arcana*). The 0x37xx run
// (usmap 0x3791 + 0x1ebe): ArcanaEnchant_RQ/RS 0x564f/0x5650, ArcanaExtract 0x5651/0x5652, ArcanaExtractPoint_NT 0x5653,
// ArcanaCreate 0x5654/0x5655, ArcanaCreateCoolTime_NT 0x5656, ArcanaQuickEquip 0x5657/0x5658. NO official Arcana message was
// ever recorded: every body here is the usmap field list with the observed conventions (results u16, arrays varint count,
// maps as key + value pairs, structs plain) - CANDIDATE throughout.
//   Create_RQ {create id i32, main stats [i32], sub stats [i32], sub skills [i32]} -> Create_RS {result, item uid i64, create id,
//     the three selections echoed, main stats [{stat i32, value i32}], sub stats [...], sub skills [{skill i32, level u8}],
//     cooltime end i64}, then the bag and CreateCoolTime_NT {end i64}. ArcanaCreate.dat: the card (ArcanaName), the material
//     (MaterialCount + MaterialAddCountMainStat/SubStat per stat picked) and the Kina (CostCurrencyValue + the per-stat adds, in
//     any Kina: GlobalSetting arcana_create_cost_currency GoldCombined); at most MainStatSelectCountMax / SubStatSelectCountMax
//     picks; one creation per arcana_create_cooltime (1440 min), kept in the setting "arcana_create". The card's stats are its
//     Item.dat MainStats; the picked stats are not applied to the item (no rolled-stat model for cards yet: CANDIDATE).
//   Enchant_RQ {card uid i64, cards fed [i64], materials map {uid i64, count i32}} -> Enchant_RS {result, uid, cards, materials,
//     level u8, refund i64}. ArcanaEnchant.dat: a fed card is worth its own group's GetExp at its level, a material item
//     (arcana_enchant_material_arcana, the Arcana_Material_* groups) its group's GetExp per unit; levels rise while the exp reaches
//     NeedExp, each level paying its CostCurrencyValue in Kina (GoldCombined). The level is upgrades.enchant, the exp upgrades.arcana_exp.
//   QuickEquip_RQ {list [{item key i64, EEquipSlot u8}]} -> RS {result, list}: each card into its slot (41-50) as the equip request.
//   Extract_RQ: refused (Arcana_Extract_InvalidData): the extract points per card are in no table we have. BLOCKED.
public sealed partial class GameSession
{
    public const ushort ArcanaEnchantRq = 0x564f, ArcanaEnchantRs = 0x5650, ArcanaExtractRq = 0x5651, ArcanaExtractRs = 0x5652,
        ArcanaCreateRq = 0x5654, ArcanaCreateRs = 0x5655, ArcanaCreateCoolTimeNt = 0x5656, ArcanaQuickEquipRq = 0x5657, ArcanaQuickEquipRs = 0x5658;
    const byte IncreaseCreateArcana = 69, DecreaseCreateArcana = 83, DecreaseEnchantArcana = 81;
    public const int ArcanaSlotFirst = 41, ArcanaSlotLast = 50;

    static Systems3Tables Systems3 => Systems3Tables.Embedded;
    long arcanaCreateUntil = -1;

    async Task<List<byte[]>?> ArcanaRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var opcode = Opcode(plain);
        if (opcode is not (ArcanaEnchantRq or ArcanaExtractRq or ArcanaCreateRq or ArcanaQuickEquipRq)) return null;
        try
        {
            return opcode switch
            {
                ArcanaCreateRq => await ArcanaCreateAsync(plain, now, ct),
                ArcanaEnchantRq => await ArcanaEnchantAsync(plain, ct),
                ArcanaQuickEquipRq => await ArcanaQuickEquipAsync(plain, ct),
                _ => [ArcanaExtractRefused(plain)],
            };
        }
        catch (ProtocolException) { return []; }
    }

    static List<int> ReadI32s(PacketReader r)
    {
        var n = r.Varint();
        if (n > 64) throw new ProtocolException("Too many entries");
        var list = new List<int>();
        for (var i = 0; i < n; i++) list.Add(r.I32());
        return list;
    }

    static PacketWriter I32s(PacketWriter w, IReadOnlyList<int> list)
    {
        w.Varint(list.Count);
        foreach (var v in list) w.I32(v);
        return w;
    }

    static byte[] ArcanaExtractRefused(byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var type = r.Remaining > 0 ? r.U8() : (byte)0;
        return PacketWriter.Opcode(ArcanaExtractRs).U16(EResult.Arcana_Extract_InvalidData).U8(type).Varint(0).Varint(0).I32(0).ToArray();
    }

    async Task<long> ArcanaCreateUntilAsync(CancellationToken ct)
    {
        if (arcanaCreateUntil >= 0) return arcanaCreateUntil;
        arcanaCreateUntil = 0;
        if (Store is { } store && CharacterId is { } character)
        {
            var settings = await StoredValue(() => store.CharacterSettingsAsync(character, ct), (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>());
            if (settings.TryGetValue("arcana_create", out var saved) && saved.ValueKind == JsonValueKind.Array && saved.GetArrayLength() > 0 && saved[0].TryGetInt64(out var until))
                arcanaCreateUntil = until;
        }
        return arcanaCreateUntil;
    }

    async Task<List<byte[]>> ArcanaCreateAsync(byte[] plain, long now, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var createId = r.I32();
        var mains = ReadI32s(r);
        var subs = ReadI32s(r);
        var skills = ReadI32s(r);
        var until = await ArcanaCreateUntilAsync(ct);
        byte[] Reply(int result, long uid, Systems3Tables.ArcanaItem? card, long end)
        {
            var w = PacketWriter.Opcode(ArcanaCreateRs).U16(result).I64(uid).I32(createId);
            I32s(I32s(I32s(w, mains), subs), skills);
            var stats = card?.MainStats ?? [];
            w.Varint(stats.Count);
            foreach (var (stat, value) in stats) w.I32(StatIds.Of(stat) ?? 0).I32(value);
            return w.Varint(0).Varint(0).I64(end).ToArray();
        }
        if (Systems3.CreateOf(createId) is not { } row || Systems3.ArcanaOf(row.Item) is not { } card) return [Reply(EResult.Arcana_Create_InvalidData, 0, null, until)];
        if (mains.Count > row.MainMax || subs.Count > row.SubMax || mains.Distinct().Count() != mains.Count || subs.Distinct().Count() != subs.Count)
            return [Reply(EResult.Arcana_Create_DuplicatedStatType, 0, null, until)];
        if (until > now) return [Reply(EResult.Arcana_Create_CoolTime, 0, null, until)];
        if (Store is not { } store || CharacterId is not { } character) return [Reply(EResult.Arcana_Create_IternalError, 0, null, until)];
        var material = row.MaterialCount + row.MaterialPerMain * mains.Count + row.MaterialPerSub * subs.Count;
        var kina = row.Kina + row.KinaPerMain * mains.Count + row.KinaPerSub * subs.Count;
        var costs = new List<(Guid, long, int)>();
        if (material > 0) { if (CostStacks(row.Material, material) is { } m) costs.AddRange(m); else return [Reply(EResult.Arcana_Create_NotEnoughCurrency, 0, null, until)]; }
        if (kina > 0) { if (CostStacks(KinaItemId, kina) is { } k) costs.AddRange(k); else return [Reply(EResult.Arcana_Create_NotEnoughCurrency, 0, null, until)]; }
        if (!BagRoomFor([(row.Item, 1)], SlotsFreedBy(costs))) return [Reply(EResult.Arcana_Create_NotEnoughInventory, 0, null, until)];   // GameSession.BagRoom.cs
        var before = storedItems.Values.Where(i => i.TemplateId == row.Item).Select(i => i.InstanceId).ToHashSet();
        var made = await Stored(async () => await store.ShopBuyAsync(character, costs, [(row.Item, 1)], [], ct) ? [[]] : []);
        if (made.Count == 0) return [Reply(EResult.Arcana_Create_IternalError, 0, null, until)];
        var end = now + Systems3.CreateCooltimeMinutes * 60_000L;
        arcanaCreateUntil = end;
        await SaveSettingAsync("arcana_create", new[] { end }, ct);
        var changes = await InventoryChangesAsync(ct, IncreaseCreateArcana, DecreaseCreateArcana);
        var uid = storedItems.Values.FirstOrDefault(i => i.TemplateId == row.Item && !before.Contains(i.InstanceId))?.WireId ?? 0;
        Note($"arcana: created {row.Item} (creation {createId}, {mains.Count} main / {subs.Count} sub picks, {material} material, {kina} Kina)");
        return [Reply(0, uid, card, end), .. changes, PacketWriter.Opcode(ArcanaCreateCoolTimeNt).I64(end).ToArray()];
    }

    static int ArcanaExp(ItemState item) => EnchantOf(item, "arcana_exp");

    async Task<List<byte[]>> ArcanaEnchantAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var target = r.I64();
        var n = r.Varint();
        if (n > 64) return [];
        var fed = new List<long>();
        for (var i = 0; i < n; i++) fed.Add(r.I64());
        var m = r.Varint();
        if (m > 64) return [];
        var materials = new List<(long Key, int Count)>();
        for (var i = 0; i < m; i++) materials.Add((r.I64(), r.I32()));
        byte[] Reply(int result, int level)
        {
            var w = PacketWriter.Opcode(ArcanaEnchantRs).U16(result).I64(target).Varint(fed.Count);
            foreach (var k in fed) w.I64(k);
            w.Varint(materials.Count);
            foreach (var (k, c) in materials) w.I64(k).I32(c);
            return w.U8(level).I64(0).ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(target, out var row) || row.Location is not ("bag" or "equipment")
            || Systems3.ArcanaOf(row.TemplateId) is not { } card) return [Reply(EResult.Arcana_Enchant_InvalidData, 0)];
        var level = EnchantOf(row);
        var exp = (long)ArcanaExp(row);
        var costs = new List<(Guid, long, int)>();
        foreach (var key in fed.Distinct())
        {
            if (key == target || !storedItems.TryGetValue(key, out var food) || food.Location != "bag" || Systems3.ArcanaOf(food.TemplateId) is not { } foodCard)
                return [Reply(EResult.Arcana_Enchant_InvalidMaterial, level)];
            exp += Systems3.Step(foodCard.EnchantGroup, EnchantOf(food))?.GetExp ?? 0;
            costs.Add((food.InstanceId, food.Revision, (int)food.Quantity));
        }
        foreach (var (key, count) in materials)
        {
            if (count < 1 || !storedItems.TryGetValue(key, out var stack) || stack.Location != "bag" || stack.Quantity < count
                || ItemDB.Embedded.Of(stack.TemplateId) is not { } item || Systems3.Step(item.InternalName, 0) is not { } worth)
                return [Reply(EResult.Arcana_Enchant_InvalidMaterial, level)];
            exp += (long)worth.GetExp * count;
            costs.Add((stack.InstanceId, stack.Revision, count));
        }
        if (costs.Count == 0) return [Reply(EResult.Arcana_Enchant_InvalidMaterial, level)];
        var after = level;
        long kina = 0;
        while (Systems3.Step(card.EnchantGroup, after) is { NeedExp: > 0 } step && exp >= step.NeedExp && after < Systems3.MaxLevel(card.EnchantGroup))
        {
            exp -= step.NeedExp;
            kina += step.Kina;
            after++;
        }
        if (kina > 0) { if (CostStacks(KinaItemId, kina, row.InstanceId) is { } k) costs.AddRange(k); else return [Reply(EResult.Arcana_Enchant_NotEnoughCost, level)]; }
        var done = await Stored(async () => await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after, EnchantOf(row, "enchant_bonus"), ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(EResult.Arcana_Enchant_InternalError, level)];
        await Stored(async () => { await store.SetItemUpgradeAsync(character, row.InstanceId, "arcana_exp", exp.ToString(System.Globalization.CultureInfo.InvariantCulture), ct); return []; });
        Note($"arcana: {row.TemplateId} enchant {level} -> {after} (exp {exp} left, {kina} Kina)");
        var replies = new List<byte[]> { Reply(0, after) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseEnchantArcana));
        if (row.Location == "equipment" && after != level) replies.AddRange(RefreshStats());
        return replies;
    }

    async Task<List<byte[]>> ArcanaQuickEquipAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var n = r.Varint();
        if (n > 10) return [];
        var list = new List<(long Key, byte Slot)>();
        for (var i = 0; i < n; i++) list.Add((r.I64(), r.U8()));
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(ArcanaQuickEquipRs).U16(result).Varint(list.Count);
            foreach (var (k, s) in list) w.I64(k).U8(s);
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || list.Count == 0) return [Reply(EResult.Arcana_QuickEquip_InvalidData)];
        if (list.Select(e => e.Slot).Distinct().Count() != list.Count || list.Select(e => e.Key).Distinct().Count() != list.Count)
            return [Reply(EResult.Arcana_QuickEquip_DuplicatedEquipSlot)];
        foreach (var (key, slot) in list)
            if (slot is < ArcanaSlotFirst or > ArcanaSlotLast || !storedItems.TryGetValue(key, out var item) || Systems3.ArcanaOf(item.TemplateId) is null || EquipRefused(item.TemplateId, lastNow))
                return [Reply(EResult.Arcana_QuickEquip_InvalidData)];
        var changes = new List<byte[]>();
        foreach (var (key, slot) in list)
        {
            var item = storedItems[key];
            if (item.Location == "equipment" && item.Slot == slot) continue;
            // The card already in that slot goes back to the bag first.
            if (storedItems.Values.FirstOrDefault(i => i.Location == "equipment" && i.Slot == slot) is { } worn)
            {
                await Stored(async () => await store.MoveItemAsync(character, worn.InstanceId, worn.Revision, null, ct) ? [[]] : []);
                changes.AddRange(await InventoryChangesAsync(ct));
            }
            var fresh = storedItems.Values.First(i => i.InstanceId == item.InstanceId);
            var moved = await Stored(async () => await store.MoveItemAsync(character, fresh.InstanceId, fresh.Revision, slot, ct) ? [[]] : []);
            changes.AddRange(await InventoryChangesAsync(ct));
            if (moved.Count == 0) return [Reply(EResult.Arcana_QuickEquip_InvalidData), .. changes];
        }
        Note($"arcana: quick equip {string.Join(", ", list.Select(e => $"{e.Key}@{e.Slot}"))}");
        return [Reply(0), .. changes, .. RefreshStats()];
    }
}
