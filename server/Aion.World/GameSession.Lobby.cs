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
}
