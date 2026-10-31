using Aion.Application.Authentication;

namespace Aion.Host.Authentication;

/// <summary>Two-step sign-in (TOTP) for the account site, launcher and staff dashboard. Ported from A2Core PR #4
/// (Artezianec) without its platform secondary_auth path. Code checks use the "totp-verify" rate limit; wrong
/// codes also lock code entry per account (PostgresTotpAuthentication).</summary>
public static class TotpEndpoints
{
    private sealed record PasswordInput(string? Password);
    private sealed record CodeInput(string? Code);
    private sealed record VerifiedInput(string? Password, string? Code, string? RecoveryCode);
    private sealed record CompleteLogin(string? ChallengeToken, string? Code, string? RecoveryCode);

    static IResult Unavailable() =>
        Results.Json(new { error = "Two-step sign-in is not available on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    public static void MapTotpEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/accounts/totp").RequireRateLimiting("account-entry");
        api.MapGet("/status", async (HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            var status = await totp.StatusAsync(account.AccountId, ct);
            return Results.Ok(new { available = status.Available, enabled = status.Enabled, recoveryCodesRemaining = status.RecoveryCodesRemaining });
        });
        api.MapPost("/enroll", async (PasswordInput input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            if (!totp.Available) return Unavailable();
            if ((await totp.StatusAsync(account.AccountId, ct)).Enabled) return Results.Conflict(new { error = "Two-step sign-in is already on." });
            var enrollment = await totp.BeginEnrollmentAsync(account.AccountId, input.Password ?? "", ct);
            return enrollment is null
                ? Results.Json(new { error = "Wrong password." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new { secret = enrollment.Secret, otpAuthUri = enrollment.OtpAuthUri });
        }).RequireRateLimiting("totp-verify");
        api.MapPost("/confirm", async (CodeInput input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            if (!totp.Available) return Unavailable();
            var recovery = await totp.ConfirmEnrollmentAsync(account.AccountId, input.Code, ct);
            return recovery is null
                ? Results.Json(new { error = "That code is not valid. Check the time on your phone, or start again." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new { enabled = true, recoveryCodes = recovery });
        }).RequireRateLimiting("totp-verify");
        api.MapPost("/recovery/regenerate", async (VerifiedInput input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            if (!totp.Available) return Unavailable();
            var codes = await totp.RegenerateRecoveryCodesAsync(account.AccountId, input.Password ?? "", input.Code, input.RecoveryCode, ct);
            return codes is null
                ? Results.Json(new { error = "Wrong password or code." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting("totp-verify");
        api.MapPost("/disable", async (VerifiedInput input, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            if (!totp.Available) return Unavailable();
            return await totp.DisableAsync(account.AccountId, input.Password ?? "", input.Code, input.RecoveryCode, ct)
                ? Results.NoContent()
                : Results.Json(new { error = "Wrong password or code." }, statusCode: StatusCodes.Status403Forbidden);
        }).RequireRateLimiting("totp-verify");
        // Second step of POST /api/accounts/login for two-step accounts.
        app.MapPost("/api/accounts/login/totp", async (CompleteLogin input, ITotpAuthentication totp, CancellationToken ct) =>
        {
            if (!totp.Available) return Unavailable();
            var session = await totp.CompleteSignInAsync(input.ChallengeToken ?? "", input.Code, input.RecoveryCode, ct);
            return session is null ? Results.Unauthorized() : Results.Ok(session);
        }).RequireRateLimiting("totp-verify");
        // Admins only (checked in the database by auth.reset_totp, which writes the totp_reset audit event).
        app.MapPost("/api/admin/accounts/{id:guid}/totp/reset", async (Guid id, HttpContext ctx, ITotpAuthentication totp, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await totp.AdminResetAsync(Bearer(ctx), id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            logs.CreateLogger("Aion.Accounts").LogInformation("Two-step sign-in reset for account {Account} by an admin", id);
            return Results.NoContent();
        }).RequireRateLimiting("admin");
    }

    static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }
}
