using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Skill projectile flights, from tools/data/projectiles.json (tools/projectile_data.py, SkillProjectile.dat of build 26262663).
/// The announced flight is the 0x3803 remaining time; the hit lands one tick after it. OBSERVED (our recordings 2026-10-13 to
/// 11-06, every 0x3803): Splash_FixTime(_Target) flies ArrivalFixTimeMin..Max (1068 of 1068), Collide CollideMoveValue01 / Speed
/// (597 of 597), all flights in 50 ms steps, and the hit (0x3804) lands 0-99 ms after the announced flight (2456 of ~2800
/// fire-hit pairs). CANDIDATE: Target and the VariableTime types fly the distance / Speed (their times spread as distances do).
/// Found by the community fork.
/// </summary>
public static class Projectiles
{
    /// <summary>The flight step and the hit's delay after the announced arrival (OBSERVED, above).</summary>
    public const int TickMs = 50;

    public sealed record Row(string Type, int Speed, int FixMin, int FixMax, int CollideTravel);

    static readonly Lazy<Dictionary<int, Row>> rows = new(() =>
    {
        using var stream = typeof(Projectiles).Assembly.GetManifestResourceStream("projectiles.json");
        if (stream is null) return [];
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("projectiles").EnumerateObject().ToDictionary(
            p => int.Parse(p.Name, CultureInfo.InvariantCulture),
            p => new Row(p.Value[0].GetString() ?? "None", p.Value[1].GetInt32(), p.Value[2].GetInt32(), p.Value[3].GetInt32(), p.Value[4].GetInt32()));
    });

    public static Row? Of(int projectile) => rows.Value.GetValueOrDefault(projectile);

    /// <summary>
    /// A projectile's flight over <paramref name="distance"/>: the announced time (0x3803) and the hit's arrival, one tick
    /// later; null when the projectile is unknown or cannot move.
    /// </summary>
    public static (int Announced, int Arrival)? Flight(int projectile, double distance, Random rng)
    {
        if (Of(projectile) is not { } row) return null;
        int announced;
        if (row.Type is "Splash_FixTime" or "Splash_FixTime_Target" && row.FixMax > 0)
        {
            var min = Math.Min(row.FixMin, row.FixMax) / TickMs;
            announced = rng.Next(min, Math.Max(row.FixMin, row.FixMax) / TickMs + 1) * TickMs;
        }
        else if (row.Speed <= 0) return null;
        else
        {
            var travel = row.Type == "Collide" && row.CollideTravel > 0 ? row.CollideTravel : distance;
            announced = (int)Math.Min(int.MaxValue / 2, travel * 1000 / row.Speed) / TickMs * TickMs;
        }
        announced = Math.Max(TickMs, announced);
        return (announced, announced + TickMs);
    }
}
