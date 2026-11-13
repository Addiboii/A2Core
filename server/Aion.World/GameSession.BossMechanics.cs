using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Boss mechanics (gamedata/provisional/boss-mechanics.xml): boss skills that call helper casters (Stage 6 data).</summary>
public sealed class BossMechanicTable
{
    public sealed record Helpers(int Skill, int Npc, int HelperSkill, int Projectile, int Effect, int Count, int Waves, int WaveGapMs,
        double RadiusMin, double RadiusStep, double RadiusJitter, int AimMs, int CastMs, double Range, int FlightMs, double HitWidth,
        int RatioMin, int RatioMax, int Hp, int LifeMs, int GoneMs);

    readonly Dictionary<int, Helpers> onCast;
    BossMechanicTable(Dictionary<int, Helpers> onCast) => this.onCast = onCast;

    static readonly Lazy<BossMechanicTable> embedded = new(() =>
    {
        using var stream = typeof(BossMechanicTable).Assembly.GetManifestResourceStream("provisional/boss-mechanics.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/boss-mechanics.xml");
        return Parse(XDocument.Load(stream));
    });

    public static BossMechanicTable Embedded => embedded.Value;

    public static BossMechanicTable Parse(XDocument document)
    {
        static int I(XElement e, string n) => int.Parse((string?)e.Attribute(n) ?? "0", CultureInfo.InvariantCulture);
        static double D(XElement e, string n) => double.Parse((string?)e.Attribute(n) ?? "0", CultureInfo.InvariantCulture);
        var rows = (document.Root?.Elements("on_cast") ?? []).Select(e => new Helpers(I(e, "skill"), I(e, "npc"), I(e, "helper_skill"),
            I(e, "projectile"), I(e, "effect"), Math.Clamp(I(e, "count"), 1, 8), Math.Clamp(I(e, "waves"), 1, 8), I(e, "wave_gap_ms"),
            D(e, "radius_min"), D(e, "radius_step"), D(e, "radius_jitter"), I(e, "aim_ms"), Math.Max(0, I(e, "cast_ms")), D(e, "range"),
            Math.Max(100, I(e, "flight_ms")), D(e, "hit_width"), I(e, "ratio_min"), I(e, "ratio_max"), Math.Max(1, I(e, "hp")),
            I(e, "life_ms"), I(e, "gone_ms")));
        return new BossMechanicTable(rows.ToDictionary(r => r.Skill));
    }

    public Helpers? OnCast(int skill) => onCast.GetValueOrDefault(skill);
}

// A boss's helper wave (the Lake Guardian's roar): each helper appears (0x3641), targets the player (0x3835, 0x8d21), shows its
// cast bar at the player (0x3809), then starts (0x3802), fires its projectile 11000 cm along that line (0x3803) and ends (0x3806).
// The shot is followed in 100 ms steps: a wall stops it (0x380e), the player within hit_width of its path is hit (0x3804 and the
// player's damage). At life_ms the helper's HP drops to 0 and it leaves combat, at gone_ms it vanishes (0x3642 flag 3), as official.
// Solo copies only (the per-session AI); a shared map's bosses do not call helpers yet.
public sealed partial class GameSession
{
    static BossMechanicTable BossMechanics => BossMechanicTable.Embedded;
    const int HelperKeyBase = 0x0F0000;
    const int ProjectileStepMs = 100;
    int helperKeys;
    /// <summary>Helpers alive (for the checks).</summary>
    readonly HashSet<int> helpers = [];

    List<byte[]> BossMechanicTick(NpcAi brainOwner, long now)
    {
        var replies = new List<byte[]>();
        foreach (var (caster, skill, _) in brainOwner.TakeCasts())
        {
            if (BossMechanics.OnCast(skill) is not { } m) continue;
            for (var wave = 0; wave < m.Waves; wave++)
            {
                var w = wave;
                if (w == 0) replies.AddRange(HelperWave(brainOwner, caster, m, w, now));
                else Schedule(now + w * m.WaveGapMs, later => HelperWave(brainOwner, caster, m, w, later));
            }
        }
        return replies;
    }

    List<byte[]> HelperWave(NpcAi brainOwner, Target boss, BossMechanicTable.Helpers m, int wave, long now)
    {
        if (boss.Dead || dead || position is null) return [];
        var replies = new List<byte[]>();
        for (var i = 0; i < m.Count; i++)
        {
            var key = HelperKeyBase + (helperKeys = helperKeys % 0xFFFF + 1);
            var angle = Random.Shared.NextDouble() * Math.PI * 2;
            var radius = m.RadiusMin + wave * m.RadiusStep + Random.Shared.NextDouble() * m.RadiusJitter;
            var at = new Vector3d(boss.Position.X + Math.Cos(angle) * radius, boss.Position.Y + Math.Sin(angle) * radius, boss.Position.Z);
            if (brainOwner.Nav?.FloorAt(at.X, at.Y, at.Z, 500) is { } floor) at = at with { Z = floor };
            var heading = position is { } p ? Math.Atan2(p.Y - at.Y, p.X - at.X) * 180 / Math.PI : 0;
            helpers.Add(key);
            replies.Add(GamePackets.NpcAppear(new NpcAppearance(key, m.Npc, at, heading, m.Hp, m.Hp, boss.Level, boss.Relationship, false, false, Monster: true)));
            Schedule(now + 50, _ => [GamePackets.TargetUpdate(key, playerKey), GamePackets.CombatState(key, true)]);
            Schedule(now + m.AimMs, aimNow => HelperAim(brainOwner, boss, m, key, at, aimNow));
            Schedule(now + m.LifeMs, _ => [GamePackets.NpcHp(key, 0), GamePackets.TargetUpdate(key, 0), GamePackets.CombatState(key, false)]);
            Schedule(now + m.GoneMs, _ => helpers.Remove(key) ? [GamePackets.Death(key, 3)] : []);
        }
        return replies;
    }

    List<byte[]> HelperAim(NpcAi brainOwner, Target boss, BossMechanicTable.Helpers m, int key, Vector3d from, long now)
    {
        if (!helpers.Contains(key) || position is not { } aim || dead) return [];
        var dx = aim.X - from.X; var dy = aim.Y - from.Y;
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var (ux, uy) = (dx / length, dy / length);
        var yaw = Math.Atan2(dy, dx) * 180 / Math.PI;
        var end = new Vector3d(from.X + ux * m.Range, from.Y + uy * m.Range, from.Z);
        Schedule(now + m.CastMs, fireNow =>
        {
            if (!helpers.Contains(key)) return [];
            var fired = new List<byte[]>
            {
                GamePackets.SkillStart(m.HelperSkill, 1, yaw, aim, targetKey: playerKey, actorKey: key),
                GamePackets.FireProjectile(key, playerKey, m.HelperSkill, 1, m.Projectile, end, m.FlightMs, 1),
                GamePackets.SkillEnd(key, m.HelperSkill, 1),
            };
            ScheduleShotStep(brainOwner, boss, m, key, from, (ux, uy), fireNow, fireNow);
            return fired;
        });
        return [GamePackets.SkillCastingStart(key, m.HelperSkill, playerKey, yaw, aim, m.CastMs)];
    }

    void ScheduleShotStep(NpcAi brainOwner, Target boss, BossMechanicTable.Helpers m, int key, Vector3d from, (double X, double Y) dir, long firedAt, long now) =>
        Schedule(now + ProjectileStepMs, stepNow => ShotStep(brainOwner, boss, m, key, from, dir, firedAt, stepNow));

    /// <summary>One 100 ms step of a helper's shot: from the last point to the new one, a wall stops it, the player on the path is hit.</summary>
    List<byte[]> ShotStep(NpcAi brainOwner, Target boss, BossMechanicTable.Helpers m, int key, Vector3d from, (double X, double Y) dir, long firedAt, long now)
    {
        var speed = m.Range / m.FlightMs;   // cm per ms
        double Along(long t) => Math.Min(m.Range, Math.Max(0, t - firedAt) * speed);
        var a = Along(now - ProjectileStepMs); var b = Along(now);
        var p0 = new Vector3d(from.X + dir.X * a, from.Y + dir.Y * a, from.Z);
        var p1 = new Vector3d(from.X + dir.X * b, from.Y + dir.Y * b, from.Z);
        // The player within hit_width of this step's segment (and about level with it) is hit; the shot ends there.
        if (position is { } p && !dead && Math.Abs(p.Z - from.Z) < 400)
        {
            var along = Math.Clamp((p.X - from.X) * dir.X + (p.Y - from.Y) * dir.Y, a, b);
            var cx = from.X + dir.X * along; var cy = from.Y + dir.Y * along;
            if ((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= (m.HitWidth + NpcAi.PlayerRadius) * (m.HitWidth + NpcAi.PlayerRadius))
            {
                var damage = Math.Max(1, (int)Math.Round(brainOwner.SkillDamage(boss, m.RatioMin, m.RatioMax) * 1000.0 / (1000 + Math.Max(0, Defense))));
                return [GamePackets.NpcSkillHit(playerKey, key, m.HelperSkill, 1, m.Effect, damage), .. NpcHitsPlayer(damage, now, key, m.HelperSkill)];
            }
        }
        if (brainOwner.Nav is { } nav && !nav.LineOfSight(p0 with { Z = p0.Z + 100 }, p1 with { Z = p1.Z + 100 }))
            return [GamePackets.ProjectileStop(key, m.HelperSkill, 1, m.Projectile, p1)];
        if (b < m.Range) ScheduleShotStep(brainOwner, boss, m, key, from, dir, firedAt, now);
        return [];
    }

    internal int HelpersForChecks => helpers.Count;
    internal List<byte[]> BossMechanicTickForChecks(long now) => BossMechanicTick(ai, now);
}

public sealed partial class GameSession
{
    /// <summary>Checks: one wave of the given boss's on-cast helpers, as its cast of `skill` would start it.</summary>
    internal List<byte[]> HelperWaveForChecks(Target boss, int skill, long now) =>
        BossMechanics.OnCast(skill) is { } m ? HelperWave(ai, boss, m, 0, now) : [];
}
