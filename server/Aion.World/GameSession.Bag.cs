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
        QuickSlotSetRq = 0x5601, QuickSlotSetRs = 0x5602, QuickSlotResetRq = 0x5603, QuickSlotResetRs = 0x5604, QuickSlotChangedNt = 0x5605,
        // QuickSlotContextSkillReset_RQ/RS: dev 0x3750/0x3751 + the run's offset (QuickSlotList_NT dev 0x3747 = live 0x5600).
        QuickSlotContextSkillResetRq = 0x5609, QuickSlotContextSkillResetRs = 0x560a,
        // ItemStore/ItemTakeOut RQ/RS: catalogue run + 5 above 0x5610, as ItemMove 0x563a and ItemDecompose 0x5640 (live 2026-11-09: the client's
        // 0x5642 store requests went unanswered).
        ItemStoreRq = 0x5642, ItemStoreRs = 0x5643, ItemTakeOutRq = 0x5644, ItemTakeOutRs = 0x5645;
    const byte DecreaseItemRemove = 13;   // EDecreaseReason kItemRemove
    readonly SortedDictionary<int, QuickSlotRecord> quickSlots = [];
    public IReadOnlyDictionary<int, QuickSlotRecord> QuickSlots => quickSlots;

    async Task LoadQuickSlotsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        quickSlots.Clear();
        // Item slots by template only (uid 0, as official): rows saved before fix/quickbar may still carry a stack's key.
        foreach (var slot in await store.QuickSlotsAsync(character, ct)) quickSlots[slot.Slot] = slot.Type == QuickSlotTables.Item ? slot with { ItemUid = 0 } : slot;
    }

    /// <summary>The bar at world entry (0x5600): the class's default bar with the player's saved slots over it, empty slots left out.</summary>
    byte[] Hotbar(IReadOnlyList<(int Slot, int SkillId)> defaults) => GamePackets.QuickSlots(SkillBar(defaults).Bar.Values);

    /// <summary>The bar (the class default with the saved slots over it), each record with its context skills, and those lists by slot.</summary>
    (SortedDictionary<int, QuickSlotRecord> Bar, Dictionary<int, IReadOnlyList<int>> Context) SkillBar(IReadOnlyList<(int Slot, int SkillId)> defaults)
    {
        var bar = ComposeBar(className, defaults, quickSlots.Values);
        var context = bar.Values.Where(r => r.ContextSkills is { Count: > 0 }).ToDictionary(r => r.Slot, r => r.ContextSkills!);
        return (bar, context);
    }

    /// <summary>
    /// A character's bar: the class's default skills (PCContextSkillSlot, all 8 - those not learned yet too, the client draws them
    /// locked) with their default context lists (unlearned ones too: OBSERVED level-1 Sorcerer Q/E [15010000, 15050000, 15310000]
    /// with neither of the last two in its skill list), the starting items (PCLevelQuickSlot: slots 10-12 and 14-16, OBSERVED on
    /// every official level-1 bar), then the saved slots over them (an empty saved slot keeps a default off).
    /// </summary>
    public static SortedDictionary<int, QuickSlotRecord> ComposeBar(string? className, IReadOnlyList<(int Slot, int SkillId)> defaults, IEnumerable<QuickSlotRecord> saved)
    {
        var bar = new SortedDictionary<int, QuickSlotRecord>();
        foreach (var (slot, skill) in defaults)
            bar[slot] = new(slot, QuickSlotTables.Skill, 0, 0, skill, QuickSlotTables.DefaultContext(className, slot, skill) is { Count: > 0 } offered ? offered : null);
        if (defaults.Count > 0)
            foreach (var (slot, item) in QuickSlotTables.DefaultItems) bar[slot] = new(slot, QuickSlotTables.Item, 0, item, 0);
        foreach (var s in saved) bar[s.Slot] = s;
        return bar;
    }

    /// <summary>Whether a slot has a class default (a default skill or a starting item) that an emptied slot must keep off.</summary>
    bool HasDefault(int slot) => classBar.Any(d => d.Slot == slot) || classBar.Count > 0 && QuickSlotTables.DefaultItems.ContainsKey(slot);

    /// <summary>
    /// 0x563a ItemMove_RQ {npc key i32, EItemContainerType u8, item key i64, slot pos i32}: a bag or warehouse item to a slot
    /// of the bag (1), the account warehouse (2 kStorage, location 'storage') or the character warehouse (15 kCharacterStorage,
    /// location 'character_storage'); dragging between the warehouse tabs is a move too. The item already on the slot takes the
    /// moved item's old place. Answered 0x563b with 0 (Success: no official ItemMove_RS is recorded, and 0 works live) or
    /// Item_Move_Fail 561. Equipped items go through ItemEquip.
    /// </summary>
    async Task<List<byte[]>> MoveItemInBagAsync(byte[] plain, CancellationToken ct)
    {
        int npc = BitConverter.ToInt32(plain, 2), slot = BitConverter.ToInt32(plain, 15);
        byte container = plain[6];
        var key = BitConverter.ToInt64(plain, 7);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemMoveRs).U16(result).I32(npc).U8(container).I64(key).I32(slot).ToArray();
        var target = LocationOf(container);
        if (target is null || Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row)
            || row.Location is not ("bag" or "storage" or "character_storage") || slot < 0
            || container != InventoryItem.Bag && slot >= SlotCapacity(container))   // a warehouse slot past its lines (the bag is not limited yet)
            return [Reply(EResult.Item_Move_Fail)];
        // Into another container: the item must be allowed there, and so must the item it swaps with on the way back.
        if (target != row.Location)
        {
            if (!AllowedIn(row.TemplateId, target)) return [Reply(EResult.Item_Move_Fail)];
            if (storedItems.Values.FirstOrDefault(i => i.Location == target && i.Slot == slot) is { } swapped && !AllowedIn(swapped.TemplateId, row.Location))
                return [Reply(EResult.Item_Move_Fail)];
        }
        var moved = await Stored(async () => (target == "bag" && row.Location == "bag"
            ? await store.MoveBagItemAsync(character, row.InstanceId, row.Revision, slot, ct)
            : await store.MoveItemToContainerAsync(character, row.InstanceId, row.Revision, target, slot, ct)) ? [[]] : []);
        if (moved.Count == 0) return [Reply(EResult.Item_Move_Fail)];
        return [Reply(EResult.Success), .. await InventoryChangesAsync(ct, 0, 0), .. target != "bag" && row.Location == "bag" ? Achieve("StorageStore") : []];
    }

    /// <summary>Whether an item may lie in a location (ItemRules.AllowedIn: Item.dat CanStorage / CanCharacterStorage / CanGuildStorage).</summary>
    static bool AllowedIn(int templateId, string location) => ItemRules.AllowedIn(templateId, location);

    /// <summary>
    /// 0x563c ItemRemove_RQ {map item key -> count (bag), map (temporary bag)}: discarded, answered with the same maps and the
    /// specific EResult (768 Item_Remove_Success, as the other official replies carry their own success code; 770 more than
    /// the stack; 771 an item the client table marks undeletable, CanDelete 0 in gamedata/items; 769 otherwise).
    /// </summary>
    async Task<List<byte[]>> RemoveItemsAsync(byte[] plain, CancellationToken ct)
    {
        var bag = new List<(long Key, long Count)>();
        var temporary = new List<(long Key, long Count)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            foreach (var list in new[] { bag, temporary })
            {
                // Every entry is read (live 2026-11-07: a 186-item discard; the old cap of 128 left the rest unread, the next count
                // was garbage and the request went unanswered). A bag holds up to a few thousand stacks.
                var count = reader.Varint();
                if (count > 5000) return [];
                for (var i = 0; i < count; i++) list.Add(((long)reader.U64(), (long)reader.U64()));
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            // kItem_Remove_Success 0x0300 (OBSERVED 2026-11-06 234912 i27396) / _CommonFail 0x0301 (usmap).
            var w = PacketWriter.Opcode(ItemRemoveRs).U16(result switch { 0 => 0x0300, 1 => 0x0301, _ => result });
            foreach (var list in new[] { bag, temporary })
            {
                w.Varint(list.Count);
                foreach (var (key, count) in list) w.I64(key).I64(count);
            }
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || temporary.Count > 0 || bag.Count == 0) return [Reply(EResult.Item_Remove_CommonFail)];
        // All checked first: one bad entry refuses the whole request and nothing is removed.
        foreach (var (key, count) in bag)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1) return [Reply(EResult.Item_Remove_CommonFail)];
            if (count > row.Quantity) return [Reply(EResult.Item_Remove_NotEnoughCount)];
            if (ItemDB.Embedded.Of(row.TemplateId) is { CanDelete: false }) return [Reply(EResult.Item_Remove_NotRemoval)];
        }
        foreach (var (key, count) in bag)
        {
            var row = storedItems[key];
            var removed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, (int)count, ct) ? [[]] : []);
            if (removed.Count == 0) return [Reply(EResult.Item_Remove_CommonFail), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
        }
        return [Reply(EResult.Item_Remove_Success), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
    }

    /// <summary>
    /// 0x5601 QuickSlotSet_RQ {QuickSlotInfo: mask u8 (bit 0: context skills), slot u8, EQuickSlotType u8, item uid i64, item id i32,
    /// skill id i32, [context skills u8 count + i32], macro u8, emote i32, toggle u8}: checked against the client's QuickSlotData
    /// (skill slots 0-7 and 20-23 take skills and stigmas, item slots 10-17 items), stored whole (the toggle and the rest were
    /// dropped before) and answered 0x5602 {result u16, the slot as stored}. An item slot names the item by its template, uid 0
    /// (OBSERVED: every official item slot, 0x5600 and 0x5602 #11024 `00 0e 01 0000000000000000 2455a71f ...`), so the slot follows
    /// the item through stacks used up, merged, split, moved or refilled and across relogs.
    /// </summary>
    async Task<List<byte[]>> SetQuickSlotAsync(byte[] plain, CancellationToken ct)
    {
        int number, type, itemId, skillId, macro, social;
        bool toggle;
        var contextSkills = new List<int>();
        try
        {
            // QuickSlotInfo starts with its presence mask (live 2026-10-16 `00 0e 01 ...` for a potion on slot 14; the reader
            // took the mask for the slot and the slot for the type, so every item drag was refused).
            var reader = new PacketReader(plain) { Offset = 2 };
            var mask = reader.U8();
            number = reader.U8(); type = reader.U8(); reader.U64(); itemId = (int)reader.U32(); skillId = (int)reader.U32();
            // Bit 0: the context skills (the Q/E slots' offered skills), u8 count + i32 each.
            if ((mask & 1) != 0)
            {
                var count = reader.U8();
                for (var i = 0; i < count && i < 16; i++) { var id = (int)reader.U32(); if (id != 0) contextSkills.Add(id); }
            }
            macro = reader.U8(); social = (int)reader.U32(); toggle = reader.U8() != 0;
        }
        catch (ProtocolException) { return []; }
        byte[] Refuse(int result) => PacketWriter.Opcode(QuickSlotSetRs).U16(result).Raw(plain.AsSpan(2)).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Refuse(1)];
        if (!QuickSlotTables.IsSlot(number)) return [Refuse(EResult.QuickSlot_InvalidSlotID)];
        if (!QuickSlotTables.Accepts(number, type)) return [Refuse(EResult.QuickSlot_MismatchSlotType)];
        if (type == QuickSlotTables.Item && itemId <= 0) return [Refuse(EResult.QuickSlot_InvalidItemID)];
        if (type != QuickSlotTables.Item && skillId <= 0) return [Refuse(EResult.QuickSlot_InvalidSkillID)];
        if (StigmaSlotResult(number, type, skillId) is > 0) return [Refuse(1)];   // stigma slots 20-23 (GameSession.Stigmas.cs)
        var slot = type == QuickSlotTables.Item
            ? new QuickSlotRecord(number, type, 0, itemId, 0, null, macro, social, toggle)
            : new QuickSlotRecord(number, type, 0, 0, skillId, contextSkills.Count > 0 ? contextSkills : null, macro, social, toggle);
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, slot, ct) ? [[]] : []);
        if (saved.Count == 0) return [Refuse(1)];
        quickSlots[number] = slot;
        await SetQuickSlotAutoUseAsync(number, toggle && type == QuickSlotTables.Item && itemId != 0, ct);   // GameSession.QuickbarAutoUse (GitHub)
        return [GamePackets.WriteQuickSlot(PacketWriter.Opcode(QuickSlotSetRs).U16(0), slot).ToArray()];
    }

    /// <summary>A skill slot's context list as the bar shows it (the slot's skill first), four entries.</summary>
    static List<int> ContextList(SortedDictionary<int, QuickSlotRecord> bar, int slot)
    {
        var record = bar.GetValueOrDefault(slot);
        var own = record is { Type: QuickSlotTables.Skill } ? record.SkillId : 0;
        var list = record?.ContextSkills is { Count: > 0 } offered ? offered.ToList() : own != 0 ? [own] : new List<int>();
        while (list.Count < 4) list.Add(0);
        return list.Take(4).ToList();
    }

    /// <summary>A context list as stored: the own skill removed, the next one takes its place.</summary>
    static List<int> Compact(List<int> list)
    {
        if (list[0] == 0) list = [.. list.Where(id => id != 0)];
        while (list.Count < 4) list.Add(0);
        return list;
    }

    /// <summary>
    /// 0x5607 QuickSlotContextSkillUpdate_RQ {slot u8, context slot u8, skill i32}: the skill bar's slots hold a skill and up to four
    /// context skills (index 0 the slot's own skill). The change is stored (a removed default skill stays removed: an empty slot is
    /// saved over the class bar), answered 0x5608 {result, slot, context slot, skill, is_swap, swap slot, swap context slot, swap
    /// skill} and followed by QuickSlotChanged_NT 0x5605 with the changed slots so the bar redraws. A skill already elsewhere on the
    /// bar swaps places with the one it replaces (the reply's swap fields, usmap; CANDIDATE: no official reply is recorded) - before,
    /// it was copied (live 2026-11-11: 15310000 put on slot 3 stayed in slot 7's list too). Refusals use the client's QuickSlot results.
    /// </summary>
    internal async Task<List<byte[]>> SetContextSkillAsync(byte[] plain, CancellationToken ct)
    {
        int number = plain[2], index = plain[3], skill = BitConverter.ToInt32(plain, 4);
        // OBSERVED layout (GitHub 6714299, 6 official replies 2026-11-05/06): presence mask u8, result u16, slot, context slot, skill
        // i32, then the fields whose bit is set: bit 0 is_swap u8, bit 1 swap slot u8, bit 2 swap context slot u8 (swap skill: none).
        byte[] Reply(int result, bool swap = false, int swapSlot = 0, int swapIndex = 0, int swapSkill = 0)
        {
            var w = PacketWriter.Opcode(QuickSlotContextSkillRs).U8(swap ? swapIndex != 0 ? 7 : 3 : 0).U16(result).U8((byte)number).U8((byte)index).I32(skill);
            if (swap) { w.U8(1).U8((byte)swapSlot); if (swapIndex != 0) w.U8((byte)swapIndex); }
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        if (!QuickSlotTables.IsSkillSlot(number)) return [Reply(EResult.QuickSlot_InvalidSlotID)];
        if (!QuickSlotTables.IsEditableSkillSlot(number)) return [Reply(EResult.QuickSlot_NotEditableSlot)];
        if (index is < 0 or > 3) return [Reply(EResult.QuickSlot_InvalidContextSkillSlotID)];
        if (skill != 0 && !learned.Contains(skill) && !stigmas.ContainsKey(skill)) return [Reply(EResult.QuickSlot_InvalidSkillID)];
        if (skill != 0 && StigmaSlotResult(number, 0, skill, moving: quickSlots.Values.Any(q => q.Slot != number && q.SkillId == skill)) is > 0)
            return [Reply(1)];   // stigma slots 20-23 (GameSession.Stigmas.cs)
        var (bar, _) = SkillBar(classBar);
        var lists = new Dictionary<int, List<int>> { [number] = ContextList(bar, number) };
        var old = lists[number][index];
        // The same skill elsewhere on the editable skill slots: it takes the replaced skill's place.
        (int Slot, int Index)? from = null;
        if (skill != 0 && skill != old)
            foreach (var s in bar.Keys.Where(s => QuickSlotTables.IsSkillSlot(s) && QuickSlotTables.IsEditableSkillSlot(s)))
            {
                var list = s == number ? lists[number] : ContextList(bar, s);
                var at = Enumerable.Range(0, 4).Where(i => (s, i) != (number, index) && list[i] == skill).Select(i => (int?)i).FirstOrDefault();
                if (at is { } i) { lists[s] = list; from = (s, i); break; }
            }
        lists[number][index] = skill;
        if (from is { } f) lists[f.Slot][f.Index] = old;
        var changed = new List<QuickSlotRecord>();
        foreach (var (s, list) in lists.OrderBy(kv => kv.Key))
        {
            var compact = Compact(list);
            var main = compact[0];
            var record = (quickSlots.GetValueOrDefault(s) is { Type: QuickSlotTables.Skill } prev ? prev : new QuickSlotRecord(s, QuickSlotTables.Skill, 0, 0, 0))
                with { SkillId = main, ContextSkills = main != 0 ? compact : null };
            // An emptied slot without a class default (the stigma slots 20-23) goes; one with a default keeps its empty record.
            var stored = main == 0 && !HasDefault(s) ? null : record;
            var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, s, stored, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
            if (stored is null) quickSlots.Remove(s); else quickSlots[s] = record;
            changed.Add(record);
        }
        var nt = PacketWriter.Opcode(QuickSlotChangedNt).Varint(changed.Count);
        foreach (var record in changed) GamePackets.WriteQuickSlot(nt, record);
        return [from is { } w ? Reply(0, true, w.Slot, w.Index, old) : Reply(0), nt.ToArray()];
    }

    /// <summary>
    /// 0x5609 QuickSlotContextSkillReset_RQ (no body): the skill slots 0-7 back to the class's default skills and context lists (the
    /// saved skill slots dropped), answered 0x560a {result u16} and QuickSlotChanged_NT with those slots. CANDIDATE: usmap layouts,
    /// no official sample; before, the request went unanswered.
    /// </summary>
    internal async Task<List<byte[]>> ResetContextSkillsAsync(CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotContextSkillResetRs).U16(result).ToArray();
        if (Store is not { } store || CharacterId is not { } character || classBar.Count == 0) return [Reply(1)];
        foreach (var slot in quickSlots.Keys.Where(s => s is >= 0 and <= 7).ToList())
        {
            var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, slot, null, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
            quickSlots.Remove(slot);
        }
        var (bar, _) = SkillBar(classBar);
        var defaults = bar.Values.Where(r => r.Slot is >= 0 and <= 7 && !r.IsEmpty).ToList();
        var nt = PacketWriter.Opcode(QuickSlotChangedNt).Varint(defaults.Count);
        foreach (var record in defaults) GamePackets.WriteQuickSlot(nt, record);
        return [Reply(0), nt.ToArray()];
    }

    /// <summary>
    /// 0x5603 QuickSlotReset_RQ {slot u8}: the slot emptied, answered 0x5604 {result, slot}. A slot with a class default (a default
    /// skill or a starting item) keeps an empty record, else the default came back at the next login.
    /// </summary>
    async Task<List<byte[]>> ResetQuickSlotAsync(byte number, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotResetRs).U16(result).U8(number).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        if (!QuickSlotTables.IsSlot(number)) return [Reply(EResult.QuickSlot_InvalidSlotID)];
        var empty = HasDefault(number) ? new QuickSlotRecord(number, QuickSlotTables.IsItemSlot(number) ? QuickSlotTables.Item : QuickSlotTables.Skill, 0, 0, 0) : null;
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, empty, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        if (empty is null) quickSlots.Remove(number); else quickSlots[number] = empty;
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
