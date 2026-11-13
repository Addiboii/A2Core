using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill authority (Stage C). Already checked in CastReplies before this: the skill exists, the caster is alive and in the world,
// learned (or an offered chain / releasing charge), the weapon type, the cooldown (with its charge group), MP/SP/HP cost, timed
// charges; cast rate is logged (TrackCastRate); charge starts need a learned charge skill off cooldown. Added here, from the
// client's own Skill table (tools/data/skill-use.json, tools/skill_use.py):
//   - use range: the aimed target (monster, or a player on the same map) must be within NeedSkillUseRange of the caster's
//     server position. Far beyond (RangeRefuseSlackCm: latency, both moving, weapon reach) the cast is refused with
//     Skill_TooFar and logged; a little beyond (RangeLogSlackCm) only logged;
//   - height range and line of sight on the navmesh: logged only (the floor mesh is not the real wall geometry, cliffs and
//     stairs fool a floor line);
//   - the cast location of a self-centred cast: the server picks the targets around the caster's server position, never
//     around a point the client names far from it (a location beyond SelfLocationSlackCm is replaced and logged).
// Not checked (the server does not track it yet): stuns/silence on players, moving while casting.
public sealed partial class GameSession
{
    /// <summary>A player skill's use limits (client Table_Skill): range and height range in cm, cooldown group, target kind.</summary>
    internal readonly record struct SkillUse(int Range, int Height, int Group, int Kind);

    public const double RangeLogSlackCm = 400, RangeRefuseSlackCm = 1200, SelfLocationSlackCm = 1500, HeightLogSlackCm = 800;

    static readonly Lazy<Dictionary<int, SkillUse>> SkillUses = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("skill-use.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-use.json");
        using var doc = JsonDocument.Parse(stream);
        var uses = new Dictionary<int, SkillUse>();
        foreach (var row in doc.RootElement.GetProperty("skills").EnumerateObject())
        {
            var v = row.Value;
            uses[int.Parse(row.Name)] = new SkillUse(v[0].GetInt32(), v[1].GetInt32(), v[2].GetInt32(), v[3].GetInt32());
        }
        return uses;
    });

    internal static SkillUse? SkillUseOf(int skill) => SkillUses.Value.TryGetValue(skill, out var use) ? use : null;

    /// <summary>The aimed target's server position and body radius: a monster/NPC of this map, or another player on it.</summary>
    (Vector3d At, double Radius, bool Player)? AimedTarget(int key)
    {
        if (key == playerKey) return null;
        if (targets is not null && targets.TryGetValue(key, out var t)) return (t.Position, t.Radius, false);
        if (key >= SharedWorld.FirstPlayerKey && worldPlayer?.Map is { } map && map.Players.TryGetValue(key, out var other))
            return (other.Position, 50, true);
        return null;
    }

    /// <summary>
    /// The server's checks of an accepted-looking cast: null to go on (with the request, possibly corrected), else the refusal.
    /// </summary>
    List<byte[]>? CheckCastAuthority(ref CastRequest request, long now)
    {
        // Before the first position report after a load the server knows the load point.
        if (IsAdmin || ServerPosition is not { } caster) return null;
        var skill = request.SkillId;
        // Self-centred: an absolute location must be where the caster is. OBSERVED (official 0x3802 echoes, 25 recordings): the
        // dodges (x000100/x000101) and some self buffs carry a short relative vector (~100 cm) instead, so a location that is
        // itself shorter than the slack is never corrected.
        if (request.TargetKey == playerKey || request.TargetKey < 1)
        {
            double sx = request.Location.X - caster.X, sy = request.Location.Y - caster.Y;
            var length = Math.Sqrt(request.Location.X * request.Location.X + request.Location.Y * request.Location.Y + request.Location.Z * request.Location.Z);
            if (Math.Sqrt(sx * sx + sy * sy) > SelfLocationSlackCm && length > SelfLocationSlackCm)
            {
                Suspect("skill_location", new { skill, offset = (int)Math.Sqrt(sx * sx + sy * sy) }, now);
                request = request with { Location = caster };
            }
            return null;
        }
        if (SkillUseOf(skill) is not { Range: > 0 } use || AimedTarget(request.TargetKey) is not { } aimed) return null;
        double dx = aimed.At.X - caster.X, dy = aimed.At.Y - caster.Y;
        var flat = Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - aimed.Radius);
        // A charge release is aimed when the charge started, seconds earlier (the target walked on): logged, not refused.
        if (flat > use.Range + RangeRefuseSlackCm && !ChargeReleasing(skill))
        {
            Suspect("skill_range", new { skill, distance = (int)flat, range = use.Range, refused = true, pvp = aimed.Player }, now);
            return [GamePackets.CastResult(skill, EResult.Skill_TooFar)];
        }
        if (flat > use.Range + RangeLogSlackCm) Suspect("skill_range", new { skill, distance = (int)flat, range = use.Range, refused = false, pvp = aimed.Player }, now);
        if (use.Height > 0 && Math.Abs(aimed.At.Z - caster.Z) > use.Height + HeightLogSlackCm)
            Suspect("skill_height", new { skill, height = (int)Math.Abs(aimed.At.Z - caster.Z), limit = use.Height }, now);
        // Line of sight on the floor mesh, logged only: beyond melee reach, both ends on the mesh, and the floor line blocked.
        if (ai.Nav is { } mesh && flat > 600 && !mesh.LineOfSight(caster, aimed.At) && !AreaVisibleFrom(mesh, caster, aimed.At, aimed.Radius))
            Suspect("skill_sight", new { skill, distance = (int)flat }, now);
        return null;
    }

    /// <summary>The floor walk from the caster towards the target reaches its body (a hill or a mesh gap is not a wall).</summary>
    static bool AreaVisibleFrom(NavMesh mesh, Vector3d from, Vector3d to, double radius)
    {
        var reached = mesh.MoveAlong(from, to);
        double rx = to.X - reached.X, ry = to.Y - reached.Y;
        return Math.Sqrt(rx * rx + ry * ry) <= radius + 100;
    }
}
