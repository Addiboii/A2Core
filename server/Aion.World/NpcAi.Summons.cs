using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// A player's summon (Target.OwnerKey) casts its NpcData ActiveSkillList like a player casts: 0x3802 with the summon as caster,
/// one 0x3803 per SkillProjectile row of tools/data/skill-areas.json, 0x3804 on every target in the skill's SkillEffectFilter
/// shape (projectile form 0x14 for projectile skills, 0x04 otherwise), then 0x3806 once the last hit landed.
/// From the client data: ranges, cooldowns, effects, damage (pc-summons.json), projectiles, speeds and shapes (skill-areas.json).
/// INFERRED (no official summon capture): the rotation (basics alternate, a special is used whenever its cooldown is over and
/// it reaches; one skill per family id / 10000, the family's first ActiveSkillList entry), the standoff of a spirit whose basic
/// is a projectile with Range 0 (its shortest special range), the row spacing <see cref="SummonRowGapMs"/>, a skill's damage
/// split evenly over its rows, and the 0x3804 forms (the player's).
/// </summary>
public sealed partial class NpcAi
{
    /// <summary>Between two projectile rows of one summon cast (OURS: no row timing is exported for NPC skills).</summary>
    public const int SummonRowGapMs = 150;

    public enum ShotKind { Projectile, Hit, End, CastingStart, SkillStart, Fire, Collision }

    public sealed record SummonShot(long DueMs, ShotKind Kind, int SkillId, int ActionSeq, int TargetKey, int Seq,
        int ProjectileId = 0, int FlightMs = 0, int Damage = 0, int Effect = 0, bool Projectile = false, int Groggy = 0);

    /// <summary>The summon's attacks: its damaging cooldown-free basics, and per skill family its first damaging special.</summary>
    internal (List<(int Id, NpcSkill Skill)> Basics, List<(int Id, NpcSkill Skill)> Specials) SummonRotation(Target summon)
    {
        List<(int, NpcSkill)> basics = [], specials = [];
        if (data is null) return (basics, specials);
        var families = new HashSet<int>();
        foreach (var id in summon.Skills)
        {
            if (data.NpcSkill(id) is not { } skill) continue;
            if (skill.Cooldown <= 0) basics.Add((id, skill));
            else if (families.Add(id / 10_000)) specials.Add((id, skill));
        }
        return (basics, specials);
    }

    /// <summary>A summon whose basic attack is a projectile fights from range.</summary>
    bool SummonRanged(Target summon) =>
        SummonRotation(summon).Basics is [var first, ..] && SkillAreas.For(first.Id) is { Projectile: > 0 };

    /// <summary>Centre distance at which a summon's skill reaches: its Range, else (a Range-0 projectile) the shortest special range.</summary>
    double SummonRange(Target summon, int id, NpcSkill skill)
    {
        var range = skill.Range;
        if (range <= 0 && SkillAreas.For(id) is { Projectile: > 0 })
            range = SummonRotation(summon).Specials.Select(s => s.Skill.Range).Where(r => r > 0).DefaultIfEmpty(Reach).Min();
        return (range > 0 ? range : Reach) + summon.Radius + PlayerRadius;
    }

    /// <summary>The distance a summon closes to: the reach of its basic attack.</summary>
    double SummonReach(Target summon) =>
        SummonRotation(summon).Basics is [var first, ..] ? SummonRange(summon, first.Id, first.Skill) : AttackRange(summon, null);

    (int Id, NpcSkill Skill)? ChooseSummon(Target summon, Brain brain, double distance, long now)
    {
        if (now < brain.NextAttackMs) return null;
        var (basics, specials) = SummonRotation(summon);
        foreach (var (id, skill) in specials)
        {
            if (!brain.Cooldowns.TryGetValue(id, out var readyAt))
            {
                brain.Cooldowns[id] = now + skill.Cooldown;
                continue;
            }
            if (now >= readyAt && distance <= SummonRange(summon, id, skill)) return (id, skill);
        }
        if (basics.Count == 0) return null;
        var basic = basics[brain.BasicTurn % basics.Count];
        if (distance > SummonRange(summon, basic.Id, basic.Skill)) return null;
        brain.BasicTurn++;
        return basic;
    }

    /// <summary>The cast's targets: the victim, then the nearest other living monsters in the skill's shape, up to its TargetCountMax.</summary>
    List<Target> SummonTargets(Target summon, Target victim, SkillAreas.Area? shape)
    {
        List<Target> chosen = [victim];
        if (shape is not { IsArea: true } || current is null) return chosen;
        var centre = shape.OnTarget ? victim.Position : summon.Position;
        var yaw = HeadingTo(summon.Position, Flat(victim.Position));
        chosen.AddRange(current.Values
            .Where(t => t.Key != victim.Key && t.OwnerKey == 0 && t.Hostile && !t.Dead && !t.CorpseRemoved
                && shape.Contains(centre, yaw, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(centre)).Take(shape.Max - 1));
        return chosen;
    }

    /// <summary>The summon's cast: a stop facing the victim, the 0x3802, and its rows scheduled (projectiles, hits, the end).</summary>
    List<byte[]> SummonCast(Target summon, Brain brain, (int Id, NpcSkill Skill) cast, Target victim, double facing, long now)
    {
        var replies = new List<byte[]>();
        replies.AddRange(Stop(summon, brain, facing, now));
        brain.ActionSeq = brain.ActionSeq % 255 + 1;
        var seq = brain.ActionSeq;
        var (id, skill) = cast;
        brain.Cooldowns[id] = now + Math.Max(skill.Cooldown, skill.Cast + HitDelayMs);
        replies.Add(GamePackets.SkillStart(id, seq, facing, summon.Position, targetKey: victim.Key, actorKey: summon.Key, cooldown: skill.Cooldown));

        var area = SkillAreas.For(id);
        var rows = area is { Projectile: > 0 } shot
            ? Enumerable.Range(0, shot.Projectiles?.Length ?? 1).Where(r => shot.ProjectileFor(r) > 0).ToList()
            : [0];
        var projectile = area is { Projectile: > 0 };
        var hitTargets = SummonTargets(summon, victim, area);
        var release = now + skill.Cast + HitDelayMs;
        var last = now + GameSession.MinSkillEndMs;
        foreach (var (row, index) in rows.Select((r, i) => (r, i)))
        {
            var at = release + index * SummonRowGapMs;
            var mainFlight = projectile ? area!.FlightMs(Math.Sqrt(summon.Position.DistanceSquared(victim.Position))) : 0;
            foreach (var t in hitTargets)
            {
                var flight = projectile ? area!.FlightMs(Math.Sqrt(summon.Position.DistanceSquared(t.Position))) : 0;
                var splash = area is { OnTarget: true, IsArea: true } && t != victim;
                if (projectile && !splash)
                    brain.Volley.Add(new(at, ShotKind.Projectile, id, seq, t.Key, row + 1, area!.ProjectileFor(row), flight));
                var lands = at + (splash ? mainFlight : flight);
                var defense = Math.Max(0, t.Defense);
                var damage = Math.Max(1, (int)Math.Round(Damage(summon, skill) * 1000.0 / (1000 + defense) / rows.Count));
                brain.Volley.Add(new(lands, ShotKind.Hit, id, seq, t.Key, row + 1, Damage: damage, Effect: skill.Effect, Projectile: projectile));
                last = Math.Max(last, lands);
            }
        }
        brain.Volley.Add(new(last, ShotKind.End, id, seq, victim.Key, 0));
        brain.Volley.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
        brain.NextAttackMs = last;
        return replies;
    }

    /// <summary>The summon's due projectiles (aimed where their target is now), hits and skill ends.</summary>
    List<byte[]> SummonVolley(Target summon, Brain brain, long now)
    {
        if (brain.Volley.Count == 0 || current is null) return [];
        var replies = new List<byte[]>();
        while (brain.Volley.Count > 0 && brain.Volley[0].DueMs <= now)
        {
            var shot = brain.Volley[0];
            brain.Volley.RemoveAt(0);
            if (shot.Kind == ShotKind.End)
            {
                replies.Add(GamePackets.SkillEnd(summon.Key, shot.SkillId, shot.ActionSeq));
                continue;
            }
            if (!current.TryGetValue(shot.TargetKey, out var t) || t.Dead) continue;
            if (shot.Kind == ShotKind.Projectile)
            {
                replies.Add(PlayerPackets.SkillFireProjectile(summon.Key, t.Key, shot.SkillId, (byte)shot.ActionSeq, shot.ProjectileId,
                    t.Position, shot.FlightMs, shot.Seq));
                continue;
            }
            replies.Add(shot.Projectile
                ? GamePackets.ProjectileHit(t.Key, summon.Key, shot.SkillId, shot.Effect, shot.Damage, false, (byte)shot.ActionSeq, shot.Seq)
                : GamePackets.SkillHit(t.Key, summon.Key, shot.SkillId, shot.Effect, shot.Damage, false, (byte)shot.ActionSeq, shot.Seq));
            replies.AddRange(HitNpc(current, t.Key, summon, shot.Damage, now));
            if (shot.Seq == 1 && summon.SummonSourceSkill > 0 && data is { } gd)
            {
                var gauge = StaggerGaugeCombat.GaugeDamage(gd, summon.SummonSourceSkill);
                replies.AddRange(StaggerGaugeCombat.AddGaugeDamage(t, gauge, summon.OwnerKey, now, session: null, firstRow: true));
            }
        }
        return replies;
    }
}
