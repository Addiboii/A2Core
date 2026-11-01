using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Area / multi-target shapes and projectiles of class skills from tools/data/skill-areas.json (tools/skill_areas.py):
/// Skill.dat SkillEffectTimeDataList -> SkillEffectFilter.dat (EffectRangeType, EffectRangeValues, TargetCountMax,
/// bEffectRangeNeedTarget) and SkillProjectile.dat (Speed). Tempest Shot 14340000: Circle 400 around the target, 4 targets.
/// </summary>
public static class SkillAreas
{
    public sealed record Area(
        [property: JsonPropertyName("shape")] string? Shape,
        [property: JsonPropertyName("values")] int[]? Values,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("on_target")] bool OnTarget,
        [property: JsonPropertyName("projectile")] int Projectile,
        [property: JsonPropertyName("speed")] int Speed,
        [property: JsonPropertyName("projectiles")] int[]? Projectiles = null,
        [property: JsonPropertyName("ground")] string? Ground = null,
        [property: JsonPropertyName("ground_radius")] int GroundRadius = 0,
        [property: JsonPropertyName("debuffs")] int[][]? Debuffs = null,
        [property: JsonPropertyName("target_debuffs")] int[][]? TargetDebuffs = null,
        [property: JsonPropertyName("caster_debuffs")] int[][]? CasterDebuffs = null,
        [property: JsonPropertyName("effects")] string[][]? Effects = null,
        [property: JsonPropertyName("scope")] string? Scope = null,
        [property: JsonPropertyName("scope_range")] int ScopeRange = 0)
    {
        /// <summary>The skill's non-damage SkillEffect rows: ESkillEffectType name, then its EffectValueList (tools/skill_areas.py).</summary>
        public IEnumerable<string[]> EffectRows => Effects ?? [];

        /// <summary>The caster-teleport row (Teleport_Caster, Pre_Cast_ / Pre_SkillStart_Teleport_Caster), or null.</summary>
        public string[]? CasterTeleport => EffectRows.FirstOrDefault(r => r.Length > 0 && SkillMoves.IsCasterTeleport(r[0]));

        public bool IsArea => Shape is not null && Max > 1;

        /// <summary>
        /// Skill.dat GroundType Location / Direction (or TargetProcessType Ground): a caster-side filter (not
        /// bEffectRangeNeedTarget) is placed at the cast's ground point (the 0x3800 location) instead of on the caster.
        /// </summary>
        public bool IsGround => Ground is not null && !OnTarget;

        static IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> ParseRows(int[][]? rows) =>
            (rows ?? []).Where(d => d.Length >= 3 && d[0] > 0).Select(d => (d[0], d[1], d[2], d.Length > 3 && d[3] != 0));

        /// <summary>Enemy-facing Abnormal_True / Abnormal_Cc rows (SkillEffectFilter enemy or target filter).</summary>
        public IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> TargetDebuffRows =>
            ParseRows(TargetDebuffs ?? Debuffs);

        /// <summary>Self / friendly-filter abnormal rows (buffs on the caster).</summary>
        public IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> CasterDebuffRows => ParseRows(CasterDebuffs);

        /// <summary>Target debuff rows (legacy name: same as <see cref="TargetDebuffRows"/>).</summary>
        public IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> DebuffRows => TargetDebuffRows;

        /// <summary>
        /// The SkillProjectile.dat row timed row <paramref name="timedRow"/> fires, 0 for none: the exported per-row ids, else the
        /// first row's id + the row (OBSERVED official Rapid Fire 2026-10-30: 140300001, 140300002).
        /// </summary>
        public int ProjectileFor(int timedRow) => Projectile <= 0 ? 0
            : Projectiles is { } rows ? (timedRow < rows.Length ? rows[timedRow] : 0)
            : Projectile + timedRow;
        int V(int i) => Values is { } v && v.Length > i ? v[i] : 0;

        /// <summary>
        /// Whether <paramref name="point"/> lies in the shape centred at <paramref name="centre"/> facing <paramref name="yawDegrees"/>.
        /// Values: [offset x, offset y (along the facing), _, _, radius or length, width or angle, height, ...]. OURS: height
        /// is ignored (2D), Ring/Cross/Triangle use their outer extent.
        /// </summary>
        public bool Contains(Vector3d centre, double yawDegrees, Vector3d point, double targetRadius = 0)
        {
            var yaw = yawDegrees * Math.PI / 180;
            double fx = Math.Cos(yaw), fy = Math.Sin(yaw);
            var c = centre with { X = centre.X + fx * V(1) - fy * V(0), Y = centre.Y + fy * V(1) + fx * V(0) };
            double dx = point.X - c.X, dy = point.Y - c.Y;
            var forward = dx * fx + dy * fy;
            var lateral = -dx * fy + dy * fx;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            switch (Shape)
            {
                case "Rectangle" or "Straight":
                    return forward >= -targetRadius && forward <= V(4) + targetRadius && Math.Abs(lateral) <= V(5) / 2.0 + targetRadius;
                case "Arc" or "RingArc":
                {
                    if (distance > V(4) + targetRadius) return false;
                    var half = Shape == "Arc" ? V(5) / 2.0 : V(7) / 2.0;
                    if (distance < 1 || half >= 180) return true;
                    return Math.Abs(Math.Atan2(lateral, forward) * 180 / Math.PI) <= half;
                }
                case "Cross":
                    return distance <= Math.Max(V(4), V(5)) + targetRadius;
                default:   // Circle, Sphere, Ring, Triangle
                    return distance <= V(4) + targetRadius;
            }
        }

        /// <summary>Projectile flight time in ms over <paramref name="distance"/> cm (SkillProjectile Speed cm/s), 0 without one.</summary>
        public int FlightMs(double distance) => Projectile > 0 && Speed > 0 ? (int)Math.Min(3000, distance * 1000 / Speed) : 0;
    }

    /// <summary>The SkillAbnormalEffect rows of an abnormal: [effect id, EAbnormalEffectType, values...]; source "client" or "override" (improvised).</summary>
    sealed record AbnormalEffectEntry([property: JsonPropertyName("effects")] JsonElement[][] Effects, [property: JsonPropertyName("source")] string? Source);

    sealed record AreaFile(
        [property: JsonPropertyName("skills")] Dictionary<string, Area> Skills,
        [property: JsonPropertyName("abnormal_effects")] Dictionary<string, AbnormalEffectEntry>? AbnormalEffects = null);

    static readonly Lazy<AreaFile> file = new(() =>
    {
        using var stream = typeof(SkillAreas).Assembly.GetManifestResourceStream("skill-areas.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-areas.json");
        return JsonSerializer.Deserialize<AreaFile>(stream) ?? throw new InvalidOperationException("Empty game data skill-areas.json");
    });

    /// <summary>The effect rows of a class-skill abnormal (tools/skill_areas.py abnormal_effects), or null; its group is its own id.</summary>
    public static AbnormalInfo? Abnormal(int abnormalId) =>
        file.Value.AbnormalEffects?.GetValueOrDefault(abnormalId.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } entry
            ? new AbnormalInfo(abnormalId, entry.Effects.Select(e => (e[0].GetInt32(), e[1].GetString()!,
                (IReadOnlyList<string>)e.Skip(2).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList())).ToList())
            : null;

    /// <summary>Whether an abnormal's rows are improvised (OURS) rather than exported from the client tables.</summary>
    public static bool IsImprovised(int abnormalId) =>
        file.Value.AbnormalEffects?.GetValueOrDefault(abnormalId.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Source == "override";

    /// <summary>The shape / projectile of a class skill, or null.</summary>
    public static Area? For(int skillId) => file.Value.Skills.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
