using System.Security.Cryptography;

namespace Aion.Launcher;

/// <summary>
/// The launcher replacing itself with the server's build. The server announces its launcher's hash
/// (GET /api/downloads/launcher/manifest); when it differs from this exe, the launcher downloads the
/// server's file next to itself, checks the hash and swaps the files. Windows lets a running exe be
/// renamed (not overwritten), so the old exe is moved aside and removed at the next start.
/// </summary>
public static class LauncherUpdate
{
    public const string NewSuffix = ".new", OldSuffix = ".old";

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>The server's launcher is a different file (either way: the server's build is the one to use).</summary>
    public static bool Needed(AccountClient.LauncherManifest manifest, string ownHash, long ownSize) =>
        manifest.Size != ownSize || !manifest.Sha256.Equals(ownHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts the downloaded file in the running exe's place: exe becomes exe.old, the download becomes exe.</summary>
    public static void Swap(string exe, string downloaded)
    {
        var old = exe + OldSuffix;
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(downloaded, exe); }
        catch { File.Move(old, exe); throw; }
    }

    /// <summary>
    /// Removes what an update left behind: an unfinished download, and the previous exe (which may
    /// still be closing when the new one starts, so it is retried for a few seconds).
    /// </summary>
    public static async Task RemoveLeftoversAsync(string exe)
    {
        TryDelete(exe + NewSuffix);
        for (var attempt = 0; attempt < 10 && File.Exists(exe + OldSuffix); attempt++)
        {
            if (attempt > 0) await Task.Delay(1000);
            TryDelete(exe + OldSuffix);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
