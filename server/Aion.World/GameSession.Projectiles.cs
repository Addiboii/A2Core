using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// When a projectile skill's shots fire and how long they fly (tools/projectile_timing.py → tools/data/projectile-timings.json,
/// OBSERVED for 22 recorded skills). Other skills with projectiles in the client table (skill-behaviour.json) use the medians
/// (PROVISIONAL), one shot per projectile id with seq 1...
/// </summary>
public sealed class ProjectileTimings
{
    public sealed record Shot([property: JsonPropertyName("projectile")] int Projectile, [property: JsonPropertyName("seq")] int Seq,
        [property: JsonPropertyName("fire_ms")] int? FireMs, [property: JsonPropertyName("flight_ms")] int FlightMs);

    sealed record FileShape([property: JsonPropertyName("default_fire_ms")] int DefaultFireMs, [property: JsonPropertyName("default_flight_ms")] int DefaultFlightMs,
        [property: JsonPropertyName("skills")] Dictionary<int, Shot[]> Skills);

    readonly FileShape file;
    ProjectileTimings(FileShape file) => this.file = file;

    static readonly Lazy<ProjectileTimings> embedded = new(() =>
    {
        using var stream = typeof(ProjectileTimings).Assembly.GetManifestResourceStream("projectile-timings.json")
            ?? throw new InvalidOperationException("Missing embedded game data projectile-timings.json");
        return new ProjectileTimings(JsonSerializer.Deserialize<FileShape>(stream) ?? throw new InvalidDataException("projectile-timings.json: empty"));
    });

    public static ProjectileTimings Embedded => embedded.Value;

    /// <summary>The shots of a skill (fire time after the start, flight), empty for a skill without projectiles.</summary>
    public IReadOnlyList<(int Projectile, int Seq, int FireMs, int FlightMs)> Shots(int skill)
    {
        if (file.Skills.TryGetValue(skill, out var recorded))
            return recorded.Select(s => (s.Projectile, s.Seq, s.FireMs ?? file.DefaultFireMs, s.FlightMs)).ToList();
        return (SkillBehaviour.Embedded.Of(skill)?.Projectiles ?? []).Select((p, i) => (p, i + 1, file.DefaultFireMs, file.DefaultFlightMs)).ToList();
    }
}

// Projectile skills (arrows, spells): each shot's 0x3803 at its fire time, to the caster and those around, and the skill's hits when
// the first shot lands (fire + flight), as official (Flame Arrow: start, 250 ms fire, hit ~200 ms later). Before 2026-11-07 the hits
// came with the start and no projectile was sent, so the client drew nothing flying.
public sealed partial class GameSession
{
    static ProjectileTimings Projectiles => ProjectileTimings.Embedded;

    /// <summary>Schedules the shots and the hits of a projectile skill; false when the skill has no projectiles (hits at once).</summary>
    bool ScheduleProjectiles(int skill, int action, CastRequest request, long now)
    {
        var shots = Projectiles.Shots(skill);
        // A projectile skill without recorded timings (e.g. Deadshot's levels 14010001-3; official recorded 14010241-3): one shot
        // at once, landing 50 ms later (official Deadshot: 0x3803 with the release, the hits ~54 ms after). PROVISIONAL.
        if (shots.Count == 0 && Behaviour.Of(skill)?.Projectiles is [var only, ..])
            shots = [(only, 1, 0, 50)];
        if (shots.Count == 0) return false;
        var at = targets is not null && targets.TryGetValue(request.TargetKey, out var target) ? target.Position : request.Location;
        foreach (var (projectile, seq, fireMs, flightMs) in shots)
            Schedule(now + fireMs, _ =>
            {
                var fired = GamePackets.FireProjectile(playerKey, request.TargetKey, skill, action, projectile, at, flightMs, seq);
                worldPlayer?.Map?.Broadcast(worldPlayer, [fired]);
                return [fired];
            });
        var landsAt = now + shots.Min(s => s.FireMs + s.FlightMs);
        Schedule(landsAt, later =>
        {
            var hits = CombatReplies(skill, request, later, action);
            worldPlayer?.Map?.Broadcast(worldPlayer, hits.Where(SharedCastPacket).ToList());
            if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
            return hits;
        });
        return true;
    }
}
