using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Host.Authentication;

/// <summary>
/// The admin API takes staff sessions only (migration 075, OURS): every /api/admin request needs the bearer of a staff
/// session (the staff dashboard's sign-in, <see cref="SessionKind.Staff"/>), checked here before any handler runs, so a
/// player session's token gets 401 even on a staff account. Handlers check again (AuthenticateStaffAsync) and the
/// database's staff operations check the session kind themselves. One request needs no session: DELETE
/// /api/admin/wardrobe-access only drops the caller's own wardrobe cookie (sign-out).
/// Then the rank (migration 076, OURS): the request's route is an action of <see cref="StaffPermissions"/>, allowed
/// only when that table and the database's auth.staff_allowed both allow the actor's rank; 403 otherwise.
/// </summary>
public static class StaffSessions
{
    public static bool IsAdminPath(PathString path) => path.StartsWithSegments("/api/admin");

    const string NoSessionAction = "api DELETE /api/admin/wardrobe-access";

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

    /// <summary>The permission-table action of an endpoint's route and method.</summary>
    public static string? Action(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route } ? StaffPermissions.ApiAction(context.Request.Method, route) : null;

    /// <summary>Admin routes with no row in the permission table (they would be refused); the host refuses to start with any.</summary>
    public static IReadOnlyList<string> Unlisted(IEnumerable<EndpointDataSource> sources) =>
        sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } route && IsAdminPath(new PathString(route.StartsWith('/') ? route : "/" + route)))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => StaffPermissions.ApiAction(method, e.RoutePattern.RawText!)))
            .Where(action => action != NoSessionAction && !StaffPermissions.Minimum.ContainsKey(action))
            .Distinct().ToList();

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
            await Refuse(context, StatusCodes.Status401Unauthorized, "staff_session_required");
            return;
        }
        context.Items[typeof(StaffSessions)] = staff;
        // No matching route: the 404 that follows says nothing about ranks.
        if (Action(context) is { } action)
        {
            var store = context.RequestServices.GetService<IGameplayStore>();
            if (store is null || !(await store.StaffAccessAsync(staff, action, context.RequestAborted)).Allows(action))
            {
                await Refuse(context, StatusCodes.Status403Forbidden, "forbidden");
                return;
            }
        }
        await next();
    });

    static async Task Refuse(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error });
    }
}
