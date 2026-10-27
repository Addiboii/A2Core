using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Characters;

namespace Aion.Host.Authentication;

// Only trusted protocol adapters know both the machine secret and a connection
// token. No caller supplies an account UUID. Every request revalidates its parent.
public static class WorldPersistenceEndpoints
{
    private sealed record SaveRequest(long Revision, CharacterProgress Progress);
    private const string IdentityKey="world-identity";
    public static void MapWorldPersistence(this WebApplication app,string secret)
    {
        var expected=Encoding.UTF8.GetBytes(secret);
        var group=app.MapGroup("/internal/world");
        group.AddEndpointFilter(async(context,next)=>
        {
            var http=context.HttpContext;
            var supplied=http.Request.Headers["X-Aion-Bridge"].ToString();
            if(supplied.Length!=secret.Length || !CryptographicOperations.FixedTimeEquals(expected,Encoding.UTF8.GetBytes(supplied))) return Results.Unauthorized();
            var sessions=http.RequestServices.GetRequiredService<IWorldSessions>();
            var identity=await sessions.ValidateAsync(http.Request.Headers["X-Aion-World"].ToString(),http.RequestAborted);
            if(identity is null) return Results.Unauthorized();
            http.Items[IdentityKey]=identity;
            try { return await next(context); }
            catch(GameplayException e) { return Results.BadRequest(new {error=e.Code}); }
            catch(ArgumentException) { return Results.BadRequest(new {error="invalid_state"}); }
        });
        group.MapPost("/close",async(HttpContext ctx,IWorldSessions sessions,CancellationToken ct)=>
        { await sessions.CloseAsync(ctx.Request.Headers["X-Aion-World"].ToString(),ct); return Results.NoContent(); });
        group.MapGet("/characters",async(HttpContext ctx,CharacterService characters,CancellationToken ct)=>
        { var who=Identity(ctx); return Results.Ok((await characters.ListAsync(who.Account,ct)).Where(x=>x.WorldId==who.WorldId)); });
        group.MapGet("/lobby",async(HttpContext ctx,IWireCharacterRepository characters,CancellationToken ct)=>
        { var who=Identity(ctx); return Results.Ok(await characters.ListWorldAsync(who.Account.AccountId,who.WorldId,ct)); });
        group.MapGet("/characters/by-wire/{key:int}",async(int key,HttpContext ctx,IWireCharacterRepository characters,CancellationToken ct)=>
        {
            var who=Identity(ctx); var state=await characters.LoadWireAsync(who.Account.AccountId,who.WorldId,key,ct);
            return state is null?Results.NotFound():Results.Ok(state);
        });
        group.MapPost("/characters",async(CreatePlayer input,HttpContext ctx,IGameplayStore game,CancellationToken ct)=>
        { var who=Identity(ctx); return Results.Ok(new {characterId=await game.CreateAsync(who.Account,input with {WorldId=who.WorldId},ct)}); });
        group.MapGet("/characters/{id:guid}",async(Guid id,HttpContext ctx,CharacterPersistenceService characters,CancellationToken ct)=>
        { var who=Identity(ctx); var state=await characters.LoadAsync(who.Account,id,ct); return state?.Summary.WorldId==who.WorldId ? Results.Ok(state) : Results.NotFound(); });
        group.MapGet("/characters/{id:guid}/inventory",async(Guid id,HttpContext ctx,CharacterService characters,IGameplayStore game,CancellationToken ct)=>
        {
            var who=Identity(ctx); var state=await characters.FindAsync(who.Account,id,ct);
            if(state?.WorldId!=who.WorldId) return Results.NotFound();
            var inventory=await game.InventoryAsync(who.Account,id,ct);
            return inventory is null ? Results.NotFound() : Results.Ok(inventory);
        });
        group.MapPost("/characters/{id:guid}/save",async(Guid id,SaveRequest input,HttpContext ctx,CharacterPersistenceService characters,CancellationToken ct)=>
        {
            var who=Identity(ctx); var state=await characters.LoadAsync(who.Account,id,ct);
            if(state?.Summary.WorldId!=who.WorldId) return Results.NotFound();
            var revision=await characters.SaveAsync(who.Account,id,input.Revision,input.Progress,ct);
            return revision is null ? Results.Conflict() : Results.Ok(new {revision});
        });
    }
    private static WorldIdentity Identity(HttpContext context)=>(WorldIdentity)context.Items[IdentityKey]!;
}
