using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// What a monster skill does to its targets (tools/npc_skill_effects.py -> tools/data/npc-skill-effects.json, OFFICIAL client
/// tables Skill / SkillEffect / SkillEffectFilter): who it names as its target (Self skills name the caster), the area its hit
/// covers and how many it hits, and the abnormal rows it applies (effect id, abnormal, level, duration, chance).
/// </summary>
public static class NpcSkillEffects
{
    /// <summary>The hit's area: Circle / Arc (Radius, full Angle) / Rectangle (Length forward, Width); Max targets; centred on the aimed target or the caster.</summary>
    public sealed record Area(string Shape, double Radius, double Angle, double Length, double Width, double Height, int Max, bool NeedTarget);

    /// <summary>An abnormal row: the SkillEffect id the official abnormal hit (0x3804 hit type 6) names, the abnormal, its level, duration and chance.</summary>
    public sealed record AbnormalRow(int EffectId, int AbnormalId, int Level, int MinMs, int MaxMs, int Chance);

    public sealed record Entry(string Target, Area? Area, IReadOnlyList<AbnormalRow> Abnormals)
    {
        public bool SelfTarget => Target == "Self";
    }

    static double D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    static readonly Lazy<Dictionary<int, Entry>> table = new(() =>
    {
        using var stream = typeof(NpcSkillEffects).Assembly.GetManifestResourceStream("npc-skill-effects.json")
            ?? throw new InvalidOperationException("Missing embedded game data npc-skill-effects.json");
        using var doc = JsonDocument.Parse(stream);
        var skills = new Dictionary<int, Entry>();
        foreach (var p in doc.RootElement.GetProperty("skills").EnumerateObject())
        {
            var v = p.Value;
            Area? area = null;
            if (v.TryGetProperty("area", out var a))
                area = new Area(a.GetProperty("shape").GetString() ?? "", D(a, "r"), D(a, "angle"), D(a, "len"), D(a, "w"), D(a, "h"),
                    (int)D(a, "max"), a.TryGetProperty("need_target", out var nt) && nt.ValueKind == JsonValueKind.True);
            var rows = v.TryGetProperty("abnormals", out var ab)
                ? ab.EnumerateArray().Select(r => new AbnormalRow(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32(), r[4].GetInt32(), r[5].GetInt32())).ToArray()
                : [];
            skills[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = new Entry(v.GetProperty("target").GetString() ?? "", area, rows);
        }
        return skills;
    });

    public static Entry? Of(int skill) => table.Value.GetValueOrDefault(skill);
    public static int Count => table.Value.Count;

    /// <summary>
    /// Whether a point is inside the skill's area: centred on <paramref name="centre"/> facing <paramref name="heading"/> degrees,
    /// widened by the target's body (PlayerRadius) and the caster's (<paramref name="casterRadius"/>, the client measures from the
    /// collision edges as the skill ranges do: CANDIDATE). Height within Height + body height (CANDIDATE: 200).
    /// </summary>
    public static bool Contains(Area area, Vector3d centre, double heading, Vector3d point, double casterRadius)
    {
        double dx = point.X - centre.X, dy = point.Y - centre.Y;
        if (Math.Abs(point.Z - centre.Z) > Math.Max(area.Height, 0) + 200) return false;
        var body = NpcAi.PlayerRadius + casterRadius;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        switch (area.Shape)
        {
            case "Circle":
                return distance <= area.Radius + body;
            case "Arc":
                if (distance > area.Radius + body) return false;
                if (distance <= body) return true;
                var bearing = Math.Atan2(dy, dx) * 180 / Math.PI;
                var delta = Math.Abs(GamePackets.PyMod(bearing - heading + 180, 360) - 180);
                return delta <= area.Angle / 2;
            case "Rectangle":
                var rad = heading * Math.PI / 180;
                double along = dx * Math.Cos(rad) + dy * Math.Sin(rad), side = -dx * Math.Sin(rad) + dy * Math.Cos(rad);
                return along >= -body && along <= area.Length + body && Math.Abs(side) <= area.Width / 2 + NpcAi.PlayerRadius;
            default:
                return false;
        }
    }
}
