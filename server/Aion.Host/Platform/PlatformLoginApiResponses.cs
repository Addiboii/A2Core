using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aion.Application.Authentication;

namespace Aion.Host.Platform;

/// <summary>
/// NC Platform <c>login_api</c> routes the title-screen "Manage Account" flow uses after GPA
/// <c>/authz/token</c> (sdk.gpa.ncsoft.com, answered on the NCGuard port). The observed call is
/// POST <c>/local-login-api/account/private-id?token=...</c> (token is not logged server-side).
/// Response field names are CANDIDATE: official capture not in-repo; shape matches other gate JSON.
/// </summary>
public static class PlatformLoginApiResponses
{
    public static bool TryReply(string method, string path, out (int Status, byte[] Body) reply)
    {
        reply = default;
        if (!path.StartsWith("/local-login-api", StringComparison.Ordinal)) return false;
        if (method == "GET" && path == "/local-login-api")
        {
            reply = (200, JsonSerializer.SerializeToUtf8Bytes(new { ok = true }, PlatformResponses.Plain));
            return true;
        }
        return false;
    }

    public static async Task<byte[]> PrivateIdBodyAsync(
        string? queryToken,
        IPlatformSecondaryAuth? platformAuth,
        IPAddress? clientAddress,
        string? platformSessionToken,
        string? fixtureGameAccountId,
        CancellationToken ct)
    {
        Guid? account = null;
        if (platformAuth is not null)
            account = await platformAuth.ResolveAccountAsync(clientAddress, platformSessionToken, ct);
        var seed = account?.ToString("N")
            ?? fixtureGameAccountId
            ?? (string.IsNullOrEmpty(queryToken) ? "aion-local-anonymous" : queryToken);
        var privateId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("aion-private-id-v1:" + seed))).ToLowerInvariant();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            private_id = privateId,
            expires_in = 3600,
        }, PlatformResponses.Plain);
    }

    /// <summary>Secondary-auth start when OTP is not enrolled: client should treat as "not registered", not a login failure.</summary>
    public static byte[] SecondaryAuthNotRegisteredStart() =>
        JsonSerializer.SerializeToUtf8Bytes(new { session_id = "", registered = false, auth_type = "OTP" }, PlatformResponses.Plain);

    public static byte[] EmptyAuthTypes() =>
        JsonSerializer.SerializeToUtf8Bytes(new { auth_types = Array.Empty<object>() }, PlatformResponses.Plain);
}
