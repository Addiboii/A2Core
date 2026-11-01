using Aion.Protocol.Native;

namespace Aion.World;

// Abnormal-only skills. A skill with no Damage row never reached ScheduleCombatHits, so its Abnormal_True / Abnormal_Cc rows
// (stuns, holds, slows, the caster's own buffs: 232 of the 8 classes' active skills in skill-areas.json) were never applied.
// The rows are read from the same export as the damage skills' debuffs; who gets them follows the client's own targeting:
// an enemy filter (SkillEffectFilter shape) or a locked monster -> the enemies, else the caster.
partial class GameSession
{
    sealed record PendingSkillAbnormals(long DueMs, int SkillId, int TargetKey);

    readonly List<PendingSkillAbnormals> pendingSkillAbnormals = [];

    List<byte[]> AbnormalOnlyCastReplies(int skill, CastRequest request, SkillDefinition definition, long now)
    {
        if (definition.Damage.Length > 0 || targets is null || SkillEffects.IsFriendly(skill) || PcTraps.For(skill) is not null || PcZones.For(skill) is not null || PcSummons.For(skill) is not null
            || CastArea(skill) is not { } area || (!area.TargetDebuffRows.Any() && !area.CasterDebuffRows.Any())) return [];
        List<Target> chosen = area.IsGround ? GroundTargets(area, request)
            : request.TargetKey is >= Combat.FirstNpcKey and < SharedWorld.FirstPlayerKey && targets.TryGetValue(request.TargetKey, out var locked)
                ? AreaTargets(area, request, locked)
            : request.TargetKey == playerKey && area.IsArea ? SelfCastTargets(request, area)
            : [];
        var due = now + definition.CastingMs + NpcAi.HitDelayMs;
        var enemies = chosen.Where(t => !t.Dead && t.Hostile && PlayerMayDamageNpc(t)).ToList();
        foreach (var enemy in enemies) pendingSkillAbnormals.Add(new(due, skill, enemy.Key));
        if (PvpVictimOf(request.TargetKey) is { } victim) pendingSkillAbnormals.Add(new(due, skill, victim.Key));
        if (enemies.Count == 0 && request.TargetKey == playerKey && !area.IsArea && area.CasterDebuffRows.Any()
            && !UsesChainCasterAbnormalOnAccept(skill))
            pendingSkillAbnormals.Add(new(due, skill, playerKey));
        return [];
    }

    List<byte[]> ResolvePendingSkillAbnormals(long now)
    {
        if (pendingSkillAbnormals.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var pending in pendingSkillAbnormals.Where(p => now >= p.DueMs).ToList())
        {
            pendingSkillAbnormals.Remove(pending);
            var area = CastArea(pending.SkillId);
            if (pending.TargetKey >= SharedWorld.FirstPlayerKey && pending.TargetKey != playerKey)
            {
                if (PvpVictimOf(pending.TargetKey) is { } victim && !victim.Session.dead) PvpSkillDebuffs(victim, pending.SkillId, now);
            }
            else if (pending.TargetKey == playerKey)
            {
                if (dead) continue;
                replies.AddRange(ApplyCasterSkillAbnormals(pending.SkillId, now));
            }
            else if (targets is not null && targets.TryGetValue(pending.TargetKey, out var target) && !target.Dead)
            {
                foreach (var (abnormal, minMs, maxMs, held) in area?.TargetDebuffRows ?? [])
                {
                    var ctx = BuildEffectConditionContext(target, null, pending.SkillId, target.Key, mainTarget: true, now);
                    if (!SkillEffectConditions.DebuffAllowed(pending.SkillId, abnormal, ctx)) continue;
                    ApplyNpcDebuff(target, abnormal, Math.Max(1, rng.Next(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs) + 1)), now, replies, held, pending.SkillId);
                }
                replies.AddRange(ai.Engage(targets, target, now, playerKey, 1));
            }
        }
        return replies;
    }
}
