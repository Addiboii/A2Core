namespace Aion.World;

/// <summary>Shared bounds for client packet fields (build pin 25767555). Server tables and inventory are authoritative.</summary>
public static class PacketSecurity
{
    public const long MaxStackCount = 1_000_000;
    public const long MaxTradeUnitPrice = 2_000_000_000;
    public const long MaxTradeTotalKina = 9_000_000_000_000_000;
    public const long MaxExchangeKina = 2_000_000_000;
    public const int MaxShopLineCount = 10_000;
    public const int MaxQuickSlotContextSkills = 4;

    public static bool IsValidStackCount(long count) => count is >= 1 and <= MaxStackCount;

    public static bool TryMultiply(long left, long right, out long product)
    {
        try
        {
            product = checked(left * right);
            return true;
        }
        catch (OverflowException)
        {
            product = 0;
            return false;
        }
    }

    public static bool TryAdd(long left, long right, out long sum)
    {
        try
        {
            sum = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            sum = 0;
            return false;
        }
    }
}
