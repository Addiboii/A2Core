using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aion.Application.Authentication;

namespace Aion.Host;

/// <summary>
/// Player downloads (the launcher) for signed-in accounts only. A browser download cannot send the
/// page's bearer token, so the page asks POST /api/downloads/launcher for a link with a random
/// ticket, valid five minutes (several requests, for resumed downloads), then follows it.
/// The launcher updates itself the same way: GET /api/downloads/launcher/manifest (no sign-in: a
/// hash and size are not secret) tells it what the server's file is, and a launcher that differs
/// downloads it through a ticket link.
/// </summary>
public sealed class Downloads(string folder, TimeProvider clock)
{
    /// <summary>The file in the downloads folder, and the name the browser saves it as (the route is /download/launcher).</summary>
    public const string LauncherFile = "A2Core Launcher.exe";
    const string Route = "/download/launcher";
    /// <summary>Optional text file next to the exe with its version, shown by the launcher before it updates.</summary>
    public const string VersionFile = LauncherFile + ".version";
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    readonly ConcurrentDictionary<string, DateTimeOffset> tickets = new();
    (long Length, DateTime Written, string Sha256)? hashed;

    public bool LauncherAvailable => File.Exists(Path.Combine(folder, LauncherFile));

    string Issue()
    {
        var now = clock.GetUtcNow();
        foreach (var stale in tickets.Where(t => t.Value <= now).ToList()) tickets.TryRemove(stale);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = now + Lifetime;
        return ticket;
    }

    bool Valid(string? ticket) => ticket is { Length: 64 } && tickets.TryGetValue(ticket, out var until) && until > clock.GetUtcNow();

    /// <summary>The launcher file's hash (computed once per file version), size and version text; null without a launcher.</summary>
    async Task<object?> ManifestAsync(CancellationToken ct)
    {
        var info = new FileInfo(Path.Combine(folder, LauncherFile));
        if (!info.Exists) return null;
        string sha256;
        if (hashed is { } cached && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc) sha256 = cached.Sha256;
        else
        {
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            hashed = (info.Length, info.LastWriteTimeUtc, sha256);
        }
        var versionPath = Path.Combine(folder, VersionFile);
        var version = File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath, ct)).Trim() : "";
        return new { sha256, size = info.Length, version = version.Length > 0 ? version : null };
    }

    public void Map(WebApplication app)
    {
        app.MapPost("/api/downloads/launcher", async (HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "", ct) is null)
                return Results.Unauthorized();
            if (!LauncherAvailable) return Results.NotFound();
            return Results.Ok(new { url = $"{Route}?ticket={Issue()}" });
        }).RequireRateLimiting("account-entry");
        app.MapGet("/api/downloads/launcher/manifest", async (CancellationToken ct) =>
            await ManifestAsync(ct) is { } manifest ? Results.Ok(manifest) : Results.NotFound());
        app.MapGet(Route, (string? ticket) =>
            Valid(ticket) && LauncherAvailable
                ? Results.File(Path.Combine(folder, LauncherFile), "application/octet-stream", LauncherFile, enableRangeProcessing: true)
                : Results.NotFound());
    }
}
