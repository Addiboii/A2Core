namespace Aion.World;

/// <summary>
/// The non-damage SkillEffect rows of a class skill (the client's ESkillEffectType names, values as documented in
/// tools/skill_effect_overrides.py) and who they land on. Rows come from tools/data/skill-areas.json: exported from the client
/// tables, else improvised by tools/skill_effect_overrides.py (SkillAreas.IsImprovised); the server never keys on a skill id.
/// </summary>
public static class SkillEffects
{
    public static readonly string[] HealTypes = ["HpHeal", "HpHealRatio", "MpHeal", "MpHealRatio", "SpHeal", "SpHealRatio", "ApHeal", "ApHealRatio"];

    /// <summary>Rows that land on the friendly recipients (the caster, the target, the party, the players nearby).</summary>
    public static readonly string[] FriendlyTypes = [.. HealTypes, "Dispel_Cc"];

    /// <summary>Rows that land on enemies (monsters and the players the caster may fight).</summary>
    public static readonly string[] HostileTypes = ["Pull", "Dispel", "MPBurn", "APBurn", "APBurnRatio", "AggroTransfer", "IncreaseCooltime"];

    /// <summary>Rows that land on the caster's own cooldowns.</summary>
    public static readonly string[] SelfTypes = ["DecreaseCooltime"];

    /// <summary>Everything the server does something with (Dispel_True goes by its first value: DeBuff cleanses friends, Buff purges enemies).</summary>
    public static readonly string[] Handled = [.. FriendlyTypes, .. HostileTypes, .. SelfTypes, "Dispel_True"];

    public static readonly HashSet<string> FriendlyScopes = new(StringComparer.OrdinalIgnoreCase) { "self", "target", "party", "area" };

    /// <summary>The skill's own export, else its family's (level / spec variants reuse the family's rows).</summary>
    static SkillAreas.Area? Source(int skill)
    {
        var own = SkillAreas.For(skill);
        if (own is { Effects: not null } or { Scope: not null }) return own;
        var family = skill / 10_000 * 10_000;
        return family != skill ? SkillAreas.For(family) ?? own : own;
    }

    /// <summary>The effect rows of the skill the server handles here (the teleports are SkillMoves').</summary>
    public static IReadOnlyList<string[]> Rows(int skill) =>
        Source(skill)?.EffectRows.Where(r => r.Length > 0 && Handled.Contains(r[0])).ToList() ?? [];

    public static bool IsFriendlyRow(string[] row) =>
        FriendlyTypes.Contains(row[0]) || row[0] == "Dispel_True" && !PurgesEnemies(row);

    public static bool IsHostileRow(string[] row) =>
        HostileTypes.Contains(row[0]) || row[0] == "Dispel_True" && PurgesEnemies(row);

    /// <summary>Dispel_True [Buff]: removes the target's beneficial abnormals (an enemy's); [DeBuff] cleanses (a friend's).</summary>
    static bool PurgesEnemies(string[] row) => row.Length > 1 && row[1].Equals("Buff", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A friendly skill: the area names a friendly scope (self, target, party, area), else it heals or cleanses. Its abnormal rows are
    /// buffs for the recipients (a heal over time, a stat buff, an immunity), not debuffs for enemies.
    /// </summary>
    public static bool IsFriendly(int skill) =>
        Source(skill) is { } area && (area.Scope is { } scope ? FriendlyScopes.Contains(scope) : area.EffectRows.Any(r => r.Length > 0 && IsFriendlyRow(r)));

    /// <summary>
    /// A skill that clears crowd control from its caster (Defiance, 60 s: Dispel_Cc on self) is cast while held or silenced: the
    /// point of it. OURS (the client's own cast rules for a held player are not in the export).
    /// </summary>
    public static bool BreaksControl(int skill) => Scope(skill) == "self" && Rows(skill).Any(r => r[0] == "Dispel_Cc");

    /// <summary>Who a friendly skill lands on: self / target / party / area (the area's scope, else the target).</summary>
    public static string Scope(int skill) => Source(skill)?.Scope is { Length: > 0 } scope ? scope.ToLowerInvariant() : "target";

    public static int ScopeRange(int skill) => Source(skill)?.ScopeRange is > 0 and var range ? range : (int)GameSession.AreaRange;

    public static int Value(string[] row, int index) => index + 1 < row.Length && int.TryParse(row[index + 1], out var v) ? v : 0;
}
