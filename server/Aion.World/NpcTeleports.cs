using System.Text.Json;

namespace Aion.World;

/// <summary>Monster skills that move their caster (tools/npc_teleports.py -> tools/data/npc-teleports.json), loaded once.</summary>
public static class NpcTeleports
{
    /// <summary>A caster move; Spot is the fixed destination of a MapLocation_MapDirection leap (Angle is then the map direction).</summary>
    public sealed record Leap(string Anchor, double Distance, double Angle, double TimeMs, Aion.Protocol.Native.Vector3d? Spot = null);

    static readonly Lazy<Dictionary<int, Leap>> table = new(() =>
    {
        using var stream = typeof(NpcTeleports).Assembly.GetManifestResourceStream("npc-teleports.json")
            ?? throw new InvalidOperationException("Missing embedded game data npc-teleports.json");
        using var doc = JsonDocument.Parse(stream);
        var skills = new Dictionary<int, Leap>();
        foreach (var p in doc.RootElement.GetProperty("skills").EnumerateObject())
        {
            var v = p.Value;
            skills[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] =
                new Leap(v[0].GetString() ?? "", v[1].GetDouble(), v[2].GetDouble(), v[3].GetDouble(),
                    v.GetArrayLength() >= 7 ? new Aion.Protocol.Native.Vector3d(v[4].GetDouble(), v[5].GetDouble(), v[6].GetDouble()) : null);
        }
        return skills;
    });

    public static Leap? Of(int skill) => table.Value.GetValueOrDefault(skill);
}
