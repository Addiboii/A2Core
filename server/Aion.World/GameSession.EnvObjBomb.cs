using Aion.Protocol.Native;

namespace Aion.World;

// The carried bomb's use at a Communication Tower. OBSERVED (live 2026-10-30, main 9874b50): with abnormal 160020 the client's F
// at the tower sends 0x384a {skill u32 = 5003} and never 0x3636 or 0x3800. 0x384a is SkillGroundReady_RQ {_skill_id}: the schema's
// opcode table is 343 below the live one (SkillLevelUp_RQ 14056 = 0x383f, SetMainTarget_RQ 14044 = 0x3833, SkillLevelReset_RS
// 14061 = 0x3844), so the ground-skill block is RQ/RS/NT Ready 0x384a-0x384c, Start 0x384d-0x384e (RQ {skill, target info}),
// Stop 0x384f-0x3851. No official sample of the replies exists: the answers below are OURS.
partial class GameSession
{
    public const ushort SkillGroundReadyRq = 0x384a, SkillGroundReadyRs = 0x384b, SkillGroundStartRq = 0x384d, SkillGroundStartRs = 0x384e,
        SkillGroundStopRq = 0x384f, SkillGroundStopRs = 0x3850;

    /// <summary>How far from the player a tower may be for the bomb to break it (cm; OURS, the client decides the prompt by its own range).</summary>
    public const double BombReach = 2500;

    /// <summary>0x3850/0x384b/0x384e result: refused.</summary>
    const int GroundSkillRefused = 1;

    /// <summary>
    /// A ground-skill reply: u16 result, i32 skill. The result is a u16 on the wire like every observed RS and the neighbouring
    /// 0x383a/0x383c cast replies (<see cref="GamePackets.CastReply(bool, int, int, byte)"/>); sent as an i32 (live 2026-10-30, main
    /// 9a80963) the client read skill 0x138b0000, never matched the reply to its pending Ready request and kept every cast locked for
    /// ~25 s after the plant, while carrying the bomb casts worked.
    /// </summary>
    static byte[] GroundReply(ushort opcode, int result, int skill) => PacketWriter.Opcode(opcode).U16(result).I32(skill).ToArray();

    /// <summary>The tower the bomb hits: the selected target when it is a shown destructible in reach, else the nearest one.</summary>
    int? BombTarget(long now)
    {
        if (position is not { } here) return null;
        var objects = WorldObjects.ObjectsOn(worldMap);
        var reach = BombReach * BombReach;
        var candidates = shownObjects.Where(i => i < objects.Count && objects[i].Destructible && !Broken(i, now) && !usedObjects.Contains(i)
                && PlayerMayUseEnvObj(i, objects[i]) && objects[i].At.DistanceSquared(here) <= reach)
            .OrderBy(i => objects[i].At.DistanceSquared(here)).ToList();
        if (candidates.Count == 0) return null;
        var selected = selectedTarget - WorldObjects.FirstKey;
        return candidates.Contains(selected) ? selected : candidates[0];
    }

    /// <summary>
    /// SkillGroundReady_RQ / SkillGroundStart_RQ / SkillGroundStop_RQ. The carried bomb's skill (the buff's ActivateSkill) breaks the
    /// tower in reach at once: the result, the tower's death and despawn, the bomb's 0x382c, then the UseEnvObj credit. Ready and
    /// Start both do it (the client may send either; only the first finds the bomb). Any other ground skill is refused, never
    /// left unanswered, so the client leaves its aiming state.
    /// </summary>
    async Task<List<byte[]>> GroundSkillAsync(ushort header, byte[] plain, long now, CancellationToken ct)
    {
        if (header == SkillGroundStopRq) return [PacketWriter.Opcode(SkillGroundStopRs).U16(0).ToArray()];
        var reply = header == SkillGroundReadyRq ? SkillGroundReadyRs : SkillGroundStartRs;
        var skill = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (resources is null || dead || skill == 0 || CarriedSkill != skill || BombTarget(now) is not { } index)
            return [GroundReply(reply, GroundSkillRefused, skill)];
        // Ready: refuse the aim so the client never enters its ground-aim state (SkillAction_InGroundAim blocks every skill), and
        // plant here at once, as the env-object use does. Only the reply and the plant's own packets go out: no Stop_NT. Start
        // (the client really aimed and clicked) is answered with success.
        var aimed = header == SkillGroundStartRq;
        var replies = new List<byte[]> { GroundReply(reply, aimed ? 0 : GroundSkillRefused, skill) };
        replies.AddRange(BreakEnvObj(index, objects[index], now));
        replies.AddRange(DropCarried(ExpireEtc));
        replies.AddRange(await QuestKillsAsync(ct));
        return await WithMapEventClearsFlushedAsync(replies, ct);
    }
}
