using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// The staff item database (USER 2026-11-09: "a full item database like shugo.gg", browse everything, open an item, grant
/// a selection): the files tools/build_item_database.py makes from the user's own client (Aion:ItemDatabasePath, else
/// &lt;Aion:DataDir&gt;/data/item-db), served to staff only. Grants go through /api/admin/grant-many (one audited grant
/// command per item). Icons are the client's own (tools/ItemIconExport) under /item-icons/&lt;IconRes&gt;.webp.
/// </summary>
public static class StaffItemDatabaseEndpoints
{
    public static void MapStaffItemDatabaseEndpoints(this WebApplication app)
    {
        var db = app.MapGroup("/api/admin/itemdb").RequireRateLimiting("admin");
        db.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });
        // Every item as a compact row plus the label tables (about 2.5 MB; the dashboard loads it once).
        db.MapGet("", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            return Serve(ctx, ItemDatabaseFiles.Index(Root(config)));
        });
        // Item names in another client language (search and list in that language).
        db.MapGet("/names/{language}", async (string language, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            return Serve(ctx, ItemDatabaseFiles.Names(Root(config), language));
        });
        // Placeholder items the client cannot use (CollectionItems.IsPlaceholder: Usable collection items without a use effect,
        // e.g. the 58 wings without a Wing.dat entry): marked "not usable in this client"; the grant refuses them.
        db.MapGet("/unusable", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            return Results.Ok(new { ids = CollectionItems.Placeholders, error = CollectionItems.PlaceholderError });
        });
        // Everything about one item (stats, random pools, upgrades, sets, box contents, sources, the raw client row).
        db.MapGet("/items/{id:int}", async (int id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is null) return Results.Unauthorized();
            return Serve(ctx, ItemDatabaseFiles.Item(Root(config), id));
        });
    }

    internal static string? Root(IConfiguration config) =>
        ItemDatabaseFiles.Root(config["Aion:ItemDatabasePath"], config["Aion:DataDir"], AppContext.BaseDirectory);

    static IResult Serve(HttpContext ctx, string? file)
    {
        if (file is null) return Results.NotFound(new { error = "itemdb_missing" });
        // The files change only when the database is rebuilt; the browser revalidates after 10 minutes.
        ctx.Response.Headers.CacheControl = "private, max-age=600";
        return Results.File(file, "application/json; charset=utf-8", enableRangeProcessing: false);
    }
}
