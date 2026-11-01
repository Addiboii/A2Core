using Aion.Protocol.Native;

namespace Aion.World;

// Dev SkillCharging* + 0x157 (usmap DevPacketData_GameServer_SkillCharging*). Charging start/end is not the 0x3800 mask wire.
partial class GameSession
{
    public const ushort SkillChargingStartNt = 0x381c;
    public const ushort SkillChargingEndRq = 0x381d, SkillChargingEndRs = 0x381e;
    public const ushort SkillChargingStopRq = 0x381f, SkillChargingStopRs = 0x3820;

    /// <summary>Hold-charge pose abnormal on the caster (OBSERVED 0x382a id 0x05F5E291).</summary>
    internal const int SkillChargingAbnormalId = 0x05F5E291;

    internal const int SkillChargingAbnormalDurationMs = 5_000;

    CastRequest? pendingSkillCharge;
    CastRequest? lastSkillChargingStart;
    bool releasingSkillCharge;
    long skillChargeStartedAtMs;
    long lastChargeRepeatAtMs;
    long skillChargeNextTickAtMs;
    readonly List<int> skillChargeAbnormalUids = [];

    /// <summary>Charging abnormal duration on the caster (OBSERVED ~5000ms).</summary>
    internal const int SkillChargeTimeoutMs = 5_000;

    /// <summary>Max hold before auto-release while the client keeps sending 0x381a (live: 3s).</summary>
    internal const int SkillChargeMaxHoldMs = 3_000;

    /// <summary>Silence after the last 0x381a before treating the button as released.</summary>
    internal const int SkillChargeReleaseSilenceMs = 400;

    internal const int SkillChargePendingTickMs = 50;

    internal bool HasPendingSkillChargeForTests => pendingSkillCharge is not null;
    internal IReadOnlyList<int> SkillChargeAbnormalUidsForTests => skillChargeAbnormalUids;

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
        if (dodging && sprinting && variant == 6) return false;
        var sprintDash = dodging && sprinting;
        if (sprintDash && variant == 10) variant = 0;
        var sprintOpeningDash = sprintDash && now - sprintOnAt <= SprintAutoDodgeWindowMs;
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
            && (sprintDash || UseConditionMet(definition, now, request.TargetKey, skill));
        var basicAuto = definition is not null && SkillCooldown.IsBasicAutoAttack(definition);
        var toggleOff = request.HasChangeToggle && request.ChangeToggle is 0 or 2;
        var toggleOn = request.HasChangeToggle && request.ChangeToggle == 1;
        if (basicAuto && toggleOff) return true;
        var castGapOk = noCool || now - lastAcceptedCastMs >= SkillCooldown.MinCastGapMs || sprintOpeningDash;
        var cooldownOk = cooldownReady || sprintOpeningDash;
        if (meetsCastRules && cooldownOk && castGapOk) return true;
        if (basicAuto && toggleOn && meetsCastRules && !cooldownReady) return true;
        return false;
    }

    /// <summary>Hold-to-charge skills (Deadshot): several damage rows on one timed shot, instant cast in data.</summary>
    static bool HasHoldChargeStages(SkillDefinition definition)
    {
        if (SkillCooldown.IsBasicAutoAttack(definition) || definition.CastingMs > 0 || definition.Damage.Length < 2)
            return false;
        var timed = SkillTiming.TimedRows(definition);
        return timed[^1] == timed[0];
    }

    bool UsesHoldChargeFlow(SkillDefinition definition, int skillId) =>
        HasHoldChargeStages(definition) && CastArea(skillId) is { Projectile: > 0 };

    static bool SameHoldChargeRequest(CastRequest a, CastRequest b) => a.SkillId == b.SkillId;

    static int ChargeStepForElapsed(SkillDefinition definition, long chargeBuildMs)
    {
        var stages = definition.Damage.Length;
        if (stages <= 1) return 1;
        var capped = Math.Min(chargeBuildMs, SkillChargeMaxHoldMs);
        var step = (int)(capped * stages / SkillChargeMaxHoldMs) + 1;
        return Math.Clamp(step, 1, stages);
    }

    long HoldChargeBuildMs(long now) =>
        Math.Min(SkillChargeMaxHoldMs, Math.Max(0, lastChargeRepeatAtMs - skillChargeStartedAtMs));

    internal bool NeedsSkillChargeTick(long now) =>
        pendingSkillCharge is not null && now >= skillChargeNextTickAtMs;

    List<byte[]> ClearSkillChargeAbnormal()
    {
        if (skillChargeAbnormalUids.Count == 0) return [];
        var uids = skillChargeAbnormalUids.ToList();
        skillChargeAbnormalUids.Clear();
        return [PlayerPackets.AbnormalRemove(playerKey, uids.Select(uid => (uid, (byte)1)).ToList())];
    }

    /// <summary>Leftover hold-charge abnormals with no pending charge (client/server desync).</summary>
    List<byte[]> ClearOrphanSkillChargeAbnormals() =>
        pendingSkillCharge is null ? ClearSkillChargeAbnormal() : [];

    /// <summary>Ends a hold charge: optional 0x3820, always removes every tracked charging abnormal uid in one 0x382c.</summary>
    List<byte[]> CancelPendingSkillCharge(bool sendStopReply)
    {
        var hadPending = pendingSkillCharge is not null;
        var hadAbnormals = skillChargeAbnormalUids.Count > 0;
        pendingSkillCharge = null;
        lastSkillChargingStart = null;
        skillChargeStartedAtMs = 0;
        lastChargeRepeatAtMs = 0;
        skillChargeNextTickAtMs = 0;
        var replies = new List<byte[]>();
        if (sendStopReply && (hadPending || hadAbnormals)) replies.Add(GamePackets.SkillChargingStopReply(0));
        replies.AddRange(ClearSkillChargeAbnormal());
        return replies;
    }

    int HoldChargeChargingActId(int skillId) => CastArea(skillId)?.ProjectileFor(0) ?? 0;

    List<byte[]> BeginSkillCharging(CastRequest request, SkillDefinition definition, long now)
    {
        pendingSkillCharge = request;
        lastSkillChargingStart = request;
        skillChargeStartedAtMs = now;
        lastChargeRepeatAtMs = now;
        skillChargeNextTickAtMs = now;
        selectedTarget = request.TargetKey;
        var uid = ++buffUid;
        skillChargeAbnormalUids.Add(uid);
        var level = (byte)Math.Clamp(SkillLevel(request.SkillId), 1, 255);
        var yaw = (float)request.Yaw;
        var chargeRate = BitConverter.ToSingle(Convert.FromHexString("40359E3F"));
        var chargingActId = HoldChargeChargingActId(request.SkillId);
        var expireAt = now + SkillChargingAbnormalDurationMs;
        return HoldChargeStartPackets(request, level, yaw, chargeRate, chargingActId, uid, expireAt);
    }

    List<byte[]> HoldChargeStartPackets(CastRequest request, byte level, float yaw, float chargeRate,
        int chargingActId, int abnormalUid, long expireAt) =>
    [
        GamePackets.SkillChargingStartReply(0, request.SkillId),
        GamePackets.SkillChargingStartNotify(playerKey, request.SkillId, level, request.TargetKey, yaw, request.Location, chargeRate,
            chargingActId: chargingActId),
        PlayerPackets.AbnormalAdd(playerKey,
        [
            new AbnormalWire(abnormalUid, SkillChargingAbnormalId, SkillChargingAbnormalDurationMs, expireAt, CasterKey: playerKey, Level: level,
                SkillId: request.SkillId, RangeBase: request.Location),
        ]),
    ];

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

    async Task<List<byte[]>> SkillChargeTimeoutTickAsync(long now, CancellationToken ct)
    {
        if (pendingSkillCharge is null || skillChargeStartedAtMs <= 0 || lastChargeRepeatAtMs <= 0)
            return [];
        var releaseSilence = now - lastChargeRepeatAtMs >= SkillChargeReleaseSilenceMs;
        var releaseMax = now - skillChargeStartedAtMs >= SkillChargeMaxHoldMs;
        if (!releaseSilence && !releaseMax)
            return [];
        var definition = ResolveSkillDefinition(pendingSkillCharge.SkillId);
        var step = definition is not null ? ChargeStepForElapsed(definition, HoldChargeBuildMs(now)) : 1;
        return await ReleasePendingSkillChargeAsync(step, pendingSkillCharge.SkillId, now, ct);
    }

    async Task<List<byte[]>> HandleSkillChargingStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        var replies = ClearOrphanSkillChargeAbnormals();
        if (ParseChargingCastWire(plain) is not { } request)
        {
            replies.Add(GamePackets.SkillChargingStartReply(1, SkillIdFromChargingWire(plain)));
            return replies;
        }
        NoteHeldCastWireReceived(now);
        var definition = ResolveSkillDefinition(request.SkillId);
        if (definition is not null && SkillCooldown.IsBasicAutoAttack(definition))
        {
            replies.AddRange(CancelPendingSkillCharge(sendStopReply: true));
            var cast = CastReplies(plain, now);
            cast.AddRange(await SealStoneConsumedAsync(ct));
            cast.AddRange(await QuestKillsAsync(ct));
            cast.AddRange(await KillExperienceAsync(ct));
            replies.AddRange(SkillChargingStartAck(cast, request));
            return replies;
        }
        if (definition is null || !TryValidateCast(request, now, out _))
        {
            replies.Add(GamePackets.SkillChargingStartReply(1, request.SkillId));
            return replies;
        }
        if (!UsesHoldChargeFlow(definition, request.SkillId))
        {
            replies.AddRange(CancelPendingSkillCharge(sendStopReply: true));
            var cast = CastReplies(plain, now);
            cast.AddRange(await SealStoneConsumedAsync(ct));
            cast.AddRange(await QuestKillsAsync(ct));
            cast.AddRange(await KillExperienceAsync(ct));
            replies.AddRange(SkillChargingStartAck(cast, request));
            return replies;
        }
        if (pendingSkillCharge is { } existing && SameHoldChargeRequest(existing, request))
        {
            lastChargeRepeatAtMs = now;
            pendingSkillCharge = request with { Yaw = request.Yaw };
            lastSkillChargingStart = pendingSkillCharge;
            selectedTarget = request.TargetKey;
            var level = (byte)Math.Clamp(SkillLevel(request.SkillId), 1, 255);
            var yaw = (float)request.Yaw;
            var chargeRate = BitConverter.ToSingle(Convert.FromHexString("40359E3F"));
            var chargingActId = HoldChargeChargingActId(request.SkillId);
            replies.Add(GamePackets.SkillChargingStartReply(0, request.SkillId));
            replies.Add(GamePackets.SkillChargingStartNotify(playerKey, request.SkillId, level, request.TargetKey, yaw, request.Location,
                chargeRate, chargingActId: chargingActId));
            return replies;
        }
        if (pendingSkillCharge is not null) replies.AddRange(CancelPendingSkillCharge(sendStopReply: true));
        replies.AddRange(BeginSkillCharging(request, definition, now));
        return replies;
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

    async Task<List<byte[]>> ReleasePendingSkillChargeAsync(int step, int selectedSkill, long now, CancellationToken ct)
    {
        var charge = pendingSkillCharge ?? lastSkillChargingStart;
        if (charge is null) return [];
        var skill = charge.SkillId;
        pendingSkillCharge = null;
        lastSkillChargingStart = null;
        skillChargeStartedAtMs = 0;
        lastChargeRepeatAtMs = 0;
        skillChargeNextTickAtMs = 0;
        var wire = BuildSkillStartWire(skill, charge.TargetKey, charge.Location, charge.Yaw);
        releasingSkillCharge = true;
        List<byte[]> cast;
        try { cast = CastReplies(wire, now); }
        finally { releasingSkillCharge = false; }
        cast = cast.Where(p => Opcode(p) != 0x3801).ToList();
        if (cast.Count == 0)
            cast = [CastReplyFor(charge, false)];
        var accepted = cast.Any(p => Opcode(p) == 0x3802);
        var endReply = GamePackets.SkillChargingEndReply(accepted ? 0 : 1, skill, step, selectedSkill);
        cast = OrderSkillChargeReleaseCast(cast, endReply, ClearSkillChargeAbnormal());
        cast.AddRange(await SealStoneConsumedAsync(ct));
        cast.AddRange(await QuestKillsAsync(ct));
        cast.AddRange(await KillExperienceAsync(ct));
        return cast;
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
        var selected = end.SelectedSkill > 0 ? end.SelectedSkill : skill;
        return await ReleasePendingSkillChargeAsync(end.Step, selected, now, ct);
    }

    List<byte[]> HandleSkillChargingStop(byte[] plain)
    {
        var replies = CancelPendingSkillCharge(sendStopReply: false);
        replies.Insert(0, GamePackets.SkillChargingStopReply(0));
        return replies;
    }

    /// <summary>Map load / world entry: drop any hold-charge state left from the previous map.</summary>
    List<byte[]> SkillChargeMapEntryCleanup() => CancelPendingSkillCharge(sendStopReply: false);
}
