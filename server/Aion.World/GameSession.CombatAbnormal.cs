using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Stun/hold/sleep rows on the player: movement and casts are rejected.</summary>
    public bool HeldByAbnormal => buffs.Any(b => b.Combat?.Held == true);

    /// <summary>Test/diagnostic: highest damage reflect permille among active buffs.</summary>
    internal int ActiveReflectPermille => BestReflect().Permille;

    /// <summary>Silence rows: active skills cannot be cast (dodge CANDIDATE still allowed).</summary>
    bool Silenced => buffs.Any(b => b.Combat?.Silence == true);

    bool CombatCastBlocked(int skill) => (HeldByAbnormal || (Silenced && !IsDodgeId(skill))) && !SkillEffects.BreaksControl(skill);

    /// <summary>A hold or a snare: position reports are refused (CheckMovement) and the client is put back.</summary>
    bool MovementBlocked => buffs.Any(b => b.Combat is { Held: true } or { Rooted: true });

    /// <summary>Apply a debuff abnormal to the player; returns false when immunity blocks it (0x382d).</summary>
    internal List<byte[]> TryApplyPlayerDebuff(int abnormalId, int durationMs, long now, Vector3d? from = null, int casterKey = 0)
    {
        if (AbnormalSpecs.Info(abnormalId) is not { } info || !AbnormalSpecs.Debuff(abnormalId)) return [];
        if (DodgeInvulnerable(now)) return [PlayerPackets.ImmuneAbnormal(playerKey, [info.Effects.Count > 0 ? info.Effects[0].Id : abnormalId])];
        var immunities = buffs.Select(b => b.Combat).Where(c => c is not null).Cast<AbnormalCombat.BuffCombat>().ToList();
        if (AbnormalCombat.BlocksDebuffEffects(immunities, info, out var effectId))
            return [PlayerPackets.ImmuneAbnormal(playerKey, [effectId > 0 ? effectId : abnormalId])];
        var speedBefore = BuffStat("MoveSpeed");
        var replies = new List<byte[]>();
        AddBuff(abnormalId, durationMs, 0, now, replies, path: SelfAbnormalPath.PlayerDebuff);
        replies.AddRange(BuffStatsChanged(speedBefore));
        // Knockback [distance, ms]: thrown away from the caster (Aerial is a hold: movement is refused while it lasts).
        if (from is { } origin)
            foreach (var move in AbnormalSpecs.Npc(abnormalId).Moves.Where(m => m.Kind == AbnormalSpecs.DisplaceKind.Knockback))
                replies.AddRange(DisplacedBy(origin, casterKey, abnormalId, move.Amount, pull: false, now));
        return replies;
    }

    /// <summary>Incoming damage to the player: barriers absorb first, then HP; reflect may hit the attacker.</summary>
    internal bool DodgeInvulnerable(long now) => now < dodgeInvulnUntil || buffs.Any(b => b.AbnormalId == DodgeAbnormal && now < b.ExpireAt);

    internal List<byte[]> PlayerDamageReplies(int rawDamage, int attackerKey, long now)
    {
        if (resources is null || dead || rawDamage <= 0) return [];
        if (DodgeInvulnerable(now) || buffs.Any(b => b.Combat?.ImmuneDamage == true)) return [];
        var replies = new List<byte[]>();
        var damage = rawDamage;
        foreach (var b in buffs.Where(b => b.Combat is { BarrierAmount: > 0 }).ToList())
        {
            var c = b.Combat!;
            var absorb = Math.Min(damage, c.BarrierAmount);
            if (absorb <= 0) continue;
            c.BarrierAmount -= absorb;
            damage -= absorb;
            replies.Add(GamePackets.CharBarrierInfo(playerKey, [(b.Uid, c.BarrierAmount)]));
            if (c.BarrierAmount <= 0)
            {
                c.BarrierAmount = 0;
                replies.AddRange(RemoveBuffs([b], ExpireEtc));
            }
        }
        if (damage > 0 && !invincible.Hp)
        {
            resources.Hp = Math.Max(0, resources.Hp - damage);
            replies.Add(GamePackets.NpcHp(playerKey, resources.Hp));
            replies.AddRange(ForceDismount());
            if (resources.Hp == 0) replies.AddRange(Die(attackerKey, now));
        }
        var (permille, cap, reflectSkill) = BestReflect();
        if (permille > 0 && attackerKey > 0 && attackerKey != playerKey)
        {
            var basis = damage > 0 ? damage : rawDamage;
            var reflected = (int)Math.Min(cap > 0 ? cap : int.MaxValue, (long)basis * permille / 10_000);
            if (reflected > 0) replies.AddRange(ReflectDamage(attackerKey, reflected, reflectSkill, now));
        }
        return replies;
    }

    (int Permille, int Cap, int SkillId) BestReflect()
    {
        var best = (0, 0, 0);
        foreach (var b in buffs)
        {
            if (b.Combat is not { ReflectPermille: > 0 } c) continue;
            if (c.ReflectPermille > best.Item1) best = (c.ReflectPermille, c.ReflectCap, c.ReflectSkillId);
        }
        return best;
    }

    List<byte[]> ReflectDamage(int attackerKey, int damage, int reflectSkill, long now)
    {
        var replies = new List<byte[]>();
        if (attackerKey == playerKey) return replies;
        if (!TryGetCombatTarget(attackerKey, out var npc) || npc.Dead || npc.Immune)
            return replies;
        npc.Hp = Math.Max(0, npc.Hp - damage);
        replies.Add(GamePackets.SkillHit(npc.Key, playerKey, reflectSkill, 0, damage, false, 1));
        replies.Add(GamePackets.NpcHp(npc.Key, npc.Hp));
        return replies;
    }

    bool TryGetCombatTarget(int key, out Target target)
    {
        if (targets?.TryGetValue(key, out target!) == true) return true;
        if (worldPlayer?.Map?.Targets.TryGetValue(key, out target!) == true) return true;
        return Targets.TryGetValue(key, out target!);
    }

    /// <summary>After skill hits on an NPC: apply combat skill debuff abnormals from dat effect rows.</summary>
    List<byte[]> SkillDebuffReplies(Target target, int skill, long now)
    {
        var replies = new List<byte[]>();
        foreach (var e in Boxes.SkillEffects(skill).Where(e => e.Type == "Abnormal_True" && e.Value(2) > 0))
        {
            var duration = Math.Max(1, rng.Next(Math.Min(e.Value(0), e.Value(1)), Math.Max(e.Value(0), e.Value(1)) + 1));
            ApplyNpcDebuff(target, e.Value(2), duration, now, replies);
        }
        // Class skills: SkillEffect.dat Abnormal_True / Abnormal_Cc rows exported with the skill's area (tools/skill_areas.py);
        // a trap's hits are its detonation, which carries its own (the skill's rows include the trap marker abnormal).
        var rows = PcTraps.For(skill) is { } trap ? trap.DebuffRows : CastArea(skill)?.TargetDebuffRows ?? [];
        foreach (var (abnormal, minMs, maxMs, held) in rows)
        {
            var ctx = BuildEffectConditionContext(target, null, skill, target.Key, mainTarget: true, now);
            if (!SkillEffectConditions.DebuffAllowed(skill, abnormal, ctx)) continue;
            var duration = Math.Max(1, rng.Next(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs) + 1));
            ApplyNpcDebuff(target, abnormal, duration, now, replies, held, skill);
        }
        return replies;
    }

    /// <summary>
    /// Parent skills with chain_next often grant a self abnormal the client expects before the hotbar chain swap (0x3818).
    /// FixSlot basic chains are excluded (official sends no self 0x382a on those hits).
    /// </summary>
    internal bool UsesChainCasterAbnormalOnAccept(int skillId) =>
        !data.IsBasicAttackChainSkill(skillId)
        && data.ChainFollowUpIds(skillId).Count > 0
        && SkillSelfAbnormalPolicy.MayApplyOnChainAccept(data, skillId)
        && CastArea(skillId) is { } area
        && area.CasterDebuffRows.Any();

    /// <summary>Export caster_debuffs only (no attack-skill fallbacks; chain hotbar opens without 0x382a when export is empty).</summary>
    IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> ChainAcceptCasterDebuffRows(int skillId) =>
        CastArea(skillId)?.CasterDebuffRows ?? [];

    /// <summary>Self-filter abnormal rows on the caster when a skill lands (or an abnormal-only cast resolves).</summary>
    internal List<byte[]> ApplyCasterSkillAbnormals(int skill, long now, bool chainAcceptPrime = false)
    {
        if (chainAcceptPrime)
        {
            if (!SkillSelfAbnormalPolicy.MayApplyOnChainAccept(data, skill)) return [];
        }
        else if (!SkillSelfAbnormalPolicy.MayApplyOnCombatHit(data, skill)) return [];
        var rows = (chainAcceptPrime ? ChainAcceptCasterDebuffRows(skill) : CastArea(skill)?.CasterDebuffRows ?? []).ToList();
        if (rows.Count == 0 || dead || resources is null) return [];
        var grant = data.Skill(skill)?.SkillCondGrant?.Abnormal;
        var speedBefore = BuffStat("MoveSpeed");
        var replies = new List<byte[]>();
        foreach (var (abnormal, minMs, maxMs, _) in rows)
        {
            if (grant is int granted && granted == abnormal) continue;
            var ctx = BuildEffectConditionContext(null, null, skill, playerKey, mainTarget: true, now);
            if (!SkillEffectConditions.DebuffAllowed(skill, abnormal, ctx)) continue;
            var duration = Math.Max(1, rng.Next(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs) + 1));
            AddBuff(abnormal, duration, skill, now, replies,
                path: chainAcceptPrime ? SelfAbnormalPath.ChainAccept : SelfAbnormalPath.CombatHit);
        }
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    internal List<byte[]> ApplyNpcDebuffForTests(Target target, int abnormalId, int durationMs, long now, bool held = false)
    {
        var replies = new List<byte[]>();
        ApplyNpcDebuff(target, abnormalId, durationMs, now, replies, held);
        return replies;
    }

    void ApplyNpcDebuff(Target target, int abnormalId, int durationMs, long now, List<byte[]> replies, bool held = false, int skillId = 0)
    {
        if (target.Dead) return;
        // OBSERVED official 2026-10-30 (259 in one Ranger fight): an abnormal re-applied while it is still on keeps its uid and
        // is refreshed with 0x382b AbnormalChange_NT, not removed (0x382c) and added again (0x382a).
        if (npcBuffs.TryGetValue(target.Key, out var on) && on.FirstOrDefault(b => b.AbnormalId == abnormalId) is { } running)
        {
            running.ExpireAt = now + durationMs;
            replies.Add(PlayerPackets.AbnormalChange(target.Key, new AbnormalWire(running.Uid, abnormalId, durationMs, running.ExpireAt,
                CasterKey: playerKey, Level: 1, SkillId: 0, RangeBase: target.Position)));
            return;
        }
        var (stops, immune) = AbnormalCombat.NpcFlags(abnormalId);
        var model = AbnormalSpecs.Npc(abnormalId);
        var buff = new NpcBuff
        {
            Uid = ++npcBuffUid, AbnormalId = abnormalId, ExpireAt = now + durationMs, Held = held || stops || model.Held, Immune = immune,
            Silence = model.Silence, SlowPermille = model.SlowPermille, SkillId = skillId,
        };
        // Dot_TargetMaxHP [interval, ratio of the maximum in total (1/10000), cap]: the total is spread over the abnormal's ticks.
        foreach (var dot in model.Dots)
        {
            var ticks = Math.Max(1, durationMs / dot.IntervalMs);
            var total = (long)target.HpMax * dot.RatioTotal / 10_000;
            if (dot.Cap > 0) total = Math.Min(total, dot.Cap);
            var perTick = (int)Math.Max(1, total / ticks);
            buff.Dots.Add(new NpcDot { EffectId = dot.EffectId, IntervalMs = dot.IntervalMs, PerTick = perTick, Remaining = perTick * ticks, NextAt = now + dot.IntervalMs });
        }
        (npcBuffs.TryGetValue(target.Key, out var list) ? list : npcBuffs[target.Key] = []).Add(buff);
        RefreshNpcFlags(target);
        if (abnormalId == StaggerGaugeCombat.StaggerAbnormalId) target.StaggerUntil = now + durationMs;
        replies.Add(PlayerPackets.AbnormalAdd(target.Key, [new AbnormalWire(buff.Uid, abnormalId, durationMs, now + durationMs,
            CasterKey: playerKey, Level: 1, SkillId: 0, RangeBase: target.Position)]));
        foreach (var dot in buff.Dots) replies.Add(RidePackets.AbnormalEffect(target.Key, playerKey, buff.Uid, dot.EffectId, dot.Remaining, null, skillId));
        if (model.Taunt && targets is not null) replies.AddRange(ai.Taunt(targets, target, now, playerKey));
        foreach (var move in model.Moves.Where(m => m.Kind == AbnormalSpecs.DisplaceKind.Knockback))
            replies.AddRange(KnockbackNpc(target, move.Amount, abnormalId, now));
    }
}
