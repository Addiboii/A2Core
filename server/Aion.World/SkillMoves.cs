using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Caster movement of class skills, from the client's own ESkillEffectType rows (usmap): Teleport_Caster,
/// Pre_Cast_Teleport_Caster and Pre_SkillStart_Teleport_Caster move the caster to the target's back. The rows come from
/// tools/data/skill-areas.json ('effects', tools/skill_areas.py); the server never keys on a skill id, so every skill that
/// carries one of the rows jumps (Assassin Shadowstrike 13070000, and any other skill an export names).
/// </summary>
public static class SkillMoves
{
    public const string TeleportCaster = "Teleport_Caster", PreCastTeleport = "Pre_Cast_Teleport_Caster", PreStartTeleport = "Pre_SkillStart_Teleport_Caster";

    /// <summary>OURS: the caster lands this far behind the target's edge (cm) and counts as this wide; the row's values are not decoded.</summary>
    public const double GapCm = 60, CasterRadiusCm = 40;

    public static bool IsCasterTeleport(string type) => type is TeleportCaster or PreCastTeleport or PreStartTeleport;

    /// <summary>The Pre_ rows move the caster as the cast starts; Teleport_Caster moves it when the skill's first hit lands.</summary>
    public static bool IsBeforeHit(string type) => type is PreCastTeleport or PreStartTeleport;

    /// <summary>The skill's teleport row: its own, else its family's (level / spec variants reuse the family's rows).</summary>
    public static string[]? CasterTeleportFor(int skillId) =>
        SkillAreas.For(skillId)?.CasterTeleport ?? (skillId / 10_000 * 10_000 is var family && family != skillId ? SkillAreas.For(family)?.CasterTeleport : null);

    /// <summary>The skill's CasterMove row (CasterMoveDirection on the client export): Back, Forward, ... plus optional distance cm.</summary>
    public static string[]? CasterMoveFor(int skillId) =>
        SkillAreas.For(skillId)?.EffectRows.FirstOrDefault(r => r.Length > 0 && r[0] == "CasterMove")
        ?? (skillId / 10_000 * 10_000 is var family && family != skillId
            ? SkillAreas.For(family)?.EffectRows.FirstOrDefault(r => r.Length > 0 && r[0] == "CasterMove") : null);

    /// <summary>OURS: back-step distance when the row carries no value (Marking Shot ~ Explosion Trap radius scale).</summary>
    public const int DefaultBackstepCm = 500;

    /// <summary>The point behind a target that faces <paramref name="facingDegrees"/>: on the line through its back, at its edge + the gap.</summary>
    public static Vector3d BehindPoint(Vector3d target, double facingDegrees, double targetRadius)
    {
        var distance = Math.Max(0, targetRadius) + CasterRadiusCm + GapCm;
        var angle = facingDegrees * Math.PI / 180;
        return new Vector3d(target.X - Math.Cos(angle) * distance, target.Y - Math.Sin(angle) * distance, target.Z);
    }

    /// <summary>The yaw in degrees (the cast request's convention) that looks from <paramref name="from"/> at <paramref name="to"/>.</summary>
    public static double Facing(Vector3d from, Vector3d to) => Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;
}
