using Aion.Protocol.Native;

namespace Aion.World;

// Live opcodes = DevPacketData_GameServer_* + 0x157 (same band as 0x383f SkillLevelUp_RQ).
// BasicAtk / toggle / charging-start are separate from 0x3800 SkillStart_RQ but share the cast body layout where noted.
public sealed partial class GameSession
{
    public const ushort SkillChargingStartRq = 0x381a, SkillChargingStartRs = 0x381b;
    public const ushort BasicAtkRq = 0x3824, BasicAtkRs = 0x3825;
    public const ushort BasicAtkToggleOnRq = 0x3826, BasicAtkToggleOnRs = 0x3827;
    public const ushort BasicAtkToggleOffRq = 0x3828, BasicAtkToggleOffRs = 0x3829;

    /// <summary>
    /// Result of either 0x3801 form. Flags-first (official cast accept 2026-10-30: 00 | result u16 | skill, 9 bytes with flags 0):
    /// u16 at 3. The dodge's result-first 9-byte form (result u16 | skill | u8): u16 at 2; told apart by a skill id at offset 5.
    /// </summary>
    internal static ushort CastResultCode(byte[] packet)
    {
        if (packet.Length < 5) return 1;
        if (packet.Length == 9 && !(packet[2] <= 3 && BitConverter.ToInt32(packet, 5) is >= 1_000_000 and < 1_000_000_000))
            return BitConverter.ToUInt16(packet, 2);
        return BitConverter.ToUInt16(packet, 3);
    }

    static bool IsSkillStartWireOpcode(ushort opcode) => opcode is 0x3800 or SkillChargingStartRq;

    /// <summary>The class combo root: the learned FixSlot basic first, else any learned chain-free basic.</summary>
    int? PrimaryBasicAutoAttackSkill()
    {
        int? fallback = null;
        foreach (var skillId in learned)
        {
            if (data.Skill(skillId) is not { } def || !SkillCooldown.IsBasicAutoAttack(def)) continue;
            if (SkillCooldown.IsFixSlot(def)) return skillId;
            fallback ??= skillId;
        }
        return fallback;
    }

    /// <summary>
    /// Left-click 0x3824 during an open FixSlot combo window: the client keeps sending BasicAtk, not 0x3800 with the chain step id.
    /// Cast the deepest ready chain skill on that tree (Burst after Flame Arrow), else the root.
    /// </summary>
    int? ResolveBasicAutoAttackSkill(long now)
    {
        if (PrimaryBasicAutoAttackSkill() is not { } root) return null;
        var comboRoot = ChainRoot(root);
        var readyChain = activeChainSkills
            .Where(p => now < p.Value && data.Skill(p.Key) is { ChainPrev: > 0 })
            .Select(p => p.Key)
            .Where(id => ChainRoot(id) == comboRoot)
            .OrderByDescending(ChainStepDepth)
            .FirstOrDefault();
        return readyChain > 0 ? readyChain : root;
    }

    int ChainStepDepth(int skillId)
    {
        var depth = 0;
        for (var i = 0; i < 8 && data.Skill(skillId) is { ChainPrev: > 0 } step; i++)
        {
            skillId = step.ChainPrev;
            depth++;
        }
        return depth;
    }

    /// <summary>Maps 0x3801 cast acks to 0x381b when the client used SkillChargingStart_RQ (right-click / charge path).</summary>
    static List<byte[]> SkillChargingStartAck(List<byte[]> castReplies, CastRequest request)
    {
        if (castReplies.Count == 0) return castReplies;
        var replies = new List<byte[]>();
        var acked = false;
        foreach (var packet in castReplies)
        {
            if (Opcode(packet) == 0x3801)
            {
                var result = CastResultCode(packet);
                replies.Add(GamePackets.SkillChargingStartReply(result, request.SkillId));
                acked = true;
            }
            else replies.Add(packet);
        }
        if (!acked) replies.Insert(0, GamePackets.SkillChargingStartReply(1, request.SkillId));
        return replies;
    }

    List<byte[]> HandleBasicAtkRequest(byte[] plain, long now)
    {
        if (plain.Length < 4 || plain[2] != 0) return [GamePackets.BasicAtkReply(1)];
        int target;
        try { target = (int)new PacketReader(plain) { Offset = 3 }.Varint(); }
        catch (ProtocolException) { return [GamePackets.BasicAtkReply(1)]; }
        if (ResolveBasicAutoAttackSkill(now) is not { } skill) return [GamePackets.BasicAtkReply(1)];
        selectedTarget = target;
        autoAttackSkill = skill;
        NoteHeldCastWireReceived(now);
        var at = position ?? Vector3d.Zero;
        var body = BuildSkillStartWire(skill, target, at, worldPlayer?.Heading ?? 0);
        var cast = CastReplies(body, now);
        LogBasicAtkCast(skill, target, cast);
        return ReplaceCastAckWithBasicAtk(cast);
    }

    List<byte[]> HandleBasicAtkToggleOn(long now)
    {
        if (PrimaryBasicAutoAttackSkill() is { } skill) autoAttackSkill = skill;
        return [GamePackets.BasicAtkToggleReply(BasicAtkToggleOnRs, 0)];
    }

    List<byte[]> HandleBasicAtkToggleOff()
    {
        var replies = new List<byte[]> { GamePackets.BasicAtkToggleReply(BasicAtkToggleOffRs, 0) };
        if (autoAttackActionUid != 0)
        {
            replies.Add(GamePackets.SkillEnd(playerKey, autoAttackSkill != 0 ? autoAttackSkill : PrimaryBasicAutoAttackSkill() ?? 0, autoAttackActionUid));
            autoAttackActionUid = 0;
        }
        return replies;
    }

    static List<byte[]> ReplaceCastAckWithBasicAtk(List<byte[]> castReplies)
    {
        if (castReplies.Count == 0) return [GamePackets.BasicAtkReply(1)];
        var replies = new List<byte[]>();
        var acked = false;
        foreach (var packet in castReplies)
        {
            if (Opcode(packet) == 0x3801)
            {
                var result = CastResultCode(packet);
                replies.Add(GamePackets.BasicAtkReply(result));
                acked = true;
            }
            else replies.Add(packet);
        }
        if (!acked) replies.Insert(0, GamePackets.BasicAtkReply(1));
        return replies;
    }

    /// <summary>Mask-zero SkillStart wire (26 bytes with opcode), same fields as 0x3800 / SkillChargingStart_RQ.</summary>
    static byte[] BuildSkillStartWire(int skillId, int targetKey, Vector3d at, double yaw) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32((float)yaw).Vec3(at).ToArray();
}
