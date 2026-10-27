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
/// manifest, then every chunk from Steam's content servers into the target folder. Each chunk is
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

    /// <summary>Downloads every file of the build into <paramref name="target"/> (signed in first).</summary>
    public async Task DownloadAsync(SupportedBuild build, string target, IProgress<Status> progress, CancellationToken ct)
    {
        uint appId = (uint)build.AppId, depotId = (uint)build.DepotId;
        var manifestId = ulong.Parse(build.ManifestId);
        progress.Report(new(0, build.TotalBytes, "Asking Steam for the game version…"));

        var key = await apps.GetDepotDecryptionKey(depotId, appId);
        if (key.Result != EResult.OK)
            throw new SteamDownloadException(key.Result == EResult.AccessDenied
                ? "This Steam account does not have the game. Add it to your Steam library, then try again."
                : $"Steam refused the game files ({key.Result}).");
        var code = await content.GetManifestRequestCode(depotId, appId, manifestId, "public", null);
        if (code == 0) throw new SteamDownloadException("Steam did not allow downloading this game version.");

        var servers = (await content.GetServersForSteamPipe())
            .Where(s => s.Type is "SteamCache" or "CDN" && (s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(appId)))
            .OrderBy(s => s.WeightedLoad).ToList();
        if (servers.Count == 0) throw new SteamDownloadException("Steam offered no download servers.");
        using var cdn = new Client(client);

        DepotManifest? manifest = null;
        for (var attempt = 0; manifest is null; attempt++)
        {
            try { manifest = await cdn.DownloadManifestAsync(depotId, manifestId, code, servers[attempt % servers.Count], key.DepotKey); }
            catch (Exception error) when (attempt < Attempts && error is HttpRequestException or SteamKitWebRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
        var files = (manifest.Files ?? []).Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
            .ToDictionary(f => f.FileName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        foreach (var file in build.Files)
            if (!files.TryGetValue(file.Path, out var listed) || (long)listed.TotalSize != file.Size)
                throw new SteamDownloadException($"Steam's file list differs from this server's version ({file.Path}).");

        Directory.CreateDirectory(target);
        var statePath = Path.Combine(target, StateFile);
        var finished = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(statePath))
            foreach (var line in await File.ReadAllLinesAsync(statePath, ct)) finished[line] = true;
        var stateLock = new object();

        long done = 0, total = build.TotalBytes, reported = 0;
        void Add(long bytes)
        {
            var now = Interlocked.Add(ref done, bytes);
            if (now - Interlocked.Read(ref reported) > 8 << 20 || now == total)
            {
                Interlocked.Exchange(ref reported, now);
                progress.Report(new(now, total, "Downloading…"));
            }
        }

        var chunkSlots = new SemaphoreSlim(ParallelChunks);
        var serverIndex = 0;
        await Parallel.ForEachAsync(build.Files, new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles, CancellationToken = ct }, async (file, token) =>
        {
            var path = Path.Combine(target, file.Path);
            var info = new FileInfo(path);
            if (finished.ContainsKey(file.Path) && info.Exists && info.Length == file.Size) { Add(file.Size); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existed = info.Exists && info.Length == file.Size;
            using (var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
            {
                if (RandomAccess.GetLength(handle) != file.Size) RandomAccess.SetLength(handle, file.Size);
                var chunks = files[file.Path].Chunks;
                await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = ParallelChunks, CancellationToken = token }, async (chunk, chunkToken) =>
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                    try
                    {
                        if (existed && await HasChunkAsync(handle, chunk, buffer, chunkToken)) { Add(chunk.UncompressedLength); return; }
                        await chunkSlots.WaitAsync(chunkToken);
                        try
                        {
                            for (var attempt = 1; ; attempt++)
                            {
                                var server = servers[(Interlocked.Increment(ref serverIndex) & int.MaxValue) % servers.Count];
                                try
                                {
                                    var length = await cdn.DownloadDepotChunkAsync(depotId, chunk, server, buffer, key.DepotKey);
                                    if (length != chunk.UncompressedLength || !SHA1.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(chunk.ChunkID))
                                        throw new InvalidDataException("Chunk does not match its id");
                                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, length), (long)chunk.Offset, chunkToken);
                                    break;
                                }
                                catch (Exception error) when (attempt < Attempts && !chunkToken.IsCancellationRequested
                                    && error is HttpRequestException or SteamKitWebRequestException or InvalidDataException or TaskCanceledException or IOException)
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), chunkToken);
                                }
                            }
                        }
                        finally { chunkSlots.Release(); }
                        Add(chunk.UncompressedLength);
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                });
            }
            lock (stateLock) File.AppendAllText(statePath, file.Path + "\n");
        });
        File.Delete(statePath);
        progress.Report(new(total, total, "Download finished."));
    }

    static async Task<bool> HasChunkAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, DepotManifest.ChunkData chunk, byte[] buffer, CancellationToken ct)
    {
        var length = (int)chunk.UncompressedLength;
        var read = 0;
        while (read < length)
        {
            var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read, length - read), (long)chunk.Offset + read, ct);
            if (n == 0) return false;
            read += n;
        }
        return SHA1.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(chunk.ChunkID);
    }

    public void Dispose()
    {
        stop.Cancel();
        if (client.IsConnected) { user.LogOff(); client.Disconnect(); }
    }
}
