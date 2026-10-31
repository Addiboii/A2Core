namespace Aion.Host.Debug;

/// <summary>HTTP request lines for playtest (chat polling and Razer Synapse at Debug; others at Information).</summary>
public sealed class PlaytestHttpLoggingMiddleware(RequestDelegate next, ILogger<PlaytestHttpLoggingMiddleware> logger, DebugOptions options)
{
    static bool QuietPath(PathString path)
    {
        var text = path.Value ?? "";
        if (text.StartsWith("/gameClient/", StringComparison.OrdinalIgnoreCase))
        {
            var route = text["/gameClient/".Length..];
            return route is "getJoinedRoomList" or "getJoinedRoomListWithRoomUsers" or "getSubscriptionInfo";
        }
        return text.StartsWith("/wyvrn/Synapse", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (!options.Enabled || !options.HttpRequests) return;
        var path = context.Request.Path;
        var method = context.Request.Method;
        var status = context.Response.StatusCode;
        if (QuietPath(path))
            logger.LogDebug("HTTP {Method} {Path} -> {Status}", method, path, status);
        else
            logger.LogInformation("HTTP {Method} {Path} -> {Status}", method, path, status);
    }
}

public static class PlaytestHttpLoggingExtensions
{
    public static IApplicationBuilder UsePlaytestHttpLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<PlaytestHttpLoggingMiddleware>();
}
