using Aion.Protocol.Native;

namespace Aion.World;

// Dev SkillCharging* + 0x157 (usmap DevPacketData_GameServer_SkillCharging*). Charging start/end is not the 0x3800 mask wire.
partial class GameSession
{
    public const ushort SkillChargingStartNt = 0x381c;
    public const ushort SkillChargingEndRq = 0x381d, SkillChargingEndRs = 0x381e;
    public const ushort SkillChargingStopRq = 0x381f, SkillChargingStopRs = 0x3820;

    /// <summary>Hold-charge pose abnormal on the caster (OBSERVED 0x382a id 0x05F5E291, duration/expire -1 until release).</summary>
    internal const int SkillChargingAbnormalId = 0x05F5E291;

    CastRequest? pendingSkillCharge;
    CastRequest? lastSkillChargingStart;
    bool releasingSkillCharge;
    long skillChargeStartedAtMs;
    int skillChargeAbnormalUid;

    /// <summary>Held charge with no 0x381d release: clear state so movement is not blocked indefinitely.</summary>
    internal const int SkillChargeTimeoutMs = 30_000;

    internal bool HasPendingSkillChargeForTests => pendingSkillCharge is not null;

    /// <summary>0x381a body (dev SkillChargingStart_RQ): skill i32 | target type u8 + key varint | parts i32 | Vec3 | auto u8 — no yaw (use heading at accept).</summary>
    public static CastRequest? ParseSkillChargingStart(byte[] body)
    {
        if (Opcode(body) != SkillChargingStartRq || body.Length < 20) return null;
        var reader = new PacketReader(body) { Offset = 2 };
        try
        {
            var skillId = reader.I32();
            if (reader.U8() != 2) return null;
            var targetKey = reader.Varint();
            _ = reader.I32(); // parts_id
            var x = reader.F32();
            var y = reader.F32();
            var z = reader.F32();
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || targetKey < 1) return null;
            _ = reader.U8(); // auto_use
            if (!reader.End) return null;
            return new CastRequest(skillId, 0, 0, false, (int)targetKey, 0, new Vector3d(x, y, z));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    static int SkillIdFromChargingWire(byte[] plain) =>
        plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;

    CastRequest? ParseChargingCastWire(byte[] plain)
    {
        if (ParseCast(plain) is { } fromCast) return fromCast;
        if (ParseSkillChargingStart(plain) is not { } fromCharge) return null;
        return fromCharge with { Yaw = worldPlayer?.Heading ?? 0 };
    }

    /// <summary>Same accept rules as <see cref="CastReplies"/> without spending resources, cooldowns, or combat packets.</summary>
    bool TryValidateCast(CastRequest request, long now, out SkillDefinition? definition)
    {
        definition = null;
        var skill = request.SkillId;
        request = WithCastLocation(request);
        var combatDodge = ClassCombatDodge;
        var dodging = IsDodgeId(skill);
        if (dodging) combatDodge = IsCombatDodgeId(skill) ? skill : skill - 1;
        var variant = DashVariant;
        if (dodging && !sprinting && variant >= 6) variant = 0;
        if (dodging && sprinting && variant is 6 or 10) return false;
        var started = skill == combatDodge && variant != 9 && (sprinting || !inCombat) ? combatDodge + 1 : skill;
        var normalDodge = dodging && started == combatDodge + 1 && learned.Contains(combatDodge);
        definition = normalDodge ? data.Skill(combatDodge) is { } dodge ? dodge with { Cost = dodge.Cost with { Sp = NormalDodgeSp } } : null
            : ResolveSkillDefinition(skill);
        var cooldownKey = data.CooldownGroup(normalDodge ? combatDodge : (data.Skill(skill) is not null ? skill : SkillFamilyId(skill)));
        var cost = definition?.Cost ?? new SkillCost(0, 0, 0);
        if (BuffStat("MPUseDecrease") is > 0 and var cheaper) cost = cost with { Mp = cost.Mp * Math.Max(0, 10_000 - cheaper) / 10_000 };
        var weapons = (definition?.Weapons ?? []).Where(w => w != "None").ToList();
        var chainSkill = definition is { ChainPrev: > 0 };
        var chainAllowed = !chainSkill || (ChainReady(skill, now) && ChainRootLearned(skill));
        var cooldownReady = SkillCooldown.CooldownReady(now, cooldowns, cooldownKey, noCool);
        var meetsCastRules = definition is not null && resources is not null && !dead && chainAllowed && !CombatCastBlocked(skill)
            && resources.Mp >= cost.Mp && resources.Sp >= cost.Sp && resources.Hp > cost.Hp
            && (resources.ApMax <= 0 || resources.Ap >= cost.Ap)
            && (SkillFamilyLearned(skill) || normalDodge || chainSkill || CarriedSkill == skill)
            && (weapons.Count == 0 || weapons.Contains(data.WeaponType(equippedWeapon) ?? ""))
            && UseConditionMet(definition, now, request.TargetKey, skill);
        var basicAuto = definition is not null && SkillCooldown.IsBasicAutoAttack(definition);
        var toggleOff = request.HasChangeToggle && request.ChangeToggle is 0 or 2;
        var toggleOn = request.HasChangeToggle && request.ChangeToggle == 1;
        if (basicAuto && toggleOff) return true;
        var castGapOk = noCool || dodging || now - lastAcceptedCastMs >= SkillCooldown.MinCastGapMs;
        if (meetsCastRules && cooldownReady && castGapOk) return true;
        if (basicAuto && toggleOn && meetsCastRules && !cooldownReady) return true;
        return false;
    }

    static bool UsesHoldChargeFlow(SkillDefinition definition) =>
        !SkillCooldown.IsBasicAutoAttack(definition) && definition.CastingMs <= 0;

    List<byte[]> BeginSkillCharging(CastRequest request, SkillDefinition definition, long now)
    {
        pendingSkillCharge = request;
        lastSkillChargingStart = request;
        skillChargeStartedAtMs = now;
        selectedTarget = request.TargetKey;
        skillChargeAbnormalUid = ++buffUid;
        var level = (byte)Math.Clamp(SkillLevel(request.SkillId), 1, 255);
        var yaw = (float)request.Yaw;
        var chargeRate = BitConverter.ToSingle(Convert.FromHexString("40359E3F"));
        return
        [
            GamePackets.SkillChargingStartNotify(playerKey, request.SkillId, level, request.TargetKey, yaw, request.Location, chargeRate),
            GamePackets.SkillChargingStartReply(0, request.SkillId),
            PlayerPackets.AbnormalAdd(playerKey,
            [
                new AbnormalWire(skillChargeAbnormalUid, SkillChargingAbnormalId, -1, -1, CasterKey: playerKey, Level: level,
                    SkillId: request.SkillId, RangeBase: request.Location),
            ]),
        ];
    }

    List<byte[]> ClearSkillChargeAbnormal()
    {
        if (skillChargeAbnormalUid == 0) return [];
        var uid = skillChargeAbnormalUid;
        skillChargeAbnormalUid = 0;
        return [PlayerPackets.AbnormalRemove(playerKey, [(uid, 1)])];
    }

    static List<byte[]> OrderSkillChargeReleaseCast(List<byte[]> cast, byte[] endReply, IReadOnlyList<byte[]> afterHits)
    {
        var ordered = new List<byte[]>();
        if (cast.FirstOrDefault(p => Opcode(p) == 0x3802) is { } start) ordered.Add(start);
        ordered.Add(endReply);
        ordered.AddRange(cast.Where(p => Opcode(p) == 0x3803));
        ordered.AddRange(afterHits);
        ordered.AddRange(cast.Where(p => Opcode(p) is not (0x3801 or 0x3802 or 0x3803)));
        return ordered;
    }

    List<byte[]> SkillChargeTimeoutTick(long now)
    {
        if (pendingSkillCharge is null || skillChargeStartedAtMs <= 0 || now - skillChargeStartedAtMs < SkillChargeTimeoutMs) return [];
        pendingSkillCharge = null;
        lastSkillChargingStart = null;
        skillChargeStartedAtMs = 0;
        var replies = new List<byte[]> { GamePackets.SkillChargingStopReply(0) };
        replies.AddRange(ClearSkillChargeAbnormal());
        return replies;
    }

    async Task<List<byte[]>> HandleSkillChargingStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (ParseChargingCastWire(plain) is not { } request)
            return [GamePackets.SkillChargingStartReply(1, SkillIdFromChargingWire(plain))];
        NoteHeldCastWireReceived(now);
        var definition = ResolveSkillDefinition(request.SkillId);
        if (definition is not null && SkillCooldown.IsBasicAutoAttack(definition))
        {
            var cast = CastReplies(plain, now);
            cast.AddRange(await SealStoneConsumedAsync(ct));
            cast.AddRange(await QuestKillsAsync(ct));
            cast.AddRange(await KillExperienceAsync(ct));
            return SkillChargingStartAck(cast, request);
        }
        if (definition is null || !UsesHoldChargeFlow(definition) || !TryValidateCast(request, now, out _))
            return [GamePackets.SkillChargingStartReply(1, request.SkillId)];
        return BeginSkillCharging(request, definition, now);
    }

    static (int Step, int SelectedSkill, bool PassLos, byte CasterDirection)? ParseSkillChargingEnd(byte[] body)
    {
        if (Opcode(body) != SkillChargingEndRq || body.Length < 12) return null;
        var reader = new PacketReader(body) { Offset = 2 };
        try
        {
            var step = reader.I32();
            var selected = reader.I32();
            var passLos = reader.U8() != 0;
            var direction = reader.U8();
            if (!reader.End) return null;
            return (step, selected, passLos, direction);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    async Task<List<byte[]>> HandleSkillChargingEndAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (ParseSkillChargingEnd(plain) is not { } end)
            return [GamePackets.SkillChargingEndReply(1, 0, 0, 0)];
        var charge = pendingSkillCharge ?? lastSkillChargingStart;
        if (charge is null)
            return [GamePackets.SkillChargingEndReply(1, end.SelectedSkill, end.Step, end.SelectedSkill)];
        var skill = end.SelectedSkill > 0 ? end.SelectedSkill : charge.SkillId;
        if (skill != charge.SkillId)
            return [GamePackets.SkillChargingEndReply(1, skill, end.Step, end.SelectedSkill)];
        pendingSkillCharge = null;
        lastSkillChargingStart = null;
        skillChargeStartedAtMs = 0;
        var wire = BuildSkillStartWire(skill, charge.TargetKey, charge.Location, charge.Yaw);
        releasingSkillCharge = true;
        List<byte[]> cast;
        try { cast = CastReplies(wire, now); }
        finally { releasingSkillCharge = false; }
        cast = cast.Where(p => Opcode(p) != 0x3801).ToList();
        if (cast.Count == 0)
            cast = [CastReplyFor(charge, false)];
        var accepted = cast.Any(p => Opcode(p) == 0x3802);
        var endReply = GamePackets.SkillChargingEndReply(accepted ? 0 : 1, skill, end.Step, end.SelectedSkill > 0 ? end.SelectedSkill : skill);
        cast = OrderSkillChargeReleaseCast(cast, endReply, ClearSkillChargeAbnormal());
        cast.AddRange(await SealStoneConsumedAsync(ct));
        cast.AddRange(await QuestKillsAsync(ct));
        cast.AddRange(await KillExperienceAsync(ct));
        return cast;
    }

    List<byte[]> HandleSkillChargingStop(byte[] plain)
    {
        pendingSkillCharge = null;
        lastSkillChargingStart = null;
        skillChargeStartedAtMs = 0;
        var replies = new List<byte[]> { GamePackets.SkillChargingStopReply(0) };
        replies.AddRange(ClearSkillChargeAbnormal());
        return replies;
    }
}
