using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Character deletion and renaming in the lobby. Opcodes: the 0x36xx lobby range (live = usmap position, as
// 0x3619/0x361b/0x361f): CharPendingDelete 0x3627/0x3628, Cancel 0x3629/0x362a, CharDelete_NT 0x362b,
// ChangeCharName 0x362c/0x362d. The character key arrives as SelectCharacter_RQ's does (varint, possibly shifted
// 16 bits; OBSERVED for select), and is echoed as sent. Results u16 as every observed RS. CANDIDATE layouts.
// Deletion rule (OURS): below level 20 at once, else after 24 hours (cancellable); a due deletion moves the
// character to the store's disabled graveyard account.
public sealed partial class GameSession
{
    public const ushort CharDeleteRq = 0x3627, CharDeleteRs = 0x3628, CharDeleteCancelRq = 0x3629, CharDeleteCancelRs = 0x362a,
        CharDeletedNt = 0x362b, CharRenameRq = 0x362c, CharRenameRs = 0x362d;
    public const int ImmediateDeleteBelowLevel = 20;
    public static readonly TimeSpan DeleteDelay = TimeSpan.FromHours(24);

    /// <summary>The stored character a lobby key names (the key as sent, or shifted down 16 bits).</summary>
    async Task<CharacterState?> LobbyCharacterAsync(INativeCharacterStore store, uint key, CancellationToken ct)
    {
        foreach (var wire in new[] { key, key >> 16 }.Where(k => k is > 0 and <= int.MaxValue).Distinct())
            if (await store.LoadWireAsync((int)wire, ct) is { } state) return state;
        return null;
    }

    /// <summary>0x3627 CharPendingDelete_RQ {character key}: deleted now (0x3628 + 0x362b) or after DeleteDelay (0x3628 with the time).</summary>
    async Task<List<byte[]>> DeleteCharacterAsync(byte[] plain, long now, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        byte[] Reply(int result, long at) => PacketWriter.Opcode(CharDeleteRs).U16(result).Varint(key).I64(at).ToArray();
        if (Store is not { } store || selected is not null) return [Reply(1, 0)];
        CharacterState? target = null;
        await Stored(async () => (target = await LobbyCharacterAsync(store, key, ct)) is null ? [] : [[]]);
        if (target is null) return [Reply(1, 0)];
        var character = target.Summary.CharacterId;
        var immediate = target.Progress.Level < ImmediateDeleteBelowLevel;
        var at = immediate ? DateTimeOffset.FromUnixTimeMilliseconds(now) : DateTimeOffset.FromUnixTimeMilliseconds(now) + DeleteDelay;
        var done = await Stored(async () => await store.RequestCharacterDeleteAsync(character, at, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(1, 0)];
        return immediate ? [Reply(0, at.ToUnixTimeMilliseconds()), PacketWriter.Opcode(CharDeletedNt).Varint(key).ToArray()] : [Reply(0, at.ToUnixTimeMilliseconds())];
    }

    /// <summary>0x3629 CharPendingDeleteCancel_RQ {character key}: a pending deletion is cancelled (0x362a).</summary>
    async Task<List<byte[]>> CancelDeleteAsync(byte[] plain, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(CharDeleteCancelRs).U16(result).Varint(key).ToArray();
        if (Store is not { } store || selected is not null) return [Reply(1)];
        CharacterState? state = null;
        await Stored(async () => (state = await LobbyCharacterAsync(store, key, ct)) is null ? [] : [[]]);
        if (state is null) return [Reply(1)];
        var done = await Stored(async () => await store.CancelCharacterDeleteAsync(state.Summary.CharacterId, ct) ? [[]] : []);
        return [Reply(done.Count == 0 ? 1 : 0)];
    }

    /// <summary>The Name Change Voucher a rename consumes (Item.dat Customize_CharName_A_s_01_b, CategoryValues ChangeNickname).</summary>
    public const int NameChangeVoucher = 632100001;

    /// <summary>
    /// 0x362c ChangeCharName_RQ {character key, nickname}: renamed for a Name Change Voucher from the character's bag (0x362d
    /// result 0, the name, cost item and count); 1 when the name is taken, 2 when it is not allowed (staff-style names
    /// are for staff; 1-12 characters), 3 without a voucher, 4 within 168 hours of the last rename (GlobalSetting
    /// character_rename_locked_period). Result codes other than 0 are CANDIDATE.
    /// </summary>
    async Task<List<byte[]>> RenameCharacterAsync(byte[] plain, CancellationToken ct)
    {
        uint key;
        string name;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            key = reader.Varint();
            name = reader.String();
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(CharRenameRs).U16(result).Varint(key).String(name)
            .I32(NameChangeVoucher).I64(result == 0 ? 1 : 0).ToArray();
        if (Store is not { } store || selected is not null) return [Reply(EResult.CharChangeName_ErrorInternal)];
        if (CharacterNames.IsStaffStyle(name) && !StaffAccount || name.Length is < 1 or > 12) return [Reply(EResult.CharChangeName_InvalidCharName)];
        CharacterState? state = null;
        await Stored(async () => (state = await LobbyCharacterAsync(store, key, ct)) is null ? [] : [[]]);
        if (state is null) return [Reply(EResult.CharChangeName_NotExist)];
        string outcome = "invalid";
        await Stored(async () => { outcome = await store.RenameCharacterAsync(state.Summary.CharacterId, name, ct); return []; });
        // The client's EResult codes for a rename (usmap kCharChangeName_*): a taken name is the invalid-name code (no duplicate code exists).
        return [Reply(outcome switch { "ok" => 0, "taken" => 8235, "voucher" => 8236, "locked" => 8237, _ => 8232 })];
    }

    // ---------- The character list (LobbyCharDesc in Login_RS 0x3615 and Lobby_RS 0x3626) ----------
    // What the lobby shows per character (client L10N UI_LOBBY_SELECTCHARACTER_*): name, "Lv. {0}", class, the legion as "<{0}>",
    // the location (the _zone_id subzone's name, "No Online Record" without a logout time), the item level, "Last Login Date"
    // (_logout_time), "Character Creation Date" (_reg_time), the "Deleted in {0}" countdown, and the 3D model (appearance,
    // equipment with skins, wing). OBSERVED values (9 official records in 6 lists, 2026-11-06/07, decoded field by field; the
    // byte checks rebuild one list and both CharCreate_RS samples): _combat_level 1 and _combat_exp 0 at every level (1..45),
    // _conqueror_level 0, _block_end_time = server time - 24 h, the real subzone of the last spot, the equipped wing and wing skin,
    // the combat power, every worn item with godstones [0,0,0] and fx 14 on the weapon only, _item_count_list empty.

    /// <summary>Character setting "lobby": what the list shows that only the world knows (combat power, the shown wing).</summary>
    public const string LobbySetting = "lobby";

    sealed record LobbyView(long Cp, int Wing, int WingSkin);
    (Guid Character, LobbyView View)? savedLobbyView;

    /// <summary>
    /// Saves the combat power and the shown wing (and its skin) for the character list, when they changed: at every forced
    /// checkpoint (Logout_RQ, Lobby_RQ, disconnect). The lobby then lists them as official without loading the character.
    /// </summary>
    async Task SaveLobbyViewAsync(CancellationToken ct)
    {
        if (Store is null || CharacterId is not { } character || resources is null) return;
        LobbyView view;
        try { view = new LobbyView(CombatPower(), wingId, wingId != 0 && wingSkin != 0 ? wingSkin : 0); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException) { return; }
        if (savedLobbyView is { } saved && saved.Character == character && saved.View == view) return;
        await SaveSettingAsync(LobbySetting, new { cp = view.Cp, wing = view.Wing, wing_skin = view.WingSkin }, ct);
        savedLobbyView = (character, view);
    }

    static LobbyView ReadLobbyView(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!settings.TryGetValue(LobbySetting, out var value) || value.ValueKind != JsonValueKind.Object) return new LobbyView(0, 0, 0);
        long Number(string name) => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        return new LobbyView(Math.Clamp(Number("cp"), 0, 2_000_000_000), (int)Number("wing"), (int)Number("wing_skin"));
    }

    /// <summary>
    /// The worn items as the lobby draws them (visual slot order, equipped skins, godstones [0,0,0] on items as official) and their
    /// item level (the item's level plus its enchant's, as the character window's EquipItemLevel). The weapon's fx bit is 14 in the
    /// official lists and 0 in CharCreate_RS; every other slot 0.
    /// </summary>
    async Task<(List<EquipBroad> Equips, int ItemLevel)> LobbyEquipsAsync(INativeCharacterStore store, Guid character, int weaponFx, CancellationToken ct)
    {
        var worn = (await store.WornItemsAsync(character, ct)).Where(i => i.Location == "equipment" && i.Slot is { } s && VisualSlot.ContainsKey(s))
            .OrderBy(i => VisualSlot[i.Slot!.Value]).ToList();
        // With the equipped skins (the lobby drew every character without them, live 2026-11-09).
        var skins = new Dictionary<int, int>();
        foreach (var (skinSlot, skinId) in (await store.ExtrasAsync(character, ct)).SkinEquips) skins[skinSlot] = skinId;
        var equips = SkinnedEquips(worn.ToDictionary(i => VisualSlot[i.Slot!.Value], i => i.TemplateId),
                worn.ToDictionary(i => VisualSlot[i.Slot!.Value], i => (byte)Math.Clamp(EnchantOf(i), 0, 255)), skins, all: false, godstones: true)
            .Select(e => e with { FxBit = e.VisualSlot == 1 ? weaponFx : 0 }).ToList();
        var itemLevel = worn.Sum(i => data.ItemLevel(i.TemplateId) + Enchants.Effect(i.TemplateId, EnchantOf(i)).ItemLevel);
        return (equips, itemLevel);
    }

    /// <summary>One character of the list, from the stored state, items, legion and the saved lobby view.</summary>
    async Task<LobbyCharacter> LobbyRecordAsync(INativeCharacterStore store, CharacterState state, long now,
        IReadOnlyDictionary<Guid, DateTimeOffset> deletions, IReadOnlyDictionary<Guid, DateTimeOffset> renames, CancellationToken ct)
    {
        var id = state.Summary.CharacterId;
        var profile = StoredProfile(state);
        var (equips, itemLevel) = await LobbyEquipsAsync(store, id, weaponFx: 14, ct);
        var view = ReadLobbyView(await store.CharacterSettingsAsync(id, ct));
        var position = StoredCharacters.Position(state);
        return new LobbyCharacter
        {
            Key = state.WireId, DbId = state.WireId, Nickname = state.Summary.Name, PcId = profile.PcId,
            Race = state.Race, Gender = state.Gender, Level = state.Progress.Level, CombatLevel = 1,   // 1 at every level (OBSERVED)
            Experience = state.Progress.Experience, EquipItemLevel = itemLevel,
            GuildName = (await store.GuildOfAsync(id, ct))?.Name ?? "",
            // The last online time (the last save, the logout's included); 0 made the lobby show "No Online Record".
            LogoutTime = state.LastOnline?.ToUnixTimeMilliseconds() ?? 0,
            Position = position, ZoneId = LobbyZones.ZoneOf(state.Progress.MapId, position, state.Race),
            RegTime = state.Created?.ToUnixTimeMilliseconds() ?? 0,
            PendingDeleteTime = deletions.TryGetValue(id, out var deleteAt) ? deleteAt.ToUnixTimeMilliseconds() : 0,
            Equips = equips, BlockEndTime = GamePackets.UnblockedSince(now), WingId = view.Wing, WingSkinId = view.Wing != 0 ? view.WingSkin : 0,
            CustomizingData = state.Appearance, VoiceType = state.VoiceType,
            LastNicknameChangeTime = renames.TryGetValue(id, out var renamedAt) ? renamedAt.ToUnixTimeMilliseconds() : 0,
            SlotNo = state.LobbySlot ?? throw new ArgumentException("Character has no assigned lobby slot"),
            CombatPower = view.Cp,
        };
    }
}
