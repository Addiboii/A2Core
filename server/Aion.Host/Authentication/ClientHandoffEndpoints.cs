using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Core.Accounts;

namespace Aion.Host.Authentication;

public static class ClientHandoffEndpoints
{
    private sealed record LaunchRequest(string Authorization);
    private sealed record LoginRequest(string Ticket, int WorldId);
    private sealed record GameRequest(string Ticket, long WireAccountId, string Grant, int WorldId);

    public static void MapClientHandoff(this WebApplication app, string bridgeSecret)
    {
        var expected = Encoding.UTF8.GetBytes(bridgeSecret);
        var group = app.MapGroup("/internal/client");
        group.AddEndpointFilter(async (context, next) =>
        {
            var header = context.HttpContext.Request.Headers["X-Aion-Bridge"].ToString();
            if (header.Length != bridgeSecret.Length || !CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(header)))
                return Results.Unauthorized();
            return await next(context);
        });
        group.MapPost("/launch-token", async (LaunchRequest request, IClientHandoff handoff, CancellationToken ct) =>
        {
            var result = await handoff.RedeemLaunchAsync(request.Authorization, ct);
            return result is null ? Results.Unauthorized() : Results.Ok(result);
        });
        group.MapPost("/login", async (LoginRequest request, IClientHandoff handoff, CharacterService characters, CancellationToken ct) =>
        {
            var result = await handoff.BeginLoginAsync(request.Ticket, request.WorldId, ct);
            if (result is null) return Results.Unauthorized();
            // The login port's character list (0x390b) shows a per-server character count.
            var owned = (await characters.ListAsync(new VerifiedAccount(result.AccountId), ct))
                .Where(character => character.WorldId == result.WorldId)
                .Select(character => new { name = character.Name, level = character.Level });
            return Results.Ok(new { result.AccountId, result.WireAccountId, result.Grant, result.WorldId, characters = owned });
        });
        group.MapPost("/game", async (GameRequest request, IWorldSessions sessions, CancellationToken ct) =>
        {
            var result = await sessions.OpenAsync(request.Ticket, request.WireAccountId, request.Grant, request.WorldId, ct);
            return result is null ? Results.Unauthorized() : Results.Ok(result);
        });
    }
}
