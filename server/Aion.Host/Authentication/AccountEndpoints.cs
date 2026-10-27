using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Infrastructure.Postgres;

namespace Aion.Host.Authentication;

public static class AccountEndpoints
{
    private sealed record Credentials(string? Username, string? Password, bool? Remember = null);
    private sealed record TicketRequest(int WorldId);

    /// <param name="registration">Self-registration on the account page (Aion:Accounts:Registration); when off,
    /// only admins create accounts (POST /api/admin/accounts).</param>
    public static void MapAccountEndpoints(this WebApplication app, bool registration = true)
    {
        app.MapPost("/api/client-launches", async (HttpContext context, IClientHandoff handoff, CancellationToken ct) =>
        {
            var launch = await handoff.AuthorizeLaunchAsync(Bearer(context), 1204, ct);
            if (launch is null) return Results.Unauthorized();
            // A client started without a launch code redeems the launch its own address asked for.
            context.RequestServices.GetService<Aion.Host.Platform.LaunchPairing>()?.Register(context.Connection.RemoteIpAddress, launch.Authorization, launch.ExpiresAt);
            return Results.Ok(launch);
        }).RequireRateLimiting("account-entry");
        app.MapPost("/api/accounts/register", async (Credentials input, IAccountAuthentication auth, CancellationToken ct) =>
        {
            if (!registration) return Results.Json(new { error = "Registration is closed. Ask an admin for an account." }, statusCode: 403);
            if (!PostgresAccountAuthentication.ValidUsername(input.Username) || !PostgresAccountAuthentication.ValidPassword(input.Password))
                return Results.BadRequest(new { error = "Use a 3–32 character username (letters, numbers, underscore) and a 12–256 character password." });
            return await auth.RegisterAsync(input.Username!, input.Password!, ct)
                ? Results.Json(new { registered = true }, statusCode: 201)
                : Results.Conflict(new { error = "That username is unavailable." });
        }).RequireRateLimiting("account-entry");
        // Admins create accounts (the way in when registration is closed). Role-checked like the staff API.
        app.MapPost("/api/admin/accounts", async (Credentials input, HttpContext context, IAccountAuthentication auth,
            Aion.Application.Gameplay.IGameplayStore game, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = await auth.AuthenticateAsync(Bearer(context), ct);
            if (actor is null) return Results.Unauthorized();
            try
            {
                if (!(await game.AdminProfileAsync(actor, ct)).Roles.Contains("admin")) return Results.StatusCode(403);
            }
            catch (Aion.Application.Gameplay.GameplayException) { return Results.StatusCode(403); }
            if (!PostgresAccountAuthentication.ValidUsername(input.Username) || !PostgresAccountAuthentication.ValidPassword(input.Password))
                return Results.BadRequest(new { error = "invalid_account" });
            if (!await auth.RegisterAsync(input.Username!, input.Password!, ct)) return Results.Conflict(new { error = "username_taken" });
            logs.CreateLogger("Aion.Accounts").LogInformation("Account {Username} created by admin account {Admin}", input.Username, actor.AccountId);
            return Results.Json(new { created = true }, statusCode: 201);
        }).RequireRateLimiting("admin");

        app.MapPost("/api/accounts/login", async (Credentials input, IAccountAuthentication auth, CancellationToken ct) =>
        {
            if (input.Username is null || input.Password is null) return Results.Unauthorized();
            // "Keep me signed in" (the staff dashboard): a 30-day session instead of the standard 8 hours.
            var session = input.Remember == true
                ? await auth.SignInAsync(input.Username, input.Password, TimeSpan.FromDays(30), ct)
                : await auth.SignInAsync(input.Username, input.Password, ct);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        }).RequireRateLimiting("account-entry");

        app.MapPost("/api/accounts/logout", async (HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            await auth.SignOutAsync(Bearer(context), ct);
            return Results.NoContent();
        });
        app.MapGet("/api/accounts/me", async (HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            return account is null ? Results.Unauthorized() : Results.Ok(new { accountId = account.AccountId });
        });
        app.MapGet("/api/characters", async (HttpContext context, IAccountAuthentication auth, CharacterService characters, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            return account is null ? Results.Unauthorized() : Results.Ok(await characters.ListAsync(account, ct));
        });
        app.MapGet("/api/characters/{id:guid}", async (Guid id, HttpContext context, IAccountAuthentication auth, CharacterPersistenceService characters, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(context), ct);
            if (account is null) return Results.Unauthorized();
            var character = await characters.LoadAsync(account, id, ct);
            return character is null ? Results.NotFound() : Results.Ok(character);
        });
        app.MapPost("/api/game-tickets", async (TicketRequest input, HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        {
            // The current development directory has this single world. Do not
            // accept an arbitrary world ID merely because a caller supplied it.
            if (input.WorldId != 1204) return Results.BadRequest(new { error = "Unknown world." });
            var ticket = await auth.IssueGameTicketAsync(Bearer(context), input.WorldId, ct);
            return ticket is null ? Results.Unauthorized() : Results.Ok(ticket);
        }).RequireRateLimiting("account-entry");
    }

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
}
