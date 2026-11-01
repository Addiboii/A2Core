using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Combat AP bar (CharPoint Ap / EHudSkillCostType AP): max and regen from tools/data/player-ap-pools.json and PcStatLevel APMax.</summary>
public static class PlayerApPools
{
    sealed record PoolEntry(
        [property: JsonPropertyName("ap_max")] int ApMax,
        [property: JsonPropertyName("ap_regen")] int ApRegen = 0,
        [property: JsonPropertyName("source")] string? Source = null);

    sealed record PoolFile(
        [property: JsonPropertyName("regen_per_second")] int RegenPerSecond = 0,
        [property: JsonPropertyName("classes")] Dictionary<string, PoolEntry>? Classes = null);

    static readonly Lazy<PoolFile> file = new(() =>
    {
        using var stream = typeof(PlayerApPools).Assembly.GetManifestResourceStream("player-ap-pools.json")
            ?? throw new InvalidOperationException("Missing embedded game data player-ap-pools.json");
        return JsonSerializer.Deserialize<PoolFile>(stream) ?? new PoolFile();
    });

    public static int ApMax(GameData data, string? className, int level, string? hudResource)
    {
        if (className is null) return 0;
        var fromStats = data.BaseStats(className, level).GetValueOrDefault("APMax");
        if (fromStats > 0) return fromStats;
        if (file.Value.Classes?.TryGetValue(className, out var entry) == true && entry.ApMax > 0) return entry.ApMax;
        return hudResource?.Equals("AP", StringComparison.OrdinalIgnoreCase) == true ? DefaultApMax : 0;
    }

    public static int ApRegen(GameData data, string? className)
    {
        if (className is null) return 0;
        if (file.Value.Classes?.TryGetValue(className, out var entry) == true && entry.ApRegen > 0) return entry.ApRegen;
        return file.Value.RegenPerSecond;
    }

    /// <summary>Default AP cap when HudSkillCostType is AP but PcStatLevel export has no APMax yet (OURS).</summary>
    public const int DefaultApMax = 1000;
}
