using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Per-character settings (game.character_settings, JSON by name) and what they drive: earned emotes, the seal stone
// toggle, auto-potion and auto-use items, presets and the item restore counts. Live opcodes = developer opcode + the
// family offset (HANDOFF): PcSocialActionAcquire_NT 0x9500; Activate/DeactivateSealStone 0x8d47-0x8d4a,
// SealStoneStatusChange_NT 0x8d4b, RegisterSealStoneAutoUse 0x8d4c/0x8d4d, BroadcastSealStoneChange_NT 0x8d4e;
// PotionItemAutoUseList_NT 0x5648 (OBSERVED "01 01 b314991f 00000000": group HP, Life Potion, none), SetAutoUsePotionItem
// 0x5649/0x564a, ItemAutoUseActivatedList_NT 0x564b, SetAutoUseItem 0x564c/0x564d; ItemRestoreStatusList_NT 0xe343
// (OBSERVED: 3 types, char db id, count 0, a time); PresetInfo_NT 0xe28c (OBSERVED 904 B), PresetEquipUpdate_NT 0xe28d,
// PresetListSet 0xe291/0xe292. Request layouts are the usmap's (no request sample): CANDIDATE.
public sealed partial class GameSession
{
    public const ushort SocialActionAcquireNt = 0x9500,
        SealStoneActivateRq = 0x8d47, SealStoneActivateRs = 0x8d48, SealStoneDeactivateRq = 0x8d49, SealStoneDeactivateRs = 0x8d4a,
        SealStoneStatusNt = 0x8d4b, SealStoneRegisterRq = 0x8d4c, SealStoneRegisterRs = 0x8d4d, SealStoneBroadcastNt = 0x8d4e,
        PotionAutoUseListNt = 0x5648, PotionAutoUseRq = 0x5649, PotionAutoUseRs = 0x564a,
        ItemAutoUseListNt = 0x564b, ItemAutoUseRq = 0x564c, ItemAutoUseRs = 0x564d,
        ItemRestoreStatusNt = 0xe343, PresetInfoNt = 0xe28c, PresetEquipUpdateNt = 0xe28d, PresetListSetRq = 0xe291, PresetListSetRs = 0xe292;

    /// <summary>OURS: auto-potion drinks below this share of HP (the client's HP condition -1 is not decoded).</summary>
    public const int PotionAutoUsePercent = 50;
    /// <summary>EPresetType Equip; the other types (Arcana, Skill, Title, Wing, Skin, ...) only keep their number.</summary>
    const int PresetEquip = 0, PresetTypes = 8, PresetCount = 3;

    sealed record SealStoneSetting(bool On, long Item);
    sealed record PresetSetting(Dictionary<int, int> Numbers, Dictionary<int, Dictionary<int, long>> Equip);
    sealed record RestoreCount(int Count, long Last);

    readonly SortedSet<int> earnedEmotes = [];
    bool sealStoneOn;
    long sealStoneItem;
    /// <summary>The official default auto-potion (OBSERVED 0x5648 "01 01 b314991f 00000000": HP group, Life Potion 530126003).</summary>
    public const int DefaultAutoPotion = 530126003;
    int[] potionSlots = [DefaultAutoPotion, 0];
    readonly SortedSet<int> autoUseItems = [];
    PresetSetting presets = new([], []);
    Dictionary<int, RestoreCount> restoreCounts = [];

    public bool SealStoneOn => sealStoneOn;
    public IReadOnlyList<int> PotionSlots => potionSlots;

    void LoadSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        T? Read<T>(string name) where T : class
        {
            try { return settings.TryGetValue(name, out var v) ? v.Deserialize<T>() : null; }
            catch (JsonException) { return null; }
        }
        earnedEmotes.Clear();
        earnedEmotes.UnionWith(Read<int[]>("social_actions") ?? []);
        var seal = Read<SealStoneSetting>("seal_stone");
        (sealStoneOn, sealStoneItem) = (seal?.On ?? false, seal?.Item ?? 0);
        potionSlots = Read<int[]>("potion_auto_use") is [var a, var b] ? [a, b] : [DefaultAutoPotion, 0];
        autoUseItems.Clear();
        autoUseItems.UnionWith(Read<int[]>("item_auto_use") ?? []);
        presets = Read<PresetSetting>("presets") ?? new([], []);
        restoreCounts = Read<Dictionary<int, RestoreCount>>("item_restores") ?? [];
    }

    async Task SaveSettingAsync(string name, object value, CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveCharacterSettingAsync(character, name, JsonSerializer.SerializeToElement(value), ct); return []; });
    }

    /// <summary>The world-entry lists these settings drive (after the skill list, as official).</summary>
    List<byte[]> SettingsAtEntry(long now) =>
    [
        GamePackets.SocialActionInfo(GamePackets.DefaultSocialActions.Concat(earnedEmotes).Distinct().Order().ToList()),
        PotionAutoUseList(),
        ItemAutoUseList(),
        PresetInfo(),
        ItemRestoreStatus(now),
    ];

    // ---------- Emotes ----------

    /// <summary>A GetSocialAction item (17 in Item.dat, emotes 32-46, 48, 49): used up, the emote is earned (0x9500 then the list).</summary>
    async Task<List<byte[]>?> UseSocialActionItemAsync(INativeCharacterStore store, Guid character, ItemState row, int emote, bool reply, CancellationToken ct)
    {
        if (emote <= 0 || GamePackets.DefaultSocialActions.Contains(emote) || earnedEmotes.Contains(emote)) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.AddRange(await GrantEmoteAsync(emote, ct));
        return replies;
    }

    /// <summary>Earns an emote (an item or the builder): PcSocialActionAcquire_NT {i32 id}, stored.</summary>
    public async Task<List<byte[]>> GrantEmoteAsync(int emote, CancellationToken ct)
    {
        if (!earnedEmotes.Add(emote)) return [];
        await SaveSettingAsync("social_actions", earnedEmotes.ToArray(), ct);
        return [PacketWriter.Opcode(SocialActionAcquireNt).I32(emote).ToArray()];
    }

    // ---------- Seal stone ----------

    /// <summary>
    /// Activate/DeactivateSealStone_RQ (no body): the toggle (EToggleType SealStone = toggle flag 1, which the official
    /// appearance carries) -> RS {u16 0}, SealStoneStatusChange_NT {key, on} to the player and those around.
    /// </summary>
    async Task<List<byte[]>> SealStoneToggleAsync(bool on, CancellationToken ct)
    {
        sealStoneOn = on;
        await SaveSettingAsync("seal_stone", new SealStoneSetting(on, sealStoneItem), ct);
        var status = PacketWriter.Opcode(SealStoneStatusNt).Varint(playerKey).Bool(on).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [status]);
        return [PacketWriter.Opcode(on ? SealStoneActivateRs : SealStoneDeactivateRs).U16(0).ToArray(), status];
    }

    /// <summary>
    /// RegisterSealStoneAutoUse_RQ {item uid i64}: a bag item (0 clears) -> RS {u16, uid}, BroadcastSealStoneChange_NT {key, item id}.
    /// INFERRED: slotting a stack also turns the toggle on (SealStoneStatusChange_NT), so a slotted stack is spent without a
    /// separate ActivateSealStone_RQ.
    /// </summary>
    async Task<List<byte[]>> RegisterSealStoneAsync(long uid, CancellationToken ct)
    {
        var itemId = 0;
        if (uid != 0)
        {
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag") return [PacketWriter.Opcode(SealStoneRegisterRs).U16(1).I64(uid).ToArray()];
            itemId = row.TemplateId;
        }
        sealStoneItem = uid;
        var turnOn = uid != 0 && !sealStoneOn;
        if (turnOn) sealStoneOn = true;
        await SaveSettingAsync("seal_stone", new SealStoneSetting(sealStoneOn, uid), ct);
        var change = PacketWriter.Opcode(SealStoneBroadcastNt).Varint(playerKey).I32(itemId).ToArray();
        List<byte[]> shown = [change];
        if (turnOn) shown.Add(PacketWriter.Opcode(SealStoneStatusNt).Varint(playerKey).Bool(true).ToArray());
        worldPlayer?.Map?.Broadcast(worldPlayer, shown);
        return [PacketWriter.Opcode(SealStoneRegisterRs).U16(0).I64(uid).ToArray(), .. shown];
    }

    int SealStoneItemId => sealStoneItem != 0 && storedItems.TryGetValue(sealStoneItem, out var row) ? row.TemplateId : 0;

    // ---------- Auto-potion and auto-use items ----------

    // The HP group record is always sent, as officially: with an empty list the client has no auto-potion group, so the X slot
    // refuses drops and never sends SetAutoUsePotionItem_RQ 0x5649 (2026-10-30 logs: no 0x5649 at all).
    byte[] PotionAutoUseList() =>
        PacketWriter.Opcode(PotionAutoUseListNt).Varint(1).U8(1).I32(potionSlots[0]).I32(potionSlots[1]).ToArray();

    byte[] ItemAutoUseList()
    {
        var w = PacketWriter.Opcode(ItemAutoUseListNt).Varint(autoUseItems.Count);
        foreach (var id in autoUseItems) w.I32(id);
        return w.ToArray();
    }

    /// <summary>SetAutoUsePotionItem_RQ {group u8 (1 HP), slot 1 item i32, slot 2 item i32} -> RS {u16 0, the setting}.</summary>
    async Task<List<byte[]>> SetPotionAutoUseAsync(byte[] plain, CancellationToken ct)
    {
        var group = plain[2];
        int first = BitConverter.ToInt32(plain, 3), second = BitConverter.ToInt32(plain, 7);
        var ok = group == 1 && new[] { first, second }.All(id => id == 0 || Boxes.Use(id) is { Effect: "Skill" });
        if (ok)
        {
            potionSlots = [first, second];
            await SaveSettingAsync("potion_auto_use", potionSlots, ct);
        }
        return [PacketWriter.Opcode(PotionAutoUseRs).U16(ok ? 0 : 1).U8(group).I32(first).I32(second).ToArray()];
    }

    /// <summary>SetAutoUseItem_RQ {item i32, activated bool} -> RS {u16 0, {item, activated}}. Stored and listed; not used automatically yet.</summary>
    async Task<List<byte[]>> SetItemAutoUseAsync(byte[] plain, CancellationToken ct)
    {
        var item = BitConverter.ToInt32(plain, 2);
        var on = plain[6] != 0;
        var changed = on ? autoUseItems.Add(item) : autoUseItems.Remove(item);
        if (changed) await SaveSettingAsync("item_auto_use", autoUseItems.ToArray(), ct);
        return [PacketWriter.Opcode(ItemAutoUseRs).U16(0).I32(item).Bool(on).ToArray()];
    }

    /// <summary>
    /// The tick's auto-potion: below <see cref="PotionAutoUsePercent"/> of HP, out of water and alive, the first slot's potion
    /// in the bag (else the second's) is used once its cooldown group is ready, as the client's own item use.
    /// </summary>
    async Task<List<byte[]>> AutoPotionAsync(long now, CancellationToken ct)
    {
        if (resources is null || dead || potionSlots is [0, 0] || Store is null || (long)resources.Hp * 100 >= (long)resources.HpMax * PotionAutoUsePercent) return [];
        foreach (var itemId in potionSlots)
        {
            if (itemId == 0) continue;
            var cooldown = Boxes.Cooldown(itemId);
            if (cooldown.Group != "None" && now < itemCooldowns.GetValueOrDefault(cooldown.Group)) return [];
            if (storedItems.Values.Where(i => i.TemplateId == itemId && i.Location == "bag").OrderBy(i => i.Quantity).FirstOrDefault() is { } stack)
                return await UseItemAsync(stack.WireId, now, ct);
        }
        return [];
    }

    // ---------- Presets ----------

    int PresetNo(int type) => presets.Numbers.GetValueOrDefault(type, 1);

    Dictionary<int, long> EquippedSlots() =>
        storedItems.Values.Where(i => i.Location == "equipment" && i.Slot is not null)
            .GroupBy(i => i.Slot!.Value)
            .ToDictionary(g => g.Key, g => g.Last().WireId);

    /// <summary>
    /// PresetInfo_NT in the official order (OBSERVED 904 B): equip no + [{no, [{i64 item, u8 slot}]}], arcana no + presets,
    /// skill no + [{mask, no, slot, ...}], title no + [], wing no + wing i32 + [], creature board no, housing no + [].
    /// The current equip preset is what is worn; skill, title and wing presets are not kept yet (empty lists).
    /// </summary>
    byte[] PresetInfo()
    {
        var equip = new SortedDictionary<int, Dictionary<int, long>>(presets.Equip) { [PresetNo(PresetEquip)] = EquippedSlots() };
        var w = PacketWriter.Opcode(PresetInfoNt).U8(PresetNo(PresetEquip));
        WriteEquipPresets(w, equip);
        w.U8(PresetNo(1)).Varint(0);
        w.U8(PresetNo(2)).Varint(0);
        w.U8(PresetNo(3)).Varint(0);
        w.U8(PresetNo(4)).I32(wingId).Varint(0);
        w.U8(PresetNo(6)).U8(1).Varint(0);
        return w.ToArray();
    }

    static void WriteEquipPresets(PacketWriter w, IReadOnlyDictionary<int, Dictionary<int, long>> equip)
    {
        w.Varint(equip.Count);
        foreach (var (no, slots) in equip)
        {
            w.U8(no).Varint(slots.Count);
            foreach (var (slot, item) in slots.OrderBy(s => s.Key)) w.I64(item).U8(slot);
        }
    }

    /// <summary>
    /// PresetSet_RQ {EPresetType u8, no u8, batch bool} (batch: every type to that number). Switching the equip preset keeps
    /// what is worn as the old preset and equips the new preset's items still owned (bag or worn); then PresetSet_RS {u16,
    /// type, no, batch, no invalid types}, PresetEquipUpdate_NT and the item and stat changes.
    /// </summary>
    async Task<List<byte[]>> PresetSetAsync(byte type, byte no, bool batch, CancellationToken ct)
    {
        if (type >= PresetTypes || no is < 1 or > PresetCount) return [PacketWriter.Opcode(PresetSetRs).U16(1).U8(type).U8(no).Bool(batch).Varint(0).ToArray()];
        var changes = new Dictionary<int, int>();
        foreach (var t in batch ? Enumerable.Range(0, PresetTypes) : new[] { (int)type }) changes[t] = no;
        var replies = new List<byte[]> { PacketWriter.Opcode(PresetSetRs).U16(0).U8(type).U8(no).Bool(batch).Varint(0).ToArray() };
        replies.AddRange(await ApplyPresetsAsync(changes, ct));
        return replies;
    }

    /// <summary>PresetListSet_RQ {map EPresetType -> no}: each type to its number -> RS {u16 0, the map, no invalid types}.</summary>
    async Task<List<byte[]>> PresetListSetAsync(byte[] plain, CancellationToken ct)
    {
        var changes = new Dictionary<int, int>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var n = r.Varint();
            for (var i = 0; i < n && i < PresetTypes; i++)
            {
                int t = r.U8(), no = r.U8();
                if (t < PresetTypes && no is >= 1 and <= PresetCount) changes[t] = no;
            }
        }
        catch (ProtocolException) { return []; }
        var reply = PacketWriter.Opcode(PresetListSetRs).U16(0).Varint(changes.Count);
        foreach (var (t, no) in changes) reply.U8(t).U8(no);
        return [reply.Varint(0).ToArray(), .. await ApplyPresetsAsync(changes, ct)];
    }

    async Task<List<byte[]>> ApplyPresetsAsync(Dictionary<int, int> changes, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        var equipFrom = PresetNo(PresetEquip);
        foreach (var (t, no) in changes) presets.Numbers[t] = no;
        if (changes.TryGetValue(PresetEquip, out var equipTo) && equipTo != equipFrom)
        {
            presets.Equip[equipFrom] = EquippedSlots();
            if (presets.Equip.Remove(equipTo, out var wanted) && Store is { } store && CharacterId is { } character)
            {
                var before = VisualItems();
                var moved = false;
                foreach (var (slot, key) in wanted)
                {
                    // Each move refreshes the stored rows: an item it displaced is back in the bag with a new revision.
                    if (!storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment") || row.Location == "equipment" && row.Slot == slot) continue;
                    if ((await Stored(async () => await store.MoveItemAsync(character, row.InstanceId, row.Revision, slot, ct) ? [[]] : [])).Count == 0) continue;
                    moved = true;
                    replies.AddRange(await InventoryChangesAsync(ct));
                }
                if (moved)
                {
                    equippedWeapon = WeaponFromInventory();
                    replies.AddRange(RefreshStats());
                    var level = EquipItemLevel();
                    replies.Add(GamePackets.ItemEquipLevelChange(level, level));
                    if (EquipChange(before, level) is { } change)
                    {
                        replies.Add(change);
                        worldPlayer?.Map?.Broadcast(worldPlayer, [change]);
                    }
                }
            }
            var update = PacketWriter.Opcode(PresetEquipUpdateNt).U8(equipTo);
            WriteEquipPresets(update, new SortedDictionary<int, Dictionary<int, long>>(presets.Equip) { [equipTo] = EquippedSlots() });
            replies.Add(update.ToArray());
        }
        await SaveSettingAsync("presets", presets, ct);
        return replies;
    }

    // ---------- Item restore counts ----------

    /// <summary>ItemRestoreStatusList_NT: per EItemRestoreType 1-3 {char db id i64, type, restores used i32, last restore i64} (OBSERVED shape).</summary>
    byte[] ItemRestoreStatus(long now)
    {
        var w = PacketWriter.Opcode(ItemRestoreStatusNt).Varint(3);
        for (var type = 1; type <= 3; type++)
        {
            var count = restoreCounts.GetValueOrDefault(type);
            w.I64(selected?.WireId ?? playerKey).U8(type).I32(count?.Count ?? 0).I64(count?.Last ?? now);
        }
        return w.ToArray();
    }

    /// <summary>A buyback restore (type 1 NpcShopSell) counted for the status list.</summary>
    async Task<byte[]> CountRestoreAsync(int type, long now, CancellationToken ct)
    {
        restoreCounts[type] = new RestoreCount((restoreCounts.GetValueOrDefault(type)?.Count ?? 0) + 1, now);
        await SaveSettingAsync("item_restores", restoreCounts, ct);
        return ItemRestoreStatus(now);
    }

    // ---------- Warp leave ----------

    /// <summary>
    /// CharWarpLeave_NT 0x373c to those around before a teleport: key, pos, dir f32, move state, substate, EWarpReason
    /// (1 TeleportCasterEffect, 5 Cutscene), skill i32, effect i32, duration i32 (usmap order; no official sample).
    /// </summary>
    void BroadcastWarpLeave(byte effect, bool cutscene)
    {
        if (worldPlayer?.Map is null || position is not { } here) return;
        var leave = PacketWriter.Opcode(0x373c).Varint(playerKey).Vec3(here).F32(worldPlayer.Heading).U8(0).U8(0).U8(cutscene ? 5 : 1)
            .I32(0).I32(effect).I32(0).ToArray();
        worldPlayer.Map.Broadcast(worldPlayer, [leave]);
    }
}
