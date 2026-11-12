using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Bag operations and the hotbar. Opcodes are catalogue index + run base: ItemMove/Remove/Sort 0x563a/0x563c/
// 0x563e (+ 0x54ca, the 0x56 run around the OBSERVED 0x5610/0x5612/0x5646), QuickSlotSet/Reset 0x5601/0x5603
// (+ 0x54c5 below 0x5610: the live client's unanswered 0x5601 is 27 bytes = QuickSlotInfo, OBSERVED; the hotbar
// list 0x5600 is QuickSlotList_NT). Layouts are the usmap structs; result u16 as every observed RS.
public sealed partial class GameSession
{
    public const ushort ItemMoveRq = 0x563a, ItemMoveRs = 0x563b, ItemRemoveRq = 0x563c, ItemRemoveRs = 0x563d, ItemSortRq = 0x563e, ItemSortRs = 0x563f,
        QuickSlotSetRq = 0x5601, QuickSlotSetRs = 0x5602, QuickSlotResetRq = 0x5603, QuickSlotResetRs = 0x5604;
    const byte DecreaseItemRemove = 13;   // EDecreaseReason kItemRemove
    readonly SortedDictionary<int, QuickSlotRecord> quickSlots = [];
    public IReadOnlyDictionary<int, QuickSlotRecord> QuickSlots => quickSlots;

    async Task LoadQuickSlotsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        quickSlots.Clear();
        foreach (var slot in await store.QuickSlotsAsync(character, ct)) quickSlots[slot.Slot] = slot;
    }

    /// <summary>The hotbar at world entry: the player's saved slots, else the class's default bar.</summary>
    /// <summary>The bar at world entry: the class's default bar with the player's saved slots over it.</summary>
    byte[] Hotbar(IEnumerable<(int Slot, int SkillId)> defaults)
    {
        var bar = new SortedDictionary<int, (int Slot, int Type, long ItemUid, int ItemId, int SkillId)>();
        foreach (var (slot, skill) in defaults) bar[slot] = (slot, 0, 0, 0, skill);
        foreach (var s in quickSlots.Values) bar[s.Slot] = (s.Slot, s.Type, s.ItemUid, s.ItemId, s.SkillId);
        return GamePackets.QuickSlots(bar.Values.ToList(), autoUseSlots.Count == 0 ? null : autoUseSlots.Where(quickSlots.ContainsKey).ToHashSet());
    }

    /// <summary>
    /// 0x563a ItemMove_RQ {npc key i32, EItemContainerType u8, item key i64, slot pos i32}: a bag or warehouse item to a
    /// slot of the bag (1) or the warehouse (2, kStorage: the Storage NPCs' depot; kept per character, OURS — the
    /// official one is account-wide). Equipped items go through ItemEquip.
    /// </summary>
    async Task<List<byte[]>> MoveItemInBagAsync(byte[] plain, CancellationToken ct)
    {
        int npc = BitConverter.ToInt32(plain, 2), slot = BitConverter.ToInt32(plain, 15);
        byte container = plain[6];
        var key = BitConverter.ToInt64(plain, 7);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemMoveRs).U16(result).I32(npc).U8(container).I64(key).I32(slot).ToArray();
        var target = container switch { InventoryItem.Bag => "bag", InventoryItem.Storage => "storage", _ => null };
        if (target is null || Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "storage") || slot < 0)
            return [Reply(1)];
        var moved = await Stored(async () => (target == "bag" && row.Location == "bag"
            ? await store.MoveBagItemAsync(character, row.InstanceId, row.Revision, slot, ct)
            : await store.MoveItemToContainerAsync(character, row.InstanceId, row.Revision, target, slot, ct)) ? [[]] : []);
        if (moved.Count == 0) return [Reply(1)];
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, 0), .. target == "storage" && row.Location == "bag" ? Achieve("StorageStore") : []];
    }

    /// <summary>0x563c ItemRemove_RQ {map item key -> count (bag), map (temporary bag)}: discarded, answered with the same maps.</summary>
    async Task<List<byte[]>> RemoveItemsAsync(byte[] plain, CancellationToken ct)
    {
        var bag = new List<(long Key, long Count)>();
        var temporary = new List<(long Key, long Count)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            foreach (var list in new[] { bag, temporary })
            {
                var count = reader.Varint();
                for (var i = 0; i < count && i < 128; i++) list.Add(((long)reader.U64(), (long)reader.U64()));
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(ItemRemoveRs).U16(result);
            foreach (var list in new[] { bag, temporary })
            {
                w.Varint(list.Count);
                foreach (var (key, count) in list) w.I64(key).I64(count);
            }
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || temporary.Count > 0 || bag.Count == 0) return [Reply(1)];
        foreach (var (key, count) in bag)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity) return [Reply(1)];
            var removed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, (int)count, ct) ? [[]] : []);
            if (removed.Count == 0) return [Reply(1), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
        }
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
    }

    /// <summary>0x563e ItemSort_RQ {npc key i32, EItemContainerType u8}: the bag ordered by item, answered 0x563f.</summary>
    async Task<List<byte[]>> SortBagAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(ItemSortRs).U16(result).I32(npc).U8(container).ToArray();
        if (container != 1 || Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var sorted = await Stored(async () => await store.SortBagAsync(character, ct) ? [[]] : []);
        return sorted.Count == 0 ? [Reply(1)] : [Reply(0), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    /// <summary>
    /// 0x5601 QuickSlotSet_RQ {QuickSlotInfo: slot u8, EQuickSlotType u8, item uid i64, item id i32, skill id i32,
    /// context skills (varint + i32), macro u8, social action i32, toggle u8}: stored, echoed in 0x5602. The toggle
    /// (usmap _toggle_yn, CANDIDATE: auto-use; 0 in all 42 recorded records) on an item slot is kept with the settings
    /// (quick_slot_auto_use) and drives QuickSlotAutoUseAsync.
    /// </summary>
    async Task<List<byte[]>> SetQuickSlotAsync(byte[] plain, CancellationToken ct)
    {
        int number, type, itemId, skillId;
        long uid;
        var toggle = false;
        try
        {
            // QuickSlotInfo starts with its presence mask (live 2026-10-16 `00 0e 01 ...` for a potion on slot 14; the reader
            // took the mask for the slot and the slot for the type, so every item drag was refused).
            var reader = new PacketReader(plain) { Offset = 2 };
            var mask = reader.U8();
            number = reader.U8(); type = reader.U8(); uid = (long)reader.U64(); itemId = (int)reader.U32(); skillId = (int)reader.U32();
            try
            {
                if ((mask & 1) != 0)
                {
                    var context = reader.Varint();
                    for (var i = 0; i < context && i < 64; i++) reader.U32();
                }
                reader.U8(); reader.U32();
                toggle = reader.U8() != 0;
            }
            catch (ProtocolException) { toggle = false; }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotSetRs).U16(result).Raw(plain.AsSpan(2)).ToArray();
        if (Store is not { } store || CharacterId is not { } character || type is < 0 or > 4) return [Reply(1)];
        if (StigmaSlotResult(number, type, skillId) is > 0) return [Reply(1)];   // stigma slots 21-24 (GameSession.Stigmas.cs)
        var slot = new QuickSlotRecord(number, type, uid, itemId, skillId);
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, slot, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        quickSlots[number] = slot;
        await SetQuickSlotAutoUseAsync(number, toggle && type == QuickSlotItem && itemId != 0, ct);
        return [Reply(0)];
    }

    /// <summary>
    /// 0x5607 QuickSlotContextSkillUpdate_RQ {slot u8, context slot u8, skill i32}: how the client puts a skill or a stigma on
    /// the bar, moves it, swaps two and clears one (skill 0). OBSERVED in the community recordings of 2026-11-05/06 (c2s of 8
    /// bytes; 0x5601 never carries a skill there). Reply 0x5608 (OBSERVED, 6 instances): presence mask u8, result u16, slot,
    /// context slot, skill i32, then the fields whose bit is set: bit 0 is_swap u8, bit 1 swap slot u8, bit 2 swap context
    /// slot u8. Context slot 0 is the slot's own skill: stored, and a skill already on another slot of the same group (skill
    /// slots or stigma slots 20-23) trades places with this slot's skill (mask 3, as #84812). Context slots 1-3 (the skills
    /// shown with it) are accepted, not stored (CANDIDATE). The official PresetSkillUpdate_NT 0xe293 sent before the reply is
    /// not sent (layout not decoded). Refusals answer result 1 (CANDIDATE: no refusal recorded).
    /// </summary>
    async Task<List<byte[]>> SetQuickSlotSkillAsync(byte[] plain, CancellationToken ct)
    {
        int number = plain[2], position = plain[3], skillId = BitConverter.ToInt32(plain, 4);
        byte[] Reply(int result, int swapSlot = -1)
        {
            var w = PacketWriter.Opcode(QuickSlotContextSkillRs).U8(swapSlot >= 0 ? 3 : 0).U16(result).U8(number).U8(position).I32(skillId);
            if (swapSlot >= 0) w.U8(1).U8(swapSlot);
            return w.ToArray();
        }
        if (position != 0) return [Reply(0)];
        if (Store is not { } store || CharacterId is not { } character || skillId < 0) return [Reply(1)];
        var stigmaSlot = StigmaData.IsStigmaSlot(number);
        var other = skillId == 0 ? null
            : quickSlots.Values.FirstOrDefault(s => s.Slot != number && s.Type == 0 && s.SkillId == skillId && StigmaData.IsStigmaSlot(s.Slot) == stigmaSlot);
        if (skillId != 0 && StigmaSlotResult(number, 0, skillId, moving: other is not null) is > 0) return [Reply(1)];
        var previous = quickSlots.GetValueOrDefault(number) is { Type: 0, SkillId: > 0 } p ? p.SkillId : 0;
        var changes = new List<(int Slot, int Skill)> { (number, skillId) };
        if (other is not null) changes.Add((other.Slot, previous));
        foreach (var (slot, skill) in changes)
        {
            var record = skill == 0 ? null : new QuickSlotRecord(slot, 0, 0, 0, skill);
            var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, slot, record, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
            if (record is null) quickSlots.Remove(slot);
            else quickSlots[slot] = record;
        }
        return [Reply(0, other?.Slot ?? -1)];
    }

    /// <summary>0x5603 QuickSlotReset_RQ {slot u8}: cleared, answered 0x5604 {result, slot}.</summary>
    async Task<List<byte[]>> ResetQuickSlotAsync(byte number, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotResetRs).U16(result).U8(number).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, null, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        quickSlots.Remove(number);
        await SetQuickSlotAutoUseAsync(number, false, ct);
        return [Reply(0)];
    }

    // ---------- Quickbar auto-use (OURS trigger) ----------

    const int QuickSlotItem = 1;   // EQuickSlotType item (OBSERVED live 2026-10-16: a potion slot)
    /// <summary>Hotbar slots whose auto-use toggle is on (character setting quick_slot_auto_use).</summary>
    readonly SortedSet<int> autoUseSlots = [];
    /// <summary>Item skills whose buff ran out on the last buff tick (their auto-use items are used again).</summary>
    readonly HashSet<int> expiredItemSkills = [];
    public IReadOnlyCollection<int> AutoUseSlots => autoUseSlots;

    async Task SetQuickSlotAutoUseAsync(int number, bool on, CancellationToken ct)
    {
        if (on ? autoUseSlots.Add(number) : autoUseSlots.Remove(number)) await SaveSettingAsync("quick_slot_auto_use", autoUseSlots.ToArray(), ct);
    }

    /// <summary>
    /// OURS: when the buff of an item on a toggled item slot runs out (expiry, not replacement or death), the server uses
    /// that item again from the bag (its smallest stack) through the ordinary item use, with all its checks (alive, cooldown,
    /// the store's revision check). Nothing happens while the buff lasts or for items without a buff.
    /// </summary>
    async Task<List<byte[]>> QuickSlotAutoUseAsync(long now, CancellationToken ct)
    {
        if (expiredItemSkills.Count == 0) return [];
        var skills = expiredItemSkills.ToHashSet();
        expiredItemSkills.Clear();
        if (autoUseSlots.Count == 0 || resources is null || dead || Store is null) return [];
        var replies = new List<byte[]>();
        foreach (var itemId in autoUseSlots.Select(n => quickSlots.GetValueOrDefault(n)).Where(s => s is { Type: QuickSlotItem, ItemId: > 0 }).Select(s => s!.ItemId).Distinct())
        {
            if (Boxes.Use(itemId) is not { Effect: "Skill", Values: [var value, ..] } || !int.TryParse(value, out var skill) || !skills.Contains(skill)) continue;
            if (storedItems.Values.Where(i => i.TemplateId == itemId && i.Location == "bag").OrderBy(i => i.Quantity).ThenBy(i => i.WireId).FirstOrDefault() is { } stack)
                replies.AddRange(await UseItemAsync(stack.WireId, now, ct, reply: false));
        }
        return replies;
    }

    /// <summary>Party members on the same map within this distance share a kill's experience (OURS).</summary>
    public const double PartyExpRange = 10_000;

    /// <summary>The killer's party members nearby (not the killer).</summary>
    List<WorldPlayer> NearbyPartyMembers()
    {
        if (worldPlayer?.Map is not { } map || position is not { } at || Social?.PartyOf(worldPlayer) is not { } party) return [];
        return party.Others(worldPlayer).Where(m => m.Map == map && !m.Session.Dead
            && (m.Position.X - at.X) * (m.Position.X - at.X) + (m.Position.Y - at.Y) * (m.Position.Y - at.Y) <= PartyExpRange * PartyExpRange).ToList();
    }

    /// <summary>Another member's share of a kill: applied on that player's own loop (its store, its messages).</summary>
    public void ShareExperience(long gained, CancellationToken ct = default) =>
        _ = InvokeAsync(async now => (true, gained > 0 ? await GainExperienceAsync(experience + gained, gained, ExpReasonNpc, ct) : []));
}
