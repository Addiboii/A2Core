namespace Aion.Protocol.Native;

/// <summary>One worn item of an inspected character (Common_ViewCharEquipItemInfo).</summary>
public sealed record ViewCharItem(int ItemId, EquipDetail Detail, byte VisualSlot = 0, int SkinId = 0, string Maker = "", long PeriodExpireTime = 0,
    bool PeriodExpired = false, bool Display = true, bool DefaultMesh = true, bool Override = true);

/// <summary>What ViewChar_RS shows of a character (the inspect window).</summary>
public sealed record ViewCharInfo
{
    public string Name { get; init; } = "";
    public int PcId { get; init; }
    public byte Race { get; init; } = 1;
    public byte Gender { get; init; } = 1;
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public int BornServerId { get; init; }
    public int CurrentServerId { get; init; }
    public bool Online { get; init; }
    public int TitleId { get; init; }
    public GuildTag? Guild { get; init; }
    public IReadOnlyList<ViewCharItem> Items { get; init; } = [];
    public byte[] CustomizingData { get; init; } = [];
    /// <summary>Daevanion boards: board id and its open node count.</summary>
    public IReadOnlyList<(int Board, int Open)> Boards { get; init; } = [];
    /// <summary>ETitleEquipCategory (1..3) -> title id.</summary>
    public IReadOnlyList<(byte Category, int Title)> Titles { get; init; } = [];
    /// <summary>ECreatureType -> its board's slots (index, ECreatureOptionGrade).</summary>
    public IReadOnlyList<(byte Type, IReadOnlyList<(byte Slot, byte Grade)> Slots)> CreatureBoards { get; init; } = [];
    /// <summary>EContentsUnlockType values.</summary>
    public IReadOnlyList<byte> Unlocks { get; init; } = [];
    public long CombatPower { get; init; }
    /// <summary>Role vote counts by type.</summary>
    public IReadOnlyList<(int Type, int Count)> RoleVotes { get; init; } = [];
}

/// <summary>
/// Inspect another player (ViewChar), player emotes (PcSocialActionPlay) and the content sweeps.
///
/// ViewChar_RQ 0x364f {target server id, target db id} -> ViewChar_RS 0x3650 (dev = live in the 0x36xx family). OFFICIAL layout,
/// decoded byte for byte from the one official reply (capture-13700-20261106-115817 #214922, 3,432 bytes, a level-45 character with
/// 28 worn items): result u16 | CharDesc (mask u8 0x07, nickname, pc_id i32, race u8, gender u8: as in 0x3645) | level i32 |
/// conqueror level i32 | equip item level i32 | born server u16 | current server u16 | online (packed bool) | title i32 |
/// CharGuildDesc (as in 0x3645) | items: varint count, each {item id i32, maker, period expire i64, expired (packed bool),
/// EquipItemDetail (the Common_Item detail of GamePackets), visual slot u8, skin i32, dye slots (varint count), display /
/// default mesh / override (packed bools)} | customizing (varint + bytes) | daevanion boards: varint count, each {mask u8 0x01,
/// board i32, open count u8} | equipped titles: varint count, {category u8, title i32} | creature boards: varint count, {creature
/// type u8, varint count, {slot u8, grade u8}} | contents unlocks: varint count, u8 each | combat power i64 | role votes: varint
/// count, {type i32, count i32}. The packed bools share one byte across the record (the official 0x38 at byte 34 holds online
/// and the first items' flags). The request body is not recorded (its bytes are encrypted in the official trace): server id u16
/// or i32 and db id i64 or varint are all taken (CANDIDATE).
///
/// PcSocialActionPlay_RQ 0x9504 {Common_PcSocialAction} -> RS 0x9505 {key, result u16, social action}, NT 0x9506 {key, social
/// action} to the players around (live = dev 0x3A85.. + 0x5A7F, the family of PcSocialActionInfo_NT 0x9501 / SocialActionStart_NT
/// 0x9507, OBSERVED anchors). No official sample of the three: the reply echoes the request's social action bytes; key varint and
/// result u16 as the other replies of the protocol (CharRest 0x3748). CANDIDATE.
///
/// ContentsSweep_RQ 0x56ea / BossChallengeSweepWithoutPlay_RQ 0x56ec -> RS 0x56eb / 0x56ed (live = dev 0x382C.. + 0x1EBE, the family
/// of ItemSuccession_RQ 0x56b0 and OdEnergyCubeReward_RQ 0x56cd, OBSERVED anchors): result u16, sweep count u8, rewards (varint
/// count of {item i32, count i64}) [, group i32, challenge level i32]. CANDIDATE (no sample).
/// </summary>
public static class InspectPackets
{
    public const ushort ViewCharRq = 0x364f, ViewCharRs = 0x3650;
    public const ushort SocialActionPlayRq = 0x9504, SocialActionPlayRs = 0x9505, SocialActionPlayNt = 0x9506;
    public const ushort ContentsSweepRq = 0x56ea, ContentsSweepRs = 0x56eb, BossChallengeSweepRq = 0x56ec, BossChallengeSweepRs = 0x56ed;

    /// <summary>ViewChar_RS: the character (an empty record with a failure result).</summary>
    public static byte[] ViewChar(int result, ViewCharInfo c)
    {
        var w = PacketWriter.Opcode(ViewCharRs).U16(result).U8(0x07).String(c.Name).I32(c.PcId).U8(c.Race).U8(c.Gender)
            .I32(c.Level).I32(c.ConquerorLevel).I32(c.EquipItemLevel).U16(c.BornServerId).U16(c.CurrentServerId).Bool(c.Online).I32(c.TitleId);
        PlayerPackets.WriteGuildDesc(w, c.Guild);
        w.Varint(c.Items.Count);
        foreach (var item in c.Items)
        {
            var d = item.Detail;
            w.I32(item.ItemId).String(item.Maker).I64(item.PeriodExpireTime).Bool(item.PeriodExpired);
            d.WriteTail(d.WriteGodstones(d.WriteStones(w.U8(d.EnchantLevel).I32(0).I32(0).U8(d.SurpassLevel).I32(d.SurpassBonus).I32(d.ItemLevel).Bool(false).U8(d.EquipSlot))
                .I64(d.SoulBindOwner).U8(d.SoulBindCount)));
            w.U8(item.VisualSlot).I32(item.SkinId).Varint(0).Bool(item.Display).Bool(item.DefaultMesh).Bool(item.Override);
        }
        w.Bytes(c.CustomizingData).Varint(c.Boards.Count);
        foreach (var (board, open) in c.Boards) w.U8(1).I32(board).U8(Math.Clamp(open, 0, 255));
        w.Varint(c.Titles.Count);
        foreach (var (category, title) in c.Titles) w.U8(category).I32(title);
        w.Varint(c.CreatureBoards.Count);
        foreach (var (type, slots) in c.CreatureBoards)
        {
            w.U8(type).Varint(slots.Count);
            foreach (var (slot, grade) in slots) w.U8(slot).U8(grade);
        }
        w.Varint(c.Unlocks.Count);
        foreach (var unlock in c.Unlocks) w.U8(unlock);
        w.I64(c.CombatPower).Varint(c.RoleVotes.Count);
        foreach (var (type, count) in c.RoleVotes) w.I32(type).I32(count);
        return w.ToArray();
    }

    /// <summary>The ViewChar_RQ target: server id and character db id (every width the usmap allows; null when none fits).</summary>
    public static (int Server, long DbId)? ParseViewChar(ReadOnlySpan<byte> plain)
    {
        var body = plain.Length >= 2 ? plain[2..] : [];
        // u16 + i64, i32 + i64 (fixed widths), then u16 / i32 + varint.
        if (body.Length == 10) return (BitConverter.ToUInt16(body), BitConverter.ToInt64(body[2..]));
        if (body.Length == 12) return (BitConverter.ToInt32(body), BitConverter.ToInt64(body[4..]));
        foreach (var serverWidth in new[] { 2, 4 })
        {
            if (body.Length <= serverWidth) continue;
            var server = serverWidth == 2 ? BitConverter.ToUInt16(body) : BitConverter.ToInt32(body);
            if (TryVarint(body[serverWidth..], out var id, out var used) && used == body.Length - serverWidth) return (server, id);
        }
        return null;
    }

    static bool TryVarint(ReadOnlySpan<byte> data, out long value, out int used)
    {
        value = 0;
        for (used = 0; used < data.Length && used < 10; used++)
        {
            value |= (long)(data[used] & 0x7f) << (7 * used);
            if (data[used] < 0x80) { used++; return true; }
        }
        return false;
    }

    /// <summary>PcSocialActionPlay_RS: the player's key, the result and the social action (the request's bytes).</summary>
    public static byte[] SocialActionPlayReply(int key, int result, ReadOnlySpan<byte> socialAction) =>
        PacketWriter.Opcode(SocialActionPlayRs).Varint(key).U16(result).Raw(socialAction).ToArray();

    /// <summary>PcSocialActionPlay_NT to the players around: the player's key and the social action.</summary>
    public static byte[] SocialActionPlayNotice(int key, ReadOnlySpan<byte> socialAction) =>
        PacketWriter.Opcode(SocialActionPlayNt).Varint(key).Raw(socialAction).ToArray();

    /// <summary>
    /// The social action id of a PcSocialActionPlay_RQ (Common_PcSocialAction: key u8, id i32, ...; with or without a leading field
    /// mask byte): the first reading that names a known action, else null.
    /// </summary>
    public static int? SocialActionId(ReadOnlySpan<byte> plain, Func<int, bool> known)
    {
        foreach (var at in new[] { 3, 4 })
            if (plain.Length >= at + 4 && BitConverter.ToInt32(plain[at..]) is var id && known(id)) return id;
        return null;
    }

    /// <summary>ContentsSweep_RS: result, sweep count, rewards (none on a refusal).</summary>
    public static byte[] ContentsSweepReply(int result, int sweepCount, IReadOnlyList<(int Item, long Count)>? rewards = null)
    {
        var w = PacketWriter.Opcode(ContentsSweepRs).U16(result).U8(Math.Clamp(sweepCount, 0, 255)).Varint(rewards?.Count ?? 0);
        foreach (var (item, count) in rewards ?? []) w.I32(item).I64(count);
        return w.ToArray();
    }

    /// <summary>BossChallengeSweepWithoutPlay_RS: result, sweep count, rewards, group, challenge level.</summary>
    public static byte[] BossChallengeSweepReply(int result, int sweepCount, int group, int level, IReadOnlyList<(int Item, long Count)>? rewards = null)
    {
        var w = PacketWriter.Opcode(BossChallengeSweepRs).U16(result).U8(Math.Clamp(sweepCount, 0, 255)).Varint(rewards?.Count ?? 0);
        foreach (var (item, count) in rewards ?? []) w.I32(item).I64(count);
        return w.I32(group).I32(level).ToArray();
    }

    /// <summary>The BossChallengeSweepWithoutPlay_RQ's group and level: its last eight bytes (after the sweep count and ticket items).</summary>
    public static (int Count, int Group, int Level)? ParseBossChallengeSweep(ReadOnlySpan<byte> plain) =>
        plain.Length >= 11 ? (plain[2], BitConverter.ToInt32(plain[^8..]), BitConverter.ToInt32(plain[^4..])) : null;
}
