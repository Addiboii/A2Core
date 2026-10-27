namespace Aion.Protocol.Native;

// Rides (pets/mounts, VehicleList ids), item use and reward boxes. Opcodes are catalogue index + run
// base (research/social-protocol.md): ride run base 0x8c24 (0x9002 CharGetOnRide_RS = 990, pinned by
// the client's ActionProtocol predicate), 0x56 run base 0x54ca (live 0x5612 ItemUse_RQ, 10 bytes, and
// 0x5646 OpenRewardBox_RQ, 15 bytes, OBSERVED unanswered 2026-10-16). Layouts are the usmap structs;
// only 0x9000 (world entry) and 0x5616/0x5617 have official samples (capture 20261013-144836).
// ActionProtocol messages (0x9002..0x9022) carry the actor key as a varint; _result is u16 as in
// every observed RS. Everything without a sample is CANDIDATE.

/// <summary>A creature board stat slot (Common_CreatureBoardStatSlot): index, ECreatureOptionGrade, EStat, value, lock, activated.</summary>
public sealed record BoardSlot(byte Index, byte Grade, int StatType, long Value, bool Locked = false, bool Activated = true);

/// <summary>A creature board (Common_CreatureBoardInfo): ECreatureType, level, exp and its stat slot panels by number.</summary>
public sealed record CreatureBoard(byte Type, int Level, long Exp, IReadOnlyDictionary<byte, IReadOnlyList<BoardSlot>> Panels)
{
    /// <summary>A new board as the official world entry shows it: level 1, three panels of one empty slot.</summary>
    public static CreatureBoard Fresh(byte type) =>
        new(type, 1, 0, Enumerable.Range(1, 3).ToDictionary(n => (byte)n, _ => (IReadOnlyList<BoardSlot>)[new BoardSlot(0, 0, 0, 0)]));
}

/// <summary>A ride (RideInfo on the wire: data id and level), its soul points, and whether it is acquired.</summary>
public sealed record RideEntry(int RideId, int Level, int SoulPoints = 0, bool Owned = true);

public static class RidePackets
{
    public const ushort RideInit = 0x9000, GetOnRq = 0x9001, GetOnRs = 0x9002, GetOnNt = 0x9003,
        GetOffRq = 0x9004, GetOffRs = 0x9005, GetOffNt = 0x9006, AcquireRq = 0x9007, AcquireRs = 0x9008, AcquireNt = 0x9009,
        DefaultSetRq = 0x900a, DefaultSetRs = 0x900b, SoulPointIncrease = 0x900d, BoardRollRq = 0x9017, BoardRollRs = 0x9018,
        BoardLockRq = 0x9019, BoardLockRs = 0x901a, BoardInfoNt = 0x901c, SoulPointDecrease = 0x900e, CrystallizeRq = 0x900f,
        CrystallizeRs = 0x9010, CrystalIncrease = 0x9011, CrystalDecrease = 0x9012, LevelUpRq = 0x9013, LevelUpRs = 0x9014;
    // Death and respawn (0x8d run, base 0x89d1: 0x8d04 CharDie_NT = 819 is the OBSERVED kill message).
    public const ushort RespawnRq = 0x8d22, RespawnRs = 0x8d23, UseRebirthRq = 0x8d24, UseRebirthRs = 0x8d25, UseRebirthNt = 0x8d26;
    /// <summary>ERespawnType.</summary>
    public const byte RespawnKisk = 1, RespawnAuto = 2, RespawnItem = 3;
    /// <summary>ESoulCrystalIncreaseReason.</summary>
    public const byte CrystallizeReason = 1, ItemReason = 2;
    public const ushort ItemUseRq = 0x5612, ItemUseRs = 0x5613, ItemUseNt = 0x5616, ItemUseStartedNt = 0x5617, ItemUseInterruptedNt = 0x5618,
        ItemUseCompletedNt = 0x5619, OpenBoxRq = 0x5646, OpenBoxRs = 0x5647;

    /// <summary>EGetOffRideReason.</summary>
    public const byte ByClient = 1, ByServer = 2, BySubzone = 3;

    /// <summary>
    /// 0x9000 RideInitData_NT: default ride i32 | acquired rides map{i32 id: RideInfo{i32 id, i32 level}} |
    /// soul points map{i32 id: i32} | soul crystals map {ESoulCrystalType u8: i32} | collection list (empty) |
    /// creature boards map {ECreatureType u8: CreatureBoardInfo} | selected panel u8.
    /// Maps are a varint count of key/value pairs (CANDIDATE, consistent with the official sample, which the
    /// fresh boards rebuild byte for byte: five boards 2..6, panel 1 selected).
    /// </summary>
    public static byte[] RideInitData(int defaultRide, IReadOnlyList<RideEntry> rides, IReadOnlyDictionary<int, int>? crystals = null,
        IReadOnlyList<CreatureBoard>? boards = null, byte selectedPanel = 1)
    {
        var owned = rides.Where(r => r.Owned).ToList();
        var w = PacketWriter.Opcode(RideInit).I32(defaultRide).Varint(owned.Count);
        foreach (var r in owned) w.I32(r.RideId).I32(r.RideId).I32(r.Level);
        var souls = rides.Where(r => r.SoulPoints > 0).ToList();
        w.Varint(souls.Count);
        foreach (var r in souls) w.I32(r.RideId).I32(r.SoulPoints);
        var held = (crystals ?? new Dictionary<int, int>()).Where(c => c.Value > 0).OrderBy(c => c.Key).ToList();
        w.Varint(held.Count);
        foreach (var (type, amount) in held) w.U8(type).I32(amount);
        w.Varint(0);
        var all = boards ?? Enumerable.Range(2, 5).Select(t => CreatureBoard.Fresh((byte)t)).ToList();
        w.Varint(all.Count);
        foreach (var b in all.OrderBy(b => b.Type)) WriteBoard(w.U8(b.Type), b);
        return w.U8(selectedPanel).ToArray();
    }

    /// <summary>CreatureBoardInfo: type | level i32 | exp i64 | panels map {u8: array of slots}. Slot bools pack across records (OBSERVED).</summary>
    public static PacketWriter WriteBoard(PacketWriter w, CreatureBoard b)
    {
        w.U8(b.Type).I32(b.Level).I64(b.Exp).Varint(b.Panels.Count);
        foreach (var (number, slots) in b.Panels.OrderBy(p => p.Key))
        {
            w.U8(number).Varint(slots.Count);
            foreach (var s in slots) WriteSlot(w, s);
        }
        return w;
    }

    /// <summary>CreatureBoardStatSlot: index u8 | grade u8 | stat type u16 (OBSERVED width) | value i64 | lock, activated (packed bools).</summary>
    static PacketWriter WriteSlot(PacketWriter w, BoardSlot s) => w.U8(s.Index).U8(s.Grade).U16(s.StatType).I64(s.Value).Bool(s.Locked).Bool(s.Activated);

    /// <summary>0x9018 CreatureBoardStatSlotRoll_RS: result | type | panel | changed slots | board level/exp {type u8, level i32, exp i64}.</summary>
    public static byte[] BoardRollReply(int result, byte type, byte panel, IReadOnlyList<BoardSlot> changed, int level, long exp)
    {
        var w = PacketWriter.Opcode(BoardRollRs).U16(result).U8(type).U8(panel).Varint(changed.Count);
        foreach (var s in changed) WriteSlot(w, s);
        return w.U8(type).I32(level).I64(exp).ToArray();
    }

    /// <summary>0x901a CreatureBoardStatSlotLock_RS: result | type | panel | slot | lock.</summary>
    public static byte[] BoardLockReply(int result, byte type, byte panel, byte slot, bool locked) =>
        PacketWriter.Opcode(BoardLockRs).U16(result).U8(type).U8(panel).U8(slot).Bool(locked).ToArray();

    /// <summary>0x901c CreatureBoardFixLevelByOnBoarding_NT: the whole board (sent when its level or slots change; CANDIDATE use).</summary>
    public static byte[] BoardInfo(CreatureBoard board) => WriteBoard(PacketWriter.Opcode(BoardInfoNt), board).ToArray();

    /// <summary>0x3655 PolymorphUpdate_NT (lobby range: live = positional): key | polymorph abnormal uid (0 = none). CANDIDATE.</summary>
    public static byte[] PolymorphUpdate(int key, int abnormalUid) => PacketWriter.Opcode(0x3655).Varint(key).Varint(abnormalUid).ToArray();



    /// <summary>0x9002 CharGetOnRide_RS: key | result | ride | move state (EMoveStateModelType) | substate | sprinting.</summary>
    public static byte[] GetOnReply(int key, int result, int rideId, byte moveState = 0, bool sprinting = false) =>
        PacketWriter.Opcode(GetOnRs).Varint(key).U16(result).I32(rideId).U8(moveState).U8(0).Bool(sprinting).ToArray();

    /// <summary>0x9003 CharGetOnRide_NT to others (and late viewers): as the RS without the result.</summary>
    public static byte[] GetOnNotice(int key, int rideId, byte moveState = 0, bool sprinting = false) =>
        PacketWriter.Opcode(GetOnNt).Varint(key).I32(rideId).U8(moveState).U8(0).Bool(sprinting).ToArray();

    /// <summary>0x9005 CharGetOffRide_RS: key | result | EGetOffRideReason.</summary>
    public static byte[] GetOffReply(int key, int result, byte reason = ByClient) =>
        PacketWriter.Opcode(GetOffRs).Varint(key).U16(result).U8(reason).ToArray();

    /// <summary>0x9006 CharGetOffRide_NT: key | reason | by swap.</summary>
    public static byte[] GetOffNotice(int key, byte reason, bool bySwap = false) =>
        PacketWriter.Opcode(GetOffNt).Varint(key).U8(reason).Bool(bySwap).ToArray();

    /// <summary>0x9008 CharRideAcquire_RS: key | result.</summary>
    public static byte[] AcquireReply(int key, int result) => PacketWriter.Opcode(AcquireRs).Varint(key).U16(result).ToArray();

    /// <summary>0x9009 CharRideAcquire_NT: key | ride | ride level | creature board {ECreatureType u8, level i32, exp i64}.</summary>
    public static byte[] Acquired(int key, int rideId, int level, byte creatureType, int boardLevel = 1, long boardExp = 0) =>
        PacketWriter.Opcode(AcquireNt).Varint(key).I32(rideId).I32(level).U8(creatureType).I32(boardLevel).I64(boardExp).ToArray();

    /// <summary>0x900b CharRideDefaultSet_RS: key | result | ride.</summary>
    public static byte[] DefaultSetReply(int key, int result, int rideId) =>
        PacketWriter.Opcode(DefaultSetRs).Varint(key).U16(result).I32(rideId).ToArray();

    /// <summary>0x900d RideSoulPointIncrease_NT: array {ride i32, points i32} (a pet item for a ride already owned).</summary>
    public static byte[] SoulPoints(IReadOnlyList<(int RideId, int Points)> rides)
    {
        var w = PacketWriter.Opcode(SoulPointIncrease).Varint(rides.Count);
        foreach (var (ride, points) in rides) w.I32(ride).I32(points);
        return w.ToArray();
    }

    /// <summary>0x900e RideSoulPointDecrease_NT: array {ride i32, points i32} (souls spent or crystallized).</summary>
    public static byte[] SoulPointsSpent(IReadOnlyList<(int RideId, int Points)> rides)
    {
        var w = PacketWriter.Opcode(SoulPointDecrease).Varint(rides.Count);
        foreach (var (ride, points) in rides) w.I32(ride).I32(points);
        return w.ToArray();
    }

    /// <summary>0x9010 RideSoulCrystallize_RS: result | crystallized array {ride i32, points i32}.</summary>
    public static byte[] CrystallizeReply(int result, IReadOnlyList<(int RideId, int Points)> crystallized)
    {
        var w = PacketWriter.Opcode(CrystallizeRs).U16(result).Varint(crystallized.Count);
        foreach (var (ride, points) in crystallized) w.I32(ride).I32(points);
        return w.ToArray();
    }

    /// <summary>0x9011 RideSoulCrystalIncrease_NT: map {ESoulCrystalType u8: amount added i32} | ESoulCrystalIncreaseReason.</summary>
    public static byte[] CrystalsGained(IReadOnlyDictionary<int, int> gained, byte reason)
    {
        var w = PacketWriter.Opcode(CrystalIncrease).Varint(gained.Count);
        foreach (var (type, amount) in gained.OrderBy(g => g.Key)) w.U8(type).I32(amount);
        return w.U8(reason).ToArray();
    }

    /// <summary>0x9012 RideSoulCrystalDecrease_NT: map {ESoulCrystalType u8: amount spent i32}.</summary>
    public static byte[] CrystalsSpent(IReadOnlyDictionary<int, int> spent)
    {
        var w = PacketWriter.Opcode(CrystalDecrease).Varint(spent.Count);
        foreach (var (type, amount) in spent.OrderBy(g => g.Key)) w.U8(type).I32(amount);
        return w.ToArray();
    }

    /// <summary>0x8d23 CharRespawn_RS: result.</summary>
    public static byte[] RespawnReply(int result) => PacketWriter.Opcode(RespawnRs).U16(result).ToArray();

    /// <summary>0x8d25 UseRebirth_RS: result | use rebirth.</summary>
    public static byte[] UseRebirthReply(int result, bool used) => PacketWriter.Opcode(UseRebirthRs).U16(result).Bool(used).ToArray();

    /// <summary>0x8d26 UseRebirth_NT: the revived character's key (to those nearby).</summary>
    public static byte[] Reborn(int key) => PacketWriter.Opcode(UseRebirthNt).Varint(key).ToArray();

    /// <summary>0x9014 RideLevelUp_RS: result | RideInfo {ride i32, level i32} | creature board {ECreatureType u8, level i32, exp i64}.</summary>
    public static byte[] LevelUpReply(int result, int rideId, int level, byte creatureType, int boardLevel = 1, long boardExp = 0) =>
        PacketWriter.Opcode(LevelUpRs).U16(result).I32(rideId).I32(level).U8(creatureType).I32(boardLevel).I64(boardExp).ToArray();

    /// <summary>0x5617 ItemUseStarted_NT (a cast item: return scroll): key varint (OBSERVED) | item key i64 | item id i32.</summary>
    public static byte[] ItemUseStarted(int key, long itemKey, int itemId) => PacketWriter.Opcode(ItemUseStartedNt).Varint(key).I64(itemKey).I32(itemId).ToArray();

    /// <summary>0x5618 ItemUseInterrupted_NT: key | reason (OBSERVED 2 bytes 20 03 after moving: varint 416) | item key | item id.</summary>
    public static byte[] ItemUseInterrupted(int key, long itemKey, int itemId, int reason = 416) =>
        PacketWriter.Opcode(ItemUseInterruptedNt).Varint(key).Varint(reason).I64(itemKey).I32(itemId).ToArray();

    /// <summary>0x5619 ItemUseCompleted_NT: key | item key | item id.</summary>
    public static byte[] ItemUseCompleted(int key, long itemKey, int itemId) => PacketWriter.Opcode(ItemUseCompletedNt).Varint(key).I64(itemKey).I32(itemId).ToArray();

    /// <summary>
    /// 0x3805 SkillEffectByAbnormal_NT (OBSERVED, Life Potion): key | mask (1 remain, 2 amount, 8 skill) | caster |
    /// abnormal uid | effect id i32 | remain varint | [amount varint] | skill i32. The first one after the buff
    /// carries only the remaining total (mask 0x09); each tick adds the healed amount (0x0b).
    /// </summary>
    public static byte[] AbnormalEffect(int key, int casterKey, int uid, int effectId, int remain, int? amount, int skillId)
    {
        var w = PacketWriter.Opcode(0x3805).Varint(key).U8(amount is null ? 0x09 : 0x0b).Varint(casterKey).Varint(uid).I32(effectId).Varint(Math.Max(0, remain));
        if (amount is { } a) w.Varint(Math.Max(0, a));
        return w.I32(skillId).ToArray();
    }

    /// <summary>0x5613 ItemUse_RS: result | item uid i64 | item id i32.</summary>
    public static byte[] ItemUseReply(int result, long uid, int itemId) => PacketWriter.Opcode(ItemUseRs).U16(result).I64(uid).I32(itemId).ToArray();

    /// <summary>0x5616 ItemUse_NT: item uid i64 | item id i32 (OBSERVED: Life Potion 530126003).</summary>
    public static byte[] ItemUsed(long uid, int itemId) => PacketWriter.Opcode(ItemUseNt).I64(uid).I32(itemId).ToArray();

    /// <summary>
    /// 0x5647 OpenRewardBox_RS: result | box uid i64 | box item id i32 | select index u8 | open count i32 |
    /// reward items array {item id i32, enchant u8, count i32}.
    /// </summary>
    public static byte[] OpenBoxReply(int result, long boxUid, int boxItemId, byte selectIndex, int openCount, IReadOnlyList<(int ItemId, long Count)> rewards)
    {
        var w = PacketWriter.Opcode(OpenBoxRs).U16(result).I64(boxUid).I32(boxItemId).U8(selectIndex).I32(openCount).Varint(rewards.Count);
        foreach (var (item, count) in rewards) w.I32(item).U8(0).I32((int)Math.Min(int.MaxValue, count));
        return w.ToArray();
    }
}
