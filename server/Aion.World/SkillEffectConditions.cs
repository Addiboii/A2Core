using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// SkillEffect.dat SkillEffectConditionList / ESkillEffectConditionType: gate debuff and effect rows at apply time.
/// Data from tools/data/skill-effect-conditions.json (tools/skill_effect_conditions.py).
/// </summary>
public static class SkillEffectConditions
{
    public sealed record Condition([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("values")] string[]? Values);

    sealed record SkillDebuffConditions(
        [property: JsonPropertyName("debuffs")] Dictionary<string, Condition[]>? Debuffs);

    sealed record ConditionFile(
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDebuffConditions>? Skills);

    static readonly Lazy<ConditionFile> file = new(() =>
    {
        using var stream = typeof(SkillEffectConditions).Assembly.GetManifestResourceStream("skill-effect-conditions.json");
        return stream is null ? new ConditionFile(null) : JsonSerializer.Deserialize<ConditionFile>(stream) ?? new ConditionFile(null);
    });

    public sealed record Context(
        GameSession Caster,
        Target? Npc,
        WorldPlayer? PlayerVictim,
        int SkillId,
        int MainTargetKey,
        long Now,
        Random Rng,
        bool MainTarget,
        double DistanceCm);

    public static IReadOnlyList<Condition> DebuffConditions(int skillId, int abnormalId)
    {
        if (file.Value.Skills?.TryGetValue(skillId.ToString(), out var skill) != true || skill?.Debuffs is null) return [];
        return skill.Debuffs.TryGetValue(abnormalId.ToString(), out var list) ? list : [];
    }

    public static bool DebuffAllowed(int skillId, int abnormalId, Context ctx)
    {
        var list = DebuffConditions(skillId, abnormalId);
        return list.Count == 0 || list.All(c => Met(c, ctx));
    }

    public static bool Met(Condition cond, Context ctx) => cond.Type switch
    {
        "Probability" => ctx.Rng.Next(10_000) < Value(cond, 0, 10_000),
        "CalcBackAttack" => ctx.Npc is not null && BackAttack(ctx.Caster, ctx.Npc),
        "CalcFrontAttack" => ctx.Npc is not null && FrontAttack(ctx.Caster, ctx.Npc),
        "AttackAngle" => ctx.Npc is not null && AttackAngle(ctx.Caster, ctx.Npc, Value(cond, 0, 0), Value(cond, 1, 360)),
        "Distance_Lower" => ctx.DistanceCm >= Value(cond, 0, 0),
        "IsMainTarget" => ctx.MainTarget,
        "Caster_Combat" => ctx.Caster.InCombatForConditions,
        "Target_Combat" => ctx.Npc is not null && ctx.Caster.TargetInCombat(ctx.Npc.Key),
        "Caster_MaxHPRatio_Lower" => ctx.Caster.ResourceRatioPermille('H') >= Value(cond, 0, 0),
        "Caster_MaxHPRatio_Upper" => ctx.Caster.ResourceRatioPermille('H') <= Value(cond, 0, 10_000),
        "Caster_MaxMPRatio_Lower" => ctx.Caster.ResourceRatioPermille('M') >= Value(cond, 0, 0),
        "Caster_MaxMPRatio_Upper" => ctx.Caster.ResourceRatioPermille('M') <= Value(cond, 0, 10_000),
        "Caster_MaxAPRatio_Lower" => ctx.Caster.ResourceRatioPermille('A') >= Value(cond, 0, 0),
        "Caster_MaxAPRatio_Upper" => ctx.Caster.ResourceRatioPermille('A') <= Value(cond, 0, 10_000),
        "Target_MaxHPRatio_Lower" => ctx.Npc is not null && Ratio(ctx.Npc.Hp, ctx.Npc.HpMax) >= Value(cond, 0, 0),
        "Target_MaxHPRatio_Upper" => ctx.Npc is not null && Ratio(ctx.Npc.Hp, ctx.Npc.HpMax) <= Value(cond, 0, 10_000),
        "Target_MaxMPRatio_Lower" or "Target_MaxMPRatio_Upper" => ctx.PlayerVictim is not null && PlayerRatioMet(ctx.PlayerVictim, cond),
        "Target_MaxAPRatio_Lower" or "Target_MaxAPRatio_Upper" => ctx.PlayerVictim is not null && PlayerApRatioMet(ctx.PlayerVictim, cond),
        "Target_Gauge_Lower" => Gauge(ctx.Npc, ctx.PlayerVictim) >= Value(cond, 0, 0),
        "Target_Gauge_Upper" => Gauge(ctx.Npc, ctx.PlayerVictim) <= Value(cond, 0, int.MaxValue),
        "Target_Gauge_Equal" => Gauge(ctx.Npc, ctx.PlayerVictim) == Value(cond, 0, 0),
        "Caster_Gauge_Lower" => ctx.Caster.CombatGauge >= Value(cond, 0, 0),
        "Caster_Gauge_Upper" => ctx.Caster.CombatGauge <= Value(cond, 0, int.MaxValue),
        "Caster_Gauge_Equal" => ctx.Caster.CombatGauge == Value(cond, 0, 0),
        "Target_HasAbnormalEffectType" => HasEffect(ctx, cond.Values?.FirstOrDefault(), byCaster: false),
        "Target_HasAbnormalEffectType_ByCaster" => HasEffect(ctx, cond.Values?.FirstOrDefault(), byCaster: true),
        "Caster_HasAbnormalEffectType" => ctx.Caster.HasAbnormalEffectType(cond.Values?.FirstOrDefault()),
        "Target_HasAbnormalGroupId" => HasGroup(ctx, Value(cond, 0, 0), byCaster: false),
        "Target_HasAbnormalGroupId_ByCaster" => HasGroup(ctx, Value(cond, 0, 0), byCaster: true),
        "Target_HasAbnormalId" => HasAbnormal(ctx, Value(cond, 0, 0), byCaster: false),
        "Target_HasAbnormalId_ByCaster" => HasAbnormal(ctx, Value(cond, 0, 0), byCaster: true),
        "Caster_HasAbnormalGroupId" => ctx.Caster.PlayerHasAbnormalGroup(Value(cond, 0, 0), null),
        "Caster_HasAbnormalId" => ctx.Caster.PlayerHasAbnormalId(Value(cond, 0, 0), null),
        "ObjectType" => ObjectTypeMet(ctx, cond.Values?.FirstOrDefault()),
        _ when SkillEffectConditions.Unimplemented.Contains(cond.Type) => false,
        _ => true,
    };

    /// <summary>Condition kinds not handled above (MoveType, GatherCount, SameProjectileGatherCount, …).</summary>
    public static readonly HashSet<string> Unimplemented =
    [
        "Target_MoveType", "Caster_MoveType", "GatherCount", "SameProjectileGatherCount",
        "SameProjectileGatherCount_Lower", "SameSubzone_TeleportCaster", "Distance_Height_Lower",
    ];

    static int Value(Condition c, int index, int fallback) =>
        c.Values is { } vals && vals.Length > index && int.TryParse(vals[index], out var v) ? v : fallback;

    static int Ratio(int current, int max) => max <= 0 ? 0 : current * 10_000 / max;

    static bool PlayerRatioMet(WorldPlayer victim, Condition cond)
    {
        var ratio = victim.Session.ResourceRatioPermille('M');
        return cond.Type switch
        {
            "Target_MaxMPRatio_Lower" => ratio >= Value(cond, 0, 0),
            "Target_MaxMPRatio_Upper" => ratio <= Value(cond, 0, 10_000),
            _ => false,
        };
    }

    static bool PlayerApRatioMet(WorldPlayer victim, Condition cond)
    {
        if (!victim.Session.HasApBar) return false;
        var ratio = victim.Session.ResourceRatioPermille('A');
        return cond.Type switch
        {
            "Target_MaxAPRatio_Lower" => ratio >= Value(cond, 0, 0),
            "Target_MaxAPRatio_Upper" => ratio <= Value(cond, 0, 10_000),
            _ => false,
        };
    }

    static int Gauge(Target? npc, WorldPlayer? player) =>
        npc is not null ? npc.StaggerGauge : player?.Session.StaggerGauge ?? 0;

    static bool BackAttack(GameSession caster, Target target)
    {
        if (caster.Position is not { } from) return false;
        return AttackSide(from, target.Position, target.Direction, behind: true);
    }

    static bool FrontAttack(GameSession caster, Target target)
    {
        if (caster.Position is not { } from) return false;
        return AttackSide(from, target.Position, target.Direction, behind: false);
    }

    static bool AttackSide(Vector3d from, Vector3d to, double targetYawDeg, bool behind)
    {
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;
        if (dx * dx + dy * dy < 1) return false;
        var angleToAttacker = Math.Atan2(dy, dx) * 180 / Math.PI;
        var facing = targetYawDeg;
        var diff = Math.Abs(NormalizeAngle(angleToAttacker - facing));
        return behind ? diff >= 90 : diff < 90;
    }

    static bool AttackAngle(GameSession caster, Target target, int minDeg, int maxDeg)
    {
        if (caster.Position is not { } from) return false;
        var dx = target.Position.X - from.X;
        var dy = target.Position.Y - from.Y;
        var toTarget = Math.Atan2(dy, dx) * 180 / Math.PI;
        var diff = Math.Abs(NormalizeAngle(toTarget - (caster.WorldPlayer?.Heading ?? 0)));
        return diff >= minDeg && diff <= maxDeg;
    }

    static double NormalizeAngle(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    static bool HasEffect(Context ctx, string? effect, bool byCaster)
    {
        if (string.IsNullOrEmpty(effect)) return false;
        if (ctx.Npc is not null)
            return ctx.Caster.NpcHasAbnormalEffect(ctx.Npc.Key, effect, ctx.Now, byCaster ? ctx.Caster.PlayerKey : null);
        if (ctx.PlayerVictim is not null)
            return ctx.PlayerVictim.Session.PlayerHasAbnormalEffect(effect, ctx.Now, byCaster ? ctx.Caster.PlayerKey : null);
        return false;
    }

    static bool HasGroup(Context ctx, int group, bool byCaster)
    {
        if (group <= 0) return false;
        if (ctx.Npc is not null) return ctx.Caster.NpcHasAbnormalGroup(ctx.Npc.Key, group, byCaster ? ctx.Caster.PlayerKey : null);
        if (ctx.PlayerVictim is not null) return ctx.PlayerVictim.Session.PlayerHasAbnormalGroup(group, byCaster ? ctx.Caster.PlayerKey : null);
        return false;
    }

    static bool HasAbnormal(Context ctx, int abnormalId, bool byCaster)
    {
        if (abnormalId <= 0) return false;
        if (ctx.Npc is not null) return ctx.Caster.NpcHasAbnormalId(ctx.Npc.Key, abnormalId, byCaster ? ctx.Caster.PlayerKey : null);
        if (ctx.PlayerVictim is not null) return ctx.PlayerVictim.Session.PlayerHasAbnormalId(abnormalId, byCaster ? ctx.Caster.PlayerKey : null);
        return false;
    }

    static bool ObjectTypeMet(Context ctx, string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        var wantPlayer = name.Contains("Player", StringComparison.OrdinalIgnoreCase) || name is "1";
        var isPlayer = ctx.PlayerVictim is not null;
        var isNpc = ctx.Npc is not null && ctx.Npc.OwnerKey == 0;
        return wantPlayer ? isPlayer : isNpc;
    }
}
