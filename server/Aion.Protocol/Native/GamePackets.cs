using System.Text;

namespace Aion.Protocol.Native;

/// <summary>DevPacketData_Common_EquipBroad (no dye slots). Defaults match the official lobby starter weapon.</summary>
public sealed record EquipBroad(int ItemId, byte Enchant = 0, byte VisualSlot = 0, long PeriodExpireTime = 0,
    bool PeriodExpired = false, int SkinId = 0, IReadOnlyList<int>? Godstones = null, bool Display = true,
    bool DefaultMesh = true, bool Override = true, int FxBit = 14);

/// <summary>DevPacketData_Common_LobbyCharDesc fields, usmap order.</summary>
public sealed record LobbyCharacter
{
    public int Key { get; init; }
    public string Nickname { get; init; } = "";
    public int PcId { get; init; }
    public int Race { get; init; } = 1;
    public int Gender { get; init; } = 1;
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public long Experience { get; init; }
    public int CombatLevel { get; init; } = 1;
    public long CombatExperience { get; init; }
    public string GuildName { get; init; } = "";
    public long LogoutTime { get; init; }
    public Vector3d Position { get; init; }
    public int ZoneId { get; init; } = 3016;
    public long RegTime { get; init; }
    public long PendingDeleteTime { get; init; }
    public IReadOnlyList<EquipBroad> Equips { get; init; } = [];
    public long DbId { get; init; }
    public int BlockReasonNo { get; init; }
    public long BlockEndTime { get; init; }
    public int WingId { get; init; }
    public int WingSkinId { get; init; }
    public byte[] CustomizingData { get; init; } = [];
    public int VoiceType { get; init; }
    public bool PreGenerated { get; init; }
    public long LastNicknameChangeTime { get; init; }
    public int SlotNo { get; init; }
    public long CombatPower { get; init; }
}

/// <summary>The player's own spawn (0x3633) inputs.</summary>
/// <summary>A quest on the wire (Common_Quest): EQuestStatus 2 Incomplete, 3 Complete, 4 Rewarded.</summary>
public sealed record QuestWire(int QuestId, byte Status, int Step, int[] Goals, int Uid, int PublisherId = 0);

/// <summary>ECharPointType: the resources in 0x8d00 (OP oxygen, FP flight).</summary>
public enum CharPoint : byte { Hp, Mp, Ap, Sp, Dp, Op, Fp, MaxHp, MaxMp, MaxAp, MaxSp, MaxDp, MaxOp, MaxFp }

/// <summary>An inventory item on the wire (Common_Item). Container 1 bag, 11 equipped.</summary>
public sealed record InventoryItem(long Key, int ItemId, long Count, byte Container, int Slot, EquipDetail? Detail = null,
    bool Locked = false, string Maker = "", long ExpireTime = 0, bool ExpireFixed = false)
{
    public const byte Bag = 1, Storage = 2, AccountStorage = 3, Equipped = 11;
}

/// <summary>Equipment detail of an item: item level and equip slot (1 weapon, 3 helm, 4 shoulder, 5 torso, 6 pants, 7 gloves, 8 boots, 19 cape; 0 in the bag).</summary>
// Soul bind: the binding character's db id (the DbId of the character list) and the bind count;
// the client soul-binds an unbound item again on every equip attempt.
public sealed record EquipDetail(
    int ItemLevel, byte EquipSlot, byte EnchantLevel = 0, long SoulBindOwner = 0, byte SoulBindCount = 0,
    byte SurpassEnchantLevel = 0, int EnchantBonusProb = 0, int SurpassEnchantBonusProb = 0, int[]? Godstones = null,
    IReadOnlyList<(int Wire, int Value)>? RolledStats = null,
    byte SkinExtractionCount = 0);

/// <summary>A 0x561b change: type 0 add / 1 update, and the official reason codes (24 grant, 38 potion use observed).</summary>
public sealed record ItemModification(byte Type, InventoryItem Item, byte Increase = 0, byte Decrease = 0, byte Update = 0);

public sealed record MyCharacterAppear
{
    /// <summary>EToggleType flags (bit 0 SealStone), state mask bit 7.</summary>
    public int ToggleFlags { get; init; }
    /// <summary>ERelationshipEntity of the character (1 PC_Light_Common, 2 PC_Darkness_Common; 0 = not sent).</summary>
    public byte Relationship { get; init; }
    public int Key { get; init; } = 1;
    /// <summary>The character's db id (0 sends 1); soul-bound items name it as their owner.</summary>
    public long DbId { get; init; }
    public string Name { get; init; } = "";
    public int ServerId { get; init; } = 1204;
    public int PcId { get; init; } = 1;
    public int Race { get; init; } = 1;
    public int CharLevel { get; init; } = 1;
    public int EquipItemLevel { get; init; }
    public int HighestTotalItemLevel { get; init; }
    public int LevelMax { get; init; } = 1;
    public long Experience { get; init; }
    public int CombatLevel { get; init; } = 1;
    /// <summary>EAscensionGrade 1..6 (CANDIDATE: the CharDetail byte after reputation; 1 in the official starter).</summary>
    public int Ascension { get; init; } = 1;
    /// <summary>Equipped wing (Wing.dat id; 0 none). Flight and gliding need one: without it the client shows MSG_WING_EQUIP_NOTHING ("Flight available after Ascension").</summary>
    public int WingId { get; init; }
    /// <summary>Flight points (CharState fp / fp_max).</summary>
    public int Fp { get; init; } = 10000;
    public int FpMax { get; init; } = 10000;
    public long CombatExperience { get; init; }
    public int Gender { get; init; } = 1;
    public Vector3d Position { get; init; } = GamePackets.SpawnPosition;
    public double Direction { get; init; } = 360.0;
    public int Hp { get; init; } = GamePackets.SpawnHp;
    public int HpMax { get; init; } = GamePackets.SpawnHpMax;
    public int Mp { get; init; } = GamePackets.SpawnMp;
    public int MpMax { get; init; } = GamePackets.SpawnMp;
    public int MapId { get; init; } = GamePackets.SpawnMapId;
    /// <summary>Subzone.dat id at the spawn (_subzone_id on MyCharAppear).</summary>
    public int SubzoneId { get; init; }
    public long CombatPower { get; init; }
    public int AppearReason { get; init; }
    /// <summary>
    /// The arrival effect after a move (0x41 with instance-layer moves, the id 0x3647/0x3621 announced). Optional mask bit 12
    /// adds it as one byte before the customizing data: the official layer appearance (#9680) is the world entry's (#382) plus this byte
    /// (and the way back, #14311, also a bit-9 byte `01` before it, not sent). OBSERVED bytes, CANDIDATE section order.
    /// </summary>
    public byte TeleportEffect { get; init; }
    public int? WeaponId { get; init; }
    /// <summary>Visible equipment by EVisualEquipSlot (occupied slots); empty sends <see cref="WeaponId"/> alone.</summary>
    public IReadOnlyList<EquipBroad> Equips { get; init; } = [];
    public byte[] CustomizingData { get; init; } = [];
    public int VoiceType { get; init; }
    /// <summary>Mounted VehicleList id (_ride_data_id); omitted on the wire when 0 (world entry).</summary>
    public int RideDataId { get; init; }
}

/// <summary>An NPC as the client draws it (0x3641).</summary>
/// <param name="UniqueKey">MapData SpawnInfoList UniqueKey (low 32 bits), sent as spawn_unique_key; 0 sends the entity key.</param>
/// <param name="SocialAction">Looping idle animation (social action id) set in the appear; 0 none.</param>
/// <param name="SocialStart">When that animation started (Unix ms; the server's spawn time).</param>
/// <param name="Walking">A patrol walking its route (move_state 2 Walk).</param>
/// <param name="MoveState">EMoveStateModelType when not walking (7 FlyingOnAirStop for air NPCs), mask bit 0.</param>
/// <param name="MoveSubstate">Mask bit 1 (2 with every official flying NPC).</param>
/// <param name="Reason">EAppearReasonType (1 Normal; 6 NpcSpawn when a spawner starts it, OBSERVED with the HQ002 Drakan and vessel).</param>
/// <param name="SpawnAct">spawn_act_id, mask bit 12: the NpcData SpawnActName played on a spawn (the Drakan's drop from the vessel).</param>
/// <param name="Abnormals">The NPC's abnormals (the vessel's untargetable arrival); none by default.</param>
/// <param name="ParentKey">_parent_key, mask bit 4: the summoner's entity key for a player's summon; 0 none.</param>
/// <param name="Parent">_parent_desc, mask bit 5: the summoner's CharDesc (the "[Owner]" tag above a summon's name).</param>
public sealed record NpcAppearance(int Key, int NpcId, Vector3d Position, double Direction, int Hp, int HpMax,
    int Level, int Relationship, bool Aggressive, bool Dead, uint UniqueKey = 0, int SocialAction = 0, long SocialStart = 0, bool Walking = false,
    byte MoveState = 0, byte MoveSubstate = 0, byte Reason = 1, int SpawnAct = 0, IReadOnlyList<AbnormalWire>? Abnormals = null,
    int ParentKey = 0, CharDescWire? Parent = null)
{
    public const byte FlyingOnAirStop = 7, NpcSpawnReason = 6;
}

/// <summary>DevPacketData_Common_CharDesc with nickname, race and gender present (mask 0x07, as in 0x3645 CharAppear).</summary>
public sealed record CharDescWire(string Nickname, int PcId, int Race, int Gender);

/// <summary>
/// Server bodies of the game port (13328 locally). Port of tools/game_server.py and its helper
/// modules; every builder is parity-checked against the Python output (tests/fixtures).
/// Layouts are observed from official samples or readable client decoders where noted there;
/// they are not general serializers.
/// </summary>
public static class GamePackets
{
    // Map.dat World_L_Starter (Poeta), and the user-marked approximate first spawn.
    public const int SpawnMapId = 1000;
    public static readonly Vector3d SpawnPosition = new(7259.90771484375, -18223.455078125, 22404.498046875);
    public const int SpawnHp = 1500, SpawnHpMax = 1500, SpawnMp = 200, SpawnSp = 100000;

    /// <summary>Community server key in Login_RS and CommServerKey_RS (official shape: four digits).</summary>
    public const string CommServerKey = "5201";

    // EContentsUnlockType. Chat (45) stays locked at world entry and is unlocked by the
    // 0xe258 update, which raises System_ContentsUnlocked and starts the Lime chat login.
    public static readonly byte[] StarterUnlockedContents = [17, 42, 50, 52, 56, 61, 62];
    public static readonly byte[] LaterUnlockedContents = [45, 43];

    /// <summary>
    /// Level-gated EContentsUnlockType rows of ContentsUnlock.dat (UnlockLevel 2..50; 99 is
    /// "never" and quest-gated rows have level 0). The skill window (Skill, 2) opens at level 4.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int> ContentsUnlockLevels = new Dictionary<byte, int>
    {
        [1] = 2, [43] = 2, [32] = 3, [2] = 4, [60] = 4, [8] = 9, [19] = 9,
        [13] = 10, [10] = 10, [11] = 45, [30] = 10, [33] = 10, [44] = 10, [46] = 10, [53] = 10, [54] = 10,
        [22] = 25, [38] = 30, [5] = 30,
        [24] = 45, [21] = 45, [26] = 45, [27] = 45, [28] = 45, [29] = 45, [6] = 45, [7] = 45, [31] = 45,
        [34] = 45, [41] = 45, [40] = 45, [47] = 45, [48] = 45, [49] = 45, [57] = 45, [58] = 45,
        [59] = 50, [63] = 50,
    };

    /// <summary>The world-entry 0xe257 list for a level: the starter set and every level-gated type reached, except the 0xe258 ones.</summary>
    public static byte[] UnlockedContents(int level) =>
        StarterUnlockedContents.Concat(ContentsUnlockLevels.Where(u => u.Value <= level).Select(u => u.Key))
            .Except(LaterUnlockedContents).Distinct().Order().ToArray();

    /// <summary>
    /// Full 0xe257 merge at world entry: level and quest gates, ride/Daevanion extras, and Chat/Customizing once their
    /// quests are done (still withheld from 0xe257 until then so 0xe258 can raise System_ContentsUnlocked on first login).
    /// </summary>
    public static byte[] WorldEntryContentsUnlocks(int level, IEnumerable<byte> questUnlockTypes, IEnumerable<byte> extraTypes)
    {
        var quest = questUnlockTypes as IReadOnlySet<byte> ?? questUnlockTypes.ToHashSet();
        var withheld = LaterUnlockedContents.Where(t => !quest.Contains(t));
        return UnlockedContents(level).Concat(questUnlockTypes).Concat(extraTypes)
            .Except(withheld).Distinct().Order().ToArray();
    }

    /// <summary>
    /// PCLevelQuickSlot.dat level-1 rows (identical for all 8 classes, AscensionGrade_1): consumable bar slot -> item.
    /// Slots 11-18 are F1-F8 (QuickSlotData: Item type, bEditable); 14 and 18 start empty.
    /// </summary>
    public static readonly IReadOnlyList<(int Slot, int ItemId)> LevelQuickSlotItems =
        [(11, 530116001), (12, 530146001), (13, 530560001), (15, 531060004), (16, 531060003), (17, 531060027)];

    public static byte[] Ping(ulong counter, long nowMs) => PacketWriter.Opcode(0x3603).U16(0).U64(counter).I64(nowMs).ToArray();
    public static byte[] Clock(long nowMs) => PacketWriter.Opcode(0x3600).I64(nowMs).ToArray();
    public static byte[] Logout() => PacketWriter.Opcode(0x3631).U16(0).ToArray();
    public static byte[] RequestAck(ushort request) => PacketWriter.Opcode((ushort)(request + 1)).U16(0).ToArray();

    /// <summary>0x361a name check: code u16 (0 available) | name | u8 1.</summary>
    /// <summary>
    /// The client's EResult codes for the lobby (usmap): the name check 0x361a and CharCreate_RS 0x361c carry them,
    /// and the client picks its message from them (a duplicate name says so instead of the generic failure).
    /// </summary>
    public const int ResultOk = 0, NameDuplicate = 8228, NameBannedWord = 8227, NameInvalidLength = 8224, NameInvalidCharset = 8226,
        CreateNotEnoughSlot = 8240, CreateInvalidPcData = 8241, CreateInvalidName = 8242, CreateDuplicateName = 8243, CreateInvalidServerRace = 8244,
        CreateInvalidCustomizing = 8245, CreateErrorInternal = 8246, CreateInvalidSlot = 8247;

    public static byte[] NameCheck(string name, bool taken = false) => NameCheck(name, taken ? NameDuplicate : ResultOk);

    /// <summary>0x361a CharCheckName_RS {result u16 (EResult), name, reason u8 CreateCharacter}: the OBSERVED success is 0000 | name | 01.</summary>
    public static byte[] NameCheck(string name, int result)
    {
        if (Encoding.UTF8.GetByteCount(name) is < 1 or > 255) throw new ArgumentException("Character name must be 1..255 UTF-8 bytes");
        return PacketWriter.Opcode(0x361a).U16(result).String(name).U8(1).ToArray();
    }

    public static void WriteEquip(PacketWriter w, EquipBroad e)
    {
        w.I32(e.ItemId).U8(e.Enchant).U8(e.VisualSlot).I64(e.PeriodExpireTime).Bool(e.PeriodExpired).I32(e.SkinId);
        w.Varint(0);  // dye slots
        var godstones = e.Godstones ?? [];
        w.Varint(godstones.Count);
        foreach (var g in godstones) w.I32(g);
        w.Bool(e.Display).Bool(e.DefaultMesh).Bool(e.Override).Varint(e.FxBit);
    }

    /// <summary>LobbyCharDesc in usmap order; bools share the writer's open byte across records.</summary>
    public static void WriteLobbyCharacter(PacketWriter w, LobbyCharacter c)
    {
        w.I32(c.Key).String(c.Nickname).I32(c.PcId).U8(c.Race).U8(c.Gender).I32(c.Level).I32(c.ConquerorLevel)
            .I32(c.EquipItemLevel).I64(c.Experience).I32(c.CombatLevel).I64(c.CombatExperience).String(c.GuildName)
            .I64(c.LogoutTime).Vec3(c.Position).I32(c.ZoneId).I64(c.RegTime).I64(c.PendingDeleteTime);
        w.Varint(c.Equips.Count);
        foreach (var e in c.Equips) WriteEquip(w, e);
        w.I64(c.DbId).I32(c.BlockReasonNo).I64(c.BlockEndTime).I32(c.WingId).I32(c.WingSkinId)
            .Bytes(c.CustomizingData).U8(c.VoiceType).Bool(c.PreGenerated)
            .Varint(0)  // item count list
            .I64(c.LastNicknameChangeTime).U8(c.SlotNo).I64(c.CombatPower);
    }

    public static byte[] LobbyCharacterRecord(LobbyCharacter c)
    {
        var w = new PacketWriter();
        WriteLobbyCharacter(w, c);
        return w.ToArray();
    }

    /// <summary>Concatenated LobbyCharDesc records (shared bool packing), as in 0x3615/0x3626 bodies.</summary>
    public static byte[] LobbyCharacterRecords(IReadOnlyList<LobbyCharacter> characters)
    {
        var w = new PacketWriter();
        foreach (var c in characters) WriteLobbyCharacter(w, c);
        return w.ToArray();
    }

    /// <summary>
    /// 0x3615 Login_RS with a character list (official layout, rebuilt byte-for-byte from the
    /// sample). Result u16, chat level varint; local time = now + 7 h in the sample.
    /// </summary>
    public static byte[] LoginCharacters(IReadOnlyList<LobbyCharacter> characters, string accountSession, string serverKey,
        string commServerKey, long nowMs, long localOffsetMs = 7 * 3600 * 1000L, int extendedSlotCount = 0,
        bool isBornServer = true, int serverRace = 1, int chatEnableLevel = 30, int userFlag = 0, bool isAdmin = false)
    {
        var w = PacketWriter.Opcode(0x3615).U16(0).I64(nowMs).I64(nowMs + localOffsetMs).Varint(characters.Count);
        foreach (var c in characters) WriteLobbyCharacter(w, c);
        return w.U8(extendedSlotCount).Bool(false).Bool(false)  // reconnect, reconnect to lobby
            .String(accountSession).String(serverKey).String(commServerKey)
            .Bool(isBornServer).U8(serverRace).Bool(isAdmin)     // is admin (CANDIDATE: untested true)
            .Varint(chatEnableLevel).Bool(false)                 // chat race subscription
            .U8(userFlag).ToArray();
    }

    /// <summary>
    /// 0x3626 Lobby_RS, the reply to 0x3625 Lobby_RQ (back to character selection): result,
    /// characters, extended slot count (usmap). Result u16 and the character records as in
    /// 0x3615 Login_RS. CANDIDATE: no official sample.
    /// </summary>
    public static byte[] LobbyCharacters(IReadOnlyList<LobbyCharacter> characters, int extendedSlotCount = 0)
    {
        var w = PacketWriter.Opcode(0x3626).U16(0).Varint(characters.Count);
        foreach (var c in characters) WriteLobbyCharacter(w, c);
        return w.U8(extendedSlotCount).ToArray();
    }

    /// <summary>0x3A16 ExtendedCharacterSlot_NT: purchased lobby slot count (GlobalSetting lobby_pc_slot_max_extended_count cap).</summary>
    public static byte[] ExtendedCharacterSlot(int extendedSlotCount) => PacketWriter.Opcode(0x3A16).U8(extendedSlotCount).ToArray();

    /// <summary>
    /// 0xe337 CharSlotSwap_RS: result u16, count u8, then each RQ pair (char dbid i64, slot u8). The client rebuilds
    /// its lobby list from this echo; count must match the request list. No trailing lobby packets.
    /// </summary>
    public static byte[] CharSlotSwap(int result, IReadOnlyList<(long WireId, int Slot)> slots)
    {
        if (slots.Count is > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(slots));
        var w = PacketWriter.Opcode(SocialOpcodes.E3Base + 1481).U16(result).U8((byte)slots.Count);
        foreach (var (wireId, slot) in slots) w.I64(wireId).U8(slot);
        return w.ToArray();
    }

    /// <summary>0x37E0 CharCustomizingList_RS: result i32, Common_CharCustomizingInfo list (slot u8, bytes, voice u8, pc_id i32).</summary>
    public static byte[] CharCustomizingList(int result, IReadOnlyList<(int Slot, byte[] Customizing, int VoiceType, int PcId)> entries)
    {
        var w = PacketWriter.Opcode(0x37E0).I32(result).Varint(entries.Count);
        foreach (var (slot, customizing, voice, pcId) in entries)
            w.U8(slot).Bytes(customizing).U8(voice).I32(pcId);
        return w.ToArray();
    }

    /// <summary>0x37ED BroadcastCharCustomizingUpdate_NT: lobby pedestal appearance refresh (key i32, customizing, voice, pc_id).</summary>
    public static byte[] BroadcastCharCustomizingUpdate(int key, byte[] customizingData, int voiceType, int pcId) =>
        PacketWriter.Opcode(0x37ED).I32(key).Bytes(customizingData).U8(voiceType).I32(pcId).ToArray();

    /// <summary>
    /// 0x3620 SelectCharacter_RS; the key echoes the request. Wire prefix as in the official
    /// recording: u16 result, varint key, i32 last_using_pc_id, i64 dbid (offset 8 there). The
    /// client keeps this dbid as its own and compares soul-bound items' owner with it (live
    /// 2026-10-15: with i32 result/key it read wire id &lt;&lt; 40 | pc id &lt;&lt; 8). Tail after the
    /// dbid is kept as the client read it before (world entry worked): five zero bytes (CANDIDATE
    /// _char_key i32 and the option-data count), then our older char key, counts and customizing
    /// data. Without the five bytes the client stalled after selection (live 2026-10-15).
    /// </summary>
    public static byte[] SelectCharacter(int key = 1, long dbId = 1, int charKey = 1, int lastUsingPcId = 1,
        byte[]? customizingData = null, long? serverStartTime = null,
        IReadOnlyList<(int CondId, int SkillId, byte Priority)>? skillCustomizing = null)
    {
        var w = PacketWriter.Opcode(0x3620).U16(0).Varint(key).I32(lastUsingPcId).I64(dbId).Zeros(5).I32(charKey)
            .Varint(0);   // option data
        var rows = skillCustomizing ?? [];
        w.Varint(rows.Count);
        foreach (var (cond, skill, priority) in rows) w.I32(cond).I32(skill).U8(priority);
        return w.Bytes(customizingData ?? []).I64(serverStartTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToArray();
    }

    /// <summary>0x36EF SkillCustomizingUpdate_RS: result u16.</summary>
    public static byte[] SkillCustomizingUpdate(int result = 0) => PacketWriter.Opcode(0x36EF).U16(result).ToArray();

    /// <summary>0x361c CharCreate_RS: result + LobbyCharDesc generated from our state and the request's appearance.</summary>
    public static byte[] CharacterCreated(string name, int gender, int pcId, int race, int key, long dbId,
        byte[] customizingData, int voiceType, int slotNo, long nowMs, int level = 1, int result = 0)
    {
        var w = PacketWriter.Opcode(0x361c).I32(result);
        WriteLobbyCharacter(w, new LobbyCharacter
        {
            Key = key, Nickname = name, PcId = pcId, Race = race, Gender = gender, Level = level, CombatLevel = level,
            LogoutTime = nowMs, Position = Vector3d.Zero, ZoneId = 3016, RegTime = nowMs, CustomizingData = customizingData,
            VoiceType = voiceType, SlotNo = slotNo, DbId = dbId,
        });
        return w.ToArray();
    }

    /// <summary>
    /// 0x3621 PrepareEnterMap_NT: map to load and where (ints i32 except the u8 teleport effect). The effect byte is a
    /// TeleportEffect.dat id (0x41 = 65 Teleport_FieldEvent_CurrentPosition_001 at the observed layer move; 12 Teleport_NoEffect)
    /// and the cutscene name the level sequence the arrival plays (a PCTeleport directive's value 4; CANDIDATE: observed empty).
    /// </summary>
    public static byte[] PrepareEnterMap(int ticket, int mapId, Vector3d position, long gameTime, double direction = 0, byte reason = 0, byte effect = 0, string cutscene = "")
    {
        return PacketWriter.Opcode(0x3621).I32(ticket).I32(mapId).I64(gameTime).Vec3(position).F32(direction)
            .U8(reason).U8(0)    // reason (12 InstanceLayer, OBSERVED), env state
            .I64(0)         // env start time
            .Varint(0)      // group env states
            .U8(effect)     // teleport effect (0x41 with the instance-layer moves, OBSERVED)
            .String(cutscene)
            .U8(0).ToArray();  // inform type
    }

    /// <summary>0x3623 LoadComplete_RS: result u16, ticket i32, spawn Vec3, frame i32, char key u16.</summary>
    public static byte[] LoadComplete(int ticket, Vector3d position, int frame = 2, int charKey = 1) =>
        PacketWriter.Opcode(0x3623).U16(0).I32(ticket).Vec3(position).I32(frame).U16(charKey).ToArray();

    static void WriteSpawnDetailTail(PacketWriter w, int ascension = 1)
    {
        // Observed starter layout (official offsets 66..183); semantics partly unknown.
        w.U8(1).I32(1).I64(0).U8(0).U8(0).U8(5);
        for (var category = 1; category <= 5; category++) w.U8(category).I32(1).I64(0);
        // Glide/sprint cooldowns, reputation, then the ascension grade (CANDIDATE).
        w.I32(0).I32(0).I64(0).U8(ascension);
        for (var n = 0; n < 2; n++)
        {
            w.U8(3);
            for (var pointType = 1; pointType <= 3; pointType++) w.U8(pointType).U16(0);
        }
    }

    public static PacketWriter Stat(PacketWriter w, int type, int value) => w.U16(type).I32(value);

    /// <summary>0x3633 MyCharAppear_NT (client decoders 0x8498b80/0x91f3870).</summary>
    public static byte[] MyCharAppear(MyCharacterAppear a)
    {
        var w = PacketWriter.Opcode(0x3633).Varint(a.Key).U32(0x2006u | (a.TeleportEffect != 0 ? 0x1000u : 0)).U8(0x37).String(a.Name).U16(a.ServerId)
            .I32(a.PcId).U8(a.Race).I32(a.CharLevel).I32(a.EquipItemLevel).I32(a.HighestTotalItemLevel).I32(a.LevelMax)
            .I64(a.Experience).I32(a.CombatLevel).I64(a.CombatExperience).U8(a.Gender);
        WriteSpawnDetailTail(w, a.Ascension);
        // CharState (client decoder 0x8498b80, the one 0x3645 uses): u16 presence mask; bit 9 adds
        // _relationship_entity (u8) and bit 12 _wing_id (i32) after fp_max. Without the relationship the client
        // drew ordinary monsters as non-hostile (green names; live 2026-10-16), the official server draws them
        // red; other players' 0x3645 carry 1 PC_Light_Common in every sample (CANDIDATE for the own character).
        w.U16((a.ToggleFlags != 0 ? 1 << 7 : 0) | (a.Relationship != 0 ? 1 << 9 : 0) | (a.WingId != 0 ? 1 << 12 : 0)).Vec3(a.Position).F32(a.Direction).U16(0).U8(1);
        w.Varint(a.Hp).Varint(a.HpMax);
        foreach (var v in new[] { a.Mp, a.MpMax, 0, 0, SpawnSp, SpawnSp, 0, 10000, 0, 100000, 100000, a.Fp, a.FpMax }) w.I32(v);
        if (a.ToggleFlags != 0) w.I32(a.ToggleFlags);   // state bit 7 (the official own appearance: 1 = the seal stone on)
        if (a.Relationship != 0) w.U8(a.Relationship);
        if (a.WingId != 0) w.I32(a.WingId);
        w.U8(3);
        Stat(w, 206, -3000); Stat(w, 208, 300); Stat(w, 551, -3000);
        w.U8(0);  // no abnormal effects
        // EquipBroad records (the weapon alone was observed); their packed bool byte (display,
        // default mesh, override = 14) also carries purchased-customizing later (bit 4).
        var equips = a.Equips.Count > 0 ? a.Equips
            : a.WeaponId is { } weapon ? [new EquipBroad(weapon, VisualSlot: 1, Godstones: [0, 0, 0])] : [];
        w.Varint(equips.Count);
        foreach (var e in equips) WriteEquip(w, e);
        // _dbid sits just before the customizing data (official recording); the client compares
        // soul-bound items' _soul_bind_char_dbid with it.
        w.U8(a.AppearReason).I64(1).I32(a.MapId).I32(a.SubzoneId).I64(a.DbId == 0 ? 1 : a.DbId);
        // Mask bit 12: the arrival effect sits between the dbid block and the customizing data (OBSERVED #9680 vs #382:
        // `... 01 | 41 | de 02 <customizing>`; the world entry has `01 | de 02`). It was placed before the voice until
        // 2026-10-19, which shifted the customizing data the client reads.
        if (a.TeleportEffect != 0) w.U8(a.TeleportEffect);
        w.Bytes(a.CustomizingData);
        if (equips.Count == 0) w.U8(0);  // purchased-customizing = false
        w.U8(a.VoiceType).I32(0).I32(0).I64(0);
        if (a.RideDataId != 0) w.I32(a.RideDataId);
        return w.I64(a.CombatPower).I64(a.CombatPower).U8(0).U8(0).ToArray();
    }

    /// <summary>0x3646 CharBarrierInfo_NT: key | count | {uid varint, remain_barrier varint}. OBSERVED empty list at world entry.</summary>
    public static byte[] CharBarrier(int key = 1) => CharBarrierInfo(key, []);

    public static byte[] CharBarrierInfo(int key, IReadOnlyList<(int Uid, int Remain)> barriers)
    {
        var w = PacketWriter.Opcode(0x3646).Varint(key).Varint(barriers.Count);
        foreach (var (uid, remain) in barriers) w.Varint(uid).Varint(Math.Max(0, remain));
        return w.ToArray();
    }
    public static byte[] CharStatsUpdate(int key = 1, long statHpMax = 0) => PacketWriter.Opcode(0x364a).Varint(key).U8(0).I64(statHpMax).ToArray();

    /// <summary>0x364a CharStatsUpdate_NT with stats: key | count u8 | {u16 type, i32 value} (as 0x3649) | i64 hp max.</summary>
    public static byte[] CharStatsUpdate(int key, IReadOnlyList<(int Type, int Value)> stats, long statHpMax)
    {
        var w = PacketWriter.Opcode(0x364a).Varint(key).U8(stats.Count);
        foreach (var (type, value) in stats) Stat(w, type, value);
        return w.I64(statHpMax).ToArray();
    }
    public static byte[] MentoringRoleUpdate(int role = 1, long expireTime = 0) => PacketWriter.Opcode(0x3657).U8(role).I64(expireTime).ToArray();
    public static byte[] ContentsUnlockList(byte[]? types = null) => PacketWriter.Opcode(0xe257).Bytes(types ?? StarterUnlockedContents).ToArray();
    public static byte[] ContentsUnlockUpdate(byte[]? types = null) => PacketWriter.Opcode(0xe258).Bytes(types ?? LaterUnlockedContents).ToArray();
    public static byte[] CommServerKeyReply(string key = CommServerKey) => PacketWriter.Opcode(0x365a).U16(0).String(key).ToArray();

    /// <summary>Stat sheet of the official 0x3649 sample (usmap names; some wire mappings unverified).</summary>
    public static readonly IReadOnlyList<(int Type, int Value)> DefaultStats =
    [
        (31, 21), (33, 17), (89, 2500), (93, 1500), (96, 100000), (97, 100000), (100, 100), (128, 150),
        (193, 119), (195, 10), (199, 1500), (201, 250), (206, -3000), (208, 300), (211, 100000), (242, 100000),
        (244, 20000), (255, 200), (275, 5), (276, 25), (277, 5000), (295, 100000), (297, 20000), (300, 2000),
        (301, 4000), (307, 38), (318, 150), (377, 500), (551, -3000),
    ];

    /// <summary>HPMax (193) and MPMax (199) follow the configured spawn pools.</summary>
    public static IReadOnlyList<(int Type, int Value)> SpawnStats() =>
        DefaultStats.Select(s => s.Type switch { 193 => (s.Type, SpawnHpMax), 199 => (s.Type, SpawnMp), _ => s }).ToList();

    /// <summary>0x3649 CharAllStats_RS: result u16 | count u8 | {u16 type, i32 value} | i64 hp max.</summary>
    public static byte[] CharAllStats(IReadOnlyList<(int Type, int Value)> stats, long statHpMax)
    {
        var w = PacketWriter.Opcode(0x3649).U16(0).U8(stats.Count);
        foreach (var (type, value) in stats) Stat(w, type, value);
        return w.I64(statHpMax).ToArray();
    }

    /// <summary>
    /// Replies to the bodyless world-entry queries after 0x3648: the official recording's
    /// bodies (fields not decoded). The 0x8a1c reply also carries the chat unlock update.
    /// </summary>
    public static readonly IReadOnlyDictionary<ushort, byte[]> WorldEntryQueryReplies = new Dictionary<ushort, byte[]>
    {
        [0x4300] = Convert.FromHexString(
            "01430100010000000000ccdb3ca1010000c0f3e43ca1010000050101000000000000000000"
            + "000000000000000000010200000000000000000000000000000000000001030000000000"
            + "000000000000000000000000000104000000000000000000000000000000000000010500"
            + "0000000000000000000000000000000000"),
        [0x570e] = Convert.FromHexString("0f57000000"),
        [0x56a7] = Convert.FromHexString("a85600000000000000000000"),
    };

    /// <summary>
    /// Synthetic 0x3615 game-login reply (single-sample candidate layout; unexplained bytes are
    /// zero). The 824-byte tail carries the record, world, session and keys at observed offsets.
    /// </summary>
    public static byte[] GameLogin(string name, Vector3d position, long nowMs, int classTemplate = 0x2A14, int worldId = 1204,
        string accountSid = "721357", string? guid = null, int? nameCode = null, int race = 0, int gender = 0,
        int detailFlag = 0, int equipLevel = 0, int level = 0, int zoneId = 0)
    {
        guid ??= "00000000-0000-0000-0000-000000000000";
        var tail = new byte[824];
        tail[0] = 1;
        BitConverter.TryWriteBytes(tail.AsSpan(1, 4), (uint)classTemplate);
        var encodedName = Encoding.ASCII.GetBytes(name);
        if (6 + encodedName.Length > 57) throw new ArgumentException("Game-login name leaves no room for the fixed tail");
        tail[5] = (byte)encodedName.Length;
        encodedName.CopyTo(tail, 6);
        if (nameCode is { } code) BitConverter.TryWriteBytes(tail.AsSpan(6 + encodedName.Length, 2), (ushort)code);
        if (race != 0 || gender != 0 || detailFlag != 0)
        {
            if (encodedName.Length != 12) throw new ArgumentException("Race and gender offsets are for a 12-byte name");
            tail[22] = (byte)race; tail[23] = (byte)gender; tail[24] = (byte)detailFlag;
        }
        if (equipLevel != 0) BitConverter.TryWriteBytes(tail.AsSpan(32, 4), (uint)equipLevel);
        if (level != 0) BitConverter.TryWriteBytes(tail.AsSpan(44, 4), level);
        BitConverter.TryWriteBytes(tail.AsSpan(57, 8), nowMs);
        var vec = new PacketWriter().Vec3(position).ToArray();
        vec.CopyTo(tail, 65);
        if (zoneId != 0) BitConverter.TryWriteBytes(tail.AsSpan(77, 2), (ushort)zoneId);
        BitConverter.TryWriteBytes(tail.AsSpan(81, 8), nowMs);
        BitConverter.TryWriteBytes(tail.AsSpan(372, 4), (uint)worldId);
        var account = Encoding.ASCII.GetBytes(accountSid + ":" + guid);
        tail[767] = (byte)account.Length;
        account.CopyTo(tail, 768);
        tail[811] = 4;
        Encoding.ASCII.GetBytes(worldId.ToString(System.Globalization.CultureInfo.InvariantCulture)).AsSpan(0, 4).CopyTo(tail.AsSpan(812));
        tail[816] = 4;
        Encoding.ASCII.GetBytes(CommServerKey).CopyTo(tail, 817);
        // After the comm key: server race 1, chat enable level 0 (left 0 on purpose), user flag 0.
        tail[821] = 1;
        return PacketWriter.Opcode(0x3615).U16(0).I64(nowMs).I64(nowMs - 25200000).Raw(tail).ToArray();
    }

    // ---------- Skills, equipment ----------

    /// <summary>
    /// 0x5101 AddSkillList_NT (OBSERVED after official level-ups): key | count | entries as 0x5100 |
    /// ESkillAcquireReason (1 LevelUp).
    /// </summary>
    public static byte[] AddSkillList(int key, IReadOnlyList<(int SkillId, int Level)> skills, byte reason = 1,
        IReadOnlyDictionary<int, int>? autoLoad = null, IReadOnlyDictionary<int, int>? originalLevels = null)
    {
        var w = PacketWriter.Opcode(0x5101).Varint(key).U8(skills.Count);
        foreach (var (id, level) in skills)
        {
            var original = originalLevels?.GetValueOrDefault(id) ?? level;
            WriteSkill(w, id, level, original, autoLoad);
        }
        return w.U8(reason).ToArray();
    }

    /// <summary>
    /// One Common_Skill entry of 0x5100 / 0x5101. Flag 1 is the ready entry (id, level, original level, nine zero bytes); flag 5
    /// is the AutoLoad (charge) variant with one more byte, the charge count (OBSERVED in the official starter 0x5100: one flag-5
    /// entry, count 3; tests/test_starter_skills.py). The client blocks a cast of an AutoLoad skill whose count is 0 without
    /// sending anything (usmap SkillAction_NotAutoLoadStack / NotAutoLoadReady), so a flag-1 entry leaves such a skill dead.
    /// </summary>
    static void WriteSkill(PacketWriter w, int id, int level, int original, IReadOnlyDictionary<int, int>? autoLoad)
    {
        if (autoLoad is not null && autoLoad.TryGetValue(id, out var count))
        {
            w.U8(5).I32(id).U8(level).U8(original).Zeros(9).U8(count);
            return;
        }
        w.U8(1).I32(id).U8(level).U8(original).Zeros(9);
    }

    /// <summary>
    /// 0x3822 SkillAutoLoadUpdate_NT (dev 0x36CB + 0x157, usmap order): skill i32 | charge count u8 | time i32 (ms until the
    /// next charge, 0 when full). CANDIDATE: the live opcode (same +0x157 as the 0x383f/0x3840 neighbours) and the time's unit.
    /// </summary>
    public static byte[] SkillAutoLoadUpdate(int skillId, int count, int timeMs) =>
        PacketWriter.Opcode(0x3822).I32(skillId).U8(count).I32(timeMs).ToArray();

    /// <summary>
    /// 0x9501 PcSocialActionInfo_NT (dev 0x3682): the owned emote ids, varint count + i32 each. OBSERVED identical at the
    /// official world entry of a level 1 (2026-10-13) and a level 3 character (2026-10-20): 2..31, 47, 50..54.
    /// </summary>
    public static readonly int[] DefaultSocialActions = [.. Enumerable.Range(2, 30), 47, .. Enumerable.Range(50, 5)];
    public static byte[] SocialActionInfo(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(0x9501).Varint(ids.Count);
        foreach (var id in ids) w.I32(id);
        return w.ToArray();
    }

    /// <summary>
    /// 0x3900 (163 B at the official world entry, unnamed in the catalogue): per class active skill i32 id, u8 3, then three
    /// {u8 slot 1..3, i32 0} option slots (OBSERVED 2026-10-13 with 3 skills, 2026-10-20 with 8). CANDIDATE: skill option slots.
    /// </summary>
    public static byte[] SkillOptionSlots(IReadOnlyList<int> skills, IReadOnlyDictionary<int, IReadOnlyList<int>>? optionSkillIds = null)
    {
        var w = PacketWriter.Opcode(0x3900).Varint(skills.Count);
        foreach (var skill in skills)
        {
            w.I32(skill).U8(3);
            var options = optionSkillIds?.GetValueOrDefault(skill);
            for (var i = 0; i < 3; i++)
                w.U8((byte)(i + 1)).I32(options is not null && i < options.Count ? options[i] : 0);
        }
        return w.ToArray();
    }

    /// <summary>
    /// 0x3818 ChainSkillActiveList_NT (dev 0x36C1 + 0x157): drives the combo prompt (next skill icon + key hint).
    /// Usmap: _prev_skill_id i32, _skill_list array of i32; window length comes from Skill.dat ChainSkillAvailableTime on the client.
    /// OBSERVED official 2026-10-30 Ranger: 18 38 a0edd500 01 b014d600 right after the 0x3802 of 14020000.
    /// </summary>
    public static byte[] ChainSkillActiveList(int prevSkillId, IReadOnlyList<int> chainSkills)
    {
        var w = PacketWriter.Opcode(0x3818).I32(prevSkillId).Varint(chainSkills.Count);
        foreach (var id in chainSkills) w.I32(id);
        return w.ToArray();
    }

    /// <summary>
    /// 0x3819 ChainSkillDeactiveList_NT (dev 0x36C2 + 0x157): chain skills whose window closed or were consumed.
    /// OBSERVED official 2026-10-30: 19 38 01 b014d600 when 14030000 is cast, after its own 0x3818.
    /// </summary>
    public static byte[] ChainSkillDeactiveList(IReadOnlyList<int> chainSkills)
    {
        var w = PacketWriter.Opcode(0x3819).Varint(chainSkills.Count);
        foreach (var id in chainSkills) w.I32(id);
        return w.ToArray();
    }

    /// <summary>
    /// Empty lists of the official world entry (each "opcode 00", OBSERVED 2026-10-20 for a character with none):
    /// 0x8d58 MyPrevSeasonRanking_NT, 0x8aaf AgitDecoList_NT. The potion and auto-use lists come from the character's settings.
    /// </summary>
    public static IReadOnlyList<byte[]> EmptyEntryLists() => [[0x58, 0x8d, 0x00], [0xaf, 0x8a, 0x00]];

    /// <summary>CharRestStart_RS 0x3748 / CharRestStop_RS 0x374b: key, u16 result (usmap; result width as the sibling 0x3743 OBSERVED).</summary>
    public static byte[] RestReply(bool start, int key, int result = 0) => PacketWriter.Opcode((ushort)(start ? 0x3748 : 0x374b)).Varint(key).U16(result).ToArray();
    /// <summary>CharRestStart_NT 0x3749 / CharRestStop_NT 0x374c: key (usmap), for the players around.</summary>
    public static byte[] RestNotice(bool start, int key) => PacketWriter.Opcode((ushort)(start ? 0x3749 : 0x374c)).Varint(key).ToArray();

    /// <summary>
    /// 0x5105 SkillPointUpdate_NT (usmap DevPacketData_GameServer_SkillPointUpdate_NT, wire opcode +0x157 from 0x3746):
    /// varint count | CommonSkillPointInfo { mask u8 0x01, ESkillPointType u8, _point varint } per entry.
    /// OBSERVED after official SkillLevelUp (#18470-18472 and 2026-10-31 captures): 15 points left → body 0101010f.
    /// </summary>
    public static byte[] SkillPointUpdate(int pointsLeft, byte pointType = 1) =>
        PacketWriter.Opcode(0x5105).Varint(1).AppendSkillPointInfo(pointsLeft, pointType).ToArray();

    /// <summary>One CommonSkillPointInfo entry (mask 0x01, type, point varint) inside 0x5105, 0x3844._modify_points, etc.</summary>
    public static PacketWriter AppendSkillPointInfo(this PacketWriter w, int point, byte pointType = 1) =>
        w.U8(1).U8(pointType).Varint(point);

    /// <summary>0x5100 skill list: count + ready entries (flag 1, i32 id, level, original level, nine zero bytes).</summary>
    public static byte[] SkillList(IReadOnlyList<(int SkillId, int Level)> skills, IReadOnlyDictionary<int, int>? originalLevels = null,
        IReadOnlyDictionary<int, int>? autoLoad = null)
    {
        if (skills.Count > 127 || skills.Select(s => s.SkillId).Distinct().Count() != skills.Count) throw new ArgumentException("Expected at most 127 unique skills");
        var w = PacketWriter.Opcode(0x5100).U8(skills.Count);
        foreach (var (id, level) in skills)
        {
            if (id <= 0 || level is < 1 or > 255) throw new ArgumentException("Invalid skill ID or level");
            var original = originalLevels?.GetValueOrDefault(id) ?? level;
            if (original is < 1 or > 255 || original > level) throw new ArgumentException("Invalid original skill level");
            // Both official recordings write the dodge (11000100) as "03 01 <id>"; sending that made
            // the live client ignore Shift entirely (2026-10-14), so the plain "01 <id>" form stays.
            WriteSkill(w, id, level, original, autoLoad);
        }
        return w.ToArray();
    }

    /// <summary>
    /// 0x5600 QuickSlotList_NT from saved slots: per slot the observed 42-byte record (01 | slot | EQuickSlotType |
    /// item uid i64 | item id i32 | skill id i32 | 4 context skills | macro, social action, toggle), as SkillSlots.
    /// Item slots carry the uid and template (CANDIDATE: only skill records were observed).
    /// </summary>
    public static byte[] QuickSlots(IReadOnlyList<(int Slot, int Type, long ItemUid, int ItemId, int SkillId)> slots)
    {
        if (slots.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).U8(slots.Count);
        foreach (var (slot, type, uid, item, skill) in slots) WriteQuickSlot(w, slot, type, uid, item, skill);
        return w.ToArray();
    }

    public static byte[] QuickSlots(IReadOnlyList<(int Slot, int Type, long ItemUid, int ItemId, int SkillId, IReadOnlyList<int>? Context)> slots)
    {
        if (slots.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).U8(slots.Count);
        foreach (var (slot, type, uid, item, skill, context) in slots) WriteQuickSlot(w, slot, type, uid, item, skill, context);
        return w.ToArray();
    }

    /// <summary>
    /// One QuickSlotInfo (OBSERVED, all 14 records of #376 parse to the byte): presence mask u8 (bit 0: the context-skill
    /// array), slot, EQuickSlotType (0 skill, 1 item), item uid i64, item id i32, skill id i32, [count u8 + i32s], macro u8,
    /// social action i32, toggle u8. Skill slots carry 4 context skills (the skill, then its chain), item slots none.
    /// </summary>
    public static PacketWriter WriteQuickSlot(PacketWriter w, int slot, int type, long uid, int item, int skill, IReadOnlyList<int>? contextSkills = null)
    {
        var context = type == 0 && skill != 0;
        w.U8(context ? 1 : 0).U8(slot).U8(type).I64(uid).I32(item).I32(skill);
        if (context)
        {
            var ids = contextSkills is { Count: >= 4 } ? contextSkills : [skill, 0, 0, 0];
            w.U8(4);
            for (var i = 0; i < 4; i++) w.I32(ids[i]);
        }
        return w.U8(0).I32(0).U8(0);
    }

    /// <summary>0x5600 observed 42-byte skill-slot records.</summary>
    public static byte[] SkillSlots(IReadOnlyList<(int Slot, int SkillId)> slots)
    {
        if (slots.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).U8(slots.Count);
        foreach (var (slot, skill) in slots) WriteQuickSlot(w, slot, 0, 0, 0, skill);
        return w.ToArray();
    }

    // ---------- Inventory (decoded from the official recordings; samples rebuilt byte-for-byte) ----------

    /// <summary>
    /// 0x5611 ItemDepotList_RS, the reply to the empty 0x5610 request: u16 0x0210 (unexplained,
    /// both official samples), the items, then six slot-extend counts. Bag items are container 1,
    /// equipped items container 11 with slot = equip slot. Replaces the 0x5611 candidate below,
    /// which was one byte off (its packed-bool byte came from a later record of the sample).
    /// </summary>
    public static byte[] ItemDepotList(IReadOnlyList<InventoryItem> items)
    {
        var w = PacketWriter.Opcode(0x5611).U16(0x0210).Varint(items.Count);
        foreach (var item in items) WriteItem(w, item);
        return w.Zeros(6).ToArray();
    }

    /// <summary>0x561b ItemModificationList_NT: per entry type (0 add, 1 update; CANDIDATE), the item, increase/decrease/update reasons.</summary>
    public static byte[] ItemModificationList(IReadOnlyList<ItemModification> changes)
    {
        var w = PacketWriter.Opcode(0x561b).Varint(changes.Count);
        foreach (var change in changes) WriteItem(w.U8(change.Type), change.Item).U8(change.Increase).U8(change.Decrease).U8(change.Update);
        return w.ToArray();
    }

    // Soul binding (equipping an unbound item binds it first). Opcodes from the catalogue order
    // anchored on 0x5611/0x561b/0x561c/0x561d/0x5672 (one offset fits all five); layouts from the
    // usmap. Result as u16, as in the other observed replies. CANDIDATE until a live reply works.

    /// <summary>0x562c ItemSoulBindStart_RS: result, item uid, cost item uid, cost item id (echoing 0x562b).</summary>
    public static byte[] SoulBindStart(long uid, long costUid, int costId, int result = 0) =>
        PacketWriter.Opcode(0x562c).U16(result).I64(uid).I64(costUid).I32(costId).ToArray();
    /// <summary>0x562e ItemSoulBindCancel_RS: result, item uid.</summary>
    public static byte[] SoulBindCancel(long uid, int result = 0) => PacketWriter.Opcode(0x562e).U16(result).I64(uid).ToArray();
    /// <summary>0x5630 ItemSoulBindComplete_RS: result, item uid, bind count, no bound stats or skills.</summary>
    public static byte[] SoulBindComplete(long uid, byte count = 1, int result = 0) =>
        PacketWriter.Opcode(0x5630).U16(result).I64(uid).U8(count).Varint(0).Varint(0).ToArray();

    /// <summary>0x561d ItemEquipLevelChange_NT: equipped item level total and the highest reached.</summary>
    public static byte[] ItemEquipLevelChange(int level, int highest) => PacketWriter.Opcode(0x561d).I32(level).I32(highest).ToArray();

    /// <summary>0x3656 CharCombatPowerUpdate_NT: current and highest combat power (usmap: two i64).</summary>
    public static byte[] CharCombatPowerUpdate(long combatPower, long highestCombatPower) =>
        PacketWriter.Opcode(0x3656).I64(combatPower).I64(highestCombatPower).ToArray();

    /// <summary>
    /// Common_Item on the wire: detail flag, key, template, count, container, slot, locked (packed
    /// bool), maker, expiry, fixed-expiry (packed bool), then for equipment the EquipItemDetail
    /// (enchant, item level, broken, equip slot, magicstone/godstone/array tails as observed).
    /// </summary>
    static PacketWriter WriteItem(PacketWriter w, InventoryItem item)
    {
        w.U8(item.Detail is null ? 0 : 1).I64(item.Key).I32(item.ItemId).I64(item.Count).U8(item.Container).I32(item.Slot)
            .Bool(item.Locked).String(item.Maker).I64(item.ExpireTime).Bool(item.ExpireFixed);
        if (item.Detail is { } d)
        {
            w.U8(d.EnchantLevel).I32(0).I32(d.EnchantBonusProb).U8(d.SurpassEnchantLevel).I32(d.SurpassEnchantBonusProb).I32(d.ItemLevel).Bool(false).U8(d.EquipSlot)
                // One magicstone slot (7 zero bytes observed; usmap id, stat, grade), then
                // _soul_bind_char_dbid i64 and _soul_bind_count u8 (usmap order; CANDIDATE placement).
                .Varint(1).Zeros(7).I64(d.SoulBindOwner).U8(d.SoulBindCount);
            var gs = d.Godstones ?? [0, 0, 0];
            w.Varint(3);
            for (var i = 0; i < 3; i++) w.I32(i < gs.Length ? gs[i] : 0);
            w.Varint(0);
            var subs = d.RolledStats ?? [];
            w.Varint(subs.Count);
            foreach (var (wire, value) in subs) Stat(w, wire, value);
            w.Varint(0).Varint(0).Varint(0).Varint(0).Varint(0).U8(d.SkinExtractionCount);
        }
        return w;
    }

    /// <summary>Candidate 0x5611 equipped inventory with one item (observed record widths).</summary>
    public static byte[] EquippedInventory(int itemId, int itemLevel = 1, long itemKey = 1, int equipSlot = 1) =>
        PacketWriter.Opcode(0x5611).U8(0x10).U8(2).U8(1)
            .U8(1).I64(itemKey).I32(itemId).I64(1).U8(11).I32(equipSlot)
            .Zeros(23).I32(itemLevel).U8(1).U8(equipSlot).Zeros(16).U8(3).Zeros(20).Zeros(9).ToArray();

    // ---------- Combat ----------

    /// <summary>0x3804 hit (PlayerPackets.SkillEffect switch 04; action uid must match the cast's 0x3802).</summary>
    public static byte[] SkillHit(int targetKey, int actorKey, int skillId, int effectId, int damage, bool critical = false,
        byte actionUid = 0, int seq = 1, int attackSpeed = 10000)
    {
        if (seq is < 0 or > 255) throw new ArgumentException("Hit sequence must fit one byte");
        return PlayerPackets.SkillEffect(targetKey, actorKey, skillId, actionUid, effectId, seq, damage, attackSpeed, critical ? 3 : 2);
    }

    /// <summary>0x3804 hit of a projectile (switch 0x14, OBSERVED official Ranger 2026-10-30); seq matches its 0x3803.</summary>
    public static byte[] ProjectileHit(int targetKey, int actorKey, int skillId, int effectId, int damage, bool critical,
        byte actionUid, int seq, int attackSpeed = 10000)
    {
        if (seq is < 0 or > 255) throw new ArgumentException("Hit sequence must fit one byte");
        return PlayerPackets.ProjectileEffect(targetKey, actorKey, skillId, actionUid, effectId, seq, damage, attackSpeed, critical ? 3 : 2);
    }

    /// <summary>0x3804 switch 06: an NPC skill hit (usmap SkillEffect_NT; see <see cref="PlayerPackets.NpcSkillEffect"/>).</summary>
    public static byte[] NpcSkillHit(int targetKey, int actorKey, int skillId, int actionSeq, int effectId, int damage, int attackSpeed = 10000, int effectSeq = 1)
    {
        if (actionSeq is < 0 or > 255) throw new ArgumentException("Action sequence must fit one byte");
        return PlayerPackets.NpcSkillEffect(targetKey, actorKey, skillId, (byte)actionSeq, effectId, effectSeq, damage, attackSpeed);
    }

    /// <summary>0xe005 UpdateGroggyInfo_NT (dev 0x3b04 + 0xa501): groggy guard energy on a boss (mask 03 max+energy).</summary>
    public static byte[] UpdateGroggyInfo(int key, int maxEnergy, int energy, bool activated = false, byte state = 1)
    {
        var w = PacketWriter.Opcode(0xe005).Varint(key).U8(3).U8(state).I32(maxEnergy).I32(energy);
        return w.U8(activated ? (byte)2 : (byte)0).ToArray();
    }

    /// <summary>0xe002 GroupRequestProgressed_NT (dev 0x3b01 + 0xa501).</summary>
    public static byte[] GroupRequestProgressed(long groupKey, int charKey) =>
        PacketWriter.Opcode(0xe002).I64(groupKey).Varint(charKey).ToArray();

    /// <summary>0xe004 GroupRequestComplete_NT (dev 0x3b03 + 0xa501).</summary>
    public static byte[] GroupRequestComplete(long groupKey) => PacketWriter.Opcode(0xe004).I64(groupKey).ToArray();

    public static int HeadingU16(double degrees) => (int)Math.Round(PyMod(degrees, 360) / 360 * 65536) & 0xFFFF;

    /// <summary>Python-style float modulo (sign of the divisor).</summary>
    public static double PyMod(double value, double divisor)
    {
        var r = value % divisor;
        return r != 0 && (r < 0) != (divisor < 0) ? r + divisor : r;
    }

    /// <summary>0x3641 NpcAppear for an ordinary standing NPC (official recording layout).</summary>
    /// <summary>
    /// 0x3641 NpcAppear_NT. The u16 after the key is a field-presence mask (OBSERVED on all 42 official friendly appears,
    /// research/local/npc-behaviour-20261017): 0x0001 move_state (2 Walk, a patrol), 0x0004 reason (1 Normal), 0x0008
    /// aggressive, 0x0400 social action {id i32, state 1 Start, env i32 0} + 0x0800 its start time (i64 Unix ms, after
    /// the level), 0x2000 spawn_unique_key (the MapData UniqueKey). The usmap's NpcAppear_NT has exactly 16 optional fields
    /// (every field but _key, _desc, _state, the arrays and _char_level), which puts 0x0010 at _parent_key and 0x0020 at
    /// _parent_desc (bits 6-9: parent guild desc, waypoints id/frame, groggy; 14/15: berserk time, polymorph). CANDIDATE: the
    /// parent key as a varint like the other entity keys of the 0x36 run.
    /// </summary>
    public static byte[] NpcAppear(NpcAppearance npc, uint? spawnTag = null)
    {
        var social = npc.SocialAction != 0 && !npc.Dead;
        var moveState = npc.Walking ? (byte)2 : npc.MoveState;
        var mask = 0x2004 | (npc.Aggressive ? 0x08 : 0) | (moveState != 0 ? 0x01 : 0) | (npc.MoveSubstate != 0 ? 0x02 : 0) | (social ? 0x0C00 : 0)
            | (npc.SpawnAct != 0 ? 0x1000 : 0) | (npc.ParentKey != 0 ? 0x10 : 0) | (npc.Parent is null ? 0 : 0x20);
        var w = PacketWriter.Opcode(0x3641).Varint(npc.Key).U16(mask).U8(0).U32((uint)npc.NpcId).U8(0).U8(2)
            .Vec3(npc.Position).F32(npc.Direction).U16(HeadingU16(npc.Direction)).U8(npc.Dead ? 0 : 1)
            .Varint(npc.Hp).Varint(npc.HpMax);
        foreach (var v in new[] { 100, 100, 0, 0, 0, 0, 100, 100, 1, 0, 0, 0, 0 }) w.I32(v);
        w.U8(npc.Relationship);
        PlayerPackets.WriteAbnormals(w, npc.Abnormals ?? []);
        if (moveState != 0) w.U8(moveState);
        if (npc.MoveSubstate != 0) w.U8(npc.MoveSubstate);
        w.U8(npc.Reason);
        if (npc.Aggressive) w.U8(1);
        if (npc.ParentKey != 0) w.Varint(npc.ParentKey);
        if (npc.Parent is { } parent) w.U8(0x07).String(parent.Nickname).I32(parent.PcId).U8(parent.Race).U8(parent.Gender);
        w.U8(0).I32(npc.Level);
        if (social) w.I32(npc.SocialAction).U8(1).I32(0).I64(npc.SocialStart);
        if (npc.SpawnAct != 0) w.I32(npc.SpawnAct);
        return w.U32(spawnTag ?? (npc.UniqueKey != 0 ? npc.UniqueKey : (uint)npc.Key)).U8(0).ToArray();
    }

    /// <summary>
    /// 0xe239 ElementalSummonActivate_NT (dev 0x3b3e): official wire (2026-10-31 Fire→Water captures) uses the same
    /// optional-field convention as 0x5105 CommonSkillPointInfo — presence mask u8, then masked fields with ints as varint.
    /// Body: mask 0x01 (group + spirit key; _life_time_ms not on wire), EElementalSummonGroupType u8, _elemental_char_key
    /// varint (the spirit entity key from 0x3641). Server-side expiry still uses <see cref="SpiritLifeTimeMs"/>.
    /// </summary>
    public const int SpiritLifeTimeMs = 3_600_000;
    /// <summary>Field mask on 0xe239/0xe23a: group type + spirit entity key present; lifetime omitted.</summary>
    public const byte ElementalSummonWireMask = 0x01;

    /// <summary>Mask, group u8, spirit key varint — shared by 0xe239 and 0xe23a (see <see cref="AppendSkillPointInfo"/>).</summary>
    public static PacketWriter AppendElementalSummonWire(this PacketWriter w, byte group, int spiritCharKey) =>
        w.U8(ElementalSummonWireMask).U8(group).Varint(spiritCharKey);

    public static byte[] ElementalSummonActivate(byte group, int spiritCharKey) =>
        PacketWriter.Opcode(0xe239).AppendElementalSummonWire(group, spiritCharKey).ToArray();

    /// <summary>0xe23a ElementalSummonDeactivate_NT (dev 0x3b3f): same mask + group + spirit key varint as activate.</summary>
    public static byte[] ElementalSummonDeactivate(byte group, int spiritCharKey) =>
        PacketWriter.Opcode(0xe23a).AppendElementalSummonWire(group, spiritCharKey).ToArray();

    /// <summary>0x8d00 entity points: kind 2 (i64), one entry, type 0 (HP). HP 0 is the kill.</summary>
    /// <summary>
    /// 0x8d00 CharPointUpdate_NT: key | mask (1: i32 map, 2: i64 map) | n,{type u8, i32} | n,{type u8, i64}.
    /// The official server drives the player's HP/MP/SP bars (and the stamina gauge above the name)
    /// with this, never with 0x364c; HP and MaxHP travel in the i64 map. OBSERVED (both recordings).
    /// </summary>
    public static byte[] CharPoints(int key, IReadOnlyList<(CharPoint Type, int Value)> ints, IReadOnlyList<(CharPoint Type, long Value)>? longs = null)
    {
        longs ??= [];
        var w = PacketWriter.Opcode(0x8d00).Varint(key).U8((ints.Count > 0 ? 1 : 0) | (longs.Count > 0 ? 2 : 0));
        if (ints.Count > 0) { w.Varint(ints.Count); foreach (var (type, value) in ints) w.U8((byte)type).I32(value); }
        if (longs.Count > 0) { w.Varint(longs.Count); foreach (var (type, value) in longs) w.U8((byte)type).I64(value); }
        return w.ToArray();
    }

    /// <summary>0x8d03 SprintCooltimeUpdate_NT: key, cooltime ms (0 observed).</summary>
    public static byte[] SprintCooltime(int key, int ms = 0) => PacketWriter.Opcode(0x8d03).Varint(key).I32(ms).ToArray();
    /// <summary>0x3741 CharMoveSprintOn_RS: key, u16 result (0 observed), answering 0x3704.</summary>
    public static byte[] SprintOnReply(int key, int result = 0) => PacketWriter.Opcode(0x3741).Varint(key).U16(result).ToArray();
    /// <summary>0x3743 CharGlideStart_RS (ActionProtocol_RS: key, result), answering 0x3706 CharGlideStart_RQ. Catalogue position; CANDIDATE.</summary>
    public static byte[] GlideStartReply(int key, int result = 0) => PacketWriter.Opcode(0x3743).Varint(key).U16(result).ToArray();
    /// <summary>0x3744 CharFlyStart_RS (ActionProtocol_RS: key, result), answering 0x370a CharFlyStart_RQ. Catalogue position; CANDIDATE.</summary>
    public static byte[] FlyStartReply(int key, int result = 0) => PacketWriter.Opcode(0x3744).Varint(key).U16(result).ToArray();
    /// <summary>
    /// 0x3745 CharRequestAirPath_RS (dev 0x36A2 + 0xa3), answering 0x3719 CharRequestAirPath_RQ. OBSERVED (official capture
    /// 2026-10-30 14:21, five replies to a quest autopath "switch to flight" Confirm): result u16 (0), ticket u8 (1), is_quest
    /// bool (1), start Vec3, target Vec3, terrains (varint count, one ETerrainType u8 per point, all 8 Air), paths (varint count,
    /// Vec3 each). The path runs from the target back to the start: the first point is the target, the last the start.
    /// </summary>
    public static byte[] RequestAirPathReply(bool isQuest, Vector3d start, Vector3d target, int result = 0, byte ticket = 1,
        IReadOnlyList<(Vector3d Point, byte Terrain)>? path = null)
    {
        var w = PacketWriter.Opcode(0x3745).U16(result).U8(ticket).Bool(isQuest).Vec3(start).Vec3(target);
        path ??= [];
        w.Varint(path.Count);
        foreach (var p in path) w.U8(p.Terrain);
        w.Varint(path.Count);
        foreach (var p in path) w.Vec3(p.Point);
        return w.ToArray();
    }
    /// <summary>0x3742 CharMoveSprintOff_RS: key, answering 0x3705.</summary>
    public static byte[] SprintOffReply(int key) => PacketWriter.Opcode(0x3742).Varint(key).ToArray();
    /// <summary>0x3722 SprintOff_NT: key; also sent alone when SP runs out.</summary>
    public static byte[] SprintOffNotice(int key) => PacketWriter.Opcode(0x3722).Varint(key).ToArray();
    /// <summary>0x3834 SetMainTarget_RS: 00, u16 result, the target, answering 0x3833.</summary>
    public static byte[] SetMainTargetReply(int target) => PacketWriter.Opcode(0x3834).U8(0).U16(0).Varint(target).ToArray();
    /// <summary>
    /// 0x8d44 ClientEnvSetting_NT (dev 0x397F, DevPacketData_Common_ClientEnvSetting): the account's gameplay settings, sent once at
    /// world entry right after SelectCharacter_RS (OBSERVED 2026-10-19 and 2026-10-20, 11 bytes "448d 01 ffffffff 01 01 0a 01").
    /// Fields in order, bools packed (shared bytes across the other fields): HP potion auto use, its secondary slot, the HP percent
    /// condition (i32, -1), 100 % in PvP, party invite (EPartyInviteSettingType 1 All), exchange / duel auto reject, buff auto use
    /// (EBuffAutoUseConditionType 1 CombatOrMove), friend auto reject, peace show weapon, title fx display / my only, nameplate title
    /// fx display / my only, item auto looting, control mode (EClientControlMode 1 Action, 2 Classic). The official values decode
    /// as: potion auto use on, nameplate title fx on, auto looting on, control mode Action. We never sent it before 2026-10-20.
    /// </summary>
    public static byte[] ClientEnvSetting(byte controlMode = 1) =>
        PacketWriter.Opcode(0x8d44).Bool(true).Bool(false).I32(-1).Bool(false).U8(1).Bool(false).Bool(false).U8(1)
            .Bool(false).Bool(false).Bool(false).Bool(false).Bool(true).Bool(false).Bool(true).U8(controlMode).ToArray();
    /// <summary>0x8d43 UpdateClientEnvSetting_RS: i32 result (usmap; the live width is a CANDIDATE), answering 0x8d42.</summary>
    public static byte[] ClientEnvSettingReply() => PacketWriter.Opcode(0x8d43).I32(0).ToArray();
    /// <summary>0x8d41 UpdateClientOption_RS: u16 result, answering the 0x8d40 option save.</summary>
    public static byte[] ClientOptionReply() => PacketWriter.Opcode(0x8d41).U16(0).ToArray();
    /// <summary>0x5672 WingList_NT: count, {i32 wing, u8 enchant}. Official world entry "00" (empty); non-empty framing is a CANDIDATE.</summary>
    public static byte[] WingList(IReadOnlyList<(int Id, byte Enchant)> wings)
    {
        var w = PacketWriter.Opcode(0x5672).Varint(wings.Count);
        foreach (var (id, enchant) in wings) w.I32(id).U8(enchant);
        return w.ToArray();
    }

    /// <summary>Every wing of Wing.dat by race (41 each; 30xxxxxx Light, 40xxxxxx Dark), for AcquireWingAll.</summary>
    public static readonly int[] LightWings =
    [
        30100300, 30100301, 30100500, 30101000, 30101100, 30101200, 30200101, 30200400, 30200500, 30200600,
        30200703, 30201400, 30201500, 30201900, 30202100, 30202101, 30202102, 30202200, 30202300, 30202400,
        30202500, 30202501, 30202502, 30202600, 30202700, 30202800, 30203300, 30300300, 30400200, 30400300,
        30400400, 30400500, 30400900, 30500100, 30500101, 30500200, 30600100, 30600200, 30600300, 30700400,
        30701000,
    ];
    public static readonly int[] DarkWings =
    [
        40100300, 40100301, 40100500, 40101000, 40101100, 40101200, 40200100, 40200400, 40200503, 40200601,
        40200700, 40201400, 40201500, 40201900, 40202100, 40202101, 40202102, 40202200, 40202300, 40202400,
        40202500, 40202501, 40202502, 40202600, 40202700, 40202800, 40203300, 40300300, 40400200, 40400300,
        40400400, 40400500, 40400900, 40500100, 40500101, 40500200, 40600100, 40600200, 40600300, 40700400,
        40701000,
    ];
    public static int[] Wings(int race) => race == 2 ? DarkWings : LightWings;

    /// <summary>0x5675 WingChange_RS: result, packed is_wing_skin, wing id (answering 0x5674). CANDIDATE.</summary>
    public static byte[] WingChangeReply(int wingId, bool skin, int result = 0) => PacketWriter.Opcode(0x5675).U16(result).Bool(skin).I32(wingId).ToArray();
    /// <summary>0x5676 WingChange_NT: character key, packed is_wing_skin, wing id. CANDIDATE.</summary>
    public static byte[] WingChangeNotice(int key, int wingId, bool skin) => PacketWriter.Opcode(0x5676).Varint(key).Bool(skin).I32(wingId).ToArray();

    /// <summary>Default wings (items 532400001/532400002 grant them on use): Light 30200600, Dark 40200601.</summary>
    public static int DefaultWing(int race) => race == 2 ? 40200601 : 30200600;

    /// <summary>0x8d02 GlideCooltimeUpdate_NT: key, cooltime ms (shape of 0x8d03).</summary>
    public static byte[] GlideCooltime(int key, int ms = 0) => PacketWriter.Opcode(0x8d02).Varint(key).I32(ms).ToArray();

    // ---------- Quests (0x8d family base 0x89d1) and NPC interaction (0xe2 base 0xdd07) ----------
    // Decoded from the official recordings: 0x8d09/0x8d10/0x8d11/0x8d0f/0x8d2c/0x8d07/0x8d08,
    // 0xe241/0xe243 OBSERVED byte-exact; the others anchored by catalogue position.

    /// <summary>Common_Quest: i32 id, u8 status, v step, v goal1..6, i32 uid, then roll/publisher/reward-list (6 zero bytes observed; split CANDIDATE).</summary>
    static PacketWriter WriteQuest(PacketWriter w, QuestWire q)
    {
        w.I32(q.QuestId).U8(q.Status).Varint(q.Step);
        for (var i = 0; i < 6; i++) w.Varint(i < q.Goals.Length ? q.Goals[i] : 0);
        return w.I32(q.Uid).Varint(0).I32(q.PublisherId).Varint(0);
    }

    /// <summary>0x8d09 QuestList_NT (world entry; the official new character got none).</summary>
    public static byte[] QuestList(IReadOnlyList<QuestWire> quests)
    {
        var w = PacketWriter.Opcode(0x8d09).Varint(quests.Count);
        foreach (var q in quests) WriteQuest(w, q);
        return w.ToArray();
    }

    /// <summary>0x8d11 QuestAdd_NT: a newly accepted quest (the official answer to an accept; no 0x8d0b was seen).</summary>
    public static byte[] QuestAdd(QuestWire q) => WriteQuest(PacketWriter.Opcode(0x8d11), q).ToArray();

    /// <summary>0x8d10 QuestUpdateProgress_NT: uid, status, step, objective counters.</summary>
    public static byte[] QuestProgress(QuestWire q)
    {
        var w = PacketWriter.Opcode(0x8d10).I32(q.Uid).U8(q.Status).Varint(q.Step);
        for (var i = 0; i < 6; i++) w.Varint(i < q.Goals.Length ? q.Goals[i] : 0);
        return w.ToArray();
    }

    /// <summary>0x8d12 QuestRemove_NT: uids, EQuestRemoveReason (5 GiveUp, 1 Command). Anchored.</summary>
    public static byte[] QuestRemove(IReadOnlyList<int> uids, byte reason)
    {
        var w = PacketWriter.Opcode(0x8d12).Varint(uids.Count);
        foreach (var uid in uids) w.I32(uid);
        return w.U8(reason).ToArray();
    }

    /// <summary>0x8d0d QuestGiveUp_RS. Anchored.</summary>
    public static byte[] QuestGiveUpReply(int uid, int result = 0) => PacketWriter.Opcode(0x8d0d).U16(result).I32(uid).ToArray();
    /// <summary>0x8d0b QuestAccept_RS (never seen; sent only for a refused accept). Anchored.</summary>
    public static byte[] QuestAcceptReply(int questId, int uid, int result = 0) => PacketWriter.Opcode(0x8d0b).U16(result).I32(questId).I32(uid).ToArray();

    /// <summary>0x8d0f QuestTakeReward_RS: result, uid, random reward indexes (none).</summary>
    public static byte[] QuestTakeRewardReply(int uid, int result = 0) => PacketWriter.Opcode(0x8d0f).U16(result).I32(uid).Varint(0).ToArray();

    /// <summary>0x8d2c QuestStepNpcConversation_RS: kind (1 quest uid, 2 map event: CANDIDATE), result, id, NPC key, objective index.</summary>
    public static byte[] QuestStepTalkReply(byte kind, int id, int npcKey, int objIndex = 0, int result = 0) =>
        PacketWriter.Opcode(0x8d2c).U8(kind).U16(result).I32(id).I32(npcKey).I32(objIndex).ToArray();

    // ---------- Map events (0x922d-0x922f OBSERVED in capture 20261013-144836 #9690-14304) ----------

    /// <summary>Common_MapEvent tail: status, stage, step, objective counters, [start time], 8 variables, 8 zero bytes (CANDIDATE end time).</summary>
    static PacketWriter WriteMapEvent(PacketWriter w, byte status, int stage, int step, IReadOnlyList<int> goals, long? startMs, IReadOnlyList<int> variables)
    {
        w.U8(status).Varint(stage).Varint(step).Varint(goals.Count);
        foreach (var g in goals) w.Varint(g);
        if (startMs is { } start) w.I64(start);
        w.Varint(variables.Count);
        foreach (var v in variables) w.Varint(v);
        return w.I64(0);
    }

    /// <summary>0x922e MapEventJoin_NT: id, name, then the event state (byte-exact against #9690).</summary>
    public static byte[] MapEventJoin(int id, string name, byte status, int stage, int step, IReadOnlyList<int> goals, long startMs, IReadOnlyList<int> variables) =>
        WriteMapEvent(PacketWriter.Opcode(0x922e).I32(id).String(name), status, stage, step, goals, startMs, variables).ToArray();

    /// <summary>0x922d MapEventUpdateProgress_NT: id and the event state without the start time (byte-exact against #10095).</summary>
    public static byte[] MapEventProgress(int id, byte status, int stage, int step, IReadOnlyList<int> goals, IReadOnlyList<int> variables) =>
        WriteMapEvent(PacketWriter.Opcode(0x922d).I32(id), status, stage, step, goals, null, variables).ToArray();

    /// <summary>0x922f MapEventLeave_NT: the id (after the exit move, #14304).</summary>
    public static byte[] MapEventLeave(int id) => PacketWriter.Opcode(0x922f).I32(id).ToArray();

    /// <summary>
    /// 0x8d15 EventFuncAction_NT (usmap: _func_type EEventFuncAction u8, _func_value1 str, _volume_name str, _group_req_key i64,
    /// _group_member_count i32, _target_char_key int array, _func_value2 str): the client-side half of a map event directive.
    /// OBSERVED: a leading 00, PlayNpcTalk (19) with the caption name and member count 1 (eight captions of the HQ001 run),
    /// ShowGuide (46) with the guide name and count 0 (once); volume name and key list empty. The leading byte as a presence
    /// mask hiding _func_value2 is a CANDIDATE (the optional-field convention of the other messages).
    /// </summary>
    public static byte[] EventFunc(byte action, string value, int memberCount = 0, string second = "", long groupReqKey = 0) =>
        PacketWriter.Opcode(0x8d15).U8(0).U8(action).String(value).String(second).I64(groupReqKey).I32(memberCount).U8(0).ToArray();

    /// <summary>
    /// StartFocusCamera (45): the camera actor and the NPC talk shown with it (OBSERVED 2026-10-19: Dungeon_0005_FocusCamera_1 with
    /// Talk_Dungeon_0005_04 in the second string; C_L0_Starter_HQ002_Camera_01 with none, 44 ms before the Dredgion vessel spawned).
    /// </summary>
    public static byte[] FocusCamera(string camera, string talk = "") => EventFunc(FuncFocusCamera, camera, 0, talk);

    /// <summary>
    /// 0xe335 CutsceneComplete_RS {result u16, cutscene name, group request key i64}: the answer to 0xe334 CutsceneComplete_RQ
    /// {cutscene name, group request key} (OBSERVED live 2026-10-19 trace 194702: `34e3 15 "Cutscene_L_A_Seq_1291" 00*8`, 1.1 s
    /// after the exit load; the catalogue's 0x3bd2 was a wrong index). CANDIDATE: the reply's fields from the usmap, the result
    /// as the u16 of every observed result.
    /// </summary>
    public static byte[] CutsceneCompleteReply(string cutscene, long groupKey, int result = 0) =>
        PacketWriter.Opcode(0xe335).U16(result).String(cutscene).I64(groupKey).ToArray();

    /// <summary>EEventFuncAction values the server sends.</summary>
    public const byte FuncCutscene = 9, FuncShowNote = 15, FuncShowMessage = 16, FuncPlayNpcTalk = 19, FuncFocusCamera = 45, FuncShowGuide = 46;

    /// <summary>A cutscene (level sequence) by name; the client plays it and reports CompleteCutscene. CANDIDATE: the caption's layout with Cutscene.</summary>
    /// <summary>
    /// The Cutscene event function. The i32 after the group key is 1 when the server waits for the client's CutsceneComplete
    /// (a CompleteCutscene objective: the lake cave's 1290 and 1291) and 0 for a dungeon's own arrival cutscene (the Ascension
    /// dungeon's 1293). OBSERVED in the 2026-10-19 recording.
    /// </summary>
    public static byte[] Cutscene(string name, bool wait = true, long groupReqKey = 0) =>
        EventFunc(FuncCutscene, name, wait ? 1 : 0, groupReqKey: groupReqKey);

    /// <summary>A caption played by name (NpcTalk.dat Name, ENpcTalkType Caption). OBSERVED.</summary>
    public static byte[] Caption(string talk) => EventFunc(FuncPlayNpcTalk, talk, 1);

    /// <summary>A guide (tutorial popup) by name. OBSERVED once (Tutorial_UI_SkillLevelUp).</summary>
    public static byte[] Guide(string guide) => EventFunc(FuncShowGuide, guide);

    /// <summary>A note (NoteData.dat Name: a letter or book the player reads and closes) by name. CANDIDATE: the caption's layout with ShowNote.</summary>
    public static byte[] Note(string note) => EventFunc(FuncShowNote, note, 1);

    /// <summary>A message by string key (ShowMessage / BroadCastingMessage directives). CANDIDATE: the caption's layout with ShowMessage.</summary>
    public static byte[] Message(string key) => EventFunc(FuncShowMessage, key, 1);

    /// <summary>0x39cd RelationshipEntityUpdate_NT: key | ERelationshipEntity u8 (usmap fields; the enum byte as in the appears). CANDIDATE: no official sample.</summary>
    public static byte[] RelationshipUpdate(int key, int relationship) => PacketWriter.Opcode(0x39cd).Varint(key).U8((byte)relationship).ToArray();

    /// <summary>0x9507 SocialActionStart_NT / 0x9508 SocialActionEnd_NT: NPC key, social action id, 1 start / 2 end, i32 0 (OBSERVED #10524/#10811).</summary>
    public static byte[] SocialAction(int key, int action, bool stop) =>
        PacketWriter.Opcode((ushort)(stop ? 0x9508 : 0x9507)).Varint(key).I32(action).U8((byte)(stop ? 2 : 1)).I32(0).ToArray();

    /// <summary>0x4007 NpcConversation_RS: 00, 0000, the NPC key (OBSERVED #9652 before the instance-layer move).</summary>
    public static byte[] NpcConversationReply(int npcKey, int result = 0) => PacketWriter.Opcode(0x4007).U8((byte)result).U16(0).I32(npcKey).ToArray();

    /// <summary>
    /// 0x9231 ClientObjectiveComplete_RS: kind (1 quest uid, 2 map event id), result, id, objective index. OBSERVED
    /// (capture 20261013-144836 #8469: 01 0000 03000000 00000000 answered the cutscene talk of quest uid 3's step 1).
    /// </summary>
    public static byte[] ClientObjectiveReply(byte kind, int id, int objIndex, int result = 0) =>
        PacketWriter.Opcode(0x9231).U8(kind).U16(result).I32(id).I32(objIndex).ToArray();

    /// <summary>0x8d07 ExpUpdate_NT: experience, the amount gained, reason (2 quest reward).</summary>
    public static byte[] ExpUpdate(long exp, long gained, byte reason) => PacketWriter.Opcode(0x8d07).I64(exp).I64(gained).U8(reason).ToArray();
    /// <summary>0x8d08 LevelUpdate_NT: level, highest level, experience.</summary>
    public static byte[] LevelUpdate(int level, int levelMax, long exp) => PacketWriter.Opcode(0x8d08).I32(level).I32(levelMax).I64(exp).ToArray();

    /// <summary>0xe241 StartNpcInteraction_RS: result, NPC key, expiry (server now + 300 s).</summary>
    // ---------- World-map fog (the 0x54ca run: catalogue index 482 + 0x54ca) ----------

    /// <summary>
    /// 0x56ac OpenSubzoneGroupIDs_NT: the uncovered world-map fog groups, a count and one ESubzoneGroupID byte each (OBSERVED,
    /// capture 20261013 #202 `01 3f`: group 63 at the world entry). Not 0x9501: that list (#375, 36 i32s 2..54) is another
    /// message; sending our fog there left the whole map clouded (live 2026-10-19).
    /// </summary>
    public static byte[] OpenSubzoneGroups(IReadOnlyList<int> groups)
    {
        var w = PacketWriter.Opcode(0x56ac).Varint(groups.Count);
        foreach (var group in groups) w.U8(checked((byte)group));
        return w.ToArray();
    }
    /// <summary>0x56ad AddSubzoneGroupID_NT: one newly uncovered group, one byte (OBSERVED #2396 `41` = 65 Poeta, #9344 `40`).</summary>
    public static byte[] AddSubzoneGroup(int group) => PacketWriter.Opcode(0x56ad).U8(checked((byte)group)).ToArray();

    // ---------- Environment objects and Kibelisks (official capture 20261013-144836) ----------
    // EnvObjSpawn 0x3634, InteractEnvObj_RQ/RS 0x3636/0x3637, Started 0x3638, Completed 0x363a (0x36
    // block, catalogue index + 0x360c); EngravedTeleportArtifactList/Add 0x9232/0x9233 (index 1087 +
    // 0x8df3, both observed). Bodies rebuild the official samples byte for byte.

    /// <summary>
    /// 0x3634 EnvObjSpawn_NT: key, desc (type 4 env object, EnvObjData id), position, origin spawn
    /// key, yaw, state 1 / can-interact / reason bytes, create time, eight zero bytes (counts,
    /// trigger actor, kisk), the MapData spawn unique key, relationship 8, phase, trailing byte.
    /// </summary>
    public static byte[] EnvObjSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(4).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(yaw).U8(1).U8(1).U8(1).I64(createTime).Zeros(8).I32(uniqueKey).U8(8).U8(0).U8(0).ToArray();

    /// <summary>
    /// 0x3634 EnvObjSpawn_NT for a trigger actor (desc type 1): key, EnvObjData id (E_Trigger 1100001, E_Trigger_air 1100002,
    /// E_Trigger_water 1100034), the trigger's location and direction, state bytes, create time, then i32 0, the MapData trigger
    /// ID (i64), unique key 0, relationship 7. OBSERVED: every trigger of the Ascension Subspace at entry (capture
    /// 20261019-231752 #34404-#34427); the client marks the open objectives' triggers and reports entering them (0x3636).
    /// </summary>
    public static byte[] TriggerSpawn(int key, int envObjId, Vector3d position, double direction, long createTime, long triggerId) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(1).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(direction).U8(1).U8(1).U8(1).I64(createTime).I32(0).I64(triggerId).I32(0).U8(7).U8(0).U8(0).ToArray();

    /// <summary>0x3635 EnvObjDespawn_NT: key, state, reason, despawn act (official sample e4ec03 01 01 00; widths CANDIDATE).</summary>
    public static byte[] EnvObjDespawn(int key, byte state = 1, byte reason = 1) => PacketWriter.Opcode(0x3635).Varint(key).U8(state).U8(reason).Varint(0).ToArray();
    /// <summary>0x39ce UpdateEnvObjHitCount_NT: object key, the hits it has taken (usmap _key, _hit_count i32). CANDIDATE: no official sample.</summary>
    public static byte[] EnvObjHitCount(int key, int hitCount) => PacketWriter.Opcode(0x39ce).Varint(key).I32(hitCount).ToArray();
    /// <summary>0x3940 EnvObjDie_NT: object key (usmap: _key only). CANDIDATE: no official sample.</summary>
    public static byte[] EnvObjDie(int key) => PacketWriter.Opcode(0x3940).Varint(key).ToArray();
    /// <summary>0x363e InteractEnvObjStop_RS: result, object key (usmap; answering 0x363d). CANDIDATE.</summary>
    public static byte[] InteractEnvObjStopReply(int key, int result = 0) => PacketWriter.Opcode(0x363e).U16(result).Varint(key).ToArray();
    /// <summary>EnvObjSpawn for a quest object: relationship from EnvObjData (Kibelisks: 8 EnvObj_Light).</summary>
    public static byte[] EnvObjSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey, byte relationship) =>
        EnvObjSpawn(key, envObjId, position, yaw, createTime, uniqueKey, relationship, state: 1, canInteract: 1, reason: 1, phase: 0);

    /// <summary>0x3634 EnvObjSpawn_NT with explicit state / can-interact / phase (map-event EnvObjChangePhase, SetEnvObjCanInteract).</summary>
    public static byte[] EnvObjSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey, byte relationship,
        byte state, byte canInteract, byte reason, byte phase) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(4).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(yaw).U8(state).U8(canInteract).U8(reason).I64(createTime).Zeros(8).I32(uniqueKey).U8(relationship).U8(phase).U8(0).ToArray();

    /// <summary>0x363c InteractEnvObjCanInteractFlag_NT: object key, can interact (0/1).</summary>
    public static byte[] InteractEnvObjCanInteract(int key, bool canInteract) =>
        PacketWriter.Opcode(0x363c).Varint(key).U8(canInteract ? (byte)1 : (byte)0).ToArray();

    /// <summary>0x3638 InteractEnvObjStarted_NT: object key, interactor key, state 1.</summary>
    public static byte[] InteractEnvObjStarted(int key, int interactor) => PacketWriter.Opcode(0x3638).Varint(key).Varint(interactor).U8(1).ToArray();
    /// <summary>0x363a InteractEnvObjCompleted_NT: object key, interactor key, interaction count, item id.</summary>
    public static byte[] InteractEnvObjCompleted(int key, int interactor) => PacketWriter.Opcode(0x363a).Varint(key).Varint(interactor).I32(0).I32(0).ToArray();
    /// <summary>0x8d6d InteractionEnvObjFinishState_NT: object key (no usmap struct at this index; OBSERVED body = the key varint).</summary>
    public static byte[] InteractEnvObjFinishState(int key) => PacketWriter.Opcode(0x8d6d).Varint(key).ToArray();
    /// <summary>0x363b InteractEnvObjAvailableState_NT: object key.</summary>
    public static byte[] InteractEnvObjAvailableState(int key) => PacketWriter.Opcode(0x363b).Varint(key).ToArray();

    /// <summary>
    /// Gather / interaction refusal on 0x3639 InteractEnvObjInterrupted_NT (DevPacketData_GameServer_InteractEnvObjInterrupted_NT:
    /// ActionProtocol _key i32, _reason i32, _interactor_key i32, _interaction_count i32). OBSERVED _reason values match
    /// ESlotRestrictIconType (LackLevel 4, LackMasteryLevel 5, …) when gather level or character level is too low.
    /// </summary>
    public static byte[] InteractEnvObjInterrupted(int key, int reason, int interactorKey, int interactionCount = 0) =>
        PacketWriter.Opcode(0x3639).I32(key).I32(reason).I32(interactorKey).I32(interactionCount).ToArray();

    /// <summary>ESlotRestrictIconType values sent as 0x3639 _reason for gather refusals (OBSERVED).</summary>
    public static class EnvObjGatherFailReason
    {
        public const int LackLevel = 4;
        public const int LackMasteryLevel = 5;
        public const int LackLevelAndMasteryLevel = 6;
    }

    /// <summary>0x3637 InteractEnvObj_RS: result, a zero byte, object key, item key (official sample; field split CANDIDATE).</summary>
    public static byte[] InteractEnvObjReply(int key, int result = 0) => PacketWriter.Opcode(0x3637).U16(result).U8(0).Varint(key).Zeros(8).ToArray();
    /// <summary>0x9232 EngravedTeleportArtifactList_NT: the Kibelisks the character has bound to.</summary>
    public static byte[] EngravedTeleportArtifacts(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(0x9232).Varint(ids.Count);
        foreach (var id in ids) w.I32(id);
        return w.ToArray();
    }
    /// <summary>0x9233 EngravedTeleportArtifactAdd_NT: a newly bound Kibelisk.</summary>
    public static byte[] EngravedTeleportArtifactAdd(int id) => PacketWriter.Opcode(0x9233).I32(id).ToArray();
    /// <summary>0x9237 UseTeleportArtifact_RS: result, artifact id (usmap _result, _teleport_artifact_id; result u16 as every observed RS). CANDIDATE.</summary>
    public static byte[] UseTeleportArtifactReply(int id, int result = 0) => PacketWriter.Opcode(0x9237).U16(result).I32(id).ToArray();

    public static byte[] StartNpcInteractionReply(int npcKey, long expireMs, int result = 0) => PacketWriter.Opcode(0xe241).U16(result).I32(npcKey).I64(expireMs).ToArray();
    /// <summary>0xe243 FinishNpcInteraction_RS: result, NPC key.</summary>
    public static byte[] FinishNpcInteractionReply(int npcKey, int result = 0) => PacketWriter.Opcode(0xe243).U16(result).I32(npcKey).ToArray();
    /// <summary>0xe245 KeepAliveNpcInteraction_RS (shape of 0xe241). Anchored.</summary>
    public static byte[] KeepAliveNpcInteractionReply(int npcKey, long expireMs, int result = 0) => PacketWriter.Opcode(0xe245).U16(result).I32(npcKey).I64(expireMs).ToArray();
    /// <summary>
    /// 0x3835 TargetUpdate_NT: an entity's target: key | 00 | target (0 = none). OBSERVED 2026-10-19: after the player's 0x3834
    /// the selected NPC with its target (0 before the fight), at the aggro start (right after 0x8d52) the NPC with the player as
    /// its target, at its death with 0. The client marks the monster that targets the player.
    /// </summary>
    public static byte[] TargetUpdate(int key, int target = 0) => PacketWriter.Opcode(0x3835).Varint(key).U8(0).Varint(target).ToArray();
    /// <summary>0x364e CharRotate_NT: key, target and current direction, reason.</summary>
    public static byte[] CharRotate(int key, float targetDir, float currentDir, byte reason = 0) => PacketWriter.Opcode(0x364e).Varint(key).F32(targetDir).F32(currentDir).U8(reason).ToArray();

    /// <summary>0x3746 LastOnGroundPosZ_NT: key, f32 height; official after the map load.</summary>
    public static byte[] LastOnGroundZ(int key, float z) => PacketWriter.Opcode(0x3746).Varint(key).F32(z).ToArray();

    public static byte[] NpcHp(int key, long hp) => PacketWriter.Opcode(0x8d00).Varint(key).U8(2).U8(1).U8(0).I64(hp).ToArray();

    /// <summary>
    /// 0x8d04 CharDie_NT (usmap DevPacketData_GameServer_CharDie_NT): victim, skill u32, killer key, PC server id u16,
    /// PC nick and guild strings, guild emblem u32, NPC caster data id u32, NPC die act u32, reason u32, EPCDieType u8.
    /// NPC kills use dieType 0; a player killed in PvE uses dieType 2 (DiePvE) with server id 0, die act 0, reason 1,
    /// and the killer NPC's data id (varint) when the killer is an NPC.
    /// </summary>
    public static byte[] Kill(int victimKey, int skillId, int killerKey, int serverId, string killerName, byte dieType = 0, int npcCasterDataId = 0,
        uint npcDieActId = 0, uint reason = 0) =>
        PacketWriter.Opcode(0x8d04).Varint(victimKey).U32((uint)skillId).Varint(killerKey).U16(serverId).String(killerName).String("")
            .U32(0).Varint(npcCasterDataId).U32(npcDieActId).U32(reason).U8(dieType).ToArray();

    /// <summary>0x8d21: key | 00 | 1 in combat / 0 out.</summary>
    public static byte[] CombatState(int key, bool inCombat) => PacketWriter.Opcode(0x8d21).Varint(key).U8(0).U8(inCombat ? 1 : 0).ToArray();

    /// <summary>0x8d52 NpcFirstAggro_NT: the NPC key (catalogue 897 + 0x89d1). OBSERVED with every official aggro start.</summary>
    public static byte[] NpcFirstAggro(int key) => PacketWriter.Opcode(0x8d52).Varint(key).ToArray();

    /// <summary>0x3642 despawn: flag 3 removes a corpse, 1 is the out-of-view removal.</summary>
    public static byte[] Death(int key, int flag = 3) => PacketWriter.Opcode(0x3642).Varint(key).U8(0).Varint(flag).ToArray();

    // ---------- Casts ----------

    /// <summary>0x3802 mask-zero skill start (a2kit cast fixtures).</summary>
    public static byte[] SkillStart(int skill, int actionUid, double yaw, Vector3d location, int targetKey = 1, int actorKey = 1,
        int attackSpeed = 10000, int cooldown = 0) =>
        PacketWriter.Opcode(0x3802).Varint(actorKey).U8(0).I32(skill).U8(actionUid).U8(2).Varint(targetKey)
            .F32(yaw).Vec3(location).Varint(attackSpeed).U8(1).Varint(cooldown).ToArray();

    /// <summary>0x364c resources (receiver 0x91f88f0): key, HP varints, 12 ints, empty parts array.</summary>
    /// <summary>
    /// 0x362f BuilderCmd_RS (usmap: i32 _result, FString[] _cmds, FString _msg), the reply to a
    /// console "s &lt;command&gt;" (0x362e). Strings are cut to the u8 FString limit.
    /// CANDIDATE: layout from the usmap and the 0x36xx catalogue block; untested with the client.
    /// </summary>
    public static byte[] BuilderCmdResponse(int result, IReadOnlyList<string> commands, string message)
    {
        var w = PacketWriter.Opcode(0x362f).I32(result).Varint(commands.Count);
        foreach (var command in commands) w.String(FitFString(command));
        return w.String(FitFString(message)).ToArray();
    }

    /// <summary>0x362e BuilderCmd_RQ: varint count + FStrings; null when it does not parse exactly.</summary>
    public static IReadOnlyList<string>? BuilderCmdRequest(byte[] body)
    {
        if (body.Length < 3 || body[0] != 0x2e || body[1] != 0x36) return null;
        try
        {
            var reader = new PacketReader(body) { Offset = 2 };
            var count = reader.Varint();
            if (count > 16) return null;
            var commands = new List<string>();
            for (var i = 0; i < count; i++) commands.Add(reader.String());
            return reader.End ? commands : null;
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Longest prefix of whole characters that fits a u8-length FString.</summary>
    static string FitFString(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= 255) return text;
        var end = text.Length;
        while (System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, end)) > 255) end--;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }

    /// <summary>
    /// 0x364d CmdInvisibleMode_NT: a single bool (catalogue name and field; the 0x36xx catalogue
    /// block matches every observed opcode, 0x364c included). CANDIDATE: untested with the client.
    /// </summary>
    public static byte[] InvisibleMode(bool on) => PacketWriter.Opcode(0x364d).Bool(on).ToArray();

    public static byte[] ResourceUpdate(int hp, int hpMax, int mp, int mpMax, int sp, int spMax, int key = 1)
    {
        var w = PacketWriter.Opcode(0x364c).Varint(key).Varint(hp).Varint(hpMax);
        foreach (var v in new[] { mp, mpMax, 0, 0, 0, 0, sp, spMax, 0, 10000, 100000, 100000 }) w.I32(v);
        return w.U8(0).ToArray();
    }

    /// <summary>0x381b SkillChargingStart_RS (OBSERVED 2026-10-31): result u16, skill i32 (8 bytes).</summary>
    public static byte[] SkillChargingStartReply(int result, int skill) =>
        PacketWriter.Opcode(0x381b).U16(result).I32(skill).ToArray();

    /// <summary>0x381c SkillChargingStart_NT (OBSERVED): actor varint, skill i32, level u8, target type u8 + key varint, yaw f32, x/y/z f32, charge rate f32, act u8 1.</summary>
    public static byte[] SkillChargingStartNotify(int actorKey, int skillId, int skillLevel, int targetKey, float yaw, Vector3d location,
        float chargeRate = 1.236f) =>
        PacketWriter.Opcode(0x381c).Varint(actorKey).I32(skillId).U8(skillLevel).U8(2).Varint(targetKey).F32(yaw)
            .F32((float)location.X).F32((float)location.Y).F32((float)location.Z).F32(chargeRate).U8(1).ToArray();

    /// <summary>0x381e SkillChargingEnd_RS (OBSERVED): step u8, result u16, skill i32, i32 0, selected skill i32.</summary>
    public static byte[] SkillChargingEndReply(int result, int skill, int step, int selectedSkill) =>
        PacketWriter.Opcode(0x381e).U8(step).U16(result).I32(skill).I32(0).I32(selectedSkill).ToArray();

    /// <summary>0x3820 SkillChargingStop_RS: result i32.</summary>
    public static byte[] SkillChargingStopReply(int result = 0) => PacketWriter.Opcode(0x3820).I32(result).ToArray();

    /// <summary>0x3825 BasicAtk_RS (dev 0x36CE + 0x157): result u16.</summary>
    public static byte[] BasicAtkReply(int result = 0) => PacketWriter.Opcode(0x3825).U16(result).ToArray();

    /// <summary>0x3827 BasicAtkToggleOn_RS or 0x3829 BasicAtkToggleOff_RS: result u16.</summary>
    public static byte[] BasicAtkToggleReply(ushort opcode, int result = 0) => PacketWriter.Opcode(opcode).U16(result).ToArray();

    /// <summary>0x3801 cast reply with both optional bytes (toggle and client action UID).</summary>
    public static byte[] CastReply(bool accepted, int skill, int uid, byte changeToggle = 0) =>
        CastReply(accepted, skill, changeToggle, includeToggle: true, uid, includeUid: true);

    /// <summary>0x3801 cast reply; flags u8 bit0 toggle, bit1 client action UID (handler RVA 0x91a6a70).</summary>
    public static byte[] CastReply(bool accepted, int skill, byte changeToggle, bool includeToggle, int uid, bool includeUid, ushort refusal = 1)
    {
        var flags = (includeToggle ? 1 : 0) | (includeUid ? 2 : 0);
        var w = PacketWriter.Opcode(0x3801);
        w.U8((byte)flags);   // official 2026-10-30: flags byte present even when 0 (01 38 00 00 00 <skill>)
        w.U16(accepted ? (ushort)0 : refusal).I32(skill);
        if (includeToggle) w.U8(changeToggle);
        if (includeUid) w.U8((byte)uid);
        return w.ToArray();
    }

    /// <summary>
    /// 0x3801 for a dodge: result u16, the skill, u8 0 and nothing else (OBSERVED, 9 bytes at all 100 official dodges of
    /// 2026-10-19, the sprint's dash included: the skill is CombatDodge 11000100 even when the start is NormalDodge 11000101).
    /// Result 1 is the refusal (DashVariant 6). The flags-first 11-byte cast reply reads as a failure to this client and drops the sprint.
    /// </summary>
    /// <summary>0x3801 9-byte form for any skill: result u16, skill i32, change toggle u8 (the dodge's observed shape).</summary>
    public static byte[] CastResult(int skill, int result = 0, byte changeToggle = 0) =>
        PacketWriter.Opcode(0x3801).U16(result).I32(skill).U8(changeToggle).ToArray();

    public static byte[] DodgeResult(int skill, int result = 0) => PacketWriter.Opcode(0x3801).U16(result).I32(skill).U8(0).ToArray();

    /// <summary>
    /// 0x3804 with switch 00: a self skill's effect (OBSERVED 50-60 ms after all 36 official CombatDodge starts): target, 00 00, actor,
    /// skill, the start's action uid, 02, effect id (skill x 100 + 13 for the dodge), i32 1, attack speed 10000, 01, 00.
    /// </summary>
    public static byte[] SkillEffect(int targetKey, int actorKey, int skill, int actionUid, int effectId) =>
        PacketWriter.Opcode(0x3804).Varint(targetKey).U8(0).U8(0).Varint(actorKey).U32((uint)skill).U8((byte)actionUid).U8(2)
            .U32((uint)effectId).U32(1).Varint(10000).U8(1).U8(0).ToArray();

    /// <summary>0x3806 skill end: key, skill, action uid, u8 0 (OBSERVED 200 ms after every official dodge and sprint dash, before the abnormal's 0x382c).</summary>
    public static byte[] SkillEnd(int key, int skill, int actionUid) => PacketWriter.Opcode(0x3806).Varint(key).I32(skill).U8((byte)actionUid).U8(0).ToArray();

    // ---------- NPC movement (official 0x371a-c) ----------

    static byte[] Move(ushort opcode, int key, Vector3d position, double heading, bool run)
    {
        var angle = HeadingU16(heading);
        var w = PacketWriter.Opcode(opcode).Varint(key);
        if (run) w.U8(5).U8(3); else w.U8(4);
        w.Vec3(position).U16(angle).U16(angle);
        if (!run) w.U8(1);
        return w.ToArray();
    }

    public static byte[] NpcMoveStart(int key, Vector3d position, double heading, bool run = false) => Move(0x371b, key, position, heading, run);
    public static byte[] NpcMove(int key, Vector3d position, double heading, bool run = false) => Move(0x371c, key, position, heading, run);
    /// <summary>
    /// 0x371d NPC move delta, every 100 ms between the 1 s full positions (OBSERVED, capture 20261013: 5984 samples). Mask
    /// 0x01 the move type byte (03 run), 0x02/0x04/0x08 dx/dy/dz as signed bytes (game units since the last sent position;
    /// dz left out when 0), 0x20 the heading pair; a walk ends with 01 as 0x371b/0x371c do. Example walk `2e ff 1f ff 5066 5066 01`
    /// (12.6 units = 126/s), run `2f 03 e9 aa fc 51b5 51b5` (Robin at 875/s).
    /// </summary>
    public static byte[] NpcMoveDelta(int key, int dx, int dy, int dz, double heading, bool run)
    {
        var angle = HeadingU16(heading);
        var w = PacketWriter.Opcode(0x371d).Varint(key).U8((run ? 0x01 : 0) | 0x20 | 0x02 | 0x04 | (dz != 0 ? 0x08 : 0));
        if (run) w.U8(3);
        w.U8((byte)(sbyte)dx).U8((byte)(sbyte)dy);
        if (dz != 0) w.U8((byte)(sbyte)dz);
        w.U16(angle).U16(angle);
        if (!run) w.U8(1);
        return w.ToArray();
    }

    public static byte[] NpcStop(int key, Vector3d position, double heading) =>
        PacketWriter.Opcode(0x371a).Varint(key).U8(0).U8(0).Vec3(position).U16(HeadingU16(heading)).ToArray();
}
