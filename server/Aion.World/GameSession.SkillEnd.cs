using Aion.Protocol.Native;

namespace Aion.World;

partial class GameSession
{
    /// <summary>Shortest wait before a cast's 0x3806 end (the dodge's recorded end is +200 ms).</summary>
    public const int MinSkillEndMs = 200;

    readonly List<(int Skill, byte Action, long At)> pendingSkillEnds = [];

    /// <summary>
    /// With the 9-byte 0x3801 accept (#140) the client fired one shot and then waited: it treats the cast as running until its
    /// 0x3806 SkillEnd_NT (live 2026-10-30 13:35, Ranger 14020000: one accept, no further 0x3800). Every accepted non-dodge cast
    /// now ends at its swing end (Skill.dat casting + last hit row), at least <see cref="MinSkillEndMs"/>, as the official
    /// server ends dodges. A projectile skill ends no earlier than its last arrow lands: at range the swing end (600 ms) came
    /// before the landing of Marking Shot (Speed 3500: ~770 ms at 25 m) and ended the cast with its arrow in the air.
    /// </summary>
    void ScheduleSkillEnd(int skill, byte action, SkillDefinition definition, long now)
    {
        var at = now + Math.Max(MinSkillEndMs, SkillCooldown.SwingEndMs(definition));
        foreach (var hit in pendingCombatHits)
            if (hit.SkillId == skill && hit.ActionUid == action && hit.DueMs > at) at = hit.DueMs;
        pendingSkillEnds.Add((skill, action, at));
    }

    List<byte[]> SkillEndTick(long now)
    {
        if (pendingSkillEnds.Count == 0) return [];
        var ended = new List<byte[]>();
        pendingSkillEnds.RemoveAll(e =>
        {
            if (now < e.At) return false;
            ended.Add(GamePackets.SkillEnd(playerKey, e.Skill, e.Action));
            return true;
        });
        if (ended.Count > 0) worldPlayer?.Map?.Broadcast(worldPlayer, ended);
        return ended;
    }
}
