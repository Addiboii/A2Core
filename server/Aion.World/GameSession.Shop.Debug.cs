namespace Aion.World;

/// <summary>
/// Optional shop packet trace (set <c>Aion:Debug:ShopFile</c> on the host or <c>AION_SHOP_DEBUG</c> to a log path).
/// Same pattern as map-event debug: append-only lines with UTC timestamps, separate from the host log.
/// </summary>
public sealed partial class GameSession
{
    static string? shopDebugPath = Environment.GetEnvironmentVariable("AION_SHOP_DEBUG");
    static readonly object ShopDebugGate = new();

    /// <summary>Host startup may override the env var (see <c>Aion:Debug:ShopFile</c>).</summary>
    public static void ConfigureShopDebug(string? path) => shopDebugPath = path;

    void DebugShop(string text)
    {
        if (string.IsNullOrEmpty(shopDebugPath)) return;
        var line = $"{DateTimeOffset.UtcNow:O} {CharacterId} {text}{Environment.NewLine}";
        try
        {
            lock (ShopDebugGate) File.AppendAllText(shopDebugPath, line);
        }
        catch
        {
            // Debug logging must not break purchases.
        }
    }

    static string ShopEntriesSummary(IEnumerable<(long A, long B)> entries) =>
        string.Join(", ", entries.Select(e => $"{e.A}x{e.B}"));
}
