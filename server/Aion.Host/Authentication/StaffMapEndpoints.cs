using System.Globalization;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// The live map's static data for staff (USER 2026-11-09: the real game map with every spawn on it, like shugo.gg/map):
/// detail tiles of the painted map and English names (tools/export_map_images.py, files in the map-images folder, never
/// in git), and per map the layers from the embedded game data - NPC spawns, objects (gathering, portals, Kibelisks,
/// doors), teleport/respawn points, quest trigger areas and dungeon entrances. Own rate limit: a zoomed-in map loads
/// a few dozen tiles at once.
/// </summary>
public static class StaffMapEndpoints
{
    public static void MapStaffMapEndpoints(this WebApplication app)
    {
        var maps = app.MapGroup("/api/admin/maps").RequireRateLimiting("admin-maps");
        maps.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });
        maps.MapGet("/names", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            var file = StaffEndpoints.MapImagesRoot(config) is { } root ? Path.Combine(root, "names.json") : null;
            if (file is null || !File.Exists(file)) return Results.Ok(new { });
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(file, "application/json");
        });
        maps.MapGet("/{id:int}/tiles/{x:int}/{y:int}", async (int id, int x, int y, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            if (x is < 0 or > 64 || y is < 0 or > 64 || StaffEndpoints.MapImagesRoot(config) is not { } root) return Results.NotFound();
            var file = Path.Combine(root, "tiles", id.ToString(CultureInfo.InvariantCulture), $"{x}_{y}.webp");
            if (!File.Exists(file)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(file, "image/webp");
        });
        maps.MapGet("/{id:int}/layers", async (int id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            if (id <= 0) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=600";
            return Results.Bytes(MapLayers.For(id), "application/json");
        });
    }
}
