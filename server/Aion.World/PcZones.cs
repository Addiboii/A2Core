using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Player skills whose damage lives in the summon their SkillEffect Spawn row places, not in the cast: the Sorcerer's Bittercold
/// Wind 15280000 has no Damage row and spawns the invisible NpcData 2920011 (no attacks of its own), so the cast was accepted
/// and nothing happened. A zone is that marker summon staying at the target for a while and hurting every enemy in its area
/// each tick (tools/data/pc-zones.json, a hand entry: the values are ours until the client rows are exported).
/// </summary>
public static class PcZones
{
    public sealed record Zone(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("npc")] int Npc,
        [property: JsonPropertyName("lifetime_ms")] int LifetimeMs,
        [property: JsonPropertyName("tick_ms")] int TickMs,
        [property: JsonPropertyName("radius")] int Radius,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("damage")] DamageRow Damage,
        [property: JsonPropertyName("debuffs")] int[][]? Debuffs = null)
    {
        /// <summary>The ticks one zone deals: one at the end of every <see cref="TickMs"/> of its life.</summary>
        public int Ticks => Math.Max(1, LifetimeMs / Math.Max(1, TickMs));

        /// <summary>The area of a tick: the skill's SkillEffectFilter shape when skill-areas.json has one (kept where the zone stands), else a circle of <see cref="Radius"/>.</summary>
        public SkillAreas.Area Blast(SkillAreas.Area? exported) => exported is { IsArea: true }
            ? exported with { OnTarget = false, Values = [0, 0, .. (exported.Values ?? []).Skip(2)] }
            : new SkillAreas.Area("Circle", [0, 0, 0, 0, Radius, Radius], Max, false, 0, 0, Debuffs: Debuffs);
    }

    sealed record ZoneFile([property: JsonPropertyName("zones")] Dictionary<string, Zone> Zones);

    static readonly Lazy<ZoneFile> file = new(() =>
    {
        using var stream = typeof(PcZones).Assembly.GetManifestResourceStream("pc-zones.json")
            ?? throw new InvalidOperationException("Missing embedded game data pc-zones.json");
        return JsonSerializer.Deserialize<ZoneFile>(stream) ?? throw new InvalidOperationException("Empty game data pc-zones.json");
    });

    /// <summary>The zone a class skill places, or null.</summary>
    public static Zone? For(int skillId) => file.Value.Zones.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
