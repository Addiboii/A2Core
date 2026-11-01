namespace Aion.World;

/// <summary>
/// Main-attribute contributions to player damage. StatConvert exists in the client usmap but is not exported yet;
/// coefficients below are CANDIDATE until table export lands.
/// </summary>
public static class CombatAttributes
{
    /// <summary>The class primary attribute (UI "Power"): STR physical, INT magical, WIS healers, AGI scouts (CANDIDATE mapping).</summary>
    public static string PrimaryPowerAttribute(string? className) => className switch
    {
        "Sorcerer" or "Elementalist" => "INT",
        "Cleric" or "Chanter" => "WIS",
        "Assassin" or "Ranger" => "AGI",
        _ => "STR",
    };

    /// <summary>CANDIDATE: basis points of attack damage per point of primary Power attribute.</summary>
    public const int PowerToAttackBpPerPoint = 10;

    /// <summary>CANDIDATE: extra crit rating per AGI point (added before /1000 chance).</summary>
    public const int AgiToCriticalPerPoint = 2;

    /// <summary>CANDIDATE: extra crit rating per DEX point.</summary>
    public const int DexToCriticalPerPoint = 1;

    /// <summary>CANDIDATE: accuracy rating per DEX point (see Combat.RowHits accuracy term).</summary>
    public const int DexToAccuracyPerPoint = 2;
}
