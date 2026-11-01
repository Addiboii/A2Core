using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

public sealed record BuildFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("quick")] bool Quick,
    [property: JsonPropertyName("sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonPropertyName("sha1"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha1 = null);

/// <summary>Steam app/depot ids and paths shared by the launcher and build tools.</summary>
public static class SupportedBuildIds
{
    public const int AppId = 3393110;
    public const int DepotId = 3393111;
    public const string Executable = "Aion2/Binaries/Win64/AION2.exe";
}

/// <summary>The one client build the server supports (embedded supported-build.json, build tools).</summary>
public sealed record SupportedBuild(
    [property: JsonPropertyName("appId")] int AppId,
    [property: JsonPropertyName("depotId")] int DepotId,
    [property: JsonPropertyName("manifestId")] string ManifestId,
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("files")] IReadOnlyList<BuildFile> Files,
    [property: JsonPropertyName("steamBuildId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SteamBuildId = null)
{
    public static SupportedBuild Embedded { get; } = Load();

    static SupportedBuild Load()
    {
        using var stream = typeof(SupportedBuild).Assembly.GetManifestResourceStream("supported-build.json")
            ?? throw new InvalidOperationException("supported-build.json is not embedded");
        return SupportedBuildJson.Read(stream);
    }

    /// <summary>Steam console command that downloads exactly this build with the player's own Steam account.</summary>
    public string DownloadCommand => $"download_depot {AppId} {DepotId} {ManifestId}";
}

public static class SupportedBuildJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SupportedBuild Read(Stream stream) =>
        JsonSerializer.Deserialize<SupportedBuild>(stream, Options) ?? throw new InvalidDataException("supported-build.json");

    public static SupportedBuild Read(string json) =>
        JsonSerializer.Deserialize<SupportedBuild>(json, Options) ?? throw new InvalidDataException("supported-build.json");

    public static void Write(Stream stream, SupportedBuild build) =>
        JsonSerializer.Serialize(stream, build, Options);

    public static string WriteText(SupportedBuild build)
    {
        using var buffer = new MemoryStream();
        Write(buffer, build);
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>One digest over the quick set (path, size, sha256), to name the build in logs and support.</summary>
    public static string ComputeFingerprint(IEnumerable<BuildFile> files)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Where(f => f.Quick).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(file.Sha256)) throw new InvalidOperationException($"Quick file {file.Path} has no sha256.");
            var line = $"{file.Path.Replace('\\', '/').ToLowerInvariant()}\0{file.Size}\0{file.Sha256.ToLowerInvariant()}\n";
            digest.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexStringLower(digest.GetCurrentHash());
    }

    /// <summary>Files hashed before every launch: the game binary and the pak index/signature files.</summary>
    public static bool IsQuickFile(string path, string executable = SupportedBuildIds.Executable)
    {
        var lower = path.Replace('\\', '/');
        if (lower.Equals(executable.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
        lower = lower.ToLowerInvariant();
        return lower.StartsWith("aion2/content/paks/", StringComparison.Ordinal)
            && (lower.EndsWith(".utoc", StringComparison.Ordinal) || lower.EndsWith(".sig", StringComparison.Ordinal));
    }
}

public enum CheckLevel { Sizes, Quick, Full }

public sealed record BuildCheck(bool Ok, string? Problem, int Checked)
{
    public static BuildCheck Pass(int count) => new(true, null, count);
    public static BuildCheck Fail(string problem, int count) => new(false, problem, count);
}

public sealed record BuildSurvey(long PresentBytes, IReadOnlyList<string> ToDownload)
{
    public long StillNeeded(SupportedBuild build) => build.TotalBytes - PresentBytes;
    public bool Complete => ToDownload.Count == 0;
}

public static class ClientFiles
{
    /// <summary>The folder that holds the game (the path itself, an AION2 child, or its parent).</summary>
    public static string? ResolveGameFolder(string candidate, SupportedBuild build)
    {
        foreach (var folder in new[] { candidate, Path.Combine(candidate, "AION2"), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate)) })
        {
            if (folder is null) continue;
            if (File.Exists(Path.Combine(folder, build.Executable))) return Path.GetFullPath(folder);
        }
        return null;
    }

    /// <summary>Which manifest files are missing, the wrong size, or fail the quick hash set.</summary>
    public static async Task<BuildSurvey> SurveyAsync(string folder, SupportedBuild build, CancellationToken ct = default)
    {
        var toDownload = new List<string>();
        long present = 0;
        foreach (var file in build.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists) { toDownload.Add(file.Path); continue; }
            if (info.Length != file.Size) { toDownload.Add(file.Path); continue; }
            present += file.Size;
            if (!file.Quick) continue;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != file.Sha256) toDownload.Add(file.Path);
        }
        return new(present, toDownload);
    }

    /// <summary>Every file that fails a full hash check (for repair).</summary>
    public static async Task<IReadOnlyList<string>> FindHashMismatchesAsync(string folder, SupportedBuild build,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var bad = new List<string>();
        long total = build.TotalBytes, done = 0;
        foreach (var file in build.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) { bad.Add(file.Path); done += file.Size; progress?.Report((done, total)); continue; }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != file.Sha256) bad.Add(file.Path);
            done += file.Size;
            progress?.Report((done, total));
        }
        return bad;
    }

    /// <summary>
    /// Checks a folder against the supported build: every file's size (instant), the quick set's
    /// hashes (game binary and pak index files, a few seconds) or every hash (minutes).
    /// Non-quick files without sha256 are checked with sha1 from the manifest (full verify/repair).
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
            if (!string.IsNullOrEmpty(file.Sha256))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (hash != file.Sha256) return BuildCheck.Fail($"Different content: {file.Path}", count);
            }
            else
            {
                if (string.IsNullOrEmpty(file.Sha1)) return BuildCheck.Fail($"No hash for {file.Path}", count);
                var hash = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct));
                if (hash != file.Sha1) return BuildCheck.Fail($"Different content: {file.Path}", count);
            }
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
    /// <summary>Places only files that match size (and quick hashes when present in the source).</summary>
    public static async Task PlaceMatchingAsync(string source, string target, SupportedBuild build, Mode mode,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        long total = build.TotalBytes, done = 0;
        foreach (var file in build.Files)
        {
            ct.ThrowIfCancellationRequested();
            var from = Path.Combine(source, file.Path);
            if (!File.Exists(from) || new FileInfo(from).Length != file.Size) { done += file.Size; progress?.Report((done, total)); continue; }
            if (file.Quick)
            {
                await using var stream = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != file.Sha256)
                { done += file.Size; progress?.Report((done, total)); continue; }
            }
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
                        while ((read = await input.ReadAsync(buffer, ct)) > 0) await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    File.Move(to + ".part", to, overwrite: true);
                    break;
            }
            done += file.Size;
            progress?.Report((done, total));
        }
    }

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
