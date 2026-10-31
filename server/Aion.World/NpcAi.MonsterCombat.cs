using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Monster NPC combat (not player summons): cast-time 0x3809 then 0x3802, multi-hit 0x3804 from tools/data/world-npcs.json
/// skill_hits, 0x3803/0x380e projectiles, 0x3806 end, 0xe005 groggy on bosses. Timing from official capture notes.
/// </summary>
public sealed partial class NpcAi
{
    public const int MonsterHitStaggerMs = 300;
    public const int MonsterEndAfterLastHitMs = 500;
    public const int MonsterNextSkillMinMs = 50;
    public const int MonsterNextSkillMaxMs = 150;
    public const int MonsterProjectileFlightMs = 1000;

    /// <summary>Default groggy guard for bosses without an explicit ratio (OBSERVED 2701401: 50/50).</summary>
    public const int DefaultGroggyMax = 50;

    IReadOnlyList<NpcSkillHitRow> HitsOf(int skillId, NpcSkill skill) =>
        data?.NpcSkillHits(skillId) is { Count: > 0 } rows ? rows : [new NpcSkillHitRow(skill.Effect, 0, 0)];

    List<byte[]> MonsterCast(Target target, Brain brain, (int Id, NpcSkill Skill) cast, Target victim, double facing, long now,
        Func<int, List<byte[]>>? hitPlayer)
    {
        var replies = new List<byte[]>();
        if (!target.NoRotation) replies.AddRange(Stop(target, brain, facing, now));
        else brain.Moving = false;
        brain.Pending = null;
        brain.Volley.Clear();
        brain.ActionSeq = brain.ActionSeq % 255 + 1;
        var seq = brain.ActionSeq;
        var (id, skill) = cast;
        var yaw = target.NoRotation ? brain.Heading : facing;
        var at = CastLocation(target, brain, id, yaw);
        var rows = HitsOf(id, skill);
        var release = now + skill.Cast + HitDelayMs;
        var last = release + (rows.Count - 1) * MonsterHitStaggerMs;
        brain.Cooldowns[id] = now + Math.Max(skill.Cooldown, skill.Cast + HitDelayMs + rows.Count * MonsterHitStaggerMs + MonsterEndAfterLastHitMs);
        if (skill.Cast > 0)
            brain.Volley.Add(new(release - skill.Cast, ShotKind.CastingStart, id, seq, victim.Key, 0));
        brain.Volley.Add(new(release, ShotKind.SkillStart, id, seq, victim.Key, 0));
        var perHit = Math.Max(1, (int)Math.Round(Damage(target, skill) / (double)rows.Count));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var land = release + i * MonsterHitStaggerMs;
            if (row.Projectile > 0)
            {
                brain.Volley.Add(new(land, ShotKind.Fire, id, seq, victim.Key, i + 1, row.Projectile, MonsterProjectileFlightMs, 0, row.Effect));
                brain.Volley.Add(new(land, ShotKind.Collision, id, seq, victim.Key, i + 1, row.Projectile, 0, 0, row.Effect));
            }
            brain.Volley.Add(new(land + (row.Projectile > 0 ? MonsterProjectileFlightMs : 0), ShotKind.Hit, id, seq, victim.Key, i + 1,
                Damage: perHit, Effect: row.Effect, Groggy: row.Groggy));
            last = Math.Max(last, land + (row.Projectile > 0 ? MonsterProjectileFlightMs : 0));
        }
        brain.Volley.Add(new(last + MonsterEndAfterLastHitMs, ShotKind.End, id, seq, victim.Key, 0));
        brain.Volley.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
        brain.NextAttackMs = last + MonsterEndAfterLastHitMs + MonsterNextSkillMinMs + rng.Next(MonsterNextSkillMaxMs - MonsterNextSkillMinMs + 1);
        return replies;
    }

    List<byte[]> MonsterVolley(Target target, Brain brain, long now, Func<int, List<byte[]>>? hitPlayer)
    {
        if (target.OwnerKey != 0 || brain.Volley.Count == 0 || current is null) return [];
        var replies = new List<byte[]>();
        while (brain.Volley.Count > 0 && brain.Volley[0].DueMs <= now)
        {
            var shot = brain.Volley[0];
            brain.Volley.RemoveAt(0);
            var victimKey = shot.TargetKey != 0 ? shot.TargetKey : Victim(brain);
            if (shot.Kind == ShotKind.CastingStart)
            {
                if (current.TryGetValue(victimKey, out var v))
                    replies.Add(PlayerPackets.SkillCastingStart(target.Key, shot.SkillId, victimKey, brain.Heading, target.Position,
                        data!.NpcSkill(shot.SkillId)?.Cast ?? 0));
                continue;
            }
            if (shot.Kind == ShotKind.SkillStart)
            {
                var sk = data!.NpcSkill(shot.SkillId);
                replies.Add(GamePackets.SkillStart(shot.SkillId, shot.ActionSeq, brain.Heading, target.Position, targetKey: victimKey,
                    actorKey: target.Key, cooldown: sk?.Cooldown ?? 0));
                continue;
            }
            if (shot.Kind == ShotKind.End)
            {
                replies.Add(GamePackets.SkillEnd(target.Key, shot.SkillId, shot.ActionSeq));
                continue;
            }
            if (!current.TryGetValue(victimKey, out var victim) || victim.Dead) continue;
            var aim = victim.Position;
            if (shot.Kind == ShotKind.Fire)
            {
                replies.Add(PlayerPackets.SkillFireProjectile(target.Key, victimKey, shot.SkillId, (byte)shot.ActionSeq, shot.ProjectileId, aim, shot.FlightMs, shot.Seq));
                continue;
            }
            if (shot.Kind == ShotKind.Collision)
            {
                replies.Add(PlayerPackets.SkillProjectileCollision(target.Key, victimKey, shot.SkillId, (byte)shot.ActionSeq, shot.ProjectileId, aim, shot.Seq));
                continue;
            }
            if (shot.Kind != ShotKind.Hit) continue;
            var defense = brain.VictimIsNpc && current.GetValueOrDefault(victimKey) is { } prey ? prey.Defense : DefenseOf?.Invoke(victimKey) ?? 0;
            var damage = Math.Max(1, (int)Math.Round(shot.Damage * 1000.0 / (1000 + Math.Max(0, defense))));
            LastAttacker = target.Key;
            replies.Add(GamePackets.NpcSkillHit(victimKey, target.Key, shot.SkillId, shot.ActionSeq, shot.Effect, damage, effectSeq: shot.Seq));
            if (hitPlayer is not null) replies.AddRange(hitPlayer(damage));
            if (shot.Groggy > 0 && target.GroggyMax > 0)
            {
                target.GroggyEnergy = Math.Max(0, target.GroggyEnergy - shot.Groggy);
                replies.Add(GamePackets.UpdateGroggyInfo(target.Key, target.GroggyMax, target.GroggyEnergy, target.GroggyEnergy <= 0));
            }
        }
        return replies;
    }

    internal static void InitGroggy(Target target)
    {
        if (target.GroggyMax > 0) return;
        if (target.SubType is "HeroMonster" or "LegendMonster" || target.Grade >= 5)
        {
            target.GroggyMax = DefaultGroggyMax;
            target.GroggyEnergy = DefaultGroggyMax;
        }
    }

    internal static byte[]? GroggyAppear(Target target) =>
        target.GroggyMax > 0 ? GamePackets.UpdateGroggyInfo(target.Key, target.GroggyMax, target.GroggyEnergy) : null;

    internal static void ResetGroggy(Target target)
    {
        if (target.GroggyMax > 0) target.GroggyEnergy = target.GroggyMax;
    }
}
