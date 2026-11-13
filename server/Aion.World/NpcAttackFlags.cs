using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Which NPC templates the players cannot hurt and which die by a number of hits, from the client's NpcData
/// (tools/npc_attack_flags.py -> tools/data/npc-attack-flags.json):
/// untouchable = bCanBeAttacked false or NpcTargetAble Unable (4,246 templates; Onlypicking stays attackable, OBSERVED dying to
/// the player; 2400082 the Drana swarm egg stays attackable, OBSERVED taking the player's hits);
/// hit count = bHitCountBased (325 templates): HP = hits, each hit one point. 7 OBSERVED for the six recorded ones
/// (2290017, 2400121, 2400122, 2400144, 2400152, 2400340: appear HP 7/7, 0x8d00 7 -> 0 one per hit); 7 for the rest CANDIDATE.
/// </summary>
public static class NpcAttackFlags
{
    sealed record Flags(HashSet<int> Untouchable, Dictionary<int, int> Hits);

    static readonly Lazy<Flags> flags = new(() =>
    {
        using var stream = typeof(NpcAttackFlags).Assembly.GetManifestResourceStream("npc-attack-flags.json")
            ?? throw new InvalidOperationException("Missing embedded game data npc-attack-flags.json");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var untouchable = root.GetProperty("untouchable").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
        var hits = root.GetProperty("hit_count").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => Math.Max(1, p.Value.GetInt32()));
        return new Flags(untouchable, hits);
    });

    /// <summary>The template cannot be hurt by the players (NpcData bCanBeAttacked false / NpcTargetAble Unable).</summary>
    public static bool Untouchable(int npcId) => flags.Value.Untouchable.Contains(npcId);

    /// <summary>A hit-count template's hits (its HP), or null.</summary>
    public static int? Hits(int npcId) => flags.Value.Hits.TryGetValue(npcId, out var hits) ? hits : null;

    public static int UntouchableCount => flags.Value.Untouchable.Count;
    public static int HitCountCount => flags.Value.Hits.Count;
}
