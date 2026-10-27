namespace Aion.Protocol.Native;

/// <summary>
/// DevPacketData_Common_Abnormal on the wire: u8 presence mask (1 caster key, 2 skill id,
/// 4 multiply caster key, 8 multiply value, 16 range base), uid varint, abnormal id i32,
/// duration i64, expire time i64, [caster varint], level u8, [skill i32], ready-to-remove
/// (packed bool), [multiply caster varint], [multiply value i32], [range base Vec3].
/// OBSERVED in 0x3645 and 0x382a (masks 0x01/0x11 seen; 2/4/8 placement follows the usmap order, CANDIDATE).
/// </summary>
public sealed record AbnormalWire(int Uid, int AbnormalId, long DurationMs, long ExpireTime, int? CasterKey = null,
    byte Level = 1, int? SkillId = null, bool ReadyToRemove = false, int? MultiplyCasterKey = null,
    int? MultiplyValue = null, Vector3d? RangeBase = null);

/// <summary>
/// Another player as the client draws it (0x3645 CharAppear_NT). Defaults are the values the
/// official server sent for ordinary low-level players (capture 20261013-144836, 89 samples).
/// Directions are degrees; HP is a varint, the other points i32.
/// </summary>
public sealed record OtherCharacter
{
    public int Key { get; init; }
    public string Name { get; init; } = "";
    public int PcId { get; init; } = 1;
    /// <summary>ERace 1 Light, 2 Dark.</summary>
    public int Race { get; init; } = 1;
    /// <summary>EGender 1 Male, 2 Female.</summary>
    public int Gender { get; init; } = 1;

    // CharState
    public Vector3d Position { get; init; }
    public double Direction { get; init; }
    /// <summary>Heading of travel sent as u16 after _dir; null sends Direction.</summary>
    public double? MoveDirection { get; init; }
    /// <summary>_move_velocity_3d (cm/s), state mask bit 3; sent for moving players.</summary>
    public Vector3d? Velocity { get; init; }
    public byte Live { get; init; } = 1;
    public long Hp { get; init; } = 100;
    public long HpMax { get; init; } = 100;
    public int Mp { get; init; } = 100;
    public int MpMax { get; init; } = 100;
    public int Ap { get; init; }
    public int ApMax { get; init; }
    public int Sp { get; init; } = 100000;
    public int SpMax { get; init; } = 100000;
    public int Dp { get; init; }
    public int DpMax { get; init; }
    public int DpStep { get; init; } = 1;
    public int Op { get; init; } = 100000;
    public int OpMax { get; init; } = 100000;
    public int Fp { get; init; }
    public int FpMax { get; init; }
    /// <summary>_combat_state (state mask bit 6; 1 = in combat observed).</summary>
    public byte CombatState { get; init; }
    /// <summary>_toggle_flags (state mask bit 7; 1 observed once).</summary>
    public int ToggleFlags { get; init; }
    /// <summary>ERelationshipEntity (state mask bit 9): 1 PC_Light_Common in every sample.</summary>
    public byte Relationship { get; init; } = 1;
    /// <summary>Equipped wing (state mask bit 12); 0 sends none.</summary>
    public int WingId { get; init; }
    /// <summary>_title_id (state mask bit 15).</summary>
    public int TitleId { get; init; }

    public int ServerId { get; init; } = 1204;
    public IReadOnlyList<AbnormalWire> Abnormals { get; init; } = [];
    /// <summary>Visual equipment; the official server sends all 15 EVisualEquipSlot records (see <see cref="PlayerPackets.VisualEquips"/>).</summary>
    public IReadOnlyList<EquipBroad> Equips { get; init; } = [];
    /// <summary>EMoveStateModelType: 0 Stop, 1 Run, 2 Walk, 6 Gravity, 8 Flying, 10 Glide, 11 WindPath...</summary>
    public byte MoveState { get; init; }
    public byte MoveSubstate { get; init; }
    public bool Sprinting { get; init; }
    public bool DeadReckoning { get; init; }
    /// <summary>EAppearReasonType: 0 EnterMap (observer's own map entry), 1 Normal (came into view), 2 Teleport, 12 InstanceLayer.</summary>
    public byte Reason { get; init; } = 1;
    public long DbId { get; init; }
    public IReadOnlyList<(int Type, int Value)> Stats { get; init; } = PlayerPackets.DefaultAppearStats;
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public int TeleportEffectId { get; init; }
    public byte[] CustomizingData { get; init; } = [];
    /// <summary>ECharacterVoiceType 0..3.</summary>
    public int VoiceType { get; init; }
    public int SealstoneItemId { get; init; }
    public long CombatPower { get; init; }
    /// <summary>EPCDieType (0 None).</summary>
    public byte DieType { get; init; }
    public bool PeaceShowWeapon { get; init; }
}

/// <summary>
/// Server messages that show and animate OTHER players, decoded from the official recording
/// research/local/capture-13700-20261013-144836-cad11c76 (combat-audit/messages.jsonl).
/// Optional fields follow one rule seen in every struct below: a leading presence mask numbers
/// the struct's optional fields in usmap order; a field is sent only when it is non-default
/// (bools only when true, then packed through <see cref="PacketWriter.Bool"/>). Which fields are
/// optional differs per struct and is taken from the samples. Directions are degrees and
/// travel as u16 (deg/360*65536) unless noted; positions are f32 cm.
/// </summary>
public static class PlayerPackets
{
    /// <summary>The CharAppear stat list of the ordinary samples (206 was -4000 on some).</summary>
    public static readonly IReadOnlyList<(int Type, int Value)> DefaultAppearStats = [(206, -3000), (208, 300), (551, -3000)];

    static int U16Dir(double degrees) => GamePackets.HeadingU16(degrees);

    /// <summary>
    /// The 15 EVisualEquipSlot records the official server always sends (slots 1..15). Slots with
    /// an item carry three empty godstone slots, empty slots none (OBSERVED).
    /// </summary>
    public static IReadOnlyList<EquipBroad> VisualEquips(IReadOnlyDictionary<int, int> itemsBySlot) =>
        Enumerable.Range(1, 15).Select(slot =>
        {
            var item = itemsBySlot.TryGetValue(slot, out var id) ? id : 0;
            return new EquipBroad(item, VisualSlot: (byte)slot, Godstones: item == 0 ? [] : [0, 0, 0]);
        }).ToList();

    static void WriteAbnormal(PacketWriter w, AbnormalWire a)
    {
        var mask = (a.CasterKey is null ? 0 : 1) | (a.SkillId is null ? 0 : 2) | (a.MultiplyCasterKey is null ? 0 : 4)
            | (a.MultiplyValue is null ? 0 : 8) | (a.RangeBase is null ? 0 : 16);
        w.U8(mask).Varint(a.Uid).I32(a.AbnormalId).I64(a.DurationMs).I64(a.ExpireTime);
        if (a.CasterKey is { } caster) w.Varint(caster);
        w.U8(a.Level);
        if (a.SkillId is { } skill) w.I32(skill);
        w.Bool(a.ReadyToRemove);
        if (a.MultiplyCasterKey is { } multiplyCaster) w.Varint(multiplyCaster);
        if (a.MultiplyValue is { } multiply) w.I32(multiply);
        if (a.RangeBase is { } range) w.Vec3(range);
    }

    internal static PacketWriter WriteAbnormals(PacketWriter w, IReadOnlyList<AbnormalWire> list)
    {
        w.Varint(list.Count);
        foreach (var a in list) WriteAbnormal(w, a);
        return w;
    }

    /// <summary>
    /// 0x3645 CharAppear_NT. OBSERVED layout (86 of 89 official samples rebuilt byte for byte
    /// by the same model; the other 3 are wind-path riders, mask bits 8/9, not supported here):
    /// key varint | u32 appear mask | u8 0x07 (CharDesc mask, constant) | name | pc_id i32 | race u8 |
    /// gender u8 | CharState: u16 mask, pos, dir f32, move dir u16, [b3 velocity Vec3], live u8,
    /// hp varint, hp_max varint, 13 i32 (mp, mp_max, ap, ap_max, sp, sp_max, dp, dp_max, dp_step,
    /// op, op_max, fp, fp_max), [b6 combat u8], [b7 toggle i32], [b9 relationship u8], [b12 wing i32],
    /// [b15 title i32] | server id u16 (appear bit 0, always) | abnormals | equips (EquipBroad) |
    /// [b2 move_state u8] [b3 move_substate u8] [b4 sprinting bool] [b12 dead_reckoning bool]
    /// [b13 reason u8] | dbid i64 | stats u8 count {u16, i32} | level i32 | conqueror i32 |
    /// equip item level i32 | [b17 teleport effect i32] | customizing (varint + bytes) |
    /// [b18 voice u8] | [b21 sealstone item i32] | combat power i64 | die type u8 | peace_show_weapon bool.
    /// Field names of bits 4/12 (sprinting vs dead reckoning) and 17 (teleport effect) are CANDIDATES
    /// from the optional-field order (bit 4 only with running, bit 12 with every moving state,
    /// bit 17 only with reasons 2/12 and matching the 0x3647 effect id); bit 21 is confirmed by
    /// 0x8d4e BroadcastSealStoneChange carrying the same item. Guild desc (bit 1) is never sent.
    /// </summary>
    public static byte[] CharAppear(OtherCharacter c)
    {
        var mask = 1u;
        if (c.MoveState != 0) mask |= 1 << 2;
        if (c.MoveSubstate != 0) mask |= 1 << 3;
        if (c.Sprinting) mask |= 1 << 4;
        if (c.DeadReckoning) mask |= 1 << 12;
        if (c.Reason != 0) mask |= 1 << 13;
        if (c.TeleportEffectId != 0) mask |= 1 << 17;
        if (c.VoiceType != 0) mask |= 1 << 18;
        if (c.SealstoneItemId != 0) mask |= 1 << 21;

        var w = PacketWriter.Opcode(0x3645).Varint(c.Key).U32(mask).U8(0x07).String(c.Name).I32(c.PcId).U8(c.Race).U8(c.Gender);

        var state = (c.Velocity is null ? 0 : 1 << 3) | (c.CombatState != 0 ? 1 << 6 : 0) | (c.ToggleFlags != 0 ? 1 << 7 : 0)
            | (c.Relationship != 0 ? 1 << 9 : 0) | (c.WingId != 0 ? 1 << 12 : 0) | (c.TitleId != 0 ? 1 << 15 : 0);
        w.U16(state).Vec3(c.Position).F32(c.Direction).U16(U16Dir(c.MoveDirection ?? c.Direction));
        if (c.Velocity is { } velocity) w.Vec3(velocity);
        w.U8(c.Live).Varint(c.Hp).Varint(c.HpMax);
        foreach (var v in new[] { c.Mp, c.MpMax, c.Ap, c.ApMax, c.Sp, c.SpMax, c.Dp, c.DpMax, c.DpStep, c.Op, c.OpMax, c.Fp, c.FpMax }) w.I32(v);
        if (c.CombatState != 0) w.U8(c.CombatState);
        if (c.ToggleFlags != 0) w.I32(c.ToggleFlags);
        if (c.Relationship != 0) w.U8(c.Relationship);
        if (c.WingId != 0) w.I32(c.WingId);
        if (c.TitleId != 0) w.I32(c.TitleId);

        w.U16(c.ServerId);
        WriteAbnormals(w, c.Abnormals);
        w.Varint(c.Equips.Count);
        foreach (var e in c.Equips) GamePackets.WriteEquip(w, e);

        if (c.MoveState != 0) w.U8(c.MoveState);
        if (c.MoveSubstate != 0) w.U8(c.MoveSubstate);
        if (c.Sprinting) w.Bool(true);
        if (c.DeadReckoning) w.Bool(true);
        if (c.Reason != 0) w.U8(c.Reason);
        w.I64(c.DbId).U8(c.Stats.Count);
        foreach (var (type, value) in c.Stats) GamePackets.Stat(w, type, value);
        w.I32(c.Level).I32(c.ConquerorLevel).I32(c.EquipItemLevel);
        if (c.TeleportEffectId != 0) w.I32(c.TeleportEffectId);
        w.Bytes(c.CustomizingData);
        if (c.VoiceType != 0) w.U8(c.VoiceType);
        if (c.SealstoneItemId != 0) w.I32(c.SealstoneItemId);
        return w.I64(c.CombatPower).U8(c.DieType).Bool(c.PeaceShowWeapon).ToArray();
    }

    /// <summary>
    /// 0x3647 CharDisappear_NT: key varint, EAppearReasonType u8, teleport effect id (one byte in
    /// every sample; varint CANDIDATE). OBSERVED reasons: 1 Normal = left view range (71 of 74),
    /// 10 Logout (1), 12 InstanceLayer with effect 65 (2; the same player later reappeared with
    /// reason 12 and effect 65).
    /// </summary>
    public static byte[] CharDisappear(int key, byte reason = 1, int teleportEffectId = 0) =>
        PacketWriter.Opcode(0x3647).Varint(key).U8(reason).Varint(teleportEffectId).ToArray();

    // ---------- Ground movement (0x371a-0x371d; same messages the NPCs use) ----------

    static byte[] MoveFull(ushort opcode, int key, Vector3d position, double moveDir, double rotateDir, bool running, bool deadReckoning)
    {
        var w = PacketWriter.Opcode(opcode).Varint(key).U8((running ? 1 : 0) | (deadReckoning ? 4 : 0));
        if (running) w.Bool(true);
        w.Vec3(position).U16(U16Dir(moveDir)).U16(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x371b CharMoveStart_NT: key | mask (1 is_running, 2 local coord, 4 dead reckoning) | [bools] | pos | move dir u16 | rotate dir u16 | [bool]. OBSERVED.</summary>
    public static byte[] MoveStart(int key, Vector3d position, double moveDir, double rotateDir, bool running = true, bool deadReckoning = true) =>
        MoveFull(0x371b, key, position, moveDir, rotateDir, running, deadReckoning);

    /// <summary>0x371c CharMoveUpdateSnapshot_NT (layout of 0x371b), about once a second while moving. OBSERVED.</summary>
    public static byte[] MoveSnapshot(int key, Vector3d position, double moveDir, double rotateDir, bool running = true, bool deadReckoning = true) =>
        MoveFull(0x371c, key, position, moveDir, rotateDir, running, deadReckoning);

    /// <summary>
    /// 0x371d CharMoveUpdateOffset_NT, every ~100 ms between snapshots: key | mask (1 running,
    /// 2/4/8 dx/dy/dz present, 16 local, 32 dead reckoning) | [bools] | [dx][dy][dz] i8 cm since the
    /// last update (omitted when 0) | move dir u16 | rotate dir u16 | [bool]. OBSERVED (4661 player samples).
    /// </summary>
    public static byte[] MoveOffset(int key, sbyte dx, sbyte dy, sbyte dz, double moveDir, double rotateDir, bool running = true, bool deadReckoning = true)
    {
        var mask = (running ? 1 : 0) | (dx != 0 ? 2 : 0) | (dy != 0 ? 4 : 0) | (dz != 0 ? 8 : 0) | (deadReckoning ? 32 : 0);
        var w = PacketWriter.Opcode(0x371d).Varint(key).U8(mask);
        if (running) w.Bool(true);
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        if (dz != 0) w.U8((byte)dz);
        w.U16(U16Dir(moveDir)).U16(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>
    /// 0x371a CharMoveStop_NT: key | u16 mask (1 move_state, 2 substate, 4 penalty_on, 8 penalty_ms) |
    /// [state] [substate] | pos | rotate dir u16 | [penalty bool] [penalty ms i64]. Mask 0 in 310 of 312
    /// player samples; 0x0c (penalty 1 ms / 50 ms) twice, right before a roll. OBSERVED; bit names CANDIDATE.
    /// </summary>
    public static byte[] MoveStop(int key, Vector3d position, double rotateDir, byte moveState = 0, byte moveSubstate = 0, long? penaltyMs = null)
    {
        var mask = (moveState != 0 ? 1 : 0) | (moveSubstate != 0 ? 2 : 0) | (penaltyMs is null ? 0 : 0x0c);
        var w = PacketWriter.Opcode(0x371a).Varint(key).U16(mask);
        if (moveState != 0) w.U8(moveState);
        if (moveSubstate != 0) w.U8(moveSubstate);
        w.Vec3(position).U16(U16Dir(rotateDir));
        if (penaltyMs is { } ms) w.Bool(true).I64(ms);
        return w.ToArray();
    }

    // ---------- Jump / fall (gravity) ----------

    /// <summary>
    /// 0x371e CharGravityStart_NT (jump or fall start): key | mask (1 gravity_mode, 2 local, 4 is_jump_up,
    /// 8 springboard, 16 dead reckoning) | [mode u8] | pos | rotate u16 | velocity Vec3 | [bools].
    /// OBSERVED masks 0x14 (jump), 0x11 (mode 2: fall), 0x04, 0x01.
    /// </summary>
    public static byte[] GravityStart(int key, Vector3d position, double rotateDir, Vector3d velocity, byte gravityMode = 0, bool jumpUp = true, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x371e).Varint(key).U8((gravityMode != 0 ? 1 : 0) | (jumpUp ? 4 : 0) | (deadReckoning ? 16 : 0));
        if (gravityMode != 0) w.U8(gravityMode);
        w.Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (jumpUp) w.Bool(true);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x371f CharGravityUpdateSnapshot_NT: key | mask (1 local, 2 dead reckoning) | pos | rotate u16 | velocity | [bool]. OBSERVED.</summary>
    public static byte[] GravitySnapshot(int key, Vector3d position, double rotateDir, Vector3d velocity, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x371f).Varint(key).U8(deadReckoning ? 2 : 0).Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>
    /// 0x3720 CharGravityUpdateOffset_NT: key | mask (1 dx, 2 dy, 4 local, 8 dead reckoning) | [dx] [dy] |
    /// dz (always) | rotate u16 | velocity | [bool]. OBSERVED (333 samples; z is never omitted).
    /// </summary>
    public static byte[] GravityOffset(int key, sbyte dx, sbyte dy, sbyte dz, double rotateDir, Vector3d velocity, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3720).Varint(key).U8((dx != 0 ? 1 : 0) | (dy != 0 ? 2 : 0) | (deadReckoning ? 8 : 0));
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        w.U8((byte)dz).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3721 CharMoveSprintOn_NT: key only (86 samples). The off notice 0x3722 is GamePackets.SprintOffNotice.</summary>
    public static byte[] SprintOn(int key) => PacketWriter.Opcode(0x3721).Varint(key).ToArray();

    // ---------- Parkour, forward-attack moves, rolling (official capture 20261013-144836) ----------
    // These directions travel as f32 in 1/65536 turns (rotU); the move direction as u16.

    static float RotU(double degrees) => (float)(((degrees % 360 + 360) % 360) * 65536.0 / 360.0);

    /// <summary>0x372b CharParkourStart_NT: key | mask (3, 7 with variation) | pos | n | points | move dir u16 | rotU | mode | [variation]. OBSERVED (18).</summary>
    public static byte[] ParkourStart(int key, Vector3d position, IReadOnlyList<Vector3d> points, int moveDir, double rotateDir, byte mode, byte? variation)
    {
        var w = PacketWriter.Opcode(0x372b).Varint(key).U8(variation is null ? 3 : 7).Vec3(position).U8(points.Count);
        foreach (var point in points) w.Vec3(point);
        w.U16(moveDir).F32(RotU(rotateDir)).U8(mode);
        if (variation is { } v) w.U8(v);
        return w.ToArray();
    }

    // ---------- Glide ----------

    /// <summary>0x3723 CharGlideStart_NT: key | mask (1 glide_mode, 2 dead reckoning) | pos | rotate u16 | velocity | [mode u8] | [bool]. OBSERVED (2, mask 0x02).</summary>
    public static byte[] GlideStart(int key, Vector3d position, double rotateDir, Vector3d velocity, byte glideMode = 0, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3723).Varint(key).U8((glideMode != 0 ? 1 : 0) | (deadReckoning ? 2 : 0))
            .Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (glideMode != 0) w.U8(glideMode);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3724 CharGlideUpdateSnapshot_NT: key | mask (1 dead reckoning) | pos | rotate u16 | velocity | [bool]. OBSERVED.</summary>
    public static byte[] GlideSnapshot(int key, Vector3d position, double rotateDir, Vector3d velocity, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3724).Varint(key).U8(deadReckoning ? 1 : 0).Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3725 CharGlideUpdateOffset_NT: key | mask (1/2/4 dx/dy/dz, 8 dead reckoning) | offsets | rotate u16 | velocity | [bool]. OBSERVED (3).</summary>
    public static byte[] GlideOffset(int key, sbyte dx, sbyte dy, sbyte dz, double rotateDir, Vector3d velocity, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3725).Varint(key).U8((dx != 0 ? 1 : 0) | (dy != 0 ? 2 : 0) | (dz != 0 ? 4 : 0) | (deadReckoning ? 8 : 0));
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        if (dz != 0) w.U8((byte)dz);
        w.U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    // ---------- Swimming (CANDIDATE: usmap field order with the glide/flight optional-field convention; no official sample:
    // nobody swam near the recorder on 2026-10-19). Live opcodes = dev struct index + 0xa3: 0x3736-0x373b. ----------

    /// <summary>0x3736 CharSwimmingStart_NT: key | mask (1 is_running, 2 swimming_mode, 4 dead reckoning) | [running bool] | pos | rotate u16 | velocity | [mode u8] | [bool].</summary>
    public static byte[] SwimStart(int key, Vector3d position, double rotateDir, Vector3d velocity, byte swimMode = 0, bool running = false, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3736).Varint(key).U8((running ? 1 : 0) | (swimMode != 0 ? 2 : 0) | (deadReckoning ? 4 : 0));
        if (running) w.Bool(true);
        w.Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (swimMode != 0) w.U8(swimMode);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3737 CharSwimmingUpdateSnapshot_NT: key | mask (1 is_running, 2 dead reckoning) | [running bool] | pos | rotate u16 | velocity | [bool].</summary>
    public static byte[] SwimSnapshot(int key, Vector3d position, double rotateDir, Vector3d velocity, bool running = false, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3737).Varint(key).U8((running ? 1 : 0) | (deadReckoning ? 2 : 0));
        if (running) w.Bool(true);
        w.Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3739 CharSwimmingDiveStart_NT: key | mask (1 swimming_mode, 2 dead reckoning) | pos | rotate u16 | velocity | [mode u8] | [bool] (the glide start's shape).</summary>
    public static byte[] DiveStart(int key, Vector3d position, double rotateDir, Vector3d velocity, byte swimMode = 0, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3739).Varint(key).U8((swimMode != 0 ? 1 : 0) | (deadReckoning ? 2 : 0))
            .Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (swimMode != 0) w.U8(swimMode);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x373a CharSwimmingDiveUpdateSnapshot_NT: key | mask (1 dead reckoning) | pos | rotate u16 | velocity | [bool] (the glide snapshot's shape).</summary>
    public static byte[] DiveSnapshot(int key, Vector3d position, double rotateDir, Vector3d velocity, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x373a).Varint(key).U8(deadReckoning ? 1 : 0).Vec3(position).U16(U16Dir(rotateDir)).Vec3(velocity);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    // ---------- Wind streams ----------

    /// <summary>
    /// 0x3733 CharTrailWindPathStart_NT: key | mask (2 dead reckoning) | pos | velocity | rotate u16 | path group i64 | node u16 |
    /// node offset varint | [bool]. OBSERVED (capture 20261019-231752 #69168, mask 2, node 1, offset 0; the group id is the one
    /// the rider's client sends in 0x3712).
    /// </summary>
    public static byte[] WindPathStart(int key, Vector3d position, Vector3d velocity, ushort rotate, long pathGroup, ushort node, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3733).Varint(key).U8(deadReckoning ? 2 : 0).Vec3(position).Vec3(velocity).U16(rotate)
            .I64(pathGroup).U16(node).Varint(0);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    public static byte[] WindPathStart(int key, Vector3d position, Vector3d velocity, double rotateDir, long pathGroup, ushort node) =>
        WindPathStart(key, position, velocity, (ushort)U16Dir(rotateDir), pathGroup, node);

    /// <summary>
    /// 0x3734 CharTrailWindPathUpdateSnapshot_NT: key | pos | velocity | rotate u16 | node u16 | node offset varint | dead reckoning
    /// bool. OBSERVED (208 in the 2026-10-13/19 captures, all 34 bytes: the node rising, offset 0, dead reckoning on).
    /// </summary>
    public static byte[] WindPathSnapshot(int key, Vector3d position, Vector3d velocity, double rotateDir, ushort node) =>
        PacketWriter.Opcode(0x3734).Varint(key).Vec3(position).Vec3(velocity).U16(U16Dir(rotateDir)).U16(node).Varint(0).Bool(true).ToArray();

    // ---------- Flight ----------

    /// <summary>0x3728 CharFlyStart_NT: key | mask (1 is_running, 2 fly_mode, 4 dead reckoning) | [bools] | pos | velocity | rotate u16 | [mode u8]. OBSERVED (masks 5, 7 with mode 3).</summary>
    public static byte[] FlyStart(int key, Vector3d position, Vector3d velocity, double rotateDir, bool running = true, byte flyMode = 0, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3728).Varint(key).U8((running ? 1 : 0) | (flyMode != 0 ? 2 : 0) | (deadReckoning ? 4 : 0));
        if (running) w.Bool(true);
        w.Vec3(position).Vec3(velocity).U16(U16Dir(rotateDir));
        if (flyMode != 0) w.U8(flyMode);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x3729 CharFlyUpdateSnapshot_NT: key | mask (1 is_running, 2 dead reckoning) | [bools] | pos | velocity | rotate u16. OBSERVED (122).</summary>
    public static byte[] FlySnapshot(int key, Vector3d position, Vector3d velocity, double rotateDir, bool running = true, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3729).Varint(key).U8((running ? 1 : 0) | (deadReckoning ? 2 : 0));
        if (running) w.Bool(true);
        w.Vec3(position).Vec3(velocity).U16(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x372a CharFlyUpdateOffset_NT: key | mask (1 is_running, 2/4/8 dx/dy/dz, 16 dead reckoning) | [bools] | offsets | velocity | rotate u16. OBSERVED (135).</summary>
    public static byte[] FlyOffset(int key, sbyte dx, sbyte dy, sbyte dz, Vector3d velocity, double rotateDir, bool running = true, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x372a).Varint(key)
            .U8((running ? 1 : 0) | (dx != 0 ? 2 : 0) | (dy != 0 ? 4 : 0) | (dz != 0 ? 8 : 0) | (deadReckoning ? 16 : 0));
        if (running) w.Bool(true);
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        if (dz != 0) w.U8((byte)dz);
        w.Vec3(velocity).U16(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    // ---------- Rolling, parkour, skill lunges ----------

    /// <summary>0x3730 CharRollingStart_NT: key | pos | move dir u16 | rotate dir u16 | dead reckoning bool (no mask). OBSERVED (1).</summary>
    public static byte[] RollingStart(int key, Vector3d position, double moveDir, double rotateDir, bool deadReckoning = true) =>
        PacketWriter.Opcode(0x3730).Varint(key).Vec3(position).U16(U16Dir(moveDir)).U16(U16Dir(rotateDir)).Bool(deadReckoning).ToArray();

    /// <summary>0x3732 CharRollingUpdateOffset_NT: key | mask (1/2/4 dx/dy/dz, 8 dead reckoning) | offsets | move dir u16 | rotate dir u16 | [bool]. OBSERVED (1, mask 0x08).</summary>
    public static byte[] RollingOffset(int key, sbyte dx, sbyte dy, sbyte dz, double moveDir, double rotateDir, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x3732).Varint(key).U8((dx != 0 ? 1 : 0) | (dy != 0 ? 2 : 0) | (dz != 0 ? 4 : 0) | (deadReckoning ? 8 : 0));
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        if (dz != 0) w.U8((byte)dz);
        w.U16(U16Dir(moveDir)).U16(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>
    /// 0x372d CharParkourUpdateOffset_NT: key | mask (1/2/4 dx/dy/dz, 8 dead reckoning) | offsets | move dir u16 |
    /// rotate f32 in u16 heading units (deg/360*65536; the usmap field is a float). OBSERVED (111, masks 0..7);
    /// the rotate scale is a CANDIDATE (it tracks the u16 move dir).
    /// </summary>
    public static byte[] ParkourOffset(int key, sbyte dx, sbyte dy, sbyte dz, double moveDir, double rotateDir, bool deadReckoning = false)
    {
        var w = PacketWriter.Opcode(0x372d).Varint(key).U8((dx != 0 ? 1 : 0) | (dy != 0 ? 2 : 0) | (dz != 0 ? 4 : 0) | (deadReckoning ? 8 : 0));
        if (dx != 0) w.U8((byte)dx);
        if (dy != 0) w.U8((byte)dy);
        if (dz != 0) w.U8((byte)dz);
        w.U16(U16Dir(moveDir)).F32(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>
    /// 0x372e CharForwardAttackMoveStart_NT (a skill lunge, e.g. the dodge the client requests with 0x370e):
    /// key | mask | skill i32 | move state u8 | pos | direction Vec3 (unit vector) | rotate f32 in u16 heading units |
    /// target key i32 | target offset Vec3 | [dead reckoning bool]. OBSERVED forms: mask 0x73 (58) and 0x33 without the
    /// bool (1); the meaning of mask bits 1, 4, 5 is unresolved, so this always sends the observed 0x33 base. CANDIDATE.
    /// </summary>
    public static byte[] ForwardAttackMoveStart(int key, int skillId, byte moveState, Vector3d position, Vector3d direction, double rotateDir,
        int targetKey, Vector3d targetOffset, bool deadReckoning = true)
    {
        var w = PacketWriter.Opcode(0x372e).Varint(key).U8(0x33 | (deadReckoning ? 0x40 : 0)).I32(skillId).U8(moveState)
            .Vec3(position).Vec3(direction).F32(U16Dir(rotateDir)).I32(targetKey).Vec3(targetOffset);
        if (deadReckoning) w.Bool(true);
        return w.ToArray();
    }

    /// <summary>0x372f CharForwardAttackMoveUpdateSnapshot_NT: key | mask (1 local, 2 dead reckoning, 4 move_end) | pos | direction Vec3 | rotate f32 (u16 units) | [bools]. OBSERVED masks 2 (113) and 0 (4); move_end CANDIDATE.</summary>
    public static byte[] ForwardAttackMoveSnapshot(int key, Vector3d position, Vector3d direction, double rotateDir, bool deadReckoning = true, bool moveEnd = false)
    {
        var w = PacketWriter.Opcode(0x372f).Varint(key).U8((deadReckoning ? 2 : 0) | (moveEnd ? 4 : 0))
            .Vec3(position).Vec3(direction).F32(U16Dir(rotateDir));
        if (deadReckoning) w.Bool(true);
        if (moveEnd) w.Bool(true);
        return w.ToArray();
    }

    // ---------- Combat and state broadcasts ----------

    /// <summary>
    /// 0x3802 SkillStart_NT for any caster: actor | 00 | skill i32 | action uid u8 | target-info mask
    /// (2 = location present) | target varint | yaw f32 | [location Vec3] | attack speed varint | 01 |
    /// cooldown varint. OBSERVED both forms (2 location, 0 without). GamePackets.SkillStart is the mask-2 form.
    /// </summary>
    public static byte[] SkillStart(int actorKey, int skillId, byte actionUid, int targetKey, double yaw, Vector3d? location,
        int attackSpeed = 10000, int cooldown = 0)
    {
        var w = PacketWriter.Opcode(0x3802).Varint(actorKey).U8(0).I32(skillId).U8(actionUid).U8(location is null ? 0 : 2)
            .Varint(targetKey).F32(yaw);
        if (location is { } at) w.Vec3(at);
        return w.Varint(attackSpeed).U8(1).Varint(cooldown).ToArray();
    }

    /// <summary>
    /// 0x3804 SkillEffect_NT of a player's damaging hit (switch 04, 20 samples with a player caster):
    /// target | 04 00 | caster | skill i32 | action uid | hit type varint (2) | effect id u32 | effect seq u32 |
    /// attack speed varint | damage varint | effect seq u8 | 00. OBSERVED; GamePackets.SkillHit is this form with uid 0.
    /// </summary>
    public static byte[] SkillEffect(int targetKey, int casterKey, int skillId, byte actionUid, int effectId, int effectSeq, int damage,
        int attackSpeed = 10000, int hitType = 2)
    {
        if (effectSeq is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(effectSeq));
        return PacketWriter.Opcode(0x3804).Varint(targetKey).U8(4).U8(0).Varint(casterKey).I32(skillId).U8(actionUid)
            .Varint(hitType).U32((uint)effectId).U32((uint)effectSeq).Varint(attackSpeed).Varint(damage).U8(effectSeq).U8(0).ToArray();
    }

    /// <summary>0x382a AbnormalAdd_NT: key | abnormals (Common_Abnormal). OBSERVED (85 for players).</summary>
    public static byte[] AbnormalAdd(int key, IReadOnlyList<AbnormalWire> abnormals) =>
        WriteAbnormals(PacketWriter.Opcode(0x382a).Varint(key), abnormals).ToArray();

    /// <summary>0x382c AbnormalRemoveList_NT: key | count | {u8 mask 0, uid varint, EAbnormalExpireReason u8}. OBSERVED (dispel fields never present).</summary>
    public static byte[] AbnormalRemove(int key, IReadOnlyList<(int Uid, byte Reason)> removed)
    {
        var w = PacketWriter.Opcode(0x382c).Varint(key).Varint(removed.Count);
        foreach (var (uid, reason) in removed) w.U8(0).Varint(uid).U8(reason);
        return w.ToArray();
    }

    /// <summary>
    /// CharMoveStart/Update/StopReject_RS (0x373e/0x373f/0x3740): key | move state (0 stop) | substate |
    /// position | reason i32. Usmap layout; key as a varint like the movement NTs; reason 1 CANDIDATE.
    /// </summary>
    public static byte[] MoveReject(ushort opcode, int key, Vector3d position, int reason = 1) =>
        PacketWriter.Opcode(opcode).Varint(key).U8(0).U8(0).Vec3(position).I32(reason).ToArray();

    /// <summary>0x364b MoveSpeedUpdate_NT: key | u8 count | {u16 stat, i32 value} (stat 205 observed: 150, 1000, 1150, 0). OBSERVED.</summary>
    public static byte[] MoveSpeedUpdate(int key, IReadOnlyList<(int Type, int Value)> stats)
    {
        var w = PacketWriter.Opcode(0x364b).Varint(key).U8(stats.Count);
        foreach (var (type, value) in stats) GamePackets.Stat(w, type, value);
        return w.ToArray();
    }

    /// <summary>0x8d91 BroadcastLevelUpdate_NT: key | level i32. OBSERVED.</summary>
    public static byte[] LevelBroadcast(int key, int level) => PacketWriter.Opcode(0x8d91).Varint(key).I32(level).ToArray();
    /// <summary>0x8d4e BroadcastSealStoneChange_NT: key | item id i32 (the CharAppear bit-21 value). OBSERVED.</summary>
    public static byte[] SealstoneChange(int key, int itemId) => PacketWriter.Opcode(0x8d4e).Varint(key).I32(itemId).ToArray();
    /// <summary>0x8dac BroadcastClientDisplayChange_NT: key | peace_show_weapon (packed bool). OBSERVED (1).</summary>
    public static byte[] ClientDisplayChange(int key, bool peaceShowWeapon) => PacketWriter.Opcode(0x8dac).Varint(key).Bool(peaceShowWeapon).ToArray();
    /// <summary>0x3832 LockOnTarget_NT: key | 00 | target varint (0 clears). OBSERVED (7).</summary>
    public static byte[] LockOnTarget(int key, int targetKey) => PacketWriter.Opcode(0x3832).Varint(key).U8(0).Varint(targetKey).ToArray();

    /// <summary>
    /// 0x561c ItemEquipChange_NT (another player's visible equipment changed): key | equip item level i32 |
    /// count | {visual slot u8, EquipBroad before, EquipBroad after}. OBSERVED (28; bools packed across records).
    /// </summary>
    public static byte[] ItemEquipChange(int key, int equipItemLevel, IReadOnlyList<(byte Slot, EquipBroad Before, EquipBroad After)> changes)
    {
        var w = PacketWriter.Opcode(0x561c).Varint(key).I32(equipItemLevel).Varint(changes.Count);
        foreach (var (slot, before, after) in changes)
        {
            w.U8(slot);
            GamePackets.WriteEquip(w, before);
            GamePackets.WriteEquip(w, after);
        }
        return w.ToArray();
    }
}
