using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Character deletion and renaming in the lobby. Opcodes: the 0x36xx lobby range (live = usmap position, as
// 0x3619/0x361b/0x361f): CharPendingDelete 0x3627/0x3628, Cancel 0x3629/0x362a, CharDelete_NT 0x362b,
// ChangeCharName 0x362c/0x362d; CharSlotSwap 0xe336/0xe337 (E3Base + usmap position 1480/1481, Common_CharacterSlot list;
// live 2026-10-30 RQ: varint count, {i64 dbid, u8 slot} each. RS 0xe337: result u16, count u8, same pairs as RQ
// (client replaces its lobby list from the RS; echo every request entry, not only moved slots). No follow-up packets.
// The dbid arrives as SelectCharacter_RQ's does (varint, possibly shifted 16 bits; OBSERVED for select).
// Deletion rule (OURS): below level 20 at once, else after 7 days (24 hours before the Oct 28 2026 patch; cancellable); a due deletion moves the
// character to the store's disabled graveyard account.
public sealed partial class GameSession
{
    public const ushort CharDeleteRq = 0x3627, CharDeleteRs = 0x3628, CharDeleteCancelRq = 0x3629, CharDeleteCancelRs = 0x362a,
        CharDeletedNt = 0x362b, CharRenameRq = 0x362c, CharRenameRs = 0x362d, CharSlotSwapRq = SocialOpcodes.E3Base + 1480, CharSlotSwapRs = SocialOpcodes.E3Base + 1481,
        CharCustomizingListRq = 0x37DF, CharCustomizingListRs = 0x37E0, BroadcastCharCustomizingUpdateNt = 0x37ED;
    /// <summary>GlobalSetting lobby_pc_slot_default_open_count (tools/data/lobby_slots.json).</summary>
    public const int DefaultLobbySlots = 4;
    /// <summary>GlobalSetting lobby_pc_slot_max_extended_count.</summary>
    public const int MaxExtendedLobbySlots = 4;
    /// <summary>Total lobby slot indices 0..BaseCharacterSlots-1 (default open + max extensions).</summary>
    public const int BaseCharacterSlots = DefaultLobbySlots + MaxExtendedLobbySlots;
    public const int ImmediateDeleteBelowLevel = 20;
    public static readonly TimeSpan DeleteDelay = Patch20261028.CharacterDeleteDelay;

    int extendedCharacterSlots = -1;
    /// <summary>Purchased extensions persisted on auth.accounts (also sent as _extended_character_slot_count).</summary>
    int ExtendedCharacterSlotCount => extendedCharacterSlots < 0 ? 0 : extendedCharacterSlots;
    int LobbySlotCapacity => DefaultLobbySlots + ExtendedCharacterSlotCount;

    async Task EnsureExtendedCharacterSlotsAsync(CancellationToken ct)
    {
        if (extendedCharacterSlots >= 0 || Store is null) return;
        extendedCharacterSlots = await Store.ExtendedCharacterSlotsAsync(ct);
        if (extendedCharacterSlots is < 0 or > MaxExtendedLobbySlots)
            extendedCharacterSlots = Math.Clamp(extendedCharacterSlots, 0, MaxExtendedLobbySlots);
    }

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
        if (Store is not { } store || selected is not null) return [Reply(2)];
        if (CharacterNames.IsStaffStyle(name) && !StaffAccount || name.Length is < 1 or > 12) return [Reply(2)];
        CharacterState? state = null;
        await Stored(async () => (state = await LobbyCharacterAsync(store, key, ct)) is null ? [] : [[]]);
        if (state is null) return [Reply(2)];
        string outcome = "invalid";
        await Stored(async () => { outcome = await store.RenameCharacterAsync(state.Summary.CharacterId, name, ct); return []; });
        // The client's EResult codes for a rename (usmap kCharChangeName_*): a taken name is the invalid-name code (no duplicate code exists).
        return [Reply(outcome switch { "ok" => 0, "taken" => 8235, "voucher" => 8236, "locked" => 8237, _ => 8232 })];
    }

    /// <summary>Non-zero u16 result on 0xe337 when the layout or ownership check fails.</summary>
    public const int CharSlotSwapFailed = 1;

    /// <summary>0xe336 CharSlotSwap_RQ: persist layout; answer is 0xe337 only, echoing every RQ entry (dbid as sent, new slot).</summary>
    async Task<List<byte[]>> SwapLobbySlotsAsync(byte[] plain, long now, CancellationToken ct)
    {
        IReadOnlyList<CharSlotRow> rows;
        try { rows = ParseCharSlotSwap(plain); }
        catch (ProtocolException) { return []; }
        if (rows.Count == 0) return [GamePackets.CharSlotSwap(CharSlotSwapFailed, [])];
        if (Store is not { } store || selected is not null)
            return [GamePackets.CharSlotSwap(CharSlotSwapFailed, [])];
        var storage = rows.Select(r => (r.RawDbId, r.Slot)).ToList();
        var echo = rows.Select(r => (r.RawDbId, r.Slot)).ToList();
        var applied = await Stored(async () =>
        {
            await EnsureExtendedCharacterSlotsAsync(ct);
            if (!await CharSlotSwapRowsValidAsync(store, rows, ct)) return [];
            if (!await store.SwapLobbySlotsAsync(storage, LobbySlotCapacity, ct)) return [];
            return [GamePackets.CharSlotSwap(0, echo)];
        });
        if (applied.Count > 0) return applied;
        return [GamePackets.CharSlotSwap(CharSlotSwapFailed, [])];
    }

    async Task<bool> CharSlotSwapRowsValidAsync(INativeCharacterStore store, IReadOnlyList<CharSlotRow> rows, CancellationToken ct)
    {
        var known = (await store.LobbyAsync(ct)).Select(s => (long)s.WireId).ToList();
        foreach (var row in rows)
            if (!LobbyWireIds.TryResolveWire(row.RawDbId, known, out _)) return false;
        return true;
    }

    /// <summary>0x37DF CharCustomizingList_RQ (no body): per-slot appearance the lobby pedestals use (live after slot swap).</summary>
    async Task<List<byte[]>> CharCustomizingListAsync(CancellationToken ct)
    {
        if (Store is null || selected is not null) return [GamePackets.CharCustomizingList(1, [])];
        await EnsureExtendedCharacterSlotsAsync(ct);
        var characters = await LobbyCharactersAsync(ct);
        var entries = characters.Select(c => (c.SlotNo, c.CustomizingData, c.VoiceType, c.PcId)).ToList();
        return [GamePackets.CharCustomizingList(0, entries)];
    }

    /// <summary>Every lobby character gets a unique display slot (creation order fills gaps).</summary>
    internal static IReadOnlyList<(CharacterState State, int SlotNo)> LobbySlotLayout(
        IReadOnlyList<CharacterState> characters, int capacity)
    {
        if (capacity <= 0) return [];
        var ordered = characters.OrderBy(s => s.RegisteredUnixMs).ThenBy(s => s.WireId).ToList();
        var used = new HashSet<int>();
        var laid = new List<(CharacterState, int)>(ordered.Count);
        var pending = new List<CharacterState>();
        foreach (var state in ordered)
        {
            if (state.LobbySlot is int slot && slot >= 0 && slot < capacity && used.Add(slot))
                laid.Add((state, slot));
            else pending.Add(state);
        }
        foreach (var state in pending)
        {
            var slot = Enumerable.Range(0, capacity).First(s => !used.Contains(s));
            used.Add(slot);
            laid.Add((state, slot));
        }
        return laid.OrderBy(p => p.Item2).ToList();
    }

    readonly record struct CharSlotRow(long RawDbId, int Slot);

    static IReadOnlyList<CharSlotRow> ParseCharSlotSwap(byte[] plain)
    {
        var reader = new PacketReader(plain) { Offset = 2 };
        var count = checked((int)reader.Varint());
        if (count is <= 0 or > 32) throw new ProtocolException("Bad slot list count");
        var list = new List<CharSlotRow>(count);
        for (var i = 0; i < count; i++)
            list.Add(new(reader.I64(), reader.U8()));
        if (!reader.End) throw new ProtocolException("Trailing CharSlotSwap data");
        return list;
    }

    /// <summary>0x3626 Lobby_RS plus 0x3A16 ExtendedCharacterSlot_NT after the stored extended count is loaded.</summary>
    List<byte[]> LobbyCharacterReplies(IReadOnlyList<LobbyCharacter> characters) =>
    [
        GamePackets.LobbyCharacters(characters, ExtendedCharacterSlotCount),
        GamePackets.ExtendedCharacterSlot(ExtendedCharacterSlotCount),
    ];
}
