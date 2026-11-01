namespace Aion.World;

/// <summary>Private-server override for real-money / Quna SKUs (optional Aion:Shop:PremiumMode).</summary>
public enum ShopPremiumMode
{
    /// <summary>Official: pay listed Cash/Quna prices from the Quna item in the bag.</summary>
    Quna,
    /// <summary>Pay listed Cash/Quna prices using Kina (private dev convenience).</summary>
    Kina,
    Free,
    Disabled,
}

public sealed class ShopOptions
{
    public ShopPremiumMode PremiumMode { get; init; } = ShopPremiumMode.Quna;

    public static ShopPremiumMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "kina" => ShopPremiumMode.Kina,
        "free" => ShopPremiumMode.Free,
        "disabled" => ShopPremiumMode.Disabled,
        _ => ShopPremiumMode.Quna,
    };
}
