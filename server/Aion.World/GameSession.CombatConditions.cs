namespace Aion.World;

partial class GameSession
{
    /// <summary>Player stagger gauge (PvP): same break rules as NPC <see cref="StaggerGaugeCombat"/>.</summary>
    internal int StaggerGauge { get; set; }
    internal int StaggerGaugeMax { get; set; }
    internal long StaggerUntil { get; set; }

    internal bool InCombatForConditions => inCombat;
    internal int CombatGauge => resources is { ApMax: > 0 } ? resources.Ap : StaggerGauge;

    internal bool TargetInCombat(int targetKey) =>
        targetKey >= SharedWorld.FirstPlayerKey
            ? worldPlayer?.Map?.Players.GetValueOrDefault(targetKey)?.Session.inCombat == true
            : inCombat;

    internal bool PlayerHasAbnormalEffect(string? effectType, long now, int? casterKey)
    {
        if (string.IsNullOrEmpty(effectType)) return false;
        if (effectType.Equals("Stagger", StringComparison.OrdinalIgnoreCase) && StaggerUntil > now) return true;
        return buffs.Any(b => (!casterKey.HasValue || b.Uid != 0)
            && AbnormalSpecs.Types(b.AbnormalId).Any(t => t.Equals(effectType, StringComparison.OrdinalIgnoreCase)));
    }

    internal bool PlayerHasAbnormalGroup(int group, int? casterKey) =>
        buffs.Any(b => b.Group == group || b.AbnormalId == group);

    internal bool PlayerHasAbnormalId(int abnormalId, int? casterKey) =>
        buffs.Any(b => b.AbnormalId == abnormalId);

    internal bool NpcHasAbnormalEffect(int targetKey, string effectType, long now, int? casterKey)
    {
        if (targets?.TryGetValue(targetKey, out var victim) != true || victim is null) return false;
        if (string.IsNullOrEmpty(effectType)) return false;
        if (effectType.Equals("Stagger", StringComparison.OrdinalIgnoreCase) && victim.StaggerUntil > now) return true;
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => AbnormalSpecs.Types(b.AbnormalId).Any(t => t.Equals(effectType, StringComparison.OrdinalIgnoreCase)));
    }

    internal bool NpcHasAbnormalGroup(int targetKey, int group, int? casterKey)
    {
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => b.AbnormalId == group || AbnormalStackRules.GroupOf(b.AbnormalId) == group);
    }

    internal bool NpcHasAbnormalId(int targetKey, int abnormalId, int? casterKey)
    {
        if (!npcBuffs.TryGetValue(targetKey, out var list)) return false;
        return list.Any(b => b.AbnormalId == abnormalId);
    }

    internal bool HasAbnormalEffectType(string? effectType) =>
        !string.IsNullOrEmpty(effectType) && HasAbnormalEffect(effectType);

    internal SkillEffectConditions.Context BuildEffectConditionContext(Target? npc, WorldPlayer? player, int skillId, int mainTargetKey, bool mainTarget, long now)
    {
        var dist = 0.0;
        if (Position is { } from)
        {
            if (npc is not null) dist = Math.Sqrt(from.DistanceSquared(npc.Position));
            else if (player is not null) dist = Math.Sqrt(from.DistanceSquared(player.Position));
        }
        return new(this, npc, player, skillId, mainTargetKey, now, rng, mainTarget, dist);
    }

    internal List<byte[]> BreakPlayerStagger(long now, int casterKey)
    {
        StaggerUntil = now + StaggerGaugeCombat.StaggerDurationMs;
        return TryApplyPlayerDebuff(StaggerGaugeCombat.StaggerAbnormalId, StaggerGaugeCombat.StaggerDurationMs, now, null, casterKey);
    }

    internal List<byte[]> ApplyStaggerGaugeOnPlayerHit(WorldPlayer victim, int skillId, int row, long now)
    {
        var gauge = StaggerGaugeCombat.GaugeDamage(data, skillId);
        return StaggerGaugeCombat.AddPlayerGaugeDamage(victim.Session, gauge, playerKey, now, row == 0);
    }

    internal void ClearPlayerStaggerIfExpired(long now)
    {
        if (StaggerUntil <= 0 || now < StaggerUntil) return;
        StaggerUntil = 0;
    }

    internal bool HasApBar => resources is { ApMax: > 0 };

    internal int ResourceRatioPermille(char kind) => kind switch
    {
        'H' => RatioPermille(resources?.Hp ?? 0, resources?.HpMax ?? 1),
        'M' => RatioPermille(resources?.Mp ?? 0, resources?.MpMax ?? 1),
        'A' => RatioPermille(resources?.Ap ?? 0, resources?.ApMax ?? 1),
        _ => 0,
    };

    static int RatioPermille(int current, int max) => max <= 0 ? 0 : current * 10_000 / max;
}
