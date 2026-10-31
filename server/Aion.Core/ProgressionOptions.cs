namespace Aion.Core;

/// <summary>
/// Live progression limits for build 25767555 (Oct 7 pin): official Global cap 45 while Exp.dat /
/// PcStatLevel tables still define levels 1–100.
/// </summary>
public sealed class ProgressionOptions
{
    public const int DefaultLevelCap = 45;
    /// <summary>Highest level row in embedded Exp.dat / PcStatLevel exports (unchanged).</summary>
    public const int TableMaxLevel = 100;

    /// <summary>Maximum character level the server grants (config: Aion:Progression:LevelCap).</summary>
    public int LevelCap { get; init; } = DefaultLevelCap;
}
