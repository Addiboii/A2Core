using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;

namespace Aion.Host;

/// <summary>
/// Staff-only access to the wardrobe export (extracted meshes, textures, icons) on a public host.
/// Images, models and module imports cannot send the dashboard's bearer token, so a staff member's
/// dashboard exchanges it for a short-lived HttpOnly, SameSite=Strict cookie. Passes are kept in
/// memory (a restart asks staff to sign in again).
/// </summary>
public sealed class WardrobeAccess(TimeProvider clock)
{
    public const string Cookie = "aion_wardrobe";
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    readonly ConcurrentDictionary<string, DateTimeOffset> passes = new();

    public string Issue()
    {
        var now = clock.GetUtcNow();
        foreach (var stale in passes.Where(p => p.Value <= now).ToList()) passes.TryRemove(stale);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        passes[token] = now + Lifetime;
        return token;
    }

    public bool Valid(string? token) => token is { Length: 64 } && passes.TryGetValue(token, out var until) && until > clock.GetUtcNow();

    public void Revoke(string? token) { if (token is not null) passes.TryRemove(token, out _); }

    public static void Map(WebApplication app, bool secure)
    {
        var access = app.Services.GetRequiredService<WardrobeAccess>();
        // Only /wardrobe/... needs the pass; wardrobe-viewer.js is our own code and stays public.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/wardrobe") && !access.Valid(context.Request.Cookies[Cookie]))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
        app.MapPost("/api/admin/wardrobe-access", async (HttpContext context, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var account = await auth.AuthenticateAsync(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "", ct);
            if (account is null) return Results.Unauthorized();
            try { await game.AdminProfileAsync(account, ct); }   // throws "forbidden" without a staff role
            catch (GameplayException) { return Results.StatusCode(403); }
            context.Response.Cookies.Append(Cookie, access.Issue(), new CookieOptions
            {
                HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = Lifetime,
            });
            return Results.NoContent();
        }).RequireRateLimiting("admin");
        app.MapDelete("/api/admin/wardrobe-access", (HttpContext context) =>
        {
            access.Revoke(context.Request.Cookies[Cookie]);
            context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/", Secure = secure, SameSite = SameSiteMode.Strict });
            return Results.NoContent();
        });
    }
}
