using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// What the launcher remembers (%LOCALAPPDATA%\A2CorePrivate\launcher-settings.json). Never a password.
/// With "Keep me signed in" it keeps the account session token (encrypted for this Windows user) so
/// the next start needs no sign-in until the server ends the session (30 days, or a sign-out).
/// During a download it also keeps the Steam sign-in (encrypted the same way) to resume; that one
/// is removed when the game is installed.
/// </summary>
sealed class LauncherSettings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2CorePrivate");
    static string FilePath => Path.Combine(Folder, "launcher-settings.json");

    public string? InstallFolder { get; set; }
    public string? Username { get; set; }
    /// <summary>The "Keep me signed in" choice (the box's default next time).</summary>
    public bool KeepSignedIn { get; set; } = true;
    /// <summary>"Run without installing" was chosen: the exe is used where it is and no longer offers to install itself.</summary>
    public bool NoInstall { get; set; }
    /// <summary>Account session token, DPAPI-protected (current Windows user), base64; and when the server ends it.</summary>
    public string? SessionToken { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    /// <summary>Sizes and write times of the quick-check files when they last passed; unchanged files skip re-hashing.</summary>
    public string? VerifiedStamp { get; set; }
    /// <summary>Folder of a started (possibly interrupted) Steam download.</summary>
    public string? DownloadFolder { get; set; }
    public string? SteamAccount { get; set; }
    /// <summary>Steam refresh token, DPAPI-protected (current Windows user), base64.</summary>
    public string? SteamToken { get; set; }
    /// <summary>When true, copy z_A2Core_Patch-Windows_P.* from the client editor deploy folder before starting the game.</summary>
    public bool AllowClientPatchPaks { get; set; }

    /// <summary>The kept account session, when there is one that has not ended yet.</summary>
    public string? Session()
    {
        if (SessionToken is null || SessionExpiresAt is { } ends && ends <= DateTimeOffset.UtcNow) return null;
        return Unprotect(SessionToken);
    }

    public void SetSession(string? token, DateTimeOffset? expiresAt)
    {
        SessionToken = token is null ? null : Protect(token);
        SessionExpiresAt = token is null ? null : expiresAt;
    }

    public SteamDownload.SignIn? SteamSignIn()
    {
        if (SteamAccount is null || SteamToken is null) return null;
        return Unprotect(SteamToken) is { } refreshToken ? new(SteamAccount, refreshToken) : null;
    }

    public void SetSteamSignIn(SteamDownload.SignIn? signIn)
    {
        SteamAccount = signIn?.Account;
        SteamToken = signIn is null ? null : Protect(signIn.RefreshToken);
    }

    static string Protect(string secret) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    static string? Unprotect(string protectedBase64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    public static LauncherSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
