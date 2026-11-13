using System.Text;
using QuickSlotRecord = Aion.Application.Gameplay.QuickSlotRecord;

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
/// <summary>A quest on the wire (Common_Quest): EQuestStatus 2 Incomplete, 3 Complete, 4 Rewarded.
/// Duty quests carry their re-draw count and offered random rewards (item, count, enchant, prob of 100,000,000; OBSERVED in the
/// official 0x8d09 of rec 20261106-115817 #173810 for the five accepted missions).</summary>
public sealed record QuestWire(int QuestId, byte Status, int Step, int[] Goals, int Uid, int RollCount = 0,
    IReadOnlyList<(int Item, long Count, byte Enchant, long Prob)>? Rewards = null);

/// <summary>ECharPointType: the resources in 0x8d00 (OP oxygen, FP flight).</summary>
public enum CharPoint : byte { Hp, Mp, Ap, Sp, Dp, Op, Fp, MaxHp, MaxMp, MaxAp, MaxSp, MaxDp, MaxOp, MaxFp }

/// <summary>An inventory item on the wire (Common_Item). Container (EItemContainerType, usmap) 1 bag, 2 account warehouse,
/// 11 equipped, 15 character warehouse.</summary>
public sealed record InventoryItem(long Key, int ItemId, long Count, byte Container, int Slot, EquipDetail? Detail = null,
    bool Locked = false, string Maker = "", long ExpireTime = 0, bool ExpireFixed = false)
{
    public const byte Bag = 1, Storage = 2, Equipped = 11, CharacterStorage = 15;
}

/// <summary>Equipment detail of an item: item level and equip slot (1 weapon, 3 helm, 4 shoulder, 5 torso, 6 pants, 7 gloves, 8 boots, 19 cape; 0 in the bag).</summary>
// Soul bind: the binding character's db id (the DbId of the character list) and the bind count;
// the client soul-binds an unbound item again on every equip attempt.
public sealed record EquipDetail(int ItemLevel, byte EquipSlot, byte EnchantLevel = 0, long SoulBindOwner = 0, byte SoulBindCount = 0,
    byte SurpassLevel = 0, int SurpassBonus = 0, IReadOnlyList<MagicStoneSlot>? Stones = null, int StoneSlots = 1,
    IReadOnlyList<(int Type, int Value)>? SoulStats = null, IReadOnlyList<int>? Godstones = null,
    IReadOnlyList<(int Type, int Value)>? SoulAddStats = null, IReadOnlyList<(int Skill, byte Level)>? SubSkills = null)
{
    /// <summary>
    /// _godstone_slots: always three i32 (OBSERVED: three zero ids on every item record); a socketed godstone sits at its
    /// 1-based slot - 1 (BroadcastItemEnchantGodstone_NT OBSERVED slot 1 on a one-slot weapon).
    /// </summary>
    public PacketWriter WriteGodstones(PacketWriter w)
    {
        var ids = Godstones ?? [];
        w.Varint(3);
        for (var i = 0; i < 3; i++) w.I32(i < ids.Count ? ids[i] : 0);
        return w;
    }

    /// <summary>
    /// The seven arrays after the godstones, then the skin extraction count. OBSERVED (115817 #206431, a soul-bound weapon, the
    /// item update right before its 0x5630): the bound stats {EStat u16, value i32} in the first array (HPMax 234, 200: 32,
    /// 298: 28155, 255: 32, the same four 0x5630 lists), the same types with value 0 in the second, then five empty arrays.
    /// </summary>
    public PacketWriter WriteTail(PacketWriter w)
    {
        var stats = SoulStats ?? [];
        w.Varint(stats.Count);
        foreach (var (type, value) in stats) w.U16((ushort)type).I32(value);
        w.Varint(stats.Count);
        foreach (var (type, _) in stats) w.U16((ushort)type).I32(0);
        // Then the sub-stat skills {skill i32, level u8} and the added sub skills (the same skills, level 0): OBSERVED (rec 210132
        // #1605, four worn items each "01 <skill> 01 01 <skill> 00"). Then an empty array, the soul add stats (CANDIDATE position:
        // the usmap's sixth array, the first array holding the bound stats as observed), the soul add skills (empty) and the skin
        // extraction count.
        var skills = SubSkills ?? [];
        w.Varint(skills.Count);
        foreach (var (skill, level) in skills) w.I32(skill).U8(level);
        w.Varint(skills.Count);
        foreach (var (skill, _) in skills) w.I32(skill).U8(0);
        var added = SoulAddStats ?? [];
        w.Zeros(1).Varint(added.Count);
        foreach (var (type, value) in added) w.U16((ushort)type).I32(value);
        return w.Zeros(1).U8(0);
    }

    /// <summary>
    /// Common_ItemMagicStoneSlotInfo array: every manastone slot of the item, the filled ones first, empty ones as zeros.
    /// OBSERVED (capture 115817): a 3-slot item listed 3 zero entries before socketing and its 3 stones after.
    /// </summary>
    public PacketWriter WriteStones(PacketWriter w)
    {
        var stones = Stones ?? [];
        var count = Math.Max(Math.Max(1, StoneSlots), stones.Count);
        w.Varint(count);
        foreach (var s in stones) s.Write(w);
        return w.Zeros(7 * (count - stones.Count));
    }

    // Compared by content: the inventory tells changed items by comparing records, the stone list included.
    public bool Equals(EquipDetail? other) =>
        other is not null && ItemLevel == other.ItemLevel && EquipSlot == other.EquipSlot && EnchantLevel == other.EnchantLevel
        && SoulBindOwner == other.SoulBindOwner && SoulBindCount == other.SoulBindCount && SurpassLevel == other.SurpassLevel
        && SurpassBonus == other.SurpassBonus && StoneSlots == other.StoneSlots && (Stones ?? []).SequenceEqual(other.Stones ?? [])
        && (SoulStats ?? []).SequenceEqual(other.SoulStats ?? []) && (Godstones ?? []).SequenceEqual(other.Godstones ?? [])
        && (SoulAddStats ?? []).SequenceEqual(other.SoulAddStats ?? []) && (SubSkills ?? []).SequenceEqual(other.SubSkills ?? []);

    public override int GetHashCode() => HashCode.Combine(ItemLevel, EquipSlot, EnchantLevel, SoulBindOwner, SoulBindCount, SurpassLevel, Stones?.Count ?? 0);
}

/// <summary>
/// One socketed manastone: stone item id i32, EStat id u16, EItemMagicStoneGrade u8 (1 Low green, 2 Mid blue, 3 High gold, 4 Max).
/// OBSERVED (ItemImprint_RS, capture 115817): 7 bytes each, no value: the client takes the value from its own table.
/// </summary>
public readonly record struct MagicStoneSlot(int ItemId, int Stat, byte Grade)
{
    public PacketWriter Write(PacketWriter w) => w.I32(ItemId).U16((ushort)Stat).U8(Grade);
}

/// <summary>A 0x561b change: type 0 add / 1 update, and the official reason codes (24 grant, 38 potion use observed).</summary>
public sealed record ItemModification(byte Type, InventoryItem Item, byte Increase = 0, byte Decrease = 0, byte Update = 0);

public sealed record MyCharacterAppear
{
    /// <summary>EToggleType flags (bit 0 SealStone), state mask bit 7.</summary>
    public int ToggleFlags { get; init; }
    /// <summary>The registered Power Shard's item id (mask bit 15 _sealstone_item_id; 0 = none registered, left out).</summary>
    public int SealstoneItemId { get; init; }
    /// <summary>ERelationshipEntity of the character (1 PC_Light_Common, 2 PC_Darkness_Common; 0 = not sent).</summary>
    public byte Relationship { get; init; }
    public int Key { get; init; } = 1;
    /// <summary>The character's db id (0 sends 1); soul-bound items name it as their owner.</summary>
    public long DbId { get; init; }
    public string Name { get; init; } = "";
    public int ServerId { get; init; } = 1204;
    /// <summary>
    /// _match_server_id (mask bit 23): OFFICIAL a server of the other race (2316 -> 1310, 2313 -> 1315, 1320 -> 2317; recordings
    /// 2026-11-06/07). 0 sends <see cref="ServerId"/> (as before the four worlds).
    /// </summary>
    public int MatchServerId { get; init; }
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
    /// <summary>ESkillPointType 1-3 points spent (skill points, stigma shards, superior shards).</summary>
    public IReadOnlyList<int> SpentPoints { get; init; } = [];
    /// <summary>ESkillPointType 1-3 points items gave.</summary>
    public IReadOnlyList<int> ItemPoints { get; init; } = [];
    /// <summary>CharDetail _gather_source_stats: gather mastery level, exp, skill and specialized points left (null: the starter's 1/0/0/0).</summary>
    public (int Level, long Exp, int Points, int Specialized)? Gather { get; init; }
    /// <summary>CharDetail _craft_stats by ECraftCategory 1..5: level and exp (null: all level 1).</summary>
    public IReadOnlyList<(int Level, long Exp)>? Crafts { get; init; }
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
    /// <summary>
    /// The map copy: _map_key = map id | instance << 32 (OFFICIAL, all 56 0x3633 2026-11-06/07: 1110 = 56040000 00000000 in the open
    /// world, 320013 | 282325 << 32 in a dungeon copy). We sent 1. The instance number is ours (CANDIDATE: the official counts up).
    /// </summary>
    public int Instance { get; init; }
    /// <summary>_abyss_ranking_point, mask bit 16: the season's Abyss ranking point (0 sends none, as every official entry).</summary>
    public long AbyssRankingPoint { get; init; }
    public long CombatPower { get; init; }
    /// <summary>The Daevanion point lists at the end (used, then item points; 5 types each); null: none (the official empty lists).</summary>
    public byte[]? DaevanionPoints { get; init; }
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
    /// <summary>EStat 206 BattleMoveSpeed of the appearance's stat list (as the stat sheet's: -3000 Gladiator/tutorial, -4000 Sorcerer/Ranger, OBSERVED).</summary>
    public int BattleMoveSpeed { get; init; } = -3000;
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
public sealed record NpcAppearance(int Key, int NpcId, Vector3d Position, double Direction, int Hp, int HpMax,
    int Level, int Relationship, bool Aggressive, bool Dead, uint UniqueKey = 0, int SocialAction = 0, long SocialStart = 0, bool Walking = false,
    byte MoveState = 0, byte MoveSubstate = 0, byte Reason = 1, int SpawnAct = 0, IReadOnlyList<AbnormalWire>? Abnormals = null,
    bool Monster = false, string? OwnerName = null, bool InCombat = false, int ParentKey = 0, IReadOnlyList<(int Stat, int Value)>? Stats = null)
{
    public const byte FlyingOnAirStop = 7, NpcSpawnReason = 6;
}

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

    // EContentsUnlockType. StarterUnlockedContents and LaterUnlockedContents are the old split (chat 45 and customizing 43 held back
    // for an 0xe258 at every world entry); they stay only as the default bodies of the two builders (native parity goldens).
    public static readonly byte[] StarterUnlockedContents = [17, 42, 50, 52, 56, 61, 62];
    public static readonly byte[] LaterUnlockedContents = [45, 43];

    /// <summary>
    /// The contents ContentsUnlock.dat opens with quest HQ1100010 (rows 19 Post, 43 Community, 44 Chat, 45 Party_Force, 51 Map,
    /// 55 RemoteStorage, 60 HudEdit, 61 DamageAnalyzer): Post 17, Community 42, Chat 45, Party_Force 50, Map 52, RemoteStorage 56,
    /// HudEdit 61, DamageAnalyzer 62. OBSERVED: a new character's first 0xe258 is exactly this set (`58e2 08 112a2d3234383d3e`, rec
    /// 20261107-010322 #3257 and -010735 #17945), and every later select lists it in 0xe257 with Chat (45) inside (rec -012837 #306,
    /// -013918 #198, -014745 #255). Our characters have it from the start (the first-entry update is not replayed).
    /// </summary>
    public static readonly byte[] TutorialQuestContents = [17, 42, 45, 50, 52, 56, 61, 62];

    /// <summary>
    /// Level-gated EContentsUnlockType rows of ContentsUnlock.dat (UnlockLevel 2..50; 99 is
    /// "never" and quest-gated rows have level 0). The skill window (Skill, 2) opens at level 4.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int> ContentsUnlockLevels = new Dictionary<byte, int>
    {
        [1] = 2, [43] = 2, [32] = 3, [2] = 4, [60] = 4, [8] = 9, [19] = 9,
        [13] = 10, [10] = 10, [11] = 10, [30] = 10, [33] = 10, [44] = 10, [46] = 10, [53] = 10, [54] = 10,
        [22] = 25, [38] = 30, [5] = 30,
        [24] = 45, [21] = 45, [26] = 45, [27] = 45, [28] = 45, [29] = 45, [6] = 45, [7] = 45, [31] = 45,
        [34] = 45, [41] = 45, [40] = 45, [47] = 45, [48] = 45, [49] = 45, [57] = 45, [58] = 45,
        [59] = 50, [63] = 50,
    };

    /// <summary>
    /// The contents open at a level: the HQ1100010 set and every level-gated type reached, Chat (45) and Customizing (43, level 2)
    /// included. OBSERVED (info-popups fix 2026-11-11): the official select lists everything already open in 0xe257 (level-4
    /// character, rec 20261107-012837 #306: `57e2 0f 0102030f11202a2b2d3234383c3d3e` = this at level 4 plus the quest-gated BMShop 3
    /// and Skin 15) and sends 0xe258 only when something opens (level 2 `02012b`, level 3 `0120`, level 4 `02023c`, level 9
    /// `020813`). We held 43 and 45 back and sent them as an 0xe258 at every login and every map load, each one a fresh
    /// System_ContentsUnlocked in the client (live traces 2026-11-09/10: `58e2 022d2b` after every 0x3622).
    /// </summary>
    public static byte[] UnlockedContents(int level) =>
        TutorialQuestContents.Concat(ContentsUnlockLevels.Where(u => u.Value <= level).Select(u => u.Key)).Distinct().Order().ToArray();

    public static byte[] Ping(ulong counter, long nowMs) => new Generated.PingRS { ClientSentTime = unchecked((long)counter), ServerGameClock = nowMs }.ToArray();
    public static byte[] Clock(long nowMs) => new Generated.MapFrameNT { ServerTime = nowMs }.ToArray();
    public static byte[] Logout() => new Generated.LogoutRS().ToArray();
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
        return new Generated.CharCheckNameRS { Result = (ushort)result, Nickname = name, Reason = 1 }.ToArray();
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

    /// <summary>
    /// 0x3620 SelectCharacter_RS; the key echoes the request. Wire prefix as in the official
    /// recording: u16 result, varint key, i32 last_using_pc_id, i64 dbid (offset 8 there). The
    /// client keeps this dbid as its own and compares soul-bound items' owner with it (live
    /// 2026-10-15: with i32 result/key it read wire id &lt;&lt; 40 | pc id &lt;&lt; 8). Tail after the
    /// dbid is kept as the client read it before (world entry worked): five zero bytes (CANDIDATE
    /// _char_key i32 and the option-data count), then our older char key, counts and customizing
    /// data. Without the five bytes the client stalled after selection (live 2026-10-15).
    /// BYTE AUDIT 2026-11-09 (13 official samples of the current client): after the dbid come _char_key VARINT (`c872`), the option
    /// data (varint count + bytes, e.g. `8a02` + a 266-byte zlib blob), the skill customizing count, the customizing data (`de02` +
    /// 350 bytes) and _server_start_time i64 (0 in all 13). With our five zeros the client read char key 0 and three empty lists, so
    /// our option and customizing data never reached it. Now the official layout. TEST LIVE before deploying (world entry).
    /// </summary>
    public static byte[] SelectCharacter(int key = 1, long dbId = 1, int charKey = 1, int lastUsingPcId = 1,
        byte[]? customizingData = null, long? serverStartTime = null, byte[]? optionData = null)
    {
        return PacketWriter.Opcode(0x3620).U16(0).Varint(key).I32(lastUsingPcId).I64(dbId).Varint(charKey)
            .Bytes(optionData ?? [])   // option data: the client's own settings blob it saved with 0x8d40
            .Varint(0)   // skill customizing list
            .Bytes(customizingData ?? []).I64(serverStartTime ?? 0).ToArray();   // _server_start_time: 0 in 13 of 13 official samples
    }

    /// <summary>
    /// 0x361c CharCreate_RS: result u16 + LobbyCharDesc generated from our state and the request's appearance. OBSERVED (rec
    /// 20261107-010322 #3182 Asmodian, -010735 #10468 Elyos, rebuilt byte for byte in LobbyChecks): the result is a u16 like every
    /// other lobby reply (we wrote an i32, so the client read the record two bytes late: its key as key &lt;&lt; 16, the "shifted
    /// selection key" of the dev runs); logout time 0, position 0, the race's creation subzone (CreatedZone), combat level 1,
    /// block end 0, combat power 0; the Asmodian record lists the worn starter weapon (godstones [0,0,0], fx 0), the Elyos one
    /// nothing. TEST LIVE: creation then selection with the now aligned key.
    /// </summary>
    public static byte[] CharacterCreated(string name, int gender, int pcId, int race, int key, long dbId,
        byte[] customizingData, int voiceType, int slotNo, long nowMs, int level = 1, int result = 0,
        IReadOnlyList<EquipBroad>? equips = null, int equipItemLevel = 0)
    {
        var w = PacketWriter.Opcode(0x361c).U16(result);
        WriteLobbyCharacter(w, new LobbyCharacter
        {
            Key = key, Nickname = name, PcId = pcId, Race = race, Gender = gender, Level = level, CombatLevel = 1,
            EquipItemLevel = equipItemLevel, LogoutTime = 0, Position = Vector3d.Zero, ZoneId = CreatedZone(race), RegTime = nowMs,
            CustomizingData = customizingData, VoiceType = voiceType, SlotNo = slotNo, DbId = dbId, Equips = equips ?? [],
        });
        return w.ToArray();
    }

    /// <summary>
    /// The Subzone.dat id a new character's record names, by race. OBSERVED (the two CharCreate_RS samples): Asmodian 4011
    /// D_Starter_Subzone_Village (Aldelle Village), Elyos 3040 L_Starter_Subzone_Layer_1_Castle_3_PcCreate.
    /// </summary>
    public static int CreatedZone(int race) => race == 2 ? 4011 : 3040;

    /// <summary>
    /// LobbyCharDesc _block_end_time of a listed character. OBSERVED in all 6 official Login_RS / Lobby_RS lists (2026-11-06/07, 9
    /// records): the server time less 24 hours (a few ms earlier, the same for every record of a list) on unblocked characters;
    /// 0 in CharCreate_RS.
    /// </summary>
    public static long UnblockedSince(long nowMs) => nowMs - 86_400_000L;

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

    /// <summary>0x3623 LoadComplete_RS: result u16, ticket i32, spawn Vec3, frame i32, char key (varint, as official; was u16 before Stage 1).</summary>
    public static byte[] LoadComplete(int ticket, Vector3d position, int frame = 2, int charKey = 1) =>
        new Generated.LoadCompleteRS { EnterMapTicket = ticket, ServerEnterMapPos = Generated.CommonVec3.From(position), Frame = frame, CharKey = charKey }.ToArray();

    static void WriteSpawnDetailTail(PacketWriter w, int ascension = 1, (int Level, long Exp, int Points, int Specialized)? gather = null,
        IReadOnlyList<(int Level, long Exp)>? crafts = null, IReadOnlyList<int>? spent = null, IReadOnlyList<int>? granted = null)
    {
        // Observed starter layout (official offsets 66..183) = CharDetail (usmap) _gather_source_stats {1 entry: gather level i32, exp
        // i64, skill point, specialized point} and _craft_stats {5 entries: ECraftCategory u8, level i32, exp i64}. These were always
        // the starter's level 1 / 0: the client showed every character as gathering Novice level 1 and craft level 1 at each login
        // (live 2026-11-11, Axel3 stored at gather level 6). The point widths: one byte each in the starter (0); written as varints
        // (the same byte below 128, totals reach 95 / 14; CANDIDATE: no official sample with points).
        var g = gather ?? (1, 0, 0, 0);
        w.U8(1).I32(g.Level).I64(g.Exp).Varint(g.Points).Varint(g.Specialized).U8(5);
        for (var category = 1; category <= 5; category++)
        {
            var (level, exp) = crafts is { } list && category - 1 < list.Count ? list[category - 1] : (1, 0L);
            w.U8(category).I32(level).I64(exp);
        }
        // Glide/sprint cooldowns, reputation, then the ascension grade (CANDIDATE).
        w.I32(0).I32(0).I64(0).U8(ascension);
        // ESkillPointType 1-3 (Point, StigmaShard, StigmaShardSuperior): first the points spent, then those items gave (OBSERVED,
        // community recordings 2026-11-04/05, e.g. 184134 #760: 383, 104 | 180, 80).
        foreach (var list in new[] { spent, granted })
        {
            w.U8(3);
            for (var pointType = 1; pointType <= 3; pointType++) w.U8(pointType).U16(Math.Clamp(list is { } l && pointType <= l.Count ? l[pointType - 1] : 0, 0, ushort.MaxValue));
        }
    }

    public static PacketWriter Stat(PacketWriter w, int type, int value) => w.U16(type).I32(value);

    public const int DpOpenLevel = 20, DpMaxValue = 150_000;

    /// <summary>0x3633 MyCharAppear_NT (client decoders 0x8498b80/0x91f3870).</summary>
    public static byte[] MyCharAppear(MyCharacterAppear a)
    {
        var w = PacketWriter.Opcode(0x3633).Varint(a.Key).U32(MyCharAppearMask | (a.TeleportEffect != 0 ? 0x1000u : 0) | (a.VoiceType != 0 ? 0x2000u : 0)
                | (a.SealstoneItemId != 0 ? 1u << 15 : 0) | (a.AbyssRankingPoint != 0 ? 1u << 16 : 0)).U8(0x37).String(a.Name).U16(a.ServerId)
            .I32(a.PcId).U8(a.Race).I32(a.CharLevel).I32(a.EquipItemLevel).I32(a.HighestTotalItemLevel).I32(a.LevelMax)
            .I64(a.Experience).I32(a.CombatLevel).I64(a.CombatExperience).U8(a.Gender);
        WriteSpawnDetailTail(w, a.Ascension, a.Gather, a.Crafts, a.SpentPoints, a.ItemPoints);
        // CharState (client decoder 0x8498b80, the one 0x3645 uses): u16 presence mask; bit 9 adds
        // _relationship_entity (u8) and bit 12 _wing_id (i32) after fp_max. Without the relationship the client
        // drew ordinary monsters as non-hostile (green names; live 2026-10-16), the official server draws them
        // red; other players' 0x3645 carry 1 PC_Light_Common in every sample (CANDIDATE for the own character).
        w.U16((a.ToggleFlags != 0 ? 1 << 7 : 0) | (a.Relationship != 0 ? 1 << 9 : 0) | (a.WingId != 0 ? 1 << 12 : 0)).Vec3(a.Position).F32(a.Direction).U16(0).U8(1);
        w.Varint(a.Hp).Varint(a.HpMax);
        // CharState mp, mp_max, ap, ap_max, sp, sp_max, dp, dp_max, dp_step, op, op_max, fp, fp_max (usmap). OBSERVED 2026-11-07 (5
        // official characters): dp_step always 1, dp_max 0 at levels 1-5 and 150000 at 20 and 45 (we sent 10000 and step 0).
        // PROVISIONAL: the level DP opens at (20 here; between 6 and 20 is unrecorded).
        var dpMax = a.CharLevel >= DpOpenLevel ? DpMaxValue : 0;
        foreach (var v in new[] { a.Mp, a.MpMax, 0, 0, SpawnSp, SpawnSp, 0, dpMax, 1, 100000, 100000, a.Fp, a.FpMax }) w.I32(v);
        if (a.ToggleFlags != 0) w.I32(a.ToggleFlags);   // state bit 7 (the official own appearance: 1 = the seal stone on)
        if (a.Relationship != 0) w.U8(a.Relationship);
        if (a.WingId != 0) w.I32(a.WingId);
        w.U8(3);
        Stat(w, 206, a.BattleMoveSpeed); Stat(w, 208, 300); Stat(w, 551, -3000);
        w.U8(0);  // no abnormal effects
        // EquipBroad records (the weapon alone was observed); their packed bool byte (display,
        // default mesh, override = 14) also carries purchased-customizing later (bit 4).
        var equips = a.Equips.Count > 0 ? a.Equips
            : a.WeaponId is { } weapon ? [new EquipBroad(weapon, VisualSlot: 1, Godstones: [0, 0, 0])] : [];
        w.Varint(equips.Count);
        foreach (var e in equips) WriteEquip(w, e);
        // _dbid sits just before the customizing data (official recording); the client compares
        // soul-bound items' _soul_bind_char_dbid with it.
        w.U8(a.AppearReason).I64((uint)a.MapId | (long)a.Instance << 32).I32(a.MapId).I32(3000).I64(a.DbId == 0 ? 1 : a.DbId);
        // Mask bits 6 _last_on_ground_pos_z (f32) and 8 _weather_type (u8 1): in every official sample (56/56), the ground height
        // under the character and weather type 1 (OFFICIAL bytes, e.g. rec 105448 #614 `... afc3040000000c09 | 0056bc46 | 01 | de02`).
        w.F32(a.Position.Z).U8(1);
        // Mask bit 12: the arrival effect sits between the dbid block and the customizing data (OBSERVED #9680 vs #382:
        // `... 01 | 41 | de 02 <customizing>`; the world entry has `01 | de 02`). It was placed before the voice until
        // 2026-10-19, which shifted the customizing data the client reads.
        if (a.TeleportEffect != 0) w.U8(a.TeleportEffect);
        w.Bytes(a.CustomizingData);
        // After the customizing data (usmap order, OBSERVED on all official samples 2026-11-06/07, 2026-11-09 re-check):
        // _is_purchased_customizing (mandatory bool, packed into the open bool byte), [bit 13 voice u8], [14 airspace i32],
        // [15 sealstone item i32], [16 abyss ranking point i64], [17 map pins], [18 ride i32], [19-21 arcana], [22 last death],
        // [bit 23 match server id u16, always], conqueror info (16 bytes, mandatory), [24-26 rift pvp / polymorph],
        // [bit 27 succession count info 16 bytes, always], combat power i64, highest combat power i64, Daevanion point lists.
        // Until 2026-11-09 we sent voice + 54 zero bytes without bits 23/27: the client read the Combat Power 18 bytes
        // after the voice, inside our zeros, and the HUD showed 0.
        w.Bool(false);
        if (a.VoiceType != 0) w.U8(a.VoiceType);   // bit 13: the official leaves a 0 voice out
        // Bit 15 _sealstone_item_id: the registered Power Shard (OBSERVED in every official 0x3633 of a character with one registered,
        // e.g. 105448 #614 `... 00 | 21f4cb1f | 1e05 ...`: 533460001 between the open bool and the match server id; world entries
        // and every map load). Left out, the client's shard slot emptied at each load (user 2026-11-12: "keeps disappearing").
        if (a.SealstoneItemId != 0) w.I32(a.SealstoneItemId);
        // bit 16 _abyss_ranking_point (usmap; bit 14 is never sent): the season's Abyss ranking point when there is one
        // (CANDIDATE: every recorded official entry had none, mask without bit 16).
        if (a.AbyssRankingPoint != 0) w.I64(a.AbyssRankingPoint);
        return w.U16(a.MatchServerId != 0 ? a.MatchServerId : a.ServerId).Zeros(16).Zeros(16).I64(a.CombatPower).I64(a.CombatPower).Raw(a.DaevanionPoints ?? DaevanionPointLists).ToArray();
    }

    /// <summary>MyCharAppear presence mask: bits 1-2 (always), 6 last on-ground z and 8 weather type, 23 match server id, 27 succession
    /// info (official always 6, 8, 23 and 27);
    /// 12 arrival effect and 13 voice are added when set.</summary>
    public const uint MyCharAppearMask = 0x0006u | 1u << 6 | 1u << 8 | 1u << 23 | 1u << 27;

    /// <summary>The official Daevanion point lists of MyCharAppear with no points: used and bonus, types 1-5 (OBSERVED).</summary>
    static readonly byte[] DaevanionPointLists = Convert.FromHexString("0501000002000003000004000005000005010000020000030000040000050000");

    public static byte[] CharBarrier(int key = 1) => new Generated.CharBarrierInfoNT { Key = key }.ToArray(0x3646);
    public static byte[] CharStatsUpdate(int key = 1, long statHpMax = 0) => PacketWriter.Opcode(0x364a).Varint(key).U8(0).I64(statHpMax).ToArray();

    /// <summary>0x364a CharStatsUpdate_NT with stats: key | count varint (TArray; official 0x3649 has 128+ as 80 01) | {u16 type, i32 value} (as 0x3649) | i64 hp max.</summary>
    public static byte[] CharStatsUpdate(int key, IReadOnlyList<(int Type, int Value)> stats, long statHpMax)
    {
        var w = PacketWriter.Opcode(0x364a).Varint(key).Varint(stats.Count);
        foreach (var (type, value) in stats) Stat(w, type, value);
        return w.I64(statHpMax).ToArray();
    }
    public static byte[] MentoringRoleUpdate(int role = 1, long expireTime = 0) => new Generated.MentoringRoleUpdateNT { MentoringRole = checked((byte)role), ExpireTime = expireTime }.ToArray();
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

    /// <summary>0x3649 CharAllStats_RS: result u16 | count varint (OBSERVED 80 01 = 128 stats, official 782-byte samples) | {u16 type, i32 value} | i64 hp max.</summary>
    public static byte[] CharAllStats(IReadOnlyList<(int Type, int Value)> stats, long statHpMax)
    {
        var w = PacketWriter.Opcode(0x3649).U16(0).Varint(stats.Count);
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
        [0x8a1c] = Convert.FromHexString("1d8a000000"),
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
    public static byte[] AddSkillList(int key, IReadOnlyList<(int SkillId, int Level)> skills, byte reason = 1, Func<int, int>? daevanion = null,
        Func<int, int>? equip = null)
    {
        var w = PacketWriter.Opcode(0x5101).Varint(key).Varint(skills.Count);
        foreach (var (id, level) in skills) SkillEntry(w, id, level, daevanion?.Invoke(id) ?? 0, equip?.Invoke(id) ?? 0);
        return w.U8(reason).ToArray();
    }

    /// <summary>
    /// A 0x5100 / 0x5101 skill entry: flag 1, i32 id, shown level (point level + Daevanion levels), point level, Daevanion levels,
    /// eight zero bytes (OFFICIAL 0x5100 entry "01 id shown point daev 00000000 00000000"; with no Daevanion levels the old
    /// "level level + nine zeros" form, byte for byte). The bonus bytes are SkillAdditionalLevel {daevanion, abnormal, equip, arcana,
    /// fix}; the worn gear's sub-stat skill levels go in _equip (OFFICIAL rec 115817 #173999: 14720000 "0c 09 02 00 01 00 00" = shown
    /// 12, point 9, Daevanion 2, equip 1, then four zero bytes).
    /// </summary>
    static void SkillEntry(PacketWriter w, int id, int level, int daevanion, int equip = 0)
    {
        var bonus = (byte)Math.Clamp(daevanion, 0, 255);
        var gear = (byte)Math.Clamp(equip, 0, 255);
        w.U8(1).I32(id).U8((byte)Math.Min(255, level + bonus + gear)).U8((byte)level).U8(bonus).U8(0).U8(gear).Zeros(6);
    }

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
    public static byte[] SkillOptionSlots(IReadOnlyList<int> skills)
    {
        var w = PacketWriter.Opcode(0x3900).Varint(skills.Count);
        foreach (var skill in skills)
        {
            w.I32(skill).U8(3);
            for (byte slot = 1; slot <= 3; slot++) w.U8(slot).I32(0);
        }
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

    /// <summary>0x5100 skill list: count + ready entries (flag 1, i32 id, level, original level, nine zero bytes).</summary>
    public static byte[] SkillList(IReadOnlyList<(int SkillId, int Level)> skills, Func<int, int>? daevanion = null, Func<int, int>? equip = null)
    {
        if (skills.Select(s => s.SkillId).Distinct().Count() != skills.Count) throw new ArgumentException("Expected unique skills");
        var w = PacketWriter.Opcode(0x5100).Varint(skills.Count);
        foreach (var (id, level) in skills)
        {
            if (id <= 0 || level is < 1 or > 255) throw new ArgumentException("Invalid skill ID or level");
            // Both official recordings write the dodge (11000100) as "03 01 <id>"; sending that made
            // the live client ignore Shift entirely (2026-10-14), so the plain "01 <id>" form stays.
            SkillEntry(w, id, level, daevanion?.Invoke(id) ?? 0, equip?.Invoke(id) ?? 0);
        }
        return w.ToArray();
    }

    /// <summary>
    /// 0x5600 QuickSlotList_NT from saved slots: per slot the observed 42-byte record (01 | slot | EQuickSlotType |
    /// item uid i64 | item id i32 | skill id i32 | 4 context skills | macro, social action, toggle), as SkillSlots.
    /// Item slots carry the uid and template (CANDIDATE: only skill records were observed). Slots in
    /// <paramref name="autoUse"/> carry toggle 1 (CANDIDATE: the auto-use toggle; 0 in all 42 recorded records).
    /// </summary>
    public static byte[] QuickSlots(IReadOnlyList<(int Slot, int Type, long ItemUid, int ItemId, int SkillId)> slots,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? context = null)
    {
        if (slots.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).Varint(slots.Count);
        foreach (var (slot, type, uid, item, skill) in slots) WriteQuickSlot(w, slot, type, uid, item, skill, context?.GetValueOrDefault(slot));
        return w.ToArray();
    }

    /// <summary>
    /// One QuickSlotInfo (OBSERVED, all 14 records of #376 parse to the byte): presence mask u8 (bit 0: the context-skill
    /// array), slot, EQuickSlotType (0 skill, 1 item), item uid i64, item id i32, skill id i32, [count u8 + i32s], macro u8,
    /// social action i32, toggle u8 (usmap _toggle_yn; 0 in all 42 recorded records; CANDIDATE: the auto-use toggle).
    /// Skill slots carry 4 context skills (the skill, then its chain), item slots none.
    /// </summary>
    /// The context list is the slot's skill and the skills it offers besides (the Q/E slots 6 and 7, OBSERVED official Sorcerer
    /// bar 2026-11-07: [15220000, 15150000, 0, 0] and [15010000, 15050000, 15310000, 0]), always 4 entries.
    public static PacketWriter WriteQuickSlot(PacketWriter w, int slot, int type, long uid, int item, int skill, IReadOnlyList<int>? contextSkills = null,
        int macro = 0, int socialAction = 0, bool toggle = false)
    {
        var context = type == 0 && skill != 0;
        w.U8(context ? 1 : 0).U8(slot).U8(type).I64(uid).I32(item).I32(skill);
        if (context)
        {
            var list = (contextSkills is { Count: > 0 } given ? given : [skill]).Take(4).ToList();
            while (list.Count < 4) list.Add(0);
            w.U8(4);
            foreach (var id in list) w.I32(id);
        }
        return w.U8((byte)Math.Clamp(macro, 0, 255)).I32(socialAction).U8(toggle ? 1 : 0);
    }

    /// <summary>
    /// 0x5600 QuickSlotList_NT from the bar's records, in slot order. Empty records (an emptied slot keeping a default off the bar)
    /// are left out: OBSERVED, the 16 official lists name only filled slots (a level-1 bar: 8 skills and 6 items, no gaps sent).
    /// </summary>
    public static byte[] QuickSlots(IEnumerable<QuickSlotRecord> slots)
    {
        var filled = slots.Where(s => !s.IsEmpty).OrderBy(s => s.Slot).ToList();
        if (filled.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).Varint(filled.Count);
        foreach (var s in filled) WriteQuickSlot(w, s);
        return w.ToArray();
    }

    /// <summary>One QuickSlotInfo from a record (see the tuple form).</summary>
    public static PacketWriter WriteQuickSlot(PacketWriter w, QuickSlotRecord s) =>
        WriteQuickSlot(w, s.Slot, s.Type, s.ItemUid, s.ItemId, s.SkillId, s.ContextSkills, s.Macro, s.SocialAction, s.Toggle);

    /// <summary>0x5600 observed 42-byte skill-slot records.</summary>
    public static byte[] SkillSlots(IReadOnlyList<(int Slot, int SkillId)> slots)
    {
        if (slots.Count > 127) throw new ArgumentException("Too many slots");
        var w = PacketWriter.Opcode(0x5600).Varint(slots.Count);
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
    public static byte[] ItemDepotList(IReadOnlyList<InventoryItem> items, IReadOnlyList<int>? slotExtends = null)
    {
        var w = PacketWriter.Opcode(0x5611).U16(0x0210).Varint(items.Count);
        foreach (var item in items) WriteItem(w, item);
        // usmap: inventory by item, inventory by gold, storage, storage by item, character storage by gold, by item.
        for (var i = 0; i < 6; i++) w.U8((byte)Math.Clamp(slotExtends is { Count: 6 } s ? s[i] : 0, 0, 255));
        return w.ToArray();
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
        new Generated.ItemSoulBindStartRS { Result = (ushort)result, Uid = uid, CostItemUid = costUid, CostItemId = costId }.ToArray();
    /// <summary>0x562e ItemSoulBindCancel_RS: result, item uid.</summary>
    public static byte[] SoulBindCancel(long uid, int result = 0) => PacketWriter.Opcode(0x562e).U16(result).I64(uid).ToArray();
    /// <summary>0x5630 ItemSoulBindComplete_RS: result, item uid, bind count, the bound stats and sub-stat skills.</summary>
    public static byte[] SoulBindComplete(long uid, byte count = 1, int result = 0, IReadOnlyList<(int Type, int Value)>? stats = null,
        IReadOnlyList<(int Skill, byte Level)>? skills = null)
    {
        // OBSERVED (2026-11-06 capture 115817 #206432): the bound stats as {EStat id u16, value i32}, then the skills (none there;
        // CANDIDATE element {skill i32, level u8}: the item record's _sub_skills form, OBSERVED in 0x5611).
        var w = PacketWriter.Opcode(0x5630).U16(result).I64(uid).U8(count).Varint(stats?.Count ?? 0);
        foreach (var (type, value) in stats ?? []) Stat(w, type, value);
        w.Varint(skills?.Count ?? 0);
        foreach (var (skill, level) in skills ?? []) w.I32(skill).U8(level);
        return w.ToArray();
    }

    /// <summary>0x561d ItemEquipLevelChange_NT: equipped item level total and the highest reached.</summary>
    public static byte[] ItemEquipLevelChange(int level, int highest) => new Generated.ItemEquipLevelChangeNT { EquipItemLevel = level, HighestTotalItemLevel = highest }.ToArray();

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
            // enchant, enchant exp, enchant bonus, surpass level, surpass bonus, item level, broken, slot, the manastone slots,
            // then _soul_bind_char_dbid i64 and _soul_bind_count u8 (usmap order; CANDIDATE placement).
            // three godstone slots, then the seven arrays and the skin extraction count
            d.WriteTail(d.WriteGodstones(d.WriteStones(w.U8(d.EnchantLevel).I32(0).I32(0).U8(d.SurpassLevel).I32(d.SurpassBonus).I32(d.ItemLevel).Bool(false).U8(d.EquipSlot))
                .I64(d.SoulBindOwner).U8(d.SoulBindCount)));
        return w;
    }

    /// <summary>
    /// The item detail after the godstones: _arcana_main_stats (empty), _sub_stats (the soul-bind stats), _add_sub_stats (their
    /// types at 0), the four skill arrays (empty) and _skin_extraction_count 0 (usmap order; OBSERVED, see EquipDetail).
    /// </summary>
    public static PacketWriter DetailTail(PacketWriter w, IReadOnlyList<(int Type, int Value)>? subStats)
    {
        var stats = subStats ?? [];
        w.Varint(0).Varint(stats.Count);
        foreach (var (type, value) in stats) w.U16(type).I32(value);
        w.Varint(stats.Count);
        foreach (var (type, _) in stats) w.U16(type).I32(0);
        return w.Zeros(4).U8(0);
    }

    /// <summary>Candidate 0x5611 equipped inventory with one item (observed record widths).</summary>
    public static byte[] EquippedInventory(int itemId, int itemLevel = 1, long itemKey = 1, int equipSlot = 1) =>
        PacketWriter.Opcode(0x5611).U8(0x10).U8(2).U8(1)
            .U8(1).I64(itemKey).I32(itemId).I64(1).U8(11).I32(equipSlot)
            .Zeros(23).I32(itemLevel).U8(1).U8(equipSlot).Zeros(16).U8(3).Zeros(20).Zeros(9).ToArray();

    // ---------- Combat ----------

    /// <summary>
    /// 0x3804 hit: target varint | u16 mask (bit 1 damage plotter, bit 2 damage) | actor varint | skill | the cast's action uid u8 |
    /// hit type | [plotter] | effect | seq u32 | attack speed varint | [damage varint] | seq u8 | 00. OBSERVED (byte audit
    /// 2026-11-09, own hits of 8 official recordings): the hit names its cast's action uid (3200 of 3289 equal the 0x3802 uid; we
    /// sent 0) and leaves the damage out (mask bit 2 clear) when it is 0 (``0000 a162 00e6aa00 0c 02 ... ee50 02 00``).
    /// </summary>
    public static byte[] SkillHit(int targetKey, int actorKey, int skillId, int effectId, int damage, bool critical = false,
        int seq = 1, int attackSpeed = 10000, byte angle = 0, ushort plotterFlags = 0, int actionUid = 0)
    {
        if (seq is < 0 or > 255) throw new ArgumentException("Hit sequence must fit one byte");
        // With an angle or flags the damage plotter follows the hit type (mask bit 2, as the official hits: 3 bytes - the plotter's
        // flag bits and EAttackAngle 1 BackAttack / 2 FrontAttack).
        var plotter = angle != 0 || plotterFlags != 0;
        var w = PacketWriter.Opcode(0x3804).Varint(targetKey).U8((plotter ? 2 : 0) | (damage != 0 ? 4 : 0)).U8(0).Varint(actorKey)
            .U32((uint)skillId).U8((byte)actionUid).Varint(critical ? 3 : 2);
        if (plotter) w.U8((byte)plotterFlags).U8((byte)(plotterFlags >> 8)).U8(angle);
        w.U32((uint)effectId).U32((uint)seq).Varint(attackSpeed);
        if (damage != 0) w.Varint(damage);
        return w.U8(seq).U8(0).ToArray();
    }

    /// <summary>EAttackAngle on the wire: 1 BackAttack, 2 FrontAttack (EAttackAngle, usmap).</summary>
    public const byte AngleBack = 1, AngleFront = 2;
    /// <summary>Damage plotter flag bits (OBSERVED official 0x3804: 02 00 with blocks, 04 00, 80 00 with criticals; CANDIDATE meanings).</summary>
    public const ushort PlotterShieldBlock = 0x01, PlotterWeaponBlock = 0x02;

    /// <summary>
    /// 0x3804 with presence byte 0x14 (bit 0x10 adds one u8 before the process uid): target | 14 00 | caster | skill | action uid |
    /// hit type | effect id u32 | effect seq u32 | attack speed | damage | 01 | process uid u16 (the seq). OBSERVED in the 2.0.6
    /// recording 20261019-231752 (#73349; 112 hits, a Gladiator's melee skills, so not projectile-only there); the order flag
    /// then process uid is OBSERVED in the community recordings 2026-11-04 to 06 (391 hits with mask 0016; with seq 1 both
    /// orders give the same bytes, so Rapid Fire's second and third arrows were wrong). CANDIDATE: what bit 0x10 means
    /// (_sealstone_used by usmap position; its byte was 01 in every sample); used for the hits of projectile skills.
    /// </summary>
    public static byte[] ProjectileHit(int targetKey, int actorKey, int skillId, int effectId, int damage, bool critical,
        byte actionUid, int seq, int attackSpeed = 10000)
    {
        if (seq is < 0 or > 255) throw new ArgumentException("Hit sequence must fit one byte");
        return PacketWriter.Opcode(0x3804).Varint(targetKey).U8(0x14).U8(0).Varint(actorKey).U32((uint)skillId).U8(actionUid)
            .Varint(critical ? 3 : 2).U32((uint)effectId).U32((uint)seq).Varint(attackSpeed).Varint(damage).U8(1).U8(seq).U8(0).ToArray();
    }

    /// <summary>
    /// 0xe005 UpdateGroggyInfo_NT (dev 0x3b04 + 0xa501): key | presence u8 (bit 0 max, bit 1 energy) | state u8 | [max i32] |
    /// [energy i32] | u8. OBSERVED (2.0.6 recording 20261019-231752, boss a2eb01, 9 messages): mask 03 state 01 max 50 with the
    /// energy falling 50 -> 7 under the player's hits, then mask 00 state 03 once it broke; the last byte was 02 in all nine.
    /// The last byte is the packed _groggy_activated (bit 1) / _groggy_pause (usmap): OBSERVED 02 on 756 of 762 messages of the
    /// community recording 2026-11-06 090856 while the guard could be drained, 00 on a final boss outside its windows (as A2Core
    /// PR #4's "activated ? 2 : 0"). We send 02 always (no deactivated guards yet). CANDIDATE: the state names.
    /// Sent by Aion.World.Stagger (SkillEffect.dat GroggyGuardDecreaseEnergy drains, the refill after GroggyTime).
    /// </summary>
    public static byte[] UpdateGroggyInfo(int key, byte state, int? maxEnergy, int? energy, byte tail = 2)
    {
        var w = PacketWriter.Opcode(0xe005).Varint(key).U8((byte)((maxEnergy is null ? 0 : 1) | (energy is null ? 0 : 2))).U8(state);
        if (maxEnergy is { } max) w.I32(max);
        if (energy is { } e) w.I32(e);
        return w.U8(tail).ToArray();
    }

    /// <summary>
    /// 0x3804 with switch 06: an NPC's skill hits a player (17 official hits, capture
    /// 20261013-144836): target, 06 00, attacker, skill, action seq, kind 2, 00 00 02, effect ID,
    /// effect seq u32, attack speed, damage, effect seq u8, 00. The 00 00 02 bytes vary rarely (02 00 02, 00 00 01);
    /// their meaning is unknown and the common form is sent. OBSERVED (our recordings 2026-10-13 to 11-06, 7460 NPC hits):
    /// both effect seq fields agree in 7422, and a skill's hits number 1, 2, ... as its skill_hits rows (2 rows: 1, 2 in 77
    /// uses; 4 rows: 1-4 in 15).
    /// </summary>
    public static byte[] NpcSkillHit(int targetKey, int actorKey, int skillId, int actionSeq, int effectId, int damage, int attackSpeed = 10000,
        int hitType = 2, ushort plotterFlags = 0, byte angle = AngleFront)
    {
        if (actionSeq is < 0 or > 255) throw new ArgumentException("Action sequence must fit one byte");
        // EHitType 1 Miss / 2 Normal / 3 Critical; the plotter: flag bits (blocks) and the attack angle (OBSERVED 00 00 02 front,
        // 00 00 01 back, 02 00 02 a block). Live 2026-11-08: every monster hit said front damage (the angle was always 2).
        return PacketWriter.Opcode(0x3804).Varint(targetKey).U8(6).U8(0).Varint(actorKey).U32((uint)skillId).U8(actionSeq)
            .Varint(hitType).U8((byte)plotterFlags).U8((byte)(plotterFlags >> 8)).U8(angle).U32((uint)effectId).U32(1).Varint(attackSpeed).Varint(damage).U8(1).U8(0).ToArray();
    }

    /// <summary>
    /// 0x3804 of a monster skill's abnormal row (SkillEffect Abnormal): target | mask 0x0100 (the abnormal id list) | attacker | skill |
    /// action seq | hit type 6 | effect id (the Abnormal row) | u32 1 | attack speed | abnormal ids | 01 00. OFFICIAL: the poison grass
    /// rec 20261106-144624 #26939 `0438 a376 0001 ae9b01 40b71200 01 06 0c954f07 01000000 904e 01 651bb700 0100` right after its damage
    /// hit; sent whether or not the abnormal lands (its 30% chance did not: no 0x382a followed), 0x382a follows when it does
    /// (rec 115817 #139520 -> #139529).
    /// </summary>
    /// <summary>
    /// 0x3804 of a skill's second effect row with no damage and no abnormal list (mask 0): target | 00 00 | attacker | skill | action seq |
    /// hit type 2 | effect id | u32 1 | attack speed | 01 00. OFFICIAL: the floor trap's knockdown row ("0438 a13f 0000 a49702 ec361e00 01
    /// 02 3c74cd0b 01000000 904e 0100", rec 013918 #16785) and the bombs' (123649012, #18132), right after the damage hit.
    /// </summary>
    public static byte[] NpcEffectHit(int targetKey, int actorKey, int skillId, int actionSeq, int effectId, int attackSpeed = 10000)
    {
        if (actionSeq is < 0 or > 255) throw new ArgumentException("Action sequence must fit one byte");
        return PacketWriter.Opcode(0x3804).Varint(targetKey).U8(0).U8(0).Varint(actorKey).U32((uint)skillId).U8(actionSeq)
            .Varint(2).U32((uint)effectId).U32(1).Varint(attackSpeed).U8(1).U8(0).ToArray();
    }

    public static byte[] NpcAbnormalHit(int targetKey, int actorKey, int skillId, int actionSeq, int effectId, IReadOnlyList<int> abnormalIds, int attackSpeed = 10000)
    {
        if (actionSeq is < 0 or > 255) throw new ArgumentException("Action sequence must fit one byte");
        var w = PacketWriter.Opcode(0x3804).Varint(targetKey).U8(0).U8(1).Varint(actorKey).U32((uint)skillId).U8(actionSeq)
            .Varint(6).U32((uint)effectId).U32(1).Varint(attackSpeed).Varint(abnormalIds.Count);
        foreach (var id in abnormalIds) w.I32(id);
        return w.U8(1).U8(0).ToArray();
    }

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
    /// the level), 0x2000 spawn_unique_key (the MapData UniqueKey).
    /// </summary>
    public static byte[] NpcAppear(NpcAppearance npc, uint? spawnTag = null)
    {
        var social = npc.SocialAction != 0 && !npc.Dead;
        var moveState = npc.Walking ? (byte)2 : npc.MoveState;
        var mask = 0x2004 | (npc.Aggressive ? 0x08 : 0) | (moveState != 0 ? 0x01 : 0) | (npc.MoveSubstate != 0 ? 0x02 : 0) | (social ? 0x0C00 : 0)
            | (npc.SpawnAct != 0 ? 0x1000 : 0) | (npc.ParentKey != 0 ? 0x10 : 0);
        // CharDesc: mask u8 (bit 0 the nickname: a summon carries its owner's name, OBSERVED on the Ranger's trap "01 09 Song4Life").
        var w = PacketWriter.Opcode(0x3641).Varint(npc.Key).U16(mask).U8((byte)(npc.OwnerName is { Length: > 0 } ? 1 : 0));
        if (npc.OwnerName is { Length: > 0 } owner) w.String(owner);
        // CharState mask: bit 9 relationship always; bit 6 combat state 1 for an NPC already fighting (OFFICIAL: 0x0240 in 372 of
        // 5,634 appears 2026-11-06/07, all with combat state 1; we sent 0x0200 always).
        w.U32((uint)npc.NpcId).U16(npc.InCombat ? 0x0240 : 0x0200)
            .Vec3(npc.Position).F32(npc.Direction).U16(HeadingU16(npc.Direction)).U8(npc.Dead ? 0 : 1)
            .Varint(npc.Hp).Varint(npc.HpMax);
        // OBSERVED (all official appears 2026-11-06/07): monsters carry 0, 0 in the 7th/8th value (963 of the monster appears;
        // a level-45 hero 0, 200000), friendly NPCs 100/600/1500/50000; we sent 100, 100 for everyone.
        var pair = npc.Monster ? 0 : 100;
        foreach (var v in new[] { 100, 100, 0, 0, 0, 0, pair, pair, 1, 0, 0, 0, 0 }) w.I32(v);
        if (npc.InCombat) w.U8(1);
        w.U8(npc.Relationship);
        PlayerPackets.WriteAbnormals(w, npc.Abnormals ?? []);
        if (moveState != 0) w.U8(moveState);
        if (npc.MoveSubstate != 0) w.U8(npc.MoveSubstate);
        w.U8(npc.Reason);
        if (npc.Aggressive) w.U8(1);
        // _parent_key (mask 0x10, i32): the owner of a summon. OBSERVED (official appears 2026-11-06/07, masks 0x1f / 0x101f / 0x5f:
        // the Ranger trap 2920630 "07 02 06 | a71e0000" = owner key 7847, the Elementalist spirits 2920122/2920142/2920147
        // "07 02 01 | 94120000"): an i32 right after the reason. The official summons also set mask bit 0x08 with no aggressive
        // byte and send no unique key (bit 0x2000); we keep our 0x2000 tail (CANDIDATE: tail difference not decoded).
        if (npc.ParentKey != 0) w.I32(npc.ParentKey);
        // _stats: {u16 stat, i32 value}. OBSERVED on the official spirits: 3 entries (205 MoveSpeed, 208, 214), on the trap 2 (205, 208).
        var stats = npc.Stats ?? [];
        w.U8((byte)stats.Count);
        foreach (var (stat, value) in stats) w.U16((ushort)stat).I32(value);
        w.I32(npc.Level);
        if (social) w.I32(npc.SocialAction).U8(1).I32(0).I64(npc.SocialStart);
        if (npc.SpawnAct != 0) w.I32(npc.SpawnAct);
        return w.U32(spawnTag ?? (npc.UniqueKey != 0 ? npc.UniqueKey : (uint)npc.Key)).U8(0).ToArray();
    }

    /// <summary>
    /// 0xe239 ElementalSummonActivate_NT / 0xe23a ElementalSummonDeactivate_NT: a presence mask (1: the element and key, 2: the
    /// lifetime), EElementalSummonGroupType u8, the spirit's key varint, [lifetime ms i32]. OBSERVED 2026-11-04 192703 (2.0.6):
    /// `39e2 01 04 dded01` (#11367), `3ae2 01 04 dded01` (#11436); the Ancient spirit `39e2 03 05 868301 30750000` (30 s).
    /// </summary>
    public static byte[] SpiritActivate(int element, int key, int? lifeMs = null)
    {
        var w = PacketWriter.Opcode(0xe239).U8((byte)(lifeMs is null ? 1 : 3)).U8((byte)element).Varint(key);
        return (lifeMs is { } life ? w.I32(life) : w).ToArray();
    }

    public static byte[] SpiritDeactivate(int element, int key) => PacketWriter.Opcode(0xe23a).U8(1).U8((byte)element).Varint(key).ToArray();

    /// <summary>
    /// 0x3641 for a Spiritmaster's own spirit. OBSERVED 2026-11-04 192703 (2.0.6; 21 summons of one level-45 owner, #11374 rebuilt
    /// byte for byte): mask 0x101f, the NpcAppear head, HP, 13 state values (<paramref name="state"/>: the recorded owner's; they
    /// changed with the owner), relationship 1, one abnormal {mask 0x11, uid 1, the summon's abnormal, duration -1, the permanent
    /// expiry, the owner, the summon level, 02, the owner's position}, move state 7 / substate 2 (floating), reason 6, the owner's
    /// key i32, stats {205: 2197, 208: 319} (as recorded), the owner's level, the element's spawn act, 00.
    /// </summary>
    public static byte[] SpiritAppear(int key, int npcId, Vector3d position, double direction, int hp, int ownerKey, Vector3d ownerPosition,
        int abnormal, int summonLevel, int level, int spawnAct, IReadOnlyList<int>? state = null, IReadOnlyList<(int Type, int Value)>? stats = null)
    {
        var w = PacketWriter.Opcode(0x3641).Varint(key).U16(0x101f).U8(0).U32((uint)npcId).U8(0).U8(2)
            .Vec3(position).F32(direction).U16(HeadingU16(direction)).U8(1).Varint(hp).Varint(hp);
        foreach (var v in state ?? SpiritState) w.I32(v);
        w.U8(1).Varint(1).U8(0x11).Varint(1).I32(abnormal).I64(-1).I64(PermanentAbnormalExpiry).Varint(ownerKey).U8((byte)summonLevel).U8(2).Vec3(ownerPosition);
        w.U8(NpcAppearance.FlyingOnAirStop).U8(2).U8(NpcAppearance.NpcSpawnReason).I32(ownerKey);
        stats ??= SpiritStats;
        w.U8((byte)stats.Count);
        foreach (var (type, value) in stats) w.U16((ushort)type).I32(value);
        return w.I32(level).I32(spawnAct).U8(0).ToArray();
    }

    /// <summary>The spirit appear's state values and stats as recorded for a level-45 owner (2026-11-04 192703; CANDIDATE elsewhere).</summary>
    public static readonly int[] SpiritState = [3308, 3308, 0, 0, 0, 106000, 100, 150000, 1, 0, 100000, 0, 570000];
    public static readonly (int Type, int Value)[] SpiritStats = [(205, 2197), (208, 319)];
    /// <summary>A permanent abnormal's expiry on the wire (2099-12-31 15:00 UTC; OBSERVED in the spirit appears).</summary>
    public const long PermanentAbnormalExpiry = 0x3bb2ad57580;

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
    public static byte[] SprintCooltime(int key, int ms = 0) => new Generated.SprintCooltimeUpdateNT { Key = key, Cooltime = ms }.ToArray();
    /// <summary>0x3741 CharMoveSprintOn_RS: key, u16 result (0 observed), answering 0x3704.</summary>
    public static byte[] SprintOnReply(int key, int result = 0) => new Generated.CharMoveSprintOnRS { Key = key, Result = (ushort)result }.ToArray();
    /// <summary>0x3743 CharGlideStart_RS (ActionProtocol_RS: key, result), answering 0x3706 CharGlideStart_RQ. Catalogue position; CANDIDATE.</summary>
    public static byte[] GlideStartReply(int key, int result = 0) => new Generated.CharFlyStartRS { Key = key, Result = (ushort)result }.ToArray();
    /// <summary>0x3744 CharFlyStart_RS (ActionProtocol_RS: key, result), answering 0x370a CharFlyStart_RQ. Catalogue position; CANDIDATE.</summary>
    public static byte[] FlyStartReply(int key, int result = 0) => new Generated.CharFlyStartRS { Key = key, Result = (ushort)result }.ToArray(0x3744);
    /// <summary>0x3742 CharMoveSprintOff_RS: key, answering 0x3705.</summary>
    public static byte[] SprintOffReply(int key) => new Generated.CharMoveSprintOffRS { Key = key }.ToArray();
    /// <summary>0x3722 SprintOff_NT: key; also sent alone when SP runs out.</summary>
    public static byte[] SprintOffNotice(int key) => new Generated.CharMoveSprintOffNT { Key = key }.ToArray(0x3722);
    /// <summary>0x3834 SetMainTarget_RS: 00, u16 result, the target, answering 0x3833.</summary>
    public static byte[] SetMainTargetReply(int target) => new Generated.SetMainTargetRS { Result = 0, TargetKey = 0, PartsId = target }.ToArray(0x3834);
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
    /// <summary>0x8d43 UpdateClientEnvSetting_RS: u16 result, answering 0x8d42. OBSERVED (byte audit 2026-11-09): the official reply is the 4 bytes `438d 0000`; we sent an i32 (2 extra bytes).</summary>
    public static byte[] ClientEnvSettingReply() => new Generated.UpdateClientEnvSettingRS().ToArray();
    /// <summary>0x8d41 UpdateClientOption_RS: u16 result, answering the 0x8d40 option save.</summary>
    public static byte[] ClientOptionReply() => new Generated.UpdateClientOptionRS().ToArray();
    /// <summary>0x5672 WingList_NT: count, {i32 wing, u8 enchant}. Official world entry "00" (empty); non-empty framing is a CANDIDATE.</summary>
    public static byte[] WingList(IReadOnlyList<(int Id, byte Enchant)> wings) =>
        new Generated.WingListNT { WingList = wings.Select(wi => new Generated.CommonWingInfo { WingId = wi.Id, EnchantLevel = wi.Enchant }).ToList() }.ToArray();

    /// <summary>0x5675 WingChange_RS: result, packed is_wing_skin, wing id (answering 0x5674). CANDIDATE.</summary>
    public static byte[] WingChangeReply(int wingId, bool skin, int result = 0) => PacketWriter.Opcode(0x5675).U16(result).Bool(skin).I32(wingId).ToArray();
    /// <summary>0x5676 WingChange_NT: character key, packed is_wing_skin, wing id. CANDIDATE.</summary>
    public static byte[] WingChangeNotice(int key, int wingId, bool skin) => new Generated.WingChangeNT { Key = key, IsWingSkin = skin, WingId = wingId }.ToArray();
    /// <summary>
    /// 0x5673 WingAcquire_NT: the player key i32, the wing, its enchant level. OBSERVED 2 of 2: 2026-10-19 231752 #35172 (ascension,
    /// the default wing 30200600, right before the stats) and 2026-11-06 234912 #61774 (wing item 512400013, GetWing 30201900, 49 ms
    /// after its 0x5619).
    /// </summary>
    public static byte[] WingAcquire(int key, int wingId, byte enchant = 0) => PacketWriter.Opcode(0x5673).I32(key).I32(wingId).U8(enchant).ToArray();

    /// <summary>Default wings (items 532400001/532400002 grant them on use): Light 30200600, Dark 40200601.</summary>
    public static int DefaultWing(int race) => race == 2 ? 40200601 : 30200600;

    /// <summary>
    /// 0xE333 RefreshCurrency_NT {currency group i32, balances map ECurrencyCategory i32 -> i64}: the account currencies the client shows
    /// (Quna = GameCash 7, its bound 95 / tradable 96 / activating 102 / pending 103 states). Wire opcode from its catalogue run (0x3BD1
    /// + 0xA762, as its neighbours MonolithList..Attendance); not seen in a recording, the group id is a CANDIDATE.
    /// </summary>
    public static byte[] RefreshCurrency(int group, IReadOnlyDictionary<int, long> balances)
    {
        var w = PacketWriter.Opcode(0xe333).I32(group).Varint(balances.Count);
        foreach (var (category, balance) in balances.OrderBy(p => p.Key)) w.I32(category).I64(balance);
        return w.ToArray();
    }

    /// <summary>ECurrencyCategory values of Quna and its states.</summary>
    public const int CurrencyQuna = 7, CurrencyQunaBound = 95, CurrencyQunaTradable = 96, CurrencyQunaActivating = 102, CurrencyQunaPending = 103;
    /// <summary>ECurrencyCategory Mileage (A2 Mileage, an account currency: CurrencyInfo FixedCol 0, BM shop Mileage goods).</summary>
    public const int CurrencyMileage = 6;

    /// <summary>0x8d02 GlideCooltimeUpdate_NT: key, cooltime ms (shape of 0x8d03).</summary>
    public static byte[] GlideCooltime(int key, int ms = 0) => new Generated.GlideCooltimeUpdateNT { Key = key, Cooltime = ms }.ToArray();

    // ---------- Quests (0x8d family base 0x89d1) and NPC interaction (0xe2 base 0xdd07) ----------
    // Decoded from the official recordings: 0x8d09/0x8d10/0x8d11/0x8d0f/0x8d2c/0x8d07/0x8d08,
    // 0xe241/0xe243 OBSERVED byte-exact; the others anchored by catalogue position.

    /// <summary>
    /// Common_Quest: i32 id, u8 status, v step, v goal1..6, i32 uid, v roll count, i32 publisher id, the random reward items
    /// {i32 item, v count, u8 enchant, i64 prob} (OBSERVED byte-exact for a duty mission in rec 20261106-115817 #173810).
    /// </summary>
    static PacketWriter WriteQuest(PacketWriter w, QuestWire q)
    {
        w.I32(q.QuestId).U8(q.Status).Varint(q.Step);
        for (var i = 0; i < 6; i++) w.Varint(i < q.Goals.Length ? q.Goals[i] : 0);
        w.I32(q.Uid).Varint(q.RollCount).I32(0).Varint(q.Rewards?.Count ?? 0);
        foreach (var r in q.Rewards ?? []) w.I32(r.Item).Varint(r.Count).U8(r.Enchant).I64(r.Prob);
        return w;
    }

    /// <summary>0x8d14 QuestRollDuty_RS {u16 result, Common_Quest} (catalogue position after QuestRemove_NT 0x8d12, request 0x8d13; never recorded).</summary>
    public static byte[] QuestRollDutyReply(int result, QuestWire q) => WriteQuest(PacketWriter.Opcode(0x8d14).U16(result), q).ToArray();

    /// <summary>0x8d09 QuestList_NT (world entry; the official new character got none).</summary>
    public static byte[] QuestList(IReadOnlyList<QuestWire> quests)
    {
        var w = PacketWriter.Opcode(0x8d09).Varint(quests.Count);
        foreach (var q in quests) WriteQuest(w, q);
        return w.ToArray();
    }

    /// <summary>0x8d11 QuestAdd_NT: a newly accepted quest, followed by QuestAccept_RS 0x8d0b on an accept (OBSERVED 2026-11-04/06).</summary>
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
    public static byte[] QuestAcceptReply(int questId, int uid, int result = 0) => new Generated.QuestAcceptRS { Result = (ushort)result, QuestId = questId, QuestUid = uid }.ToArray();

    /// <summary>0x8d0f QuestTakeReward_RS: result, uid, the indexes of the random reward items paid (duty quests; none otherwise).</summary>
    public static byte[] QuestTakeRewardReply(int uid, int result = 0, IReadOnlyList<int>? randomPaid = null) =>
        new Generated.QuestTakeRewardRS { Result = (ushort)result, QuestUid = uid, RandomRewardIndexList = [.. randomPaid ?? []] }.ToArray();

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
    public static byte[] EventFunc(byte action, string value, int memberCount = 0, string second = "", long groupKey = 0) =>
        PacketWriter.Opcode(0x8d15).U8(0).U8(action).String(value).String(second).I64(groupKey).I32(memberCount).U8(0).ToArray();

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
        new Generated.CutsceneCompleteRS { Result = (ushort)result, CutsceneName = cutscene, GroupReqKey = groupKey }.ToArray();

    /// <summary>EEventFuncAction values the server sends.</summary>
    public const byte FuncCutscene = 9, FuncShowNote = 15, FuncShowMessage = 16, FuncPlayNpcTalk = 19, FuncFocusCamera = 45, FuncShowGuide = 46;

    /// <summary>A cutscene (level sequence) by name; the client plays it and reports CompleteCutscene. CANDIDATE: the caption's layout with Cutscene.</summary>
    /// <summary>
    /// The Cutscene event function. The i32 after the group key is 1 when the server waits for the client's CutsceneComplete
    /// (a CompleteCutscene objective: the lake cave's 1290 and 1291) and 0 for a dungeon's own arrival cutscene (the Ascension
    /// dungeon's 1293). OBSERVED in the 2026-10-19 recording.
    /// </summary>
    public static byte[] Cutscene(string name, bool wait = true, long groupKey = 0) => EventFunc(FuncCutscene, name, wait ? 1 : 0, groupKey: groupKey);

    /// <summary>
    /// A map event's cutscene carries a group request key, which the client sends back with CutsceneComplete; the answer is
    /// 0xe335, then GroupRequestProgressed_NT 0xe002 {key i64, character key i32 (the own player key)} and
    /// GroupRequestComplete_NT 0xe004 {key i64}. OBSERVED 2026-10-19 231752: Cutscene_L_A_Seq_1290 and the two first
    /// Teleport_Mirror_interaction_IN cutscenes with keys 0x28273, 0x2829e, 0x282a8 (#29708-#37985); the cutscene before a
    /// map move (1291, mirror 03) and the arrival cutscene carry 0 and get 0xe335 alone. The community fork found the same.
    /// </summary>
    public static byte[] GroupRequestProgressed(long groupKey, int characterKey) => PacketWriter.Opcode(0xe002).I64(groupKey).I32(characterKey).ToArray();
    public static byte[] GroupRequestComplete(long groupKey) => PacketWriter.Opcode(0xe004).I64(groupKey).ToArray();

    /// <summary>A caption played by name (NpcTalk.dat Name, ENpcTalkType Caption). OBSERVED.</summary>
    public static byte[] Caption(string talk) => EventFunc(FuncPlayNpcTalk, talk, 1);

    /// <summary>A guide (tutorial popup) by name. OBSERVED once (Tutorial_UI_SkillLevelUp).</summary>
    public static byte[] Guide(string guide) => EventFunc(FuncShowGuide, guide);

    /// <summary>
    /// A note (NoteData.dat Name: a letter or book the player reads and closes) by name: EventFuncAction ShowNote. OBSERVED
    /// (official, 3 samples): `158d 00 0f 14 Note_D1_DQ2202015_04` + 14 zero bytes, the EventFunc layout; ours had 6 (8 short).
    /// </summary>
    public static byte[] Note(string note) => EventFunc(FuncShowNote, note);

    /// <summary>A message by string key (ShowMessage / BroadCastingMessage directives). CANDIDATE: the caption's layout with ShowMessage.</summary>
    public static byte[] Message(string key) => EventFunc(FuncShowMessage, key, 1);

    /// <summary>
    /// 0x8d92 RelationshipEntityUpdate_NT: key | ERelationshipEntity u8. OBSERVED (official recordings 2026-11-06/07: 0x8d92 with
    /// 13 NPC_Companion, 3, 2, 23 PC_FriendlyToAll; the catalogue's 0x39cd at the 0x8d block offset +0x53c5, between
    /// BroadcastLevelUpdate 0x8d91 and UpdateEnvObjHitCount 0x8d93). The old 0x39cd was never official: the client ignored every
    /// scripted relationship change (live 2026-11-07: Elvida stayed green and untargetable while she fought the player).
    /// </summary>
    public static byte[] RelationshipUpdate(int key, int relationship) => new Generated.RelationshipEntityUpdateNT { Key = key, RelationshipEntity = (byte)relationship }.ToArray();

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
    public static byte[] ExpUpdate(long exp, long gained, byte reason) => new Generated.ExpUpdateNT { Exp = exp, RewardExp = gained, Reason = reason }.ToArray();
    /// <summary>0x8d08 LevelUpdate_NT: level, highest level, experience.</summary>
    public static byte[] LevelUpdate(int level, int levelMax, long exp) => new Generated.LevelUpdateNT { CharLevel = level, LevelMax = levelMax, Exp = exp }.ToArray();

    /// <summary>0xe241 StartNpcInteraction_RS: result, NPC key, expiry (server now + 300 s).</summary>
    // ---------- World-map fog (the 0x54ca run: catalogue index 482 + 0x54ca) ----------

    /// <summary>
    /// 0x56ac OpenSubzoneGroupIDs_NT: the uncovered world-map fog groups, a count and one ESubzoneGroupID byte each (OBSERVED,
    /// capture 20261013 #202 `01 3f`: group 63 at the world entry). Not 0x9501: that list (#375, 36 i32s 2..54) is another
    /// message; sending our fog there left the whole map clouded (live 2026-10-19).
    /// </summary>
    public static byte[] OpenSubzoneGroups(IReadOnlyList<int> groups) =>
        new Generated.OpenSubzoneGroupIDsNT { OpenSubzoneGroupIdList = groups.Select(g => checked((byte)g)).ToList() }.ToArray();
    /// <summary>0x56ad AddSubzoneGroupID_NT: one newly uncovered group, one byte (OBSERVED #2396 `41` = 65 Poeta, #9344 `40`).</summary>
    public static byte[] AddSubzoneGroup(int group) => new Generated.AddSubzoneGroupIDNT { AddSubzoneGroupId = checked((byte)group) }.ToArray();

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

    /// <summary>0x8d05 EnvObjDie_NT: the object's key (OBSERVED: 191 in 40 of our recordings, e.g. `058d c98003`).</summary>
    public static byte[] EnvObjDie(int key) => PacketWriter.Opcode(0x8d05).Varint(key).ToArray();
    /// <summary>0x3635 EnvObjDespawn_NT: key, state, reason, despawn act (official sample e4ec03 01 01 00; widths CANDIDATE).</summary>
    public static byte[] EnvObjDespawn(int key, byte state = 1, byte reason = 1) => new Generated.EnvObjDespawnNT { Key = key, State = state, Reason = reason }.ToArray();
    /// <summary>0x363e InteractEnvObjStop_RS: result, object key (usmap; answering 0x363d). CANDIDATE.</summary>
    public static byte[] InteractEnvObjStopReply(int key, int result = 0) => new Generated.InteractEnvObjStopRS { Result = (ushort)result, Key = key }.ToArray();
    /// <summary>0x363e's result for a stop with no use in progress (OBSERVED 0x3813, key 0).</summary>
    public const int InteractStopNothing = 0x3813;
    /// <summary>0x3639 reasons OBSERVED: 0x1905 after a hit on the player, 0x1906 with the player's stop. CANDIDATE: the names.</summary>
    public const ushort InterruptDamaged = 0x1905, InterruptStopped = 0x1906;
    /// <summary>
    /// 0x3639 InteractEnvObjInterrupted_NT: key varint, reason u16, interactor key varint, interaction count i32 (OBSERVED, 18 samples
    /// of the community recordings 2026-11-04 to 06, e.g. 185000 #5957 `3936 bcef01 0519 d223 00000000`).
    /// </summary>
    public static byte[] InteractEnvObjInterrupted(int key, ushort reason, int interactor, int count = 0) =>
        PacketWriter.Opcode(0x3639).Varint(key).U16(reason).Varint(interactor).I32(count).ToArray();
    /// <summary>EnvObjSpawn for a quest object: relationship from EnvObjData (Kibelisks: 8 EnvObj_Light).</summary>
    /// <summary>EnvObjSpawn for a quest object: relationship from EnvObjData (Kibelisks: 8 EnvObj_Light).</summary>
    public static byte[] EnvObjSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey, byte relationship) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(4).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(yaw).U8(1).U8(1).U8(1).I64(createTime).Zeros(8).I32(uniqueKey).U8(relationship).U8(0).U8(0).ToArray();

    /// <summary>
    /// 0x3634 EnvObjSpawn_NT with the can-interact and phase bytes set (map-event SetEnvObjCanInteract / EnvObjChangePhase;
    /// A2Core PR #4). Which byte is can-interact and which is phase: CANDIDATE (layout comment above; every recorded spawn
    /// has 01 01 01 and phase 0).
    /// </summary>
    public static byte[] EnvObjSpawn(int key, int envObjId, Vector3d position, double yaw, long createTime, int uniqueKey, byte relationship,
        byte canInteract, byte phase) =>
        PacketWriter.Opcode(0x3634).Varint(key).U8(4).U8(0).I32(envObjId).F32(position.X).F32(position.Y).F32(position.Z)
            .I32(0).F32(yaw).U8(1).U8(canInteract).U8(1).I64(createTime).Zeros(8).I32(uniqueKey).U8(relationship).U8(phase).U8(0).ToArray();

    /// <summary>0x363c InteractEnvObjCanInteractFlag_NT {object key varint, can interact u8} (usmap name; CANDIDATE: no recording has one).</summary>
    public static byte[] InteractEnvObjCanInteract(int key, bool canInteract) =>
        PacketWriter.Opcode(0x363c).Varint(key).U8(canInteract ? (byte)1 : (byte)0).ToArray();

    /// <summary>0x3638 InteractEnvObjStarted_NT: object key, interactor key, state 1.</summary>
    public static byte[] InteractEnvObjStarted(int key, int interactor) => new Generated.InteractEnvObjStartedNT { Key = key, InteractorKey = interactor, State = 1 }.ToArray();
    /// <summary>0x363a InteractEnvObjCompleted_NT: object key, interactor key, interaction count, item id.
    /// A gather node carries its collection number 1, 2, 3 (OBSERVED 20261107-211036 #9034/#10153/#11678: the client's node bar).</summary>
    public static byte[] InteractEnvObjCompleted(int key, int interactor, int count = 0) =>
        new Generated.InteractEnvObjCompletedNT { Key = key, InteractorKey = interactor, InteractionCount = count }.ToArray();
    /// <summary>0x8d6d InteractionEnvObjFinishState_NT: object key (no usmap struct at this index; OBSERVED body = the key varint).</summary>
    public static byte[] InteractEnvObjFinishState(int key) => new Generated.InteractionEnvObjFinishStateNT { Key = key }.ToArray();
    /// <summary>0x363b InteractEnvObjAvailableState_NT: object key.</summary>
    public static byte[] InteractEnvObjAvailableState(int key) => new Generated.InteractEnvObjAvailableStateNT { Key = key }.ToArray();
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
    public static byte[] TargetUpdate(int key, int target = 0) => new Generated.TargetUpdateNT { Key = key, TargetKey = 0, PartsId = target }.ToArray(0x3835);
    /// <summary>0x364e CharRotate_NT: key, target and current direction, reason.</summary>
    public static byte[] CharRotate(int key, float targetDir, float currentDir, byte reason = 0) => new Generated.CharRotateNT { Key = key, TargetDir = targetDir, CurrentDir = currentDir, Reason = reason }.ToArray();

    /// <summary>0x3746 LastOnGroundPosZ_NT: key, f32 height; official after the map load.</summary>
    public static byte[] LastOnGroundZ(int key, float z) => new Generated.LastOnGroundPosZNT { Key = key, PosZ = z }.ToArray();

    public static byte[] NpcHp(int key, long hp) => PacketWriter.Opcode(0x8d00).Varint(key).U8(2).U8(1).U8(0).I64(hp).ToArray();

    /// <summary>
    /// 0x8d04 CharDie_NT (catalogue index 819 under the 0x8d base; official capture 20261013-144836, 15 kills,
    /// also sent for other players' kills; the last byte is EPCDieType: 0 for NPCs, 2 DiePvE for a player CANDIDATE):
    /// victim, skill u32, killer, u16 (0x0523 for both killers; CANDIDATE server id), killer name, killer guild name, 2 bytes
    /// (guild emblem, CANDIDATE split), the killing NPC's data id varint, NPC die act id i32, reason u8, die type u8 (OBSERVED,
    /// all 430 deaths of the community recordings 2026-11-04 to 06 parse; the old "8 zero bytes" are these four when empty).
    /// A player killed by an NPC: the NPC's skill, its key, server id 0, no name, its NpcData id, reason 01, die type 02
    /// (2026-11-06 090856 #96371). Follows NPC HP 0 and 0x3835; CANDIDATE trigger of the death animation.
    /// Reason, OBSERVED in all 137 recorded deaths (2.0.6 recordings 20261013-144836: 15, 20261019-231752: 122):
    /// <see cref="DieByKiller"/> 01 in all 69 with a killer (skill, killer key, server 0x0523, name); <see cref="DieNoKiller"/> 00 in
    /// all 68 with none (skill 0, killer 0, server 0, empty name, e.g. #32324): NPCs that died with no attacker after their own
    /// skill (0x3641 appear, own 0x3802/0x3806, HP 0, then 0x8d04; #88660-#88664 five at once). CANDIDATE: the reason's name.
    /// </summary>
    public static byte[] Kill(int victimKey, int skillId, int killerKey, int serverId, string killerName, byte dieType = 0, byte reason = DieByKiller,
        int killerNpcId = 0) =>
        PacketWriter.Opcode(0x8d04).Varint(victimKey).U32((uint)skillId).Varint(killerKey).U16(serverId).String(killerName)
            .String("").U16(0).Varint(killerNpcId).I32(0).U8(reason).U8(dieType).ToArray();

    /// <summary>0x8d04 reason byte: 01 a killer is named, 00 no killer (OBSERVED, see <see cref="Kill"/>).</summary>
    public const byte DieByKiller = 1, DieNoKiller = 0;

    /// <summary>0x8d21: key | 00 | 1 in combat / 0 out.</summary>
    public static byte[] CombatState(int key, bool inCombat) => new Generated.CombatStateUpdateNT { Key = key, PresenceMask = 0, CombatState = (byte)(inCombat ? 1 : 0) }.ToArray();
    /// <summary>0x8d21 with mask 1: the boss's berserk apply time (CombatStateUpdate_NT _boss_berserk_apply_time, OBSERVED once: 218d 88ad02 01 01 [i64]).</summary>
    public static byte[] CombatState(int key, bool inCombat, long berserkAt) => new Generated.CombatStateUpdateNT { Key = key, PresenceMask = 1, CombatState = (byte)(inCombat ? 1 : 0), BossBerserkApplyTime = berserkAt }.ToArray();

    /// <summary>
    /// 0x3809 SkillCastingStart_NT: caster | mask (2: attack speed present) | skill i32 | level u8 | target info (mask 2, target
    /// varint, yaw f32, location Vec3) | casting time varint | act move u8 1 | [attack speed varint]. OBSERVED (521 official NPC
    /// casts, capture 20261107-013918: "0938 bcf601 02 56ab1200 01 02 a13f yaw xyz dc0b 01 904e", then 0x3802 after the cast).
    /// </summary>
    /// Mask 0 (<paramref name="attackSpeed"/> null): no attack speed at the end, OFFICIAL for the magic floor trap 2290014 (rec 013918
    /// #16634 "0938 8d8102 00 ec361e00 01 02 8d8102 yaw xyz f403 01").
    public static byte[] SkillCastingStart(int key, int skill, int targetKey, double yaw, Vector3d location, int castingMs, int? attackSpeed = 10000)
    {
        var w = PacketWriter.Opcode(0x3809).Varint(key).U8(attackSpeed is null ? 0 : 2).I32(skill).U8(1).U8(2).Varint(targetKey).F32((float)yaw).Vec3(location)
            .Varint(castingMs).U8(1);
        if (attackSpeed is { } speed) w.Varint(speed);
        return w.ToArray();
    }

    /// <summary>
    /// 0x8d04 CharDie_NT of an NPC that died by itself (a trap or bomb after its blast): the key and 18 zero bytes (no skill, no killer,
    /// die type 0, the trailing flag 0). OFFICIAL: all 55 floor bombs and the 3 floor traps of rec 013918 ("048d 91b301 00..00").
    /// </summary>
    public static byte[] SelfDeath(int key) => PacketWriter.Opcode(0x8d04).Varint(key).Zeros(18).ToArray();

    /// <summary>0x8d52 NpcFirstAggro_NT: the NPC key (catalogue 897 + 0x89d1). OBSERVED with every official aggro start.</summary>
    public static byte[] NpcFirstAggro(int key) => new Generated.NpcFirstAggroNT { Key = key }.ToArray();

    /// <summary>0x3642 despawn: flag 3 removes a corpse, 1 is the out-of-view removal.</summary>
    public static byte[] Death(int key, int flag = 3) => new Generated.NpcDisappearNT { Key = key, DespawnActId = flag }.ToArray();

    // ---------- Casts ----------

    /// <summary>0x3802 mask-zero skill start (a2kit cast fixtures).</summary>
    /// Auto-load skills (OBSERVED 2026-11-06/07): mask 4 adds the charges left (u8), mask 8 the time to the next refill (varint),
    /// after the cooldown ("0c ... 9e55 01 00 01 d448": 1 left, 9,300 ms; "08 ... 0100 ca43": none left, 8,650 ms).
    public static byte[] SkillStart(int skill, int actionUid, double yaw, Vector3d location, int targetKey = 1, int actorKey = 1,
        int attackSpeed = 10000, int cooldown = 0, int? chargesLeft = null, int? refillInMs = null)
    {
        var mask = (chargesLeft is > 0 ? 4 : 0) | (refillInMs is not null ? 8 : 0);
        var w = PacketWriter.Opcode(0x3802).Varint(actorKey).U8(mask).I32(skill).U8(actionUid).U8(2).Varint(targetKey)
            .F32(yaw).Vec3(location).Varint(attackSpeed).U8(1).Varint(cooldown);
        if (chargesLeft is > 0 and var left) w.U8((byte)left);
        if (refillInMs is { } refill) w.Varint(refill);
        return w.ToArray();
    }

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

    /// <summary>0x3801 cast reply with both optional bytes (toggle and client action UID).</summary>
    public static byte[] CastReply(bool accepted, int skill, int uid) =>
        PacketWriter.Opcode(0x3801).U8(3).U16(accepted ? 0 : 1).I32(skill).U8(0).U8(uid).ToArray();

    /// <summary>
    /// 0x3801 for a dodge: presence 00, result u16, the skill and nothing else (OBSERVED, 9 bytes at all 100 official dodges of
    /// 2026-10-19, the sprint's dash included: the skill is CombatDodge 11000100 even when the start is NormalDodge 11000101).
    /// Result 1 is the refusal (DashVariant 6). The flags-first 11-byte cast reply reads as a failure to this client and drops the sprint.
    /// </summary>
    /// Corrected 2026-11-06 (official dodge replies "0138 00 0000 24d9a700": flags u8 0, result u16, skill i32, the same 9-byte form as
    /// every normal cast): the old builder wrote result, skill, 00, which put the skill one byte early. Every dash variant tried live
    /// before carried that malformed reply.
    public static byte[] DodgeResult(int skill, int result = 0) => CastResult(skill, result);

    /// <summary>
    /// 0x3804 with switch 00: a self skill's effect (OBSERVED 50-60 ms after all 36 official CombatDodge starts): target, 00 00, actor,
    /// skill, the start's action uid, 02, effect id (skill x 100 + 13 for the dodge), i32 1, attack speed 10000, 01, 00.
    /// The same form carries a skill's groggy-guard drain on its hit (OBSERVED 20261019-231752 #31014: effect 1119000022, the hit's
    /// seq 3 in both the u32 and the u8 where the dodge has 1).
    /// </summary>
    /// The same form names a godstone proc's crowd control (OBSERVED rec 115817 #36370: the caster's attack speed, e.g. d38401).
    public static byte[] SkillEffect(int targetKey, int actorKey, int skill, int actionUid, int effectId, int attackSpeed = 10000) =>
        PacketWriter.Opcode(0x3804).Varint(targetKey).U8(0).U8(0).Varint(actorKey).U32((uint)skill).U8((byte)actionUid).U8(2)
            .U32((uint)effectId).U32(1).Varint(attackSpeed).U8(1).U8(0).ToArray();

    /// <summary>
    /// 0x3806 skill end: key, skill, action uid, u8 tail (OBSERVED: 0 after every dodge and sprint dash, before the abnormal's
    /// 0x382c; 12 on most chain/combo skill ends, per skill in skill-timings.json).
    /// </summary>
    /// <summary>
    /// 0x3803 SkillFireProjectile_NT: caster | mask 0 | target varint | skill i32 | action uid | projectile varint | position Vec3 |
    /// flight ms varint | seq i32 | seq u8 | 0. OBSERVED (2,715 official, tools/projectile_timing.py; seq 1.. per shot of a multi-shot skill).
    /// </summary>
    /// <summary>
    /// 0x380e projectile stopped (a wall, or its target): caster | 02 | 00 | skill i32 | action uid | projectile varint | position |
    /// i32 1 | 01 00 01 | position. OBSERVED (capture 20261107-012837: the Lake Guardian helpers' shots at 1100-1400 ms; the two
    /// positions differ by ~140 cm, here both the stop point). CANDIDATE field meaning.
    /// </summary>
    public static byte[] ProjectileStop(int key, int skill, int actionUid, int projectile, Vector3d at) =>
        PacketWriter.Opcode(0x380e).Varint(key).U8(2).U8(0).I32(skill).U8((byte)actionUid).Varint(projectile).Vec3(at)
            .I32(1).U8(1).U8(0).U8(1).Vec3(at).ToArray();

    /// <summary>
    /// 0x3848 SkillRangeNoticeFilterLocation_NT: the warned impact spot of one location of a multi-location skill, sent with the
    /// caster's 0x3802 and right before the matching 0x3803 (same spot, same index, its flight time). OFFICIAL bytes (83 samples,
    /// rec 115817 #186090-186094 skill 1221085 five spots, rec 012837 #21929 skill 1223510 four spots): caster varint | skill i32 |
    /// action uid u8 | effect seq i32 (1) | index u8 (1..n) | u8 0 | position Vec3 | direction f32 (degrees) | time u16 (ms =
    /// the 0x3803 flight time). The usmap names _effect_seq/_skill_process_uid/_effect_time_ms; widths of the index and time
    /// from the bytes (CANDIDATE names for the index and the 0 byte).
    /// </summary>
    public static byte[] SkillRangeNotice(int key, int skill, int actionUid, int effectSeq, int index, Vector3d at, double direction, int timeMs) =>
        PacketWriter.Opcode(0x3848).Varint(key).I32(skill).U8((byte)actionUid).I32(effectSeq).U8((byte)index).U8(0).Vec3(at)
            .F32(direction).U16(timeMs).ToArray();

    /// <summary>
    /// 0x3b04 UpdateGroggyInfo_NT (usmap DevPacketData_GameServer_UpdateGroggyInfo_NT, catalogue opcode): key varint | GroggyInfo
    /// {state u8 (EGroggyState), guard max energy i32, guard energy i32, attack count i32, attack duration ms i64, attack expire
    /// time i64, pause bool, cooltime expire time i64, activated bool}. CANDIDATE: never in an official recording (no sample of
    /// the field widths, presence masks or EGroggyState values); sent only with boss-client.xml groggy_wire.
    /// </summary>
    public static byte[] UpdateGroggyInfo(int key, byte state, int maxEnergy, int energy, int attackCount, long durationMs, long expireTime,
        bool pause, long cooltimeExpire, bool activated) =>
        PacketWriter.Opcode(0x3b04).Varint(key).U8(state).I32(maxEnergy).I32(energy).I32(attackCount).I64(durationMs).I64(expireTime)
            .Bool(pause).I64(cooltimeExpire).Bool(activated).ToArray();

    /// <summary>
    /// 0x380c SkillCastingStop_NT: caster varint | result u16 (0, or 6152 Skill_Abnormal when crowd control broke the cast) | skill
    /// i32 | remaining cooldown varint. OFFICIAL (122 samples, all NPC casts, all decode to the end).
    /// </summary>
    public static byte[] SkillCastingStop(int key, int result, int skill, int cooldownMs) =>
        PacketWriter.Opcode(0x380c).Varint(key).U16(result).I32(skill).Varint(cooldownMs).ToArray();

    public static byte[] SkillEnd(int key, int skill, int actionUid, byte tail = 0) => new Generated.SkillEndNT { Key = key, SkillId = skill, SkillActionUid = (byte)actionUid, StopReason = tail }.ToArray();

    /// <summary>
    /// 0x3801 Skill start result for a normal cast: flags u8 0 (no toggle, no client action uid), result u16, skill i32 = 9 bytes,
    /// sent right AFTER the caster's 0x3802 (OBSERVED: 1,062 of 1,062 accepted official casts 2026-11-06, refusals with
    /// EResult Skill_* codes such as 6174 Skill_Moving and 6173 Skill_ChainSkillError).
    /// </summary>
    public static byte[] CastResult(int skill, int result = 0) => PacketWriter.Opcode(0x3801).U8(0).U16(result).I32(skill).ToArray();


    /// <summary>
    /// 0x3803 SkillFireProjectile_NT: caster, 00, target, skill, action uid, projectile id varint (the skill_hits ProjectileId),
    /// target position, remaining flight ms varint, effect seq i32, effect seq varint, 00. OBSERVED: every NPC 0x3803 of our
    /// recordings (2022) parses in this form; the remaining flight is 100-1300 ms, median 250.
    /// </summary>
    public static byte[] FireProjectile(int casterKey, int targetKey, int skill, int actionUid, int projectile, Vector3d targetPos, int remainMs, int effectSeq) =>
        PacketWriter.Opcode(0x3803).Varint(casterKey).U8(0).Varint(targetKey).I32(skill).U8((byte)actionUid).Varint(projectile).Vec3(targetPos)
            .Varint(remainMs).I32(effectSeq).Varint(effectSeq).U8(0).ToArray();

    /// <summary>
    /// 0x380e SkillProjectileCollision_NT: caster, mask (02: destroyed), target hit (0: none), skill, action uid, projectile id
    /// varint, collision position, effect seq i32, effect seq varint, 00, [01 when destroyed], the projectile's position.
    /// OBSERVED: all 402 NPC 0x380e of our recordings parse in this form (359 destroyed); the collision comes ~50 ms before the hit.
    /// </summary>
    public static byte[] ProjectileCollision(int casterKey, int targetKey, int skill, int actionUid, int projectile, Vector3d at, int effectSeq,
        bool destroyed, Vector3d projectilePos)
    {
        var w = PacketWriter.Opcode(0x380e).Varint(casterKey).U8(destroyed ? 2 : 0).Varint(targetKey).I32(skill).U8((byte)actionUid).Varint(projectile)
            .Vec3(at).I32(effectSeq).Varint(effectSeq).U8(0);
        if (destroyed) w.U8(1);
        return w.Vec3(projectilePos).ToArray();
    }

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
        // dx / dy only when not 0, as dz (OFFICIAL NPC masks 2026-11-06/07: 0x22 dx only 4,966, 0x24 dy only 5,104, 0x20 neither;
        // we always sent both).
        var w = PacketWriter.Opcode(0x371d).Varint(key).U8((run ? 0x01 : 0) | 0x20 | (dx != 0 ? 0x02 : 0) | (dy != 0 ? 0x04 : 0) | (dz != 0 ? 0x08 : 0));
        if (run) w.U8(3);
        if (dx != 0) w.U8((byte)(sbyte)dx);
        if (dy != 0) w.U8((byte)(sbyte)dy);
        if (dz != 0) w.U8((byte)(sbyte)dz);
        w.U16(angle).U16(angle);
        if (!run) w.U8(1);
        return w.ToArray();
    }

    public static byte[] NpcStop(int key, Vector3d position, double heading) =>
        PacketWriter.Opcode(0x371a).Varint(key).U8(0).U8(0).Vec3(position).U16(HeadingU16(heading)).ToArray();
}
