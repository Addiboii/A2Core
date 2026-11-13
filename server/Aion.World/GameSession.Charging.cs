using Aion.Protocol.Native;

namespace Aion.World;

// Charge skills (hold the key, release to fire: Ranger Deadshot 14010000, Skill.dat ChargeId = itself). Live opcodes from the
// catalogue order (index + 0x157, as ChainSkillActiveList 0x3818 and SkillAutoLoadUpdate 0x3822): ChargingStart_RQ 0x381a ->
// RS 0x381b, NT 0x381c; ChargingEnd_RQ 0x381d -> RS 0x381e; ChargingStop_RQ 0x381f -> RS 0x3820, NT 0x3821.
// OBSERVED (official 115817 #177503, Deadshot 14010240): at the start 0x381c {caster, 01, skill, level, target info as in
// SkillStart: 02, target, yaw, location; charge rate f32, act move type 01} then 0x381b {result u16 0, skill}; at the release
// the selected charge level is cast as any skill (0x3802 naming 14010243 = "Deadshot - Max", projectile, hits, 0x3806) with
// 0x381e {01, result u16 0, skill, charging step i32 3, selected skill} right after its 0x3802. The levels are the next ids
// (Lv.1, Lv.2, Max). Without these replies the client waits for them and the character stays locked (live 2026-11-07).
// Our client's start request (trace 2026-11-07 15:15): skill i32, target info {02, target varint, u8, yaw f32, location}, auto use u8.
public sealed partial class GameSession
{
    public const ushort SkillChargingStartRq = 0x381a, SkillChargingStartRs = 0x381b, SkillChargingStartNt = 0x381c,
        SkillChargingEndRq = 0x381d, SkillChargingEndRs = 0x381e, SkillChargingStopRq = 0x381f, SkillChargingStopRs = 0x3820,
        SkillChargingStopNt = 0x3821;
    /// <summary>
    /// A charge held to the end fires by itself at its top level. PROVISIONAL 1050 ms: the official Deadshot release came
    /// 1048 ms after its 0x381b, at step 3 (Max) (115817 #177503); live 2026-11-07 a held charge stayed "loading at max".
    /// </summary>
    public const int ChargeMaxMs = 1050;

    /// <summary>
    /// The client SkillCharge table (tools/skill_charges.py -> skill-charges.json): the last level's ChargeTime (Deadshot 14010240:
    /// 1050, 14010000: 1500). OFFICIAL table; the held charge reached Max at 1000-1050 ms with charge rate 1.091 and 850-900 ms
    /// with 1.241 (11 official full charges), so the time is divided by the charge rate (CANDIDATE scaling). Unknown skills keep
    /// <see cref="ChargeMaxMs"/>.
    /// </summary>
    int ChargeFullMs(int skill) => ChargeTimes.Value.TryGetValue(skill, out var ms) && ms > 0 ? (int)Math.Round(ms / Math.Max(0.5, ChargeRate)) : ChargeMaxMs;

    static readonly Lazy<Dictionary<int, int>> ChargeTimes = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("skill-charges.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-charges.json");
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("charges").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value[1].EnumerateArray().Select(t => t.GetInt32()).DefaultIfEmpty(0).Last());
    });
    /// <summary>
    /// The charging abnormal: OBSERVED (tools/data/abnormal-observed.json, 11 charges) 100000401 on the caster for the whole
    /// charge (duration -1), with MoveSpeed and FlySpeed -3000 in the stats update; removed with reason 7 at the end.
    /// </summary>
    public const int ChargingAbnormal = 100000401;
    const byte ChargingEndReason = 7;

    /// <summary>
    /// The charge rate of 0x381c: the caster's attack speed / 10000 (OFFICIAL: 1.091 in 25 of 48 samples = attack speed 10910 of the
    /// same character's 0x3802; 1.03-1.583 for others). We sent 1.0.
    /// </summary>
    float ChargeRate => AttackSpeed / 10_000f;

    List<byte[]> AddChargingBuff(int skill, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        if (AddBuff(ChargingAbnormal, -1, skill, now, replies) is { } buff) { buff.Stats["MoveSpeed"] = -3000; buff.Stats["FlySpeed"] = -3000; }
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    List<byte[]> RemoveChargingBuff()
    {
        var gone = buffs.Where(b => b.AbnormalId == ChargingAbnormal).ToList();
        if (gone.Count == 0) return [];
        var speedBefore = MoveSpeedBonus();
        return [.. RemoveBuffs(gone, ChargingEndReason), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>The charge being held: the skill, its target and aim, and when it began.</summary>
    (int Skill, int Target, float Yaw, Vector3d At, long Since)? charging;
    /// <summary>A charge level cast right now by a release (accepted as learned through its charge skill).</summary>
    int releasing;
    /// <summary>The last charge skill started (for a late cancel after the server already ended that charge).</summary>
    int lastChargeSkill;
    /// <summary>
    /// The last charge the server fired by itself at Max: its 0x381e and when. A release that crosses it gets the same answer.
    /// </summary>
    (byte[] End, long At)? lastAutoFired;
    /// <summary>How long after an auto-fire a crossing release is still answered as that fire (CANDIDATE: the live crossings came ~110 ms after).</summary>
    const int LateReleaseMs = 2000;

    /// <summary>The charge skill a charge level belongs to (14010003 -> 14010000), null for any other skill.</summary>
    static int? ChargeBaseOf(int skill)
    {
        for (var step = 1; step <= 3; step++)
            if (Behaviour.Of(skill - step) is { Charge: true }) return skill - step;
        return null;
    }

    bool ChargeReleasing(int skill) => releasing == skill;

    /// <summary>
    /// A charge skill's levels from the client SkillCharge table: per level its hold time (ms) and the skill fired (12090000:
    /// 500/1000/1500 -> 12090001/2/3; 11360030: one level 500 -> 11360031). OFFICIAL client data. Empty when the table lacks it.
    /// </summary>
    internal static IReadOnlyList<(int Ms, int Skill)> ChargeSteps(int skill) => SkillClient.Embedded.Of(skill)?.Charge?.Steps ?? [];
    internal void LearnForChecks(int skill) => learned.Add(skill);

    List<byte[]> ChargingStart(byte[] plain, long now)
    {
        if (plain.Length < 2 + 4 + 1 + 1 + 16 + 1) return [];
        var skill = BitConverter.ToInt32(plain, 2);
        byte[] Rs(int result) => PacketWriter.Opcode(SkillChargingStartRs).U16(result).I32(skill).ToArray();
        int target;
        try { target = (int)new PacketReader(plain) { Offset = 7 }.Varint(); }
        catch (ProtocolException) { return [Rs(1)]; }
        // The aim is the last 16 bytes before the auto-use flag.
        var yaw = BitConverter.ToSingle(plain, plain.Length - 17);
        var at = new Vector3d(BitConverter.ToSingle(plain, plain.Length - 13), BitConverter.ToSingle(plain, plain.Length - 9), BitConverter.ToSingle(plain, plain.Length - 5));
        if (Behaviour.Of(skill) is not { Charge: true } || !learned.Contains(skill) || dead || resources is null
            || !noCool && now < cooldowns.GetValueOrDefault(CooldownKeyOf(skill)) || CcBlocksCast(skill, now) || !float.IsFinite(yaw)
            || target < 0 || !double.IsFinite(at.X) || !double.IsFinite(at.Y) || !double.IsFinite(at.Z))
            return [Rs(1)];
        // OFFICIAL (rec 105448 #35850/#36005: 125.62 / 120.58 = the bearing from the caster to the aimed location to 0.001 degree):
        // the notice's yaw is the direction from the caster to the target location, not the request's (ours was 0.0 in every trace).
        if (position is { } from && (Math.Abs(at.X - from.X) > 1 || Math.Abs(at.Y - from.Y) > 1))
            yaw = (float)(Math.Atan2(at.Y - from.Y, at.X - from.X) * 180 / Math.PI);
        charging = (skill, target, yaw, at, now);
        lastChargeSkill = skill;
        var since = now;
        Schedule(now + ChargeFullMs(skill), due => charging is { } still && still.Since == since ? ReleaseCharge(still, 3, due, auto: true) : []);
        // The skill's level (official 14 and 20 for level-14/20 Deadshots; we sent 1).
        var notice = ChargingStartNotice(playerKey, skill, Math.Clamp(CombatSkillLevel(skill), 1, 255), target, yaw, at, ChargeRate);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        return [notice, Rs(0), .. AddChargingBuff(skill, now)];
    }

    /// <summary>
    /// 0x381c SkillChargingStart_NT: caster | 01 | skill i32 | level u8 | SkillTargetInfo {02, target varint, yaw f32, location} |
    /// charge rate f32 | act move type 01. OFFICIAL (48 samples; rec 105448 #35850 rebuilt byte for byte in the checks).
    /// </summary>
    public static byte[] ChargingStartNotice(int key, int skill, int level, int target, float yaw, Vector3d at, float rate) =>
        PacketWriter.Opcode(SkillChargingStartNt).Varint(key).U8(1).I32(skill).U8((byte)level).U8(2).Varint(target)
            .F32(yaw).Vec3(at).F32(rate).U8(1).ToArray();

    List<byte[]> ChargingEnd(byte[] plain, long now)
    {
        // A release that crossed the server's own fire at Max (live 2026-11-09 zenyc, Deadshot: fired at 1375 ms, the client's
        // release at 1516 ms): the failure answer left the client charging, unable to walk (dodge and skills still worked) until
        // the next full charge. It is answered as the fire it crossed (same 0x381e, no second shot).
        if (charging is null && lastAutoFired is { } fired && now - fired.At <= LateReleaseMs)
        {
            lastAutoFired = null;
            return [fired.End];
        }
        if (charging is not { } held) return [PacketWriter.Opcode(SkillChargingEndRs).U8(1).U16(1).I32(0).I32(0).I32(0).ToArray()];
        // The request names the selected level (usmap: charging step, selected skill, LOS pass, caster direction): the first i32
        // that is one of the skill's levels. Without one the charge was released before its first level: step 0, the charge skill
        // itself fires (live 2026-11-08 `1d38 07 00000000 20cce500 01 08`, Hellfire released after 450 ms: we fired Level 1 and
        // answered step 1, the client waited for its step-0 shot and the Sorcerer could no longer walk, only dodge).
        var levels = ChargeSteps(held.Skill);
        var step = 0;
        for (var i = 2; i + 4 <= plain.Length && step == 0; i++)
        {
            var id = BitConverter.ToInt32(plain, i);
            if (levels.Count > 0) { for (var k = 0; k < levels.Count; k++) if (levels[k].Skill == id) { step = k + 1; break; } }
            else if (id > held.Skill && id <= held.Skill + 3 && Behaviour.Of(id) is not null) step = id - held.Skill;
        }
        // The server's own clock decides how far the charge got (the client table's ChargeTime per level, at the charge rate as the
        // full charge; 150 ms of slack for the network): a release claiming a level not reached yet fires the reached one.
        if (levels.Count > 0)
        {
            var heldMs = (now - held.Since) * Math.Max(0.5, ChargeRate) + 150;
            var reached = levels.Count(l => l.Ms <= heldMs);
            step = Math.Min(step, reached);
        }
        return ReleaseCharge(held, step, now);
    }

    /// <summary>The charge fired at a level (a release, or the top level when held to the end): the level's cast and 0x381e.</summary>
    List<byte[]> ReleaseCharge((int Skill, int Target, float Yaw, Vector3d At, long Since) held, int step, long now, bool auto = false)
    {
        charging = null;
        lastAutoFired = null;
        var levels = ChargeSteps(held.Skill);
        int selected;
        if (levels.Count > 0)
        {
            step = Math.Clamp(step, 0, levels.Count);
            selected = step == 0 ? held.Skill : levels[step - 1].Skill;
        }
        else
        {
            while (step > 0 && Behaviour.Of(held.Skill + step) is null) step--;
            selected = held.Skill + step;
        }
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(selected).U8(2).Varint(held.Target).F32(held.Yaw).Vec3(held.At).ToArray();
        releasing = selected;
        List<byte[]> replies;
        try { replies = CastReplies(cast, now); }
        finally { releasing = 0; }
        var at = replies.FindIndex(b => b.Length > 1 && b[0] == 0x02 && b[1] == 0x38);
        if (at < 0)
        {
            // The level's cast was refused (target out of range or gone, ...): no 0x3802 follows, so a success 0x381e left the client
            // waiting for its shot, charging for ever and unable to move (live 2026-11-11 Axel3, released far from the target). The
            // charge is ended like an interrupted one: a failed 0x381e for the client's own release, then SkillChargingStop_NT 0x3821
            // (OBSERVED layout, the notice the client lets go on). CANDIDATE: no official out-of-range release recorded.
            if (!auto) replies.Add(PacketWriter.Opcode(SkillChargingEndRs).U8(1).U16(1).I32(held.Skill).I32(0).I32(0).ToArray());
            replies.Add(PacketWriter.Opcode(SkillChargingStopNt).Varint(playerKey).U16(0).I32(held.Skill).Varint(0).ToArray());
            if (worldPlayer?.Map is { } around) around.Broadcast(worldPlayer, [replies[^1]]);
            replies.AddRange(RemoveChargingBuff());
            return replies;
        }
        var end = PacketWriter.Opcode(SkillChargingEndRs).U8(1).U16(0).I32(held.Skill).I32(step).I32(selected).ToArray();
        replies.Insert(at + 1, end);
        if (auto) lastAutoFired = (end, now);
        if (worldPlayer?.Map is { } map) map.Broadcast(worldPlayer, replies.Where(SharedCastPacket));
        replies.AddRange(RemoveChargingBuff());
        return replies;
    }

    List<byte[]> ChargingStop(long now)
    {
        // A cancel is answered (0x3820) and the charge is ended with SkillChargingStop_NT (0x3821, the observed end of a charge):
        // with the reply alone the client repeated its cancel (live 2026-11-07, 4+ times).
        // A cancel that arrives after the server already ended the charge (interrupted, auto-fired at Max) still gets the stop
        // notice for the last charge skill, so the client lets go of it instead of repeating the cancel.
        var held = charging;
        charging = null;
        List<byte[]> replies = [PacketWriter.Opcode(SkillChargingStopRs).U16(0).ToArray()];
        if ((held?.Skill ?? lastChargeSkill) is var stopSkill and > 0)
            replies.Add(PacketWriter.Opcode(SkillChargingStopNt).Varint(playerKey).U16(0).I32(stopSkill).Varint(0).ToArray());
        return [.. replies, .. RemoveChargingBuff()];
    }

    /// <summary>
    /// A charge interrupted by another action (movement, jump, dodge, another skill): ended for the client with
    /// SkillChargingStop_NT {key, result u16, skill, cooltime} (the OBSERVED 0x3821 layout) so it never stays charging.
    /// </summary>
    List<byte[]> InterruptCharge()
    {
        if (charging is not { } held) return [];
        charging = null;
        return [PacketWriter.Opcode(SkillChargingStopNt).Varint(playerKey).U16(0).I32(held.Skill).Varint(0).ToArray(), .. RemoveChargingBuff()];
    }
}
