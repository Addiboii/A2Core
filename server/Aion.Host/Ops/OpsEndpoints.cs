using System.Net;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Authentication;

namespace Aion.Host.Ops;

/// <summary>Message names of tools/data/opcode-names.json (embedded) for the health page's request table.</summary>
public static class OpcodeNames
{
    static readonly Lazy<Dictionary<int, string>> names = new(() =>
    {
        try
        {
            using var stream = typeof(OpcodeNames).Assembly.GetManifestResourceStream("opcode-names.json");
            if (stream is null) return [];
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            return raw.Where(kv => kv.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => Convert.ToInt32(kv.Key[2..], 16), kv => kv.Value);
        }
        catch (Exception error) when (error is JsonException or FormatException or IOException) { return []; }
    });

    public static string? Get(int opcode) => names.Value.GetValueOrDefault(opcode);
}

/// <summary>
/// The staff "Server health" API (Stage G): GET /api/admin/health (any staff role) with the live numbers, the green / yellow /
/// red checks, the last warnings and errors and the alert log; and GET /health/ops for scripts on the server itself
/// (deploy\windows\health-check.ps1): loopback only, the overall state and the checks, no player data.
/// </summary>
public static class OpsEndpoints
{
    public static void MapOpsEndpoints(this WebApplication app, bool staff)
    {
        if (staff)
            app.MapGet("/api/admin/health", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, OpsMonitor monitor, CancellationToken ct) =>
            {
                try { if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized(); }
                catch (GameplayException error) when (error.Code == "forbidden") { return Results.StatusCode(403); }
                return Results.Ok(new
                {
                    health = monitor.Snapshot(),
                    log = OpsLog.Shared.Latest(50).Select(e => new { at = e.At, last = e.Last, e.Level, e.Category, e.Message, e.Exception, e.Count }),
                    alerts = monitor.RecentAlerts(30),
                });
            }).RequireRateLimiting("admin");

        app.MapGet("/health/ops", (HttpContext ctx, OpsMonitor monitor) =>
        {
            if (ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return Results.NotFound();
            var (overall, checks) = monitor.Rated();
            var (errors1, warnings1) = OpsLog.Shared.Recent(TimeSpan.FromMinutes(1));
            var (errors5, _) = OpsLog.Shared.Recent(TimeSpan.FromMinutes(5));
            return Results.Ok(new
            {
                overall, checks, errorsLastMinute = errors1, warningsLastMinute = warnings1, errorsLast5Minutes = errors5,
                errorsTotal = OpsLog.Shared.ErrorsTotal, warningsTotal = OpsLog.Shared.WarningsTotal,
                lastErrors = OpsLog.Shared.Latest(10).Where(e => e.Level != "warning").Select(e => new { at = e.At, e.Category, e.Message, e.Count }),
            });
        });
    }
}
