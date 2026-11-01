namespace Aion.World;

public sealed partial class GameSession
{
    internal List<byte[]> BreakNpcStagger(Target target, long now)
    {
        target.StaggerUntil = now + StaggerGaugeCombat.StaggerDurationMs;
        var replies = new List<byte[]>();
        ApplyNpcDebuff(target, StaggerGaugeCombat.StaggerAbnormalId, StaggerGaugeCombat.StaggerDurationMs, now, replies, held: true);
        return replies;
    }

    internal List<byte[]> ApplyStaggerGaugeOnNpcHit(Target target, int skillId, int row, long now)
    {
        var gauge = StaggerGaugeCombat.GaugeDamage(data, skillId);
        return StaggerGaugeCombat.AddGaugeDamage(target, gauge, playerKey, now, this, row == 0);
    }

    bool NpcTargetHasAbnormalEffect(int targetKey, string effectType, long now)
    {
        if (targets?.TryGetValue(targetKey, out var victim) == true && victim.OwnerKey == 0
            && effectType.Equals("Stagger", StringComparison.OrdinalIgnoreCase) && victim.StaggerUntil > now)
            return true;
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => AbnormalSpecs.Types(b.AbnormalId).Any(t => t.Equals(effectType, StringComparison.OrdinalIgnoreCase)));
    }
}
