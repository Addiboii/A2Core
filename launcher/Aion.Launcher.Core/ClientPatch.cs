using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>One piece of a depot file: where it sits, how long it is, and its SHA-1 (Steam's chunk id, lowercase hex).</summary>
public sealed record ChunkRef(
    [property: JsonPropertyName("o")] long Offset,
    [property: JsonPropertyName("l")] int Length,
    [property: JsonPropertyName("s")] string Sha1);

public sealed record ManifestFile(
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("chunks")] IReadOnlyList<ChunkRef> Chunks);

/// <summary>
/// The pieces of the build a folder holds (written after a finished download, <see cref="FileName"/> in that folder): the next
/// build's download reuses every piece the folder already has, wherever it sits, and fetches only the rest, as Steam does.
/// </summary>
public sealed record InstalledManifest(
    [property: JsonPropertyName("manifestId")] string ManifestId,
    [property: JsonPropertyName("files")] Dictionary<string, ManifestFile> Files)
{
    public const string FileName = ".installed-build.json";

    public static InstalledManifest? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var loaded = JsonSerializer.Deserialize<InstalledManifest>(File.ReadAllText(path));
            return loaded is null ? null : loaded with { Files = new(loaded.Files, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception error) when (error is JsonException or IOException) { return null; }
    }

    public void Save(string folder)
    {
        var path = Path.Combine(folder, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Does <paramref name="folder"/> look like it holds this manifest's files? Most listed files present at their listed size. A wrong
    /// guess only costs speed: every reused piece is checked against its SHA-1 before it is used.
    /// </summary>
    public bool Matches(string folder)
    {
        if (Files.Count == 0) return false;
        var present = Files.Count(f => new FileInfo(Path.Combine(folder, f.Key)) is { Exists: true } info && info.Length == f.Value.Size);
        return present * 2 >= Files.Count;
    }
}

/// <summary>Where a piece can be read from in the folder as it is now.</summary>
public sealed record ChunkSource(string Path, long Offset);

/// <summary>
/// Builds one build's files from another's: every piece whose SHA-1 the folder already has is copied from there, the rest is fetched
/// (Steam's content servers in <see cref="SteamDownload"/>). A changed file is put together beside the old one (<c>.patch</c>) and
/// then moved over it, so an old file is never written in place (it may be a hard link into the player's own Steam install, or the
/// source of pieces for another file).
/// </summary>
public static class ClientPatch
{
    public const string PartSuffix = ".patch";

    /// <summary>Every piece of the old build by its SHA-1 (the first place it sits).</summary>
    public static Dictionary<string, ChunkSource> Index(InstalledManifest? old)
    {
        var index = new Dictionary<string, ChunkSource>(StringComparer.Ordinal);
        if (old is null) return index;
        foreach (var (path, file) in old.Files)
            foreach (var chunk in file.Chunks)
                index.TryAdd(chunk.Sha1, new(path, chunk.Offset));
        return index;
    }

    /// <summary>Same size and the same pieces at the same places: nothing to do.</summary>
    public static bool Unchanged(ManifestFile? before, ManifestFile after) =>
        before is not null && before.Size == after.Size && before.Chunks.Count == after.Chunks.Count
        && before.Chunks.OrderBy(c => c.Offset).Zip(after.Chunks.OrderBy(c => c.Offset)).All(p => p.First.Offset == p.Second.Offset && p.First.Sha1 == p.Second.Sha1);

    /// <summary>Bytes that have to be fetched: pieces of changed files the old build does not have.</summary>
    public static long BytesToFetch(InstalledManifest? old, InstalledManifest now)
    {
        var index = Index(old);
        long total = 0;
        foreach (var (path, file) in now.Files)
        {
            if (Unchanged(old?.Files.GetValueOrDefault(path), file)) continue;
            total += file.Chunks.Where(c => !index.ContainsKey(c.Sha1)).Sum(c => (long)c.Length);
        }
        return total;
    }

    /// <summary>Files of the old build that the new one no longer has (only these are ever removed).</summary>
    public static IReadOnlyList<string> Removed(InstalledManifest? old, InstalledManifest now) =>
        old is null ? [] : old.Files.Keys.Where(path => !now.Files.ContainsKey(path)).ToList();

    /// <summary>
    /// Puts one file of the new build together in <paramref name="folder"/>. Each piece comes from, in order: the file's own
    /// <c>.patch</c> (an interrupted run), the old build's piece with the same SHA-1 (<paramref name="sources"/>), the old file at the
    /// same place (a folder without a manifest), else <paramref name="fetch"/>. <paramref name="done"/> gets every piece's length.
    /// </summary>
    public static async Task PatchFileAsync(string folder, string path, ManifestFile file, IReadOnlyDictionary<string, ChunkSource> sources,
        Func<ChunkRef, byte[], CancellationToken, Task<int>> fetch, Action<long> done, int parallel, CancellationToken ct)
    {
        var destination = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + PartSuffix;
        var resuming = File.Exists(part);
        using (var handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            if (RandomAccess.GetLength(handle) != file.Size) RandomAccess.SetLength(handle, file.Size);
            await Parallel.ForEachAsync(file.Chunks, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (chunk, token) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                try
                {
                    if (resuming && await ReadMatchingAsync(handle, chunk.Offset, chunk, buffer, token)) { done(chunk.Length); return; }
                    var found = sources.TryGetValue(chunk.Sha1, out var source) && await ReadFromAsync(Path.Combine(folder, source.Path), source.Offset, chunk, buffer, token)
                        || await ReadFromAsync(destination, chunk.Offset, chunk, buffer, token);
                    if (!found)
                    {
                        var length = await fetch(chunk, buffer, token);
                        if (length != chunk.Length || !Matches(buffer, chunk)) throw new InvalidDataException($"Piece {chunk.Sha1} of {path} does not match its id");
                    }
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, chunk.Length), chunk.Offset, token);
                    done(chunk.Length);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            });
        }
        await ReplaceAsync(part, destination, ct);
    }

    /// <summary>Does the file already hold every piece at its place (a folder without a manifest)? Read only.</summary>
    public static async Task<bool> AlreadyMatchesAsync(string folder, string path, ManifestFile file, CancellationToken ct)
    {
        var destination = Path.Combine(folder, path);
        if (new FileInfo(destination) is not { Exists: true } info || info.Length != file.Size) return false;
        var buffer = new byte[file.Chunks.Count == 0 ? 0 : file.Chunks.Max(c => c.Length)];
        using var handle = File.OpenHandle(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        foreach (var chunk in file.Chunks)
            if (!await ReadMatchingAsync(handle, chunk.Offset, chunk, buffer, ct)) return false;
        return true;
    }

    /// <summary>Moves the finished file over the old one; another file's patch may be reading that one for a moment, so it is retried.</summary>
    static async Task ReplaceAsync(string part, string destination, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(part, destination, overwrite: true); return; }
            catch (Exception error) when (attempt < 40 && error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    static async Task<bool> ReadFromAsync(string path, long offset, ChunkRef chunk, byte[] buffer, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            return await ReadMatchingAsync(handle, offset, chunk, buffer, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static async Task<bool> ReadMatchingAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, ChunkRef chunk, byte[] buffer, CancellationToken ct)
    {
        if (offset + chunk.Length > RandomAccess.GetLength(handle)) return false;
        var read = 0;
        while (read < chunk.Length)
        {
            var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read, chunk.Length - read), offset + read, ct);
            if (n == 0) return false;
            read += n;
        }
        return Matches(buffer, chunk);
    }

    static bool Matches(byte[] buffer, ChunkRef chunk) =>
        Convert.ToHexStringLower(SHA1.HashData(buffer.AsSpan(0, chunk.Length))) == chunk.Sha1;
}
