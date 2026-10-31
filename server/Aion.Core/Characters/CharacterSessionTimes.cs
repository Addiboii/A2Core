namespace Aion.Core.Characters;

public static class CharacterSessionTimes
{
    public static long LastPlayedUnixMs(long lastLoginUnixMs, long lastLogoutUnixMs, long registeredUnixMs) =>
        Math.Max(Math.Max(lastLoginUnixMs, lastLogoutUnixMs), registeredUnixMs);

    /// <summary>Wire LobbyCharDesc._logout_time (Unix ms UTC; the lobby UI treats it as last played).</summary>
    public static long LobbyLogoutWireMs(long lastLoginUnixMs, long lastLogoutUnixMs, long registeredUnixMs)
    {
        if (lastLogoutUnixMs > 0) return lastLogoutUnixMs;
        if (lastLoginUnixMs > 0) return lastLoginUnixMs;
        return registeredUnixMs;
    }
}
