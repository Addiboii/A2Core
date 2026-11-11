using Aion.Application.Authentication;
using Aion.Core.Accounts;

namespace Aion.Host.Authentication;

/// <summary>
/// The admin API takes staff sessions only (migration 075, OURS): every /api/admin request needs the bearer of a staff
/// session (the staff dashboard's sign-in, <see cref="SessionKind.Staff"/>), checked here before any handler runs, so a
/// player session's token gets 401 even on a staff account. Handlers check again (AuthenticateStaffAsync) and the
/// database's staff operations check the session kind themselves. One request needs no session: DELETE
/// /api/admin/wardrobe-access only drops the caller's own wardrobe cookie (sign-out).
/// </summary>
public static class StaffSessions
{
    public static bool IsAdminPath(PathString path) => path.StartsWithSegments("/api/admin");

    static bool NeedsNoSession(HttpRequest request) =>
        HttpMethods.IsDelete(request.Method) && request.Path.Equals("/api/admin/wardrobe-access", StringComparison.OrdinalIgnoreCase);

    public static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
    }

    /// <summary>The staff account of this request (the middleware put it there), else a fresh staff-session check.</summary>
    public static async Task<VerifiedAccount?> StaffAsync(HttpContext context, IAccountAuthentication auth, CancellationToken ct) =>
        context.Items[typeof(StaffSessions)] as VerifiedAccount ?? await auth.AuthenticateStaffAsync(Bearer(context), ct);

    public static void UseStaffSessions(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (!IsAdminPath(context.Request.Path) || NeedsNoSession(context.Request))
        {
            await next();
            return;
        }
        var auth = context.RequestServices.GetService<IAccountAuthentication>();
        if (auth is null || await auth.AuthenticateStaffAsync(Bearer(context), context.RequestAborted) is not { } staff)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "staff_session_required" });
            return;
        }
        context.Items[typeof(StaffSessions)] = staff;
        await next();
    });
}
