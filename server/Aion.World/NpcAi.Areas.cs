using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// An NPC's area skill (a SkillEffectFilter shape in tools/data/skill-areas.json: Circle, Arc, Rectangle, ... with
/// TargetCountMax and bEffectRangeNeedTarget): the 0x3802 names the shape's centre (the victim for on-target filters, else
/// the caster) as the player's ground casts do, and when the hit lands every target inside the shape gets its own 0x3804
/// (switch 06, the NPC hit form), nearest first, up to TargetCountMax.
/// INFERRED (no official capture of a monster area hit): the shape is placed at the cast and tested at the hit, so a player who
/// leaves the telegraphed area is missed; a monster's area hits players, a friendly NPC's or summon's hits monsters; each
/// target's damage is the skill's own roll reduced by that target's defense.
/// </summary>
public sealed partial class NpcAi
{
    /// <summary>The placed shape of each NPC's pending area cast: centre and facing at the 0x3802.</summary>
    readonly Dictionary<int, (Vector3d Centre, double Yaw)> areaCasts = [];
    IReadOnlyList<(int Key, Vector3d Position)> currentPlayers = [];
    Func<int, int, List<byte[]>>? currentHitPlayer;

    /// <summary>The 0x3802 location: an area skill's centre (recorded for its hit), else the caster.</summary>
    Vector3d CastLocation(Target caster, Brain brain, int skillId, double yaw)
    {
        areaCasts.Remove(caster.Key);
        if (SkillAreas.For(skillId) is not { IsArea: true } area) return caster.Position;
        // A GroundType Location skill lands where it is aimed: the victim's spot at the cast (INFERRED for NPCs).
        var centre = (area.OnTarget || area.Ground == "Location") && VictimPosition(brain) is { } at ? at : caster.Position;
        areaCasts[caster.Key] = (centre, yaw);
        return centre;
    }

    Vector3d? VictimPosition(Brain brain)
    {
        var key = Victim(brain);
        if (brain.VictimIsNpc) return current?.GetValueOrDefault(key)?.Position;
        foreach (var p in currentPlayers)
            if (p.Key == key) return p.Position;
        return null;
    }

    /// <summary>
    /// The landing area cast's hits on everyone else in its shape, and whether the victim is still in it (true for a skill
    /// without a shape).
    /// </summary>
    (List<byte[]> Hits, bool VictimInside) Splash(Target caster, PendingHit hit, int victimKey, Vector3d victimAt, double victimRadius, long now)
    {
        if (!areaCasts.Remove(caster.Key, out var cast) || SkillAreas.For(hit.SkillId) is not { IsArea: true } area) return ([], true);
        var inside = area.Contains(cast.Centre, cast.Yaw, victimAt, victimRadius);
        var others = inside ? area.Max - 1 : area.Max;
        var replies = new List<byte[]>();
        if (caster.Hostile)
        {
            if (currentHitPlayer is null) return (replies, inside);
            foreach (var (key, _) in currentPlayers
                         .Where(p => p.Key != victimKey && area.Contains(cast.Centre, cast.Yaw, p.Position, PlayerRadius))
                         .OrderBy(p => p.Position.DistanceSquared(cast.Centre)).Take(others).ToList())
            {
                var damage = Mitigated(caster, hit.Skill, DefenseOf?.Invoke(key) ?? 0);
                replies.Add(GamePackets.NpcSkillHit(key, caster.Key, hit.SkillId, hit.ActionSeq, hit.Skill.Effect, damage));
                replies.AddRange(currentHitPlayer(key, damage));
            }
            return (replies, inside);
        }
        if (current is null) return (replies, inside);
        foreach (var t in current.Values
                     .Where(t => t.Key != victimKey && t.Key != caster.Key && t.Hostile && !t.Dead && !t.CorpseRemoved
                         && area.Contains(cast.Centre, cast.Yaw, t.Position, t.Radius))
                     .OrderBy(t => t.Position.DistanceSquared(cast.Centre)).Take(others).ToList())
        {
            var damage = Mitigated(caster, hit.Skill, t.Defense);
            replies.Add(GamePackets.NpcSkillHit(t.Key, caster.Key, hit.SkillId, hit.ActionSeq, hit.Skill.Effect, damage));
            replies.AddRange(HitNpc(current, t.Key, caster, damage, now));
        }
        return (replies, inside);
    }

    int Mitigated(Target caster, NpcSkill skill, int defense) =>
        Math.Max(1, (int)Math.Round(Damage(caster, skill) * 1000.0 / (1000 + Math.Max(0, defense))));
}
