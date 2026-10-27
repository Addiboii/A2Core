using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

public sealed class AccountException(string message) : Exception(message);

/// <summary>
/// The account site's API: sign in (optionally a 30-day session the launcher keeps), one launch
/// authorization per game start, the launcher's own update, sign out.
/// </summary>
public sealed class AccountClient : IDisposable
{
    readonly HttpMessageHandler transport;
    readonly HttpClient http, files;
    string? token;

    public AccountClient(LauncherConfig config, HttpMessageHandler? handler = null)
    {
        transport = handler ?? new HttpClientHandler();
        var account = new Uri(config.AccountUrl);
        // The API answers within 20 seconds; the launcher download runs as long as it needs (cancellation stops it).
        http = new HttpClient(transport, disposeHandler: false) { BaseAddress = account, Timeout = TimeSpan.FromSeconds(20) };
        files = new HttpClient(transport, disposeHandler: false) { BaseAddress = account, Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool SignedIn => token is not null;
    /// <summary>The current session's bearer token (kept, encrypted, for "Keep me signed in").</summary>
    public string? Token => token;
    public DateTimeOffset? ExpiresAt { get; private set; }

    sealed record Session([property: JsonPropertyName("token")] string Token, [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);
    sealed record DownloadLink([property: JsonPropertyName("url")] string Url);
    public sealed record Launch(
        [property: JsonPropertyName("authorization")] string Authorization,
        [property: JsonPropertyName("worldId")] int WorldId,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
    /// <summary>The server's launcher file: what this exe compares itself with.</summary>
    public sealed record LauncherManifest(
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("version")] string? Version);

    /// <summary><paramref name="remember"/> asks for a 30-day session instead of the standard 8 hours.</summary>
    public async Task SignInAsync(string username, string password, bool remember = false, CancellationToken ct = default)
    {
        using var response = await Send(HttpMethod.Post, "/api/accounts/login", new { username, password, remember }, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AccountException("Wrong username or password.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new AccountException("Too many attempts. Wait a minute and try again.");
        if (!response.IsSuccessStatusCode) throw new AccountException($"The account server answered {(int)response.StatusCode}.");
        var session = await response.Content.ReadFromJsonAsync<Session>(ct) ?? throw new AccountException("The account server sent no session.");
        token = session.Token;
        ExpiresAt = session.ExpiresAt;
    }

    /// <summary>Continues a kept session. False when the server no longer accepts it (it ended or was signed out elsewhere).</summary>
    public async Task<bool> ResumeAsync(string savedToken, CancellationToken ct = default)
    {
        token = savedToken;
        using var response = await Send(HttpMethod.Get, "/api/accounts/me", null, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { token = null; return false; }
        if (!response.IsSuccessStatusCode) { token = null; throw new AccountException($"The account server answered {(int)response.StatusCode}."); }
        return true;
    }

    /// <summary>A one-use launch authorization (valid five minutes); the client redeems it through the bootstrap URL.</summary>
    public async Task<Launch> AuthorizeLaunchAsync(CancellationToken ct = default)
    {
        using var response = await Send(HttpMethod.Post, "/api/client-launches", null, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { token = null; throw new AccountException("Your session ended. Sign in again."); }
        if (!response.IsSuccessStatusCode) throw new AccountException($"The account server answered {(int)response.StatusCode}.");
        var launch = await response.Content.ReadFromJsonAsync<Launch>(ct) ?? throw new AccountException("The account server sent no launch.");
        if (launch.Authorization.Length != 64 || !launch.Authorization.All(char.IsAsciiHexDigit)) throw new AccountException("Unexpected launch authorization.");
        return launch;
    }

    /// <summary>The server's launcher file, or null when the server offers none.</summary>
    public async Task<LauncherManifest?> LauncherManifestAsync(CancellationToken ct = default)
    {
        using var response = await Send(HttpMethod.Get, "/api/downloads/launcher/manifest", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new AccountException($"The account server answered {(int)response.StatusCode}.");
        var manifest = await response.Content.ReadFromJsonAsync<LauncherManifest>(ct) ?? throw new AccountException("The account server sent no launcher manifest.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(char.IsAsciiHexDigit) || manifest.Size <= 0) throw new AccountException("Unexpected launcher manifest.");
        return manifest;
    }

    /// <summary>Downloads the server's launcher (signed in: through a ticket link) to a file. The caller checks its hash.</summary>
    public async Task DownloadLauncherAsync(string to, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        using var link = await Send(HttpMethod.Post, "/api/downloads/launcher", null, ct);
        if (link.StatusCode == HttpStatusCode.Unauthorized) { token = null; throw new AccountException("Your session ended. Sign in again."); }
        if (link.StatusCode == HttpStatusCode.NotFound) throw new AccountException("The server has no launcher download.");
        if (!link.IsSuccessStatusCode) throw new AccountException($"The account server answered {(int)link.StatusCode}.");
        var url = (await link.Content.ReadFromJsonAsync<DownloadLink>(ct))?.Url ?? throw new AccountException("The account server sent no download link.");
        if (!url.StartsWith('/')) throw new AccountException("Unexpected download link.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendRaw(files, request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new AccountException($"The launcher download answered {(int)response.StatusCode}.");
        var total = response.Content.Headers.ContentLength ?? 0;
        long done = 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }
    }

    public async Task SignOutAsync()
    {
        if (token is null) return;
        try { using var _ = await Send(HttpMethod.Post, "/api/accounts/logout", null, CancellationToken.None); }
        catch (AccountException) { }
        token = null;
        ExpiresAt = null;
    }

    async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await SendRaw(http, request, HttpCompletionOption.ResponseContentRead, ct);
    }

    async Task<HttpResponseMessage> SendRaw(HttpClient client, HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try { return await client.SendAsync(request, completion, ct); }
        catch (HttpRequestException error) { throw new AccountException($"Could not reach the account server ({error.Message})."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("The account server did not answer in time."); }
    }

    public void Dispose()
    {
        http.Dispose();
        files.Dispose();
        transport.Dispose();
    }
}
