using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace Aion.Launcher;

public sealed class SteamDownloadException(string message) : Exception(message);

/// <summary>
/// Downloads the supported build straight from Steam with the player's own account (SteamKit2):
/// QR sign-in in the Steam mobile app, the depot key and manifest request code for that one
/// manifest, then the chunks the target folder does not already hold (ClientPatch) from Steam's content servers. Each chunk is
/// checked by SteamKit2 (Adler) and against its SHA-1 id; a bad chunk is fetched again from
/// another server. Interrupted downloads resume: finished files are listed in a state file and
/// other existing chunks are kept when their SHA-1 matches.
/// </summary>
public sealed class SteamDownload : IDisposable
{
    public const string StateFile = ".download-state";
    const int ParallelFiles = 6, ParallelChunks = 16, Attempts = 8;

    public sealed record Status(long Bytes, long Total, string Phase);
    public sealed record SignIn(string Account, string RefreshToken);

    readonly SteamClient client = new();
    readonly CallbackManager callbacks;
    readonly SteamUser user;
    readonly SteamApps apps;
    readonly SteamContent content;
    readonly CancellationTokenSource stop = new();
    TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<EResult> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The URL to show as a QR code; raised again when Steam renews it.</summary>
    public event Action<string>? QrCode;

    public SteamDownload()
    {
        callbacks = new CallbackManager(client);
        user = client.GetHandler<SteamUser>()!;
        apps = client.GetHandler<SteamApps>()!;
        content = client.GetHandler<SteamContent>()!;
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(c =>
        {
            if (c.UserInitiated) return;
            connected.TrySetException(new SteamDownloadException("Could not connect to Steam."));
            loggedOn.TrySetException(new SteamDownloadException("Steam closed the connection."));
        });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn.TrySetResult(c.Result));
        new Thread(() => { while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250)); })
            { IsBackground = true, Name = "Steam callbacks" }.Start();
    }

    /// <summary>
    /// Signs in: with a saved refresh token when it still works, else by QR code (approved in the
    /// Steam mobile app). Returns the account and a refresh token the caller may keep for resuming.
    /// </summary>
    public async Task<SignIn> SignInAsync(SignIn? saved, CancellationToken ct)
    {
        if (saved is not null)
        {
            if (await LogOnAsync(saved, ct) == EResult.OK) return saved;
        }
        AuthPollResult result;
        while (true)
        {
            // Steam ends an unanswered QR session after a few renewals; start a new one until the
            // player scans it (or cancels).
            await ConnectAsync(ct);
            var session = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = "Private server launcher",
                IsPersistentSession = true,
            });
            session.ChallengeURLChanged = () => QrCode?.Invoke(session.ChallengeURL);
            QrCode?.Invoke(session.ChallengeURL);
            try { result = await session.PollingWaitForResultAsync(ct); break; }
            catch (AuthenticationException error) when (error.Result is not (EResult.Expired or EResult.Timeout))
            { throw new SteamDownloadException($"Steam sign-in was not approved ({error.Result})."); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is AuthenticationException or AsyncJobFailedException or TaskCanceledException)
            {
                client.Disconnect();
                await Task.Delay(1000, ct);
            }
        }
        var signIn = new SignIn(result.AccountName, result.RefreshToken);
        var logOn = await LogOnAsync(signIn, ct);
        if (logOn != EResult.OK) throw new SteamDownloadException($"Steam sign-in failed ({logOn}).");
        return signIn;
    }

    async Task ConnectAsync(CancellationToken ct)
    {
        if (client.IsConnected) return;
        connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    async Task<EResult> LogOnAsync(SignIn signIn, CancellationToken ct)
    {
        await ConnectAsync(ct);
        loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        user.LogOn(new SteamUser.LogOnDetails { Username = signIn.Account, AccessToken = signIn.RefreshToken, ShouldRememberPassword = true });
        var result = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (result != EResult.OK)
        {
            // Steam drops the connection after a refused logon; reconnect for the next attempt.
            client.Disconnect();
            await Task.Delay(500, ct);
        }
        return result;
    }

    /// <summary>
    /// Brings <paramref name="target"/> to the build (signed in first), fetching only what it does not already hold: the folder's
    /// <see cref="InstalledManifest"/> (or, for a folder from before it was written, the build's <c>previousManifests</c> fetched from
    /// Steam when the folder matches one) tells which pieces are there; unchanged files are skipped, changed ones are put together
    /// from the pieces at hand plus the missing ones from Steam (<see cref="ClientPatch"/>), files the build dropped are removed.
    /// </summary>
    public async Task DownloadAsync(SupportedBuild build, string target, IProgress<Status> progress, CancellationToken ct)
    {
        uint appId = (uint)build.AppId, depotId = (uint)build.DepotId;
        progress.Report(new(0, build.TotalBytes, "Asking Steam for the game version…"));

        var key = await apps.GetDepotDecryptionKey(depotId, appId);
        if (key.Result != EResult.OK)
            throw new SteamDownloadException(key.Result == EResult.AccessDenied
                ? "This Steam account does not have the game. Add it to your Steam library, then try again."
                : $"Steam refused the game files ({key.Result}).");
        var servers = (await content.GetServersForSteamPipe())
            .Where(s => s.Type is "SteamCache" or "CDN" && (s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(appId)))
            .OrderBy(s => s.WeightedLoad).ToList();
        if (servers.Count == 0) throw new SteamDownloadException("Steam offered no download servers.");
        using var cdn = new Client(client);

        var manifest = await ManifestAsync(cdn, servers, appId, depotId, ulong.Parse(build.ManifestId), key.DepotKey, ct)
            ?? throw new SteamDownloadException("Steam did not allow downloading this game version.");
        var files = Listed(manifest);
        foreach (var file in build.Files)
            if (!files.TryGetValue(file.Path, out var listed) || (long)listed.TotalSize != file.Size)
                throw new SteamDownloadException($"Steam's file list differs from this server's version ({file.Path}).");
        var now = ToInstalled(build.ManifestId, build.Files.Select(f => f.Path), files);

        Directory.CreateDirectory(target);
        var old = InstalledManifest.Load(target);
        if (old is null && build.PreviousManifests is { Count: > 0 } previous && Directory.Exists(Path.Combine(target, "Aion2")))
        {
            progress.Report(new(0, build.TotalBytes, "Looking at the game files already in the folder…"));
            foreach (var id in previous)
            {
                if (await ManifestAsync(cdn, servers, appId, depotId, ulong.Parse(id), key.DepotKey, ct) is not { } before) continue;
                var candidate = ToInstalled(id, null, Listed(before));
                if (candidate.Matches(target)) { old = candidate; break; }
            }
        }
        var sources = ClientPatch.Index(old);
        // SteamKit fetches a piece by its own chunk record (compressed length and checksum included).
        var chunkData = new Dictionary<string, DepotManifest.ChunkData>(StringComparer.Ordinal);
        foreach (var path in now.Files.Keys)
            foreach (var chunk in files[path].Chunks)
                chunkData.TryAdd(Convert.ToHexStringLower(chunk.ChunkID!), chunk);

        var statePath = Path.Combine(target, StateFile);
        var finished = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(statePath))
            foreach (var line in await File.ReadAllLinesAsync(statePath, ct)) finished[line] = true;
        var stateLock = new object();

        long done = 0, total = build.TotalBytes, reported = 0;
        var phase = old is null ? "Downloading…" : $"Updating: about {ClientPatch.BytesToFetch(old, now) / 1e9:0.0} GB to download, the rest is already here…";
        progress.Report(new(0, total, phase));
        void Add(long bytes)
        {
            var at = Interlocked.Add(ref done, bytes);
            if (at - Interlocked.Read(ref reported) > 8 << 20 || at == total)
            {
                Interlocked.Exchange(ref reported, at);
                progress.Report(new(at, total, phase));
            }
        }

        var chunkSlots = new SemaphoreSlim(ParallelChunks);
        var serverIndex = 0;
        async Task<int> FetchAsync(ChunkRef piece, byte[] buffer, CancellationToken token)
        {
            var data = chunkData[piece.Sha1];
            await chunkSlots.WaitAsync(token);
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    var server = servers[(Interlocked.Increment(ref serverIndex) & int.MaxValue) % servers.Count];
                    try
                    {
                        var length = await cdn.DownloadDepotChunkAsync(depotId, data, server, buffer, key.DepotKey);
                        if (length != piece.Length || !SHA1.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(data.ChunkID))
                            throw new InvalidDataException("Chunk does not match its id");
                        return length;
                    }
                    catch (Exception error) when (attempt < Attempts && !token.IsCancellationRequested
                        && error is HttpRequestException or SteamKitWebRequestException or InvalidDataException or TaskCanceledException or IOException)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), token);
                    }
                }
            }
            finally { chunkSlots.Release(); }
        }

        await Parallel.ForEachAsync(now.Files, new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles, CancellationToken = ct }, async (entry, token) =>
        {
            var (path, file) = (entry.Key, entry.Value);
            var info = new FileInfo(Path.Combine(target, path));
            var skip = finished.ContainsKey(path) && info.Exists && info.Length == file.Size
                || old is not null && info.Exists && info.Length == file.Size && ClientPatch.Unchanged(old.Files.GetValueOrDefault(path), file)
                || old is null && await ClientPatch.AlreadyMatchesAsync(target, path, file, token);
            if (skip) Add(file.Size);
            else await ClientPatch.PatchFileAsync(target, path, file, sources, FetchAsync, Add, ParallelChunks, token);
            lock (stateLock) File.AppendAllText(statePath, path + "\n");
        });
        foreach (var path in ClientPatch.Removed(old, now))
        {
            var stale = Path.Combine(target, path);
            if (File.Exists(stale)) File.Delete(stale);
        }
        now.Save(target);
        File.Delete(statePath);
        progress.Report(new(total, total, old is null ? "Download finished." : "Update finished."));
    }

    /// <summary>A manifest from Steam, or null when Steam does not hand out its request code (an old or withdrawn version).</summary>
    async Task<DepotManifest?> ManifestAsync(Client cdn, IReadOnlyList<Server> servers, uint appId, uint depotId, ulong manifestId, byte[]? depotKey, CancellationToken ct)
    {
        var code = await content.GetManifestRequestCode(depotId, appId, manifestId, "public", null);
        if (code == 0) return null;
        for (var attempt = 0; ; attempt++)
        {
            try { return await cdn.DownloadManifestAsync(depotId, manifestId, code, servers[attempt % servers.Count], depotKey); }
            catch (Exception error) when (attempt < Attempts && error is HttpRequestException or SteamKitWebRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
    }

    static Dictionary<string, DepotManifest.FileData> Listed(DepotManifest manifest) =>
        (manifest.Files ?? []).Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
            .ToDictionary(f => f.FileName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    /// <summary>The pieces of the listed files (all files when <paramref name="only"/> is null) as an <see cref="InstalledManifest"/>.</summary>
    static InstalledManifest ToInstalled(string manifestId, IEnumerable<string>? only, IReadOnlyDictionary<string, DepotManifest.FileData> files)
    {
        var result = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in only ?? files.Keys)
        {
            var file = files[path];
            result[path] = new((long)file.TotalSize, file.Chunks.Select(c => new ChunkRef((long)c.Offset, (int)c.UncompressedLength, Convert.ToHexStringLower(c.ChunkID!))).ToList());
        }
        return new(manifestId, result);
    }

    public void Dispose()
    {
        stop.Cancel();
        if (client.IsConnected) { user.LogOff(); client.Disconnect(); }
    }
}
