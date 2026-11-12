using Aion.Protocol.Native;

namespace Aion.World;

// Charged skills (Ranger Dead Shot 14010000 and the other Skill.dat ChargeId skills; tools/data/class-skills.json `charges`
// from SkillCharge.dat, client build 26262663). The server side of the exchange is OBSERVED in the community recordings of
// 2026-11-06 (234912 #144954-147637, Dead Shot, five releases at steps 0-3; 192703 #51383-51508, an Elementalist):
// - SkillChargingStart_RQ 0x381a -> SkillChargingStart_NT 0x381c to the caster and those nearby {key varint, 01, skill i32,
//   skill level u8, target info (mask 02, target varint, yaw f32, location), charge rate f32, 01}, then SkillChargingStart_RS
//   0x381b {result u16, skill i32}, then combat on.
// - SkillChargingEnd_RQ 0x381d -> a normal cast of the selected skill (0x3802, the selected skill's own damage) with
//   SkillChargingEnd_RS 0x381e {01, result u16, skill i32, step i32, selected skill i32} in place of the cast's 0x3801;
//   MP is paid at the release.
// - Step n is reached after SkillChargeDataList[n-1].ChargeTime ms divided by the charge rate (the recorded holds fit: 0.59 s
//   step 1, 0.75 s step 2, 1.00 s step 3 at rate 1.5786); the selected skill is that step's ChargeSkillId, step 0 the skill.
// CANDIDATE: the client's request bodies (encrypted in every recording; read from the usmap field order with a u8 presence
// mask, the 0x3800 target-info form, and a fallback without the mask); the rate (1.0: attack speed is not modelled; official
// = attack speed / 10000); the step clamp (the client's step, at most what the held time allows plus ChargeStepToleranceMs,
// OURS); the stop messages 0x381f/0x3820/0x3821 (never recorded). Not sent yet: the charging abnormal 100000401 (stats 205/214
// while holding) the official server adds and removes with reason 7.
public sealed partial class GameSession
{
    public const ushort SkillChargingStartRq = 0x381a, SkillChargingStartRs = 0x381b, SkillChargingStartNt = 0x381c,
        SkillChargingEndRq = 0x381d, SkillChargingEndRs = 0x381e, SkillChargingStopRq = 0x381f, SkillChargingStopRs = 0x3820,
        SkillChargingStopNt = 0x3821;
    /// <summary>Latency allowance for the client's charging step (OURS, ms of held time).</summary>
    public const int ChargeStepToleranceMs = 200;
    // EResult (read from the usmap of client 2.0.6): kSkill_Failed, kSkill_NoWeapon, kSkill_NoMP, kSkill_CoolTime, kSkill_TooFar,
    // kSkill_NotCharging.
    const int SkillFailed = 0x1800, SkillNoWeapon = 0x1809, SkillNoMp = 0x180a, SkillCoolTime = 0x1811, SkillTooFar = 0x1804,
        SkillNotCharging = 0x1825;

    sealed record Charging(int Skill, ChargeRow Charge, CastRequest Request, long StartedAt, float Rate);
    Charging? charging;
    /// <summary>The selected skill of the release being cast (lets CastReplies accept a step skill that is never learned).</summary>
    int chargeRelease;

    bool ChargeReleaseReady(int skill) => chargeRelease != 0 && chargeRelease == skill;

    /// <summary>The skill being charged, 0 when none (checks).</summary>
    public int ChargingSkill => charging?.Skill ?? 0;

    public static byte[] ChargeStartNt(int key, int skill, int skillLevel, int targetKey, double yaw, Vector3d at, float rate) =>
        PacketWriter.Opcode(SkillChargingStartNt).Varint(key).U8(1).I32(skill).U8((byte)skillLevel).U8(2).Varint(targetKey).F32(yaw).Vec3(at)
            .F32(rate).U8(1).ToArray();

    public static byte[] ChargeStartRs(int result, int skill) => PacketWriter.Opcode(SkillChargingStartRs).U16(result).I32(skill).ToArray();

    public static byte[] ChargeEndRs(int result, int skill, int step, int selected) =>
        PacketWriter.Opcode(SkillChargingEndRs).U8(1).U16(result).I32(skill).I32(step).I32(selected).ToArray();

    /// <summary>0x381a {mask u8, skill i32, target info (mask, key varint, [parts varint], yaw, location), [auto use]} (CANDIDATE).</summary>
    public static CastRequest? ParseChargeStart(byte[] plain)
    {
        foreach (var maskWidth in new[] { 1, 2, 0 })
        {
            try
            {
                var r = new PacketReader(plain) { Offset = 2 + maskWidth };
                var skill = r.I32();
                var targetMask = r.U8();
                if ((targetMask & 2) == 0 || (targetMask & ~3) != 0) continue;
                var target = (int)r.Varint();
                if ((targetMask & 1) != 0) r.Varint();
                float yaw = r.F32(), x = r.F32(), y = r.F32(), z = r.F32();
                if (skill < 1 || target < 1 || !float.IsFinite(yaw) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || r.Remaining > 2)
                    continue;
                return new CastRequest(skill, 0, 0, target, yaw, new Vector3d(x, y, z));
            }
            catch (ProtocolException) { }
        }
        return null;
    }

    /// <summary>0x381d {mask u8, step i32, selected skill i32, [pass LOS], [caster direction]} (CANDIDATE), else without the mask.</summary>
    static (int Step, int Selected)? ParseChargeEnd(byte[] plain, ChargeRow charge, int skill)
    {
        bool Fits(int step, int selected) => step >= 0 && step <= charge.Steps.Length
            && (selected == skill || charge.Steps.Any(s => s.Length == 2 && s[1] == selected));
        foreach (var offset in new[] { 3, 2 })
            if (plain.Length >= offset + 8 && BitConverter.ToInt32(plain, offset) is var step && BitConverter.ToInt32(plain, offset + 4) is var selected
                && Fits(step, selected))
                return (step, selected);
        return null;
    }

    /// <summary>The step a hold of <paramref name="heldMs"/> at <paramref name="rate"/> reaches (0: the skill itself).</summary>
    public static int ChargeStepReached(ChargeRow charge, long heldMs, float rate)
    {
        var scaled = (heldMs + ChargeStepToleranceMs) * (double)rate;
        var step = 0;
        while (step < charge.Steps.Length && charge.Steps[step][0] <= scaled) step++;
        return step;
    }

    List<byte[]> ChargeStart(byte[] plain, long now)
    {
        var request = ParseChargeStart(plain);
        var skill = request?.SkillId ?? (plain.Length >= 7 ? BitConverter.ToInt32(plain, 3) : 0);
        var definition = request is null ? null : data.Skill(skill);
        if (request is null || definition is not { Charge: > 0 } || data.Charge(definition.Charge) is not { } charge
            || resources is null || dead || !(learned.Contains(skill) || StigmaReady(skill)))
            return [ChargeStartRs(SkillFailed, skill)];
        var weapons = definition.Weapons.Where(w => w != "None").ToList();
        if (weapons.Count > 0 && !weapons.Contains(data.WeaponType(equippedWeapon) ?? "")) return [ChargeStartRs(SkillNoWeapon, skill)];
        if (resources.Mp < definition.Cost.Mp) return [ChargeStartRs(SkillNoMp, skill)];
        if (!noCool && now < cooldowns.GetValueOrDefault(skill)) return [ChargeStartRs(SkillCoolTime, skill)];
        if (!InCastRange(definition, request.TargetKey)) return [ChargeStartRs(SkillTooFar, skill)];
        const float rate = 1f;
        charging = new Charging(skill, charge, request, now, rate);
        var nt = ChargeStartNt(playerKey, skill, SkillLevel(skill), request.TargetKey, request.Yaw, request.Location, rate);
        worldPlayer?.Map?.Broadcast(worldPlayer, [nt]);
        return [.. ForceDismount(), nt, ChargeStartRs(0, skill), .. EnterCombat(now)];
    }

    List<byte[]> ChargeEnd(byte[] plain, long now)
    {
        if (charging is not { } held)
            return [ChargeEndRs(SkillNotCharging, plain.Length >= 7 ? BitConverter.ToInt32(plain, 3) : 0, 0, 0)];
        charging = null;
        var asked = ParseChargeEnd(plain, held.Charge, held.Skill);
        var step = Math.Min(asked?.Step ?? 0, ChargeStepReached(held.Charge, now - held.StartedAt, held.Rate));
        var selected = step == 0 ? held.Skill : held.Charge.Steps[step - 1][1];
        if (data.Skill(selected) is null) (step, selected) = (0, held.Skill);
        chargeRelease = selected;
        List<byte[]> cast;
        try { cast = CastRequestReplies(held.Request with { SkillId = selected }, now); }
        finally { chargeRelease = 0; }
        var started = cast.FindIndex(b => Opcode(b) == 0x3802);
        if (started < 0) return [ChargeEndRs(SkillFailed, held.Skill, step, selected)];
        // The base skill's cooldown is the one the client shows and the next charge checks (CANDIDATE: the steps share it).
        cooldowns[held.Skill] = cooldowns.GetValueOrDefault(selected);
        cast.RemoveAll(b => Opcode(b) == 0x3801);
        cast.Insert(started + 1, ChargeEndRs(0, held.Skill, step, selected));
        return cast;
    }

    /// <summary>0x381f SkillChargingStop_RQ: the hold ends without a cast -> 0x3820 {result}; 0x3821 {key, result, skill, cooldown} nearby (CANDIDATE layouts).</summary>
    List<byte[]> ChargeStop()
    {
        if (charging is not { } held) return [PacketWriter.Opcode(SkillChargingStopRs).U16(SkillNotCharging).ToArray()];
        charging = null;
        worldPlayer?.Map?.Broadcast(worldPlayer, [PacketWriter.Opcode(SkillChargingStopNt).Varint(playerKey).U16(0).I32(held.Skill).I32(0).ToArray()]);
        return [PacketWriter.Opcode(SkillChargingStopRs).U16(0).ToArray()];
    }
}
