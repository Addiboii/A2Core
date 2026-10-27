using Aion.Application.Authentication;
using Aion.Application.Chat;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Chat;
using Aion.Host.Native;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// Staff dashboard live tools: announcements (a notice in the middle of the screen and/or chat), actions on a player in
/// the world (message, kick, revive, unstuck, teleport), server health, the activity log and a scheduled restart.
/// Every request is role-checked in the store; actions are recorded as staff_* audit events.
/// </summary>
public static class StaffEndpoints
{
    sealed record Announcement(string? Text, bool Notice = true, bool Chat = true, int Size = 20, string? Color = null);
    sealed record PlayerMessage(string? Text);
    sealed record KickRequest(string? Reason);
    sealed record TeleportRequest(int MapId, double? X = null, double? Y = null, double? Z = null);
    sealed record RestartRequest(int Minutes, string? Reason);

    public static void MapStaffEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/admin").RequireRateLimiting("admin");
        staff.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });

        MapImages(staff);

        staff.MapPost("/announce", async (Announcement input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            if (services.GetService<WorldPresence>() is not { } presence) return Results.NotFound();
            var text = (input.Text ?? "").Trim();
            if (text.Length is 0 or > 200 || text.Any(char.IsControl) || !input.Notice && !input.Chat) return Results.BadRequest(new { error = "invalid_announcement" });
            var color = ValidColor(input.Color) ? input.Color! : "FFFFFF";
            var shown = 0;
            if (input.Notice)
            {
                var packet = GameSession.Notice(text, Math.Clamp(input.Size, 10, 60), color);
                foreach (var entry in presence.All)
                {
                    _ = entry.Session.InvokeAsync(_ => (true, new List<byte[]> { packet }));
                    shown++;
                }
            }
            var chatted = input.Chat ? await ChatAnnounceAsync(services, text) : 0;
            await game.AdminRecordAsync(account, "staff_announce", null, new { text, input.Notice, input.Chat, shown, chatted }, ct);
            return Results.Ok(new { shown, chatted });
        });

        staff.MapPost("/players/{name}/message", async (string name, PlayerMessage input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            var presence = services.GetService<WorldPresence>();
            var text = (input.Text ?? "").Trim();
            if (text.Length is 0 or > 200 || text.Any(char.IsControl)) return Results.BadRequest(new { error = "invalid_announcement" });
            return await OnPlayerAsync(name, presence, game, account, "staff_message", new { text }, (_, _) => [GameSession.Notice(text)], ct);
        });

        staff.MapPost("/players/{name}/kick", async (string name, KickRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            var presence = services.GetService<WorldPresence>();
            var reason = (input.Reason ?? "").Trim();
            if (reason.Length > 120 || reason.Any(char.IsControl)) return Results.BadRequest(new { error = "invalid_reason" });
            return await OnPlayerAsync(name, presence, game, account, "staff_kick", new { reason }, (s, _) => s.StaffKick(reason), ct);
        });

        staff.MapPost("/players/{name}/restore", async (string name, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            var presence = services.GetService<WorldPresence>();
            return await OnPlayerAsync(name, presence, game, account, "staff_restore", new { }, (s, now) => s.StaffRestore(now), ct);
        });

        staff.MapPost("/players/{name}/unstuck", async (string name, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            var presence = services.GetService<WorldPresence>();
            return await OnPlayerAsync(name, presence, game, account, "staff_unstuck", new { }, (s, now) => s.StaffUnstuck(now), ct);
        });

        staff.MapPost("/players/{name}/teleport", async (string name, TeleportRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is not { } account) return Results.Unauthorized();
            var presence = services.GetService<WorldPresence>();
            Vector3d? at = input is { X: { } x, Y: { } y, Z: { } z } ? new Vector3d(x, y, z) : null;
            if (input.MapId <= 0 || at is { IsFinite: false }) return Results.BadRequest(new { error = "invalid_destination" });
            return await OnPlayerAsync(name, presence, game, account, "staff_teleport", new { input.MapId, input.X, input.Y, input.Z },
                (s, now) => s.StaffTeleport(input.MapId, at, now), ct);
        });

        // Server health for the overview: players, CPU, memory, how busy the world lock is (since the last request).
        staff.MapGet("/server", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            if (services.GetService<WorldLoadReporter>() is not { } load || services.GetService<WorldPresence>() is not { } presence) return Results.NotFound();
            return Results.Ok(load.Health(presence, services.GetService<ScheduledRestart>()?.At));
        });

        staff.MapGet("/audit", async (string? action, Guid? character, long? before, int? limit, HttpContext ctx, IAccountAuthentication auth,
            IGameplayStore game, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminAuditAsync(account, action, character, before, limit ?? 100, ct));
        });

        // Admins: a restart with countdown notices (notice and chat at 5 min, 1 min, 30 s, 10 s), then a clean stop
        // (everyone saved on disconnect) and a failure exit code so systemd (Restart=on-failure) starts the host again.
        staff.MapPost("/restart", async (RestartRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct, admin: true) is not { } account) return Results.Unauthorized();
            if (services.GetService<ScheduledRestart>() is not { } restart) return Results.NotFound();
            if (input.Minutes is < 0 or > 120 || (input.Reason ?? "").Length > 100) return Results.BadRequest(new { error = "invalid_restart" });
            var at = restart.Schedule(TimeSpan.FromMinutes(input.Minutes), (input.Reason ?? "").Trim());
            await game.AdminRecordAsync(account, "staff_restart_scheduled", null, new { input.Minutes, input.Reason, at }, ct);
            return Results.Ok(new { at });
        });
        staff.MapDelete("/restart", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct, admin: true) is not { } account) return Results.Unauthorized();
            if (services.GetService<ScheduledRestart>() is not { } restart) return Results.NotFound();
            var cancelled = restart.Cancel();
            if (cancelled) await game.AdminRecordAsync(account, "staff_restart_cancelled", null, new { }, ct);
            return Results.Ok(new { cancelled });
        });
    }

    /// <summary>
    /// The game's world-map images for the overview (tools/MapImageExport): Aion:MapImagesPath, else research/local/map-images
    /// above the host. maps.json lists each map's file, size and world-to-pixel transform.
    /// </summary>
    static string? MapImagesRoot(IConfiguration config)
    {
        if (config["Aion:MapImagesPath"] is { Length: > 0 } configured) return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "research", "local", "map-images"))) return Path.Combine(dir.FullName, "research", "local", "map-images");
        return null;
    }

    static void MapImages(RouteGroupBuilder staff)
    {
        staff.MapGet("/maps", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            var root = MapImagesRoot(config);
            var index = root is null ? null : Path.Combine(root, "maps.json");
            return index is not null && File.Exists(index) ? Results.File(index, "application/json") : Results.Ok(new { });
        });
        staff.MapGet("/maps/{id:int}/image", async (int id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            if (MapImagesRoot(config) is not { } root) return Results.NotFound();
            foreach (var ext in new[] { ".webp", ".jpg", ".png" })
            {
                var file = Path.Combine(root, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);
                if (File.Exists(file))
                {
                    ctx.Response.Headers.CacheControl = "private, max-age=86400";
                    return Results.File(file, ext switch { ".webp" => "image/webp", ".jpg" => "image/jpeg", _ => "image/png" });
                }
            }
            return Results.NotFound();
        });
    }

    static bool ValidColor(string? color) => color is { Length: 6 or 8 } && color.All(char.IsAsciiHexDigit);

    /// <summary>The account when it holds a staff role (admin: the admin role); a forbidden GameplayException otherwise.</summary>
    public static async Task<VerifiedAccount?> StaffAsync(HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct, bool admin = false)
    {
        var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
        if (account is null) return null;
        var profile = await game.AdminProfileAsync(account, ct);
        if (admin && !profile.Roles.Contains("admin")) throw new GameplayException("forbidden");
        return account;
    }

    /// <summary>Runs a staff action on the player's own loop and records it.</summary>
    static async Task<IResult> OnPlayerAsync(string name, WorldPresence? presence, IGameplayStore game, VerifiedAccount account, string action, object details,
        Func<GameSession, long, List<byte[]>> run, CancellationToken ct)
    {
        if (presence?.Find(name) is not { } entry) return Results.NotFound(new { error = "not_online" });
        try
        {
            await entry.Session.InvokeAsync(now => (true, run(entry.Session, now))).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return Results.BadRequest(new { error = "not_possible", detail = error.Message });
        }
        catch (TimeoutException) { return Results.StatusCode(504); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Results.NotFound(new { error = "not_online" }); }
        await game.AdminRecordAsync(account, action, entry.Session.CharacterId, new { name, details }, ct);
        return Results.Ok(new { done = true });
    }

    /// <summary>A notice in the middle of every player's screen and the same text in chat; the number of screens.</summary>
    public static async Task<int> AnnounceAllAsync(IServiceProvider services, string text, string color = "FFFFFF", int size = 20)
    {
        var shown = 0;
        if (services.GetService<WorldPresence>() is { } presence)
        {
            var packet = GameSession.Notice(text, size, color);
            foreach (var entry in presence.All)
            {
                _ = entry.Session.InvokeAsync(_ => (true, new List<byte[]> { packet }));
                shown++;
            }
        }
        try { await ChatAnnounceAsync(services, text); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
        return shown;
    }

    /// <summary>A chat announcement to everyone (as the update script's restart warnings); the number of deliveries.</summary>
    public static async Task<int> ChatAnnounceAsync(IServiceProvider services, string text)
    {
        if (services.GetService<ChatRelay>() is not { } relay || services.GetService<StompHub>() is not { } hub) return 0;
        var deliveries = relay.Announce(text);
        await hub.DeliverAsync(deliveries);
        return deliveries.Count;
    }

    static string Bearer(HttpContext context)
    {
        var h = context.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : "";
    }
}

/// <summary>One pending restart with countdown notices; the host stops with exit code 75 so systemd starts it again.</summary>
public sealed class ScheduledRestart(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<ScheduledRestart> logger)
{
    static readonly TimeSpan[] Warnings = [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)];
    readonly object sync = new();
    CancellationTokenSource? pending;

    public DateTimeOffset? At { get; private set; }
    public string Reason { get; private set; } = "";

    public DateTimeOffset Schedule(TimeSpan delay, string reason)
    {
        CancellationTokenSource next = new();
        lock (sync)
        {
            pending?.Cancel();
            pending = next;
            At = DateTimeOffset.UtcNow + delay;
            Reason = reason;
        }
        _ = RunAsync(At.Value, reason, next.Token);
        return At.Value;
    }

    public bool Cancel()
    {
        lock (sync)
        {
            if (pending is null) return false;
            pending.Cancel();
            pending = null;
            At = null;
        }
        _ = AnnounceAsync("The server restart was cancelled.");
        return true;
    }

    async Task RunAsync(DateTimeOffset at, string reason, CancellationToken ct)
    {
        try
        {
            var suffix = reason.Length > 0 ? $" ({reason})" : "";
            foreach (var warning in Warnings)
            {
                var due = at - warning;
                if (due < DateTimeOffset.UtcNow) continue;
                await Task.Delay(due - DateTimeOffset.UtcNow, ct);
                await AnnounceAsync($"The server restarts in {Spoken(warning)}{suffix}. Find a safe spot.");
            }
            var left = at - DateTimeOffset.UtcNow;
            if (left > TimeSpan.Zero) await Task.Delay(left, ct);
            logger.LogWarning("Scheduled restart now{Suffix}", suffix);
            Environment.ExitCode = 75;
            lifetime.StopApplication();
        }
        catch (TaskCanceledException) { }
    }

    static string Spoken(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} minute{(span.TotalMinutes >= 2 ? "s" : "")}" : $"{span.TotalSeconds:0} seconds";

    Task AnnounceAsync(string text) => StaffEndpoints.AnnounceAllAsync(services, text, "FFD54A");
}
