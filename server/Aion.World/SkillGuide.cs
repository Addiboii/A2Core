using System.Text.Json;

namespace Aion.World;

/// <summary>
/// The class skill guide (tools/data/skill-guide.json, tools/skill_guide.py): per skill what the game's skill text says the
/// server must do, beyond the client data we have (Skill.dat damage rows, abnormals): how many enemies it hits and in what area
/// (around the caster, around the target, along a dash path), chance effects, debuffs, damage over time, lifesteal, MP restore.
/// Source: the shugo.gg class skill books (research/shugo-skills). Missing skills simply have no guide entry.
/// </summary>
public sealed class SkillGuide
{
    public sealed record Effect(string Name, int Chance, int DurationMs, string? When);

    public sealed record Entry(int Id, string Name, int Targets, string? Shape, double RadiusCm, double LengthCm, double RangeCm,
        IReadOnlyList<Effect> Effects, string? RequiresTarget, int Repeat, int LifestealPercent, int MpRestore,
        IReadOnlyDictionary<string, double> DebuffStats, int DebuffMs, int DotMs, int DotIntervalMs = 1000,
        IReadOnlyList<int>? DotAmountsByLevel = null, string? Element = null, IReadOnlyList<int>? DamageMinByLevel = null, string? DamageType = null,
        int RatioPercent = 0)
    {
        public IReadOnlyList<int> DotAmounts => DotAmountsByLevel ?? [];
        public IReadOnlyList<int> DamageByLevel => DamageMinByLevel ?? [];

        /// <summary>How much more (or less) the skill hits at <paramref name="level"/> than at level 1, from its own per-level
        /// damage table (1.0 when the table is missing or has one row).</summary>
        public double LevelScale(int level)
        {
            if (DamageByLevel.Count < 2) return 1.0;
            var at = DamageByLevel[Math.Clamp(level, 1, DamageByLevel.Count) - 1];
            var one = DamageByLevel[0];
            return one > 0 ? (double)at / one : 1.0;
        }
    }

    readonly Dictionary<int, Entry> skills;
    SkillGuide(Dictionary<int, Entry> skills) => this.skills = skills;

    static readonly Lazy<SkillGuide> embedded = new(() =>
    {
        using var stream = typeof(SkillGuide).Assembly.GetManifestResourceStream("skill-guide.json");
        return stream is null ? new SkillGuide([]) : Parse(JsonDocument.Parse(stream));
    });

    public static SkillGuide Embedded => embedded.Value;
    public Entry? Of(int skill) => skills.GetValueOrDefault(skill);
    public int Count => skills.Count;

    public static SkillGuide Parse(JsonDocument doc)
    {
        var all = new Dictionary<int, Entry>();
        foreach (var p in doc.RootElement.GetProperty("skills").EnumerateObject())
        {
            if (!int.TryParse(p.Name, out var id)) continue;
            var s = p.Value;
            static double D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            static int I(JsonElement e, string name) => (int)D(e, name);
            string? shape = null; double radius = 0, length = 0;
            if (s.TryGetProperty("area", out var area) && area.ValueKind == JsonValueKind.Object)
            {
                shape = area.TryGetProperty("shape", out var sh) ? sh.GetString() : null;
                radius = D(area, "radius_m") * 100;
                length = D(area, "length_m") * 100;
            }
            var effects = new List<Effect>();
            if (s.TryGetProperty("effects", out var ef) && ef.ValueKind == JsonValueKind.Array)
                foreach (var e in ef.EnumerateArray())
                    effects.Add(new Effect(e.GetProperty("effect").GetString() ?? "", I(e, "chance"), I(e, "duration_ms"),
                        e.TryGetProperty("when", out var w) ? w.GetString() : null));
            var debuff = new Dictionary<string, double>();
            var debuffMs = 0;
            if (s.TryGetProperty("debuff", out var db) && db.ValueKind == JsonValueKind.Object)
            {
                debuffMs = I(db, "duration_ms");
                if (db.TryGetProperty("stats", out var st)) foreach (var x in st.EnumerateObject()) debuff[x.Name] = x.Value.GetDouble();
            }
            List<int>? dmgLevels = null;
            var ratioPct = 0;
            if (s.TryGetProperty("damage", out var dmg) && dmg.ValueKind == JsonValueKind.Object)
            {
                if (dmg.TryGetProperty("min_by_level", out var ml) && ml.ValueKind == JsonValueKind.Array)
                    dmgLevels = ml.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList();
                if (dmg.TryGetProperty("ratio_percent", out var rp) && rp.ValueKind == JsonValueKind.Array && rp.GetArrayLength() > 0)
                    ratioPct = (int)rp[0].GetDouble();
            }
            var dotMs = 0; var dotInterval = 1000; List<int>? dotAmounts = null;
            if (s.TryGetProperty("dot", out var dot) && dot.ValueKind == JsonValueKind.Object)
            {
                dotMs = I(dot, "duration_ms");
                if (I(dot, "interval_ms") > 0) dotInterval = I(dot, "interval_ms");
                if (dot.TryGetProperty("amount_by_level", out var am) && am.ValueKind == JsonValueKind.Array) dotAmounts = am.EnumerateArray().Select(x => x.GetInt32()).ToList();
            }
            all[id] = new Entry(id, s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", I(s, "targets"), shape, radius, length,
                D(s, "range_m") * 100, effects, s.TryGetProperty("requires_target", out var rq) ? rq.GetString() : null,
                Math.Max(1, I(s, "repeat")), I(s, "lifesteal_percent"), I(s, "mp_restore"), debuff, debuffMs, dotMs, dotInterval, dotAmounts,
                s.TryGetProperty("element", out var el) ? el.GetString() : null, dmgLevels,
                s.TryGetProperty("damage_type", out var dt) ? dt.GetString() : null, ratioPct);
        }
        return new SkillGuide(all);
    }
}

public sealed partial class GameSession
{
    static SkillGuide Guide => SkillGuide.Embedded;

    /// <summary>Path skills hit this far to either side of the dash line (OURS: the client's width is not in our data).</summary>
    public const double PathHalfWidth = 150;

    /// <summary>
    /// The monsters an area skill hits (skill guide: "up to N enemies within Xm" of the caster, of the target, or along a dash): the
    /// aimed target first, then the nearest ones, at most N. Null when the guide has no area for the skill (the old single target /
    /// self-centred rule stays).
    /// </summary>
    internal List<Target>? GuideTargets(int skill, CastRequest request, Target? aimed)
    {
        if (Guide.Of(skill) is not { Targets: > 1, Shape: not null } g || targets is null) return null;
        var caster = position ?? request.Location;
        var centre = g.Shape == "target" && aimed is not null ? aimed.Position : caster;
        var yaw = request.Yaw * Math.PI / 180;
        double fx = Math.Cos(yaw), fy = Math.Sin(yaw);
        bool Inside(Target t)
        {
            if (g.Shape == "path")
            {
                double dx = t.Position.X - caster.X, dy = t.Position.Y - caster.Y;
                var along = dx * fx + dy * fy;
                var side = Math.Abs(-dx * fy + dy * fx);
                return along >= -t.Radius && along <= g.LengthCm + t.Radius && side <= PathHalfWidth + t.Radius;
            }
            var reach = g.RadiusCm + t.Radius;
            return t.Position.DistanceSquared(centre) <= reach * reach;
        }
        var hit = targets.Values.Where(t => t.Attackable && !t.Dead && !t.CorpseRemoved && Inside(t))
            .OrderBy(t => t == aimed ? 0 : 1).ThenBy(t => t.Position.DistanceSquared(centre))
            .Take(g.Targets).ToList();
        if (aimed is not null && aimed.Hostile && !aimed.Dead && !hit.Contains(aimed)) { hit.Insert(0, aimed); if (hit.Count > g.Targets) hit.RemoveAt(hit.Count - 1); }
        return hit;
    }
}
