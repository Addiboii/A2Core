using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

public sealed record BuildFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("quick")] bool Quick);

/// <summary>The one client build the server supports (embedded supported-build.json, tools/launcher_build.py).</summary>
public sealed record SupportedBuild(
    [property: JsonPropertyName("appId")] int AppId,
    [property: JsonPropertyName("depotId")] int DepotId,
    [property: JsonPropertyName("manifestId")] string ManifestId,
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("files")] IReadOnlyList<BuildFile> Files)
{
    public static SupportedBuild Embedded { get; } = Load();

    static SupportedBuild Load()
    {
        using var stream = typeof(SupportedBuild).Assembly.GetManifestResourceStream("supported-build.json")
            ?? throw new InvalidOperationException("supported-build.json is not embedded");
        return JsonSerializer.Deserialize<SupportedBuild>(stream) ?? throw new InvalidDataException("supported-build.json");
    }

    /// <summary>Steam console command that downloads exactly this build with the player's own Steam account.</summary>
    public string DownloadCommand => $"download_depot {AppId} {DepotId} {ManifestId}";
}

public enum CheckLevel { Sizes, Quick, Full }

public sealed record BuildCheck(bool Ok, string? Problem, int Checked)
{
    public static BuildCheck Pass(int count) => new(true, null, count);
    public static BuildCheck Fail(string problem, int count) => new(false, problem, count);
}

public static class ClientFiles
{
    /// <summary>
    /// Checks a folder against the supported build: every file's size (instant), the quick set's
    /// hashes (game binary and pak index files, a few seconds) or every hash (minutes).
    /// </summary>
    public static async Task<BuildCheck> CheckAsync(string folder, SupportedBuild build, CheckLevel level,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var file in build.Files)
        {
            var path = Path.Combine(folder, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists) return BuildCheck.Fail($"Missing {file.Path}", count);
            if (info.Length != file.Size) return BuildCheck.Fail($"Different size: {file.Path}", count);
            count++;
        }
        if (level == CheckLevel.Sizes) return BuildCheck.Pass(count);
        var hashed = build.Files.Where(f => level == CheckLevel.Full || f.Quick).ToList();
        long total = hashed.Sum(f => f.Size), done = 0;
        foreach (var file in hashed)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = new FileStream(Path.Combine(folder, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != file.Sha256) return BuildCheck.Fail($"Different content: {file.Path}", count);
            done += file.Size;
            progress?.Report((done, total));
        }
        return BuildCheck.Pass(hashed.Count);
    }

    /// <summary>Bytes of the build already present in a folder (each file counted up to its expected size): download progress.</summary>
    public static long PresentBytes(string folder, SupportedBuild build)
    {
        long total = 0;
        foreach (var file in build.Files)
        {
            var info = new FileInfo(Path.Combine(folder, file.Path));
            if (info.Exists) total += Math.Min(info.Length, file.Size);
        }
        return total;
    }

    /// <summary>Same volume (hard links and moves are instant there).</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public enum Mode { Copy, HardLink, Move }

    /// <summary>
    /// Places the build's files from <paramref name="source"/> into <paramref name="target"/>: copies,
    /// hard links (same volume, no extra space) or moves (Steam's download_depot folder). Only the
    /// listed files are touched.
    /// </summary>
    public static async Task PlaceAsync(string source, string target, SupportedBuild build, Mode mode,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        long total = build.TotalBytes, done = 0;
        foreach (var file in build.Files)
        {
            ct.ThrowIfCancellationRequested();
            var from = Path.Combine(source, file.Path);
            var to = Path.Combine(target, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            var existing = new FileInfo(to);
            if (existing.Exists && existing.Length == file.Size && mode != Mode.Move) { done += file.Size; progress?.Report((done, total)); continue; }
            if (existing.Exists) existing.Delete();
            switch (mode)
            {
                case Mode.Move: File.Move(from, to); break;
                case Mode.HardLink:
                    if (!OperatingSystem.IsWindows() || !CreateHardLink(to, from, IntPtr.Zero))
                        throw new IOException($"Could not link {file.Path} (error {Marshal.GetLastPInvokeError()})");
                    break;
                default:
                    await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
                    await using (var output = new FileStream(to + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[1 << 20];
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            done += read;
                            progress?.Report((done, total));
                        }
                    }
                    File.Move(to + ".part", to, overwrite: true);
                    continue;
            }
            done += file.Size;
            progress?.Report((done, total));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
