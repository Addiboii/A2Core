using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Player trap skills from tools/data/pc-traps.json (a hand entry from the client tables): the Ranger's Explosion Trap
/// 14170000 places NpcData 2920620 Ranger_Skill017_Trap (SkillEffect Spawn group 141700001) and detonates group 141700011
/// (Damage + Abnormal_Cc stun 100000311) on the enemies around it.
/// </summary>
public static class PcTraps
{
    public sealed record Trap(
        [property: JsonPropertyName("npc")] int Npc,
        [property: JsonPropertyName("lifetime_ms")] int LifetimeMs,
        [property: JsonPropertyName("trigger_radius")] int TriggerRadius,
        [property: JsonPropertyName("radius")] int Radius,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("damage")] DamageRow Damage,
        [property: JsonPropertyName("debuffs")] int[][]? Debuffs = null)
    {
        /// <summary>The blast: the skill's SkillEffectFilter shape when skill-areas.json has one, else a circle of <see cref="Radius"/>.</summary>
        public SkillAreas.Area Blast(SkillAreas.Area? exported) => exported is { IsArea: true }
            ? exported with { OnTarget = false, Values = [0, 0, .. (exported.Values ?? []).Skip(2)] }
            : new SkillAreas.Area("Circle", [0, 0, 0, 0, Radius, Radius], Max, false, 0, 0, Debuffs: Debuffs);

        public IEnumerable<(int Abnormal, int MinMs, int MaxMs, bool Held)> DebuffRows =>
            new SkillAreas.Area(null, null, 0, false, 0, 0, Debuffs: Debuffs).DebuffRows;
    }

    sealed record TrapFile([property: JsonPropertyName("traps")] Dictionary<string, Trap> Traps);

    static readonly Lazy<TrapFile> file = new(() =>
    {
        using var stream = typeof(PcTraps).Assembly.GetManifestResourceStream("pc-traps.json")
            ?? throw new InvalidOperationException("Missing embedded game data pc-traps.json");
        return JsonSerializer.Deserialize<TrapFile>(stream) ?? throw new InvalidOperationException("Empty game data pc-traps.json");
    });

    /// <summary>The trap a class skill places, or null.</summary>
    public static Trap? For(int skillId) => file.Value.Traps.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
