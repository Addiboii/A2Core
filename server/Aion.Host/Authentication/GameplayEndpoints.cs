using Aion.Application.Authentication;
using Aion.Application.Gameplay;

namespace Aion.Host.Authentication;

public static class GameplayEndpoints
{
    private sealed record MoveItem(long Revision, int? EquipmentSlot);
    private sealed record ChangePassword(string CurrentPassword, string NewPassword);
    private sealed record StaffRoleChange(string Role, bool Granted);
    public static void MapGameplayEndpoints(this WebApplication app)
    {
        var api=app.MapGroup("/api").RequireRateLimiting("account-entry");
        api.AddEndpointFilter(async (context,next) =>
        {
            try { return await next(context); }
            catch(GameplayException error)
            { return error.Code=="forbidden" ? Results.StatusCode(403) : Results.BadRequest(new {error=error.Code}); }
        });
        api.MapPost("/characters", async(CreatePlayer input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct);
            if(account is null) return Results.Unauthorized();
            var id=await game.CreateAsync(account,input,ct);
            return Results.Ok(new {characterId=id});
        });
        api.MapGet("/characters/{id:guid}/inventory",async(Guid id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            var inventory=await game.InventoryAsync(account,id,ct); return inventory is null ? Results.NotFound() : Results.Ok(inventory);
        });
        api.MapPost("/characters/{id:guid}/items/{item:guid}/move",async(Guid id,Guid item,MoveItem input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return await game.MoveItemAsync(account,id,item,input.Revision,input.EquipmentSlot,ct) ? Results.NoContent() : Results.Conflict();
        });
        api.MapPost("/accounts/password",async(ChangePassword input,HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.ChangePasswordAsync(Bearer(ctx),input.CurrentPassword,input.NewPassword,ct) ? Results.NoContent() : Results.Unauthorized());
        api.MapPost("/accounts/revoke-sessions",async(HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.RevokeSessionsAsync(Bearer(ctx),ct) ? Results.NoContent() : Results.Unauthorized());
        // Staff dashboard: role-checked in the store; a separate, larger rate budget than account entry.
        var admin=app.MapGroup("/api/admin").RequireRateLimiting("admin");
        admin.AddEndpointFilter(async (context,next) =>
        {
            try { return await next(context); }
            catch(GameplayException error)
            { return error.Code=="forbidden" ? Results.StatusCode(403) : Results.BadRequest(new {error=error.Code}); }
        });
        admin.MapGet("/me",async(HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminProfileAsync(account,ct));
        });
        admin.MapGet("/characters",async(string? q,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminFindCharactersAsync(account,q??"",ct));
        });
        admin.MapGet("/characters/{id:guid}",async(Guid id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            var detail=await game.AdminCharacterAsync(account,id,ct); return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
        admin.MapGet("/items",async(string? q,string? category,int? offset,int? limit,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminCatalogAsync(account,q??"",category,offset??0,limit??120,ct));
        });
        admin.MapPost("/commands",async(AdminCommand input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminAsync(account,input,ct));
        });
        // Live overview: who is in the world now and where (in-memory, read under the world gate).
        admin.MapGet("/live",async(HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,IServiceProvider services,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            await game.AdminProfileAsync(account,ct);   // staff only
            var presence=services.GetService<Aion.World.WorldPresence>();
            var world=services.GetService<Aion.World.SharedWorld>();
            var entries=presence?.All ?? [];
            var views=new List<(Aion.World.WorldPresence.Entry Entry,Aion.World.GameSession.LiveView View)>();
            if(world is not null) await world.Lock.EnterExclusiveAsync(ct);
            try { foreach(var e in entries) views.Add((e,e.Session.Live())); }
            finally { world?.Lock.ExitExclusive(); }
            var players=new List<object>();
            foreach(var (e,v) in views.OrderBy(p => p.View.Name,StringComparer.OrdinalIgnoreCase))
            {
                var stored=v.CharacterId is { } id ? (await game.AdminFindCharactersAsync(account,id.ToString(),ct)).FirstOrDefault() : null;
                players.Add(new {
                    v.Name, v.CharacterId, AccountName=stored?.AccountName, v.StaffRole, v.Level, v.ClassName, v.MapId, v.Area,
                    Position=v.Position is { } at ? new[]{Math.Round(at.X),Math.Round(at.Y),Math.Round(at.Z)} : null,
                    v.Hp, v.HpMax, v.Mp, v.MpMax, v.Invisible, v.PartyLeader, v.PartySize, InWorldSince=e.Since,
                    Heading=Math.Round(v.Heading,1), v.Dead, v.SpeedRejects });
            }
            return Results.Ok(new {
                ServerTime=DateTimeOffset.UtcNow,
                StartedAt=new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()),
                Connections=presence?.Connections ?? 0,
                Players=players });
        });
        // Minimap outlines: a map's subzone polygons (2D, cm), the same data that clears world-map fog.
        admin.MapGet("/maps/{id:int}/outline",async(int id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            await game.AdminProfileAsync(account,ct);   // staff only
            var areas=Aion.World.Subzones.OnMap(id).OrderBy(a => a.Priority).Select(a => new {
                a.Name, a.Priority, Points=a.Points.Select(p => new[]{Math.Round(p[0]),Math.Round(p[1])}).ToArray() });
            return Results.Ok(new { MapId=id, Areas=areas });
        });
        api.MapPost("/admin/accounts/{id:guid}/disable",async(Guid id,HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.DisableAccountAsync(Bearer(ctx),id,ct) ? Results.NoContent() : Results.StatusCode(403));
        // Admins only; the player sees a role change at their next game login.
        api.MapPost("/admin/accounts/{id:guid}/roles",async(Guid id,StaffRoleChange input,HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.SetStaffRoleAsync(Bearer(ctx),id,input.Role,input.Granted,ct) ? Results.NoContent() : Results.StatusCode(403));
    }
    private static string Bearer(HttpContext context)
    { var h=context.Request.Headers.Authorization.ToString(); return h.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?h[7..]:""; }
}
