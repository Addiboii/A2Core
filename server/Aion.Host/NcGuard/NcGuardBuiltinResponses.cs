using System.Text;
using System.Text.Json;

namespace Aion.Host.NcGuard;

/// <summary>
/// Fallback NCGuard TLS answers when the deployed fixture has no rule (local dev and partial fixtures).
/// Matches the JSON field names used on other GPA OAuth token replies (snake_case access_token, token_type, expires_in).
/// </summary>
public static class NcGuardBuiltinResponses
{
    sealed record BuiltinRule(string Host, string? Method, string? Path, string ContentType, byte[] Body);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    static readonly BuiltinRule[] Rules =
    [
        new("sdk.gpa.ncsoft.com", "POST", "/authz/token", "application/json", AuthzTokenBody()),
        // CANDIDATE: some client builds call authn after authz; same token envelope until a capture refines it.
        new("sdk.gpa.ncsoft.com", "POST", "/authn/token", "application/json", AuthzTokenBody()),
    ];

    public static bool TryReply(string host, string method, string path, out byte[] body, out string contentType)
    {
        foreach (var rule in Rules)
        {
            if (!string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Method is not null && !string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Path is not null && path != rule.Path) continue;
            body = rule.Body;
            contentType = rule.ContentType;
            return true;
        }
        body = [];
        contentType = "text/plain";
        return false;
    }

    static byte[] AuthzTokenBody() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        access_token = "aion-local-gpa-access-token",
        token_type = "Bearer",
        expires_in = 86_400,
        refresh_token = "aion-local-gpa-refresh-token",
        scope = "openid profile",
    }, Json));
}
