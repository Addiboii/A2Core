using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aion.Application.Authentication;

namespace Aion.Host.Platform;

/// <summary>
/// Configuration of the dev client's platform bootstrap (GamePlatform.ini
/// ConfigurationServerUrl points here). Port of the HTTP side of tools/probe_dev_bootstrap.py
/// and probe_local_bootstrap.py; editing GamePlatform.ini and launching stay in the launcher.
/// </summary>
public sealed record PlatformOptions
{
    public int? Port { get; init; }
    /// <summary>Directory with app-key.json, app-maintenance.json, local-test-key.json and the session/auth candidates.</summary>
    public string? FixtureDirectory { get; init; }
    /// <summary>Encrypted policy ({"value"}) served as-is; or use <see cref="PolicyPlaintextFile"/>.</summary>
    public string? PolicyFile { get; init; }
    /// <summary>Plain policy JSON, encrypted at startup with local-test-key.json (AES-128-CBC, IV = key).</summary>
    public string? PolicyPlaintextFile { get; init; }
    /// <summary>Our GET geolocations/v1.0/me body (region-screen distances).</summary>
    public string? GeolocationFile { get; init; }
    /// <summary>Account-page launch authorization file; its single-use token is redeemed for the authn_token.</summary>
    public string? LaunchAuthorizationFile { get; init; }
    /// <summary>Optional JSONL of requests (method, path, status; MobileAppConfigSdk queries). Never bodies.</summary>
    public string? RequestLog { get; init; }
    /// <summary>
    /// Policy custom_data keys set (or overridden) on the served policy, by name: the game's own switches (the key table
    /// read from the client on 2026-10-18: SecondaryAuthCheckType, SteamShowOnlySteamLogin, CommunityUIEnable, the help
    /// and privacy URLs, ...). Configuration: Aion:Platform:CustomData:&lt;Key&gt;=&lt;value&gt;.
    /// </summary>
    public Dictionary<string, string>? CustomData { get; init; }
}

/// <summary>The responses, validated once at startup like the Python fixture checks.</summary>
public sealed class PlatformResponses
{
    // Plain '+', '/' and non-ASCII, as Python's json.dumps writes them (no unicode escapes).
    internal static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public byte[]? Key { get; private init; }
    /// <summary>The policy for clients without a launch code (the fixture's own bytes when nothing needed re-addressing).</summary>
    public byte[]? Policy { get; private init; }
    public byte[]? Maintenance { get; private init; }
    public byte[]? Session { get; private init; }
    /// <summary>
    /// The Steam token login reply (POST sessions/v1.0/login_sessions;token): the session fields plus the shape the client's
    /// SDK reads around them (a user object, access_token / refresh_token / expires_in, the login type). CANDIDATE: the
    /// official reply is unrecorded; the plain session object was refused on 2026-10-18.
    /// </summary>
    public byte[]? SteamLogin { get; private init; }
    public string? SessionId { get; private init; }
    public byte[]? Authn { get; private init; }
    public byte[]? SecondaryStart { get; private init; }
    public byte[]? SecondaryFinish { get; private init; }
    public byte[]? Geolocation { get; private init; }
    public string? LaunchAuthorization { get; private init; }

    public static PlatformResponses Load(PlatformOptions options, int? loginPort, int? chatPort) =>
        Load(options, new NetworkOptions(), options.Port ?? 8087, loginPort, chatPort);

    public static PlatformResponses Load(PlatformOptions options, NetworkOptions network, int platformPort, int? loginPort, int? chatPort)
    {
        string? Fixture(string name) => options.FixtureDirectory is { } dir && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null;
        byte[]? keyResponse = null, policy = null, maintenance = null;
        string? aesKey = null;
        if (Fixture("app-key.json") is { } keyPath)
        {
            var value = ValueFixture(keyPath);
            if (Convert.FromBase64String(value).Length != 256) throw new InvalidDataException("Expected a 256-byte RSA ciphertext in app-key.json");
            keyResponse = PythonJson("value", value);
            if (Fixture("local-test-key.json") is { } local)
                aesKey = JsonNode.Parse(File.ReadAllText(local))?["key"]?.GetValue<string>();
        }
        JsonObject? template = null;
        if (options.PolicyPlaintextFile is { } plainPath)
        {
            if (keyResponse is null || aesKey is null) throw new InvalidDataException("A plaintext policy needs app-key.json and local-test-key.json");
            template = JsonNode.Parse(File.ReadAllText(plainPath))?.AsObject() ?? throw new InvalidDataException("Policy must be a JSON object");
            // Point the client at this host's own address and ports.
            PointAtHost(template, network.PublicAddress, platformPort, loginPort, chatPort, null, options.CustomData);
            policy = PythonJson("value", Convert.ToBase64String(Encrypt(aesKey, Encoding.UTF8.GetBytes(template.ToJsonString(Plain)))));
        }
        else if ((options.PolicyFile ?? Fixture("app-policy.json")) is { } policyPath)
        {
            if (keyResponse is null) throw new InvalidDataException("Policy response requires a matching key response");
            var cipher = BlockFixture(policyPath);
            policy = PythonJson("value", cipher);
            // With the settings key the policy can be re-addressed (public host, launch codes);
            // unchanged addresses keep the fixture's own bytes.
            if (aesKey is not null && Decrypt(aesKey, Convert.FromBase64String(cipher)) is { } decrypted)
            {
                template = decrypted;
                if (PointAtHost(template, network.PublicAddress, platformPort, loginPort, chatPort, null, options.CustomData))
                    policy = PythonJson("value", Convert.ToBase64String(Encrypt(aesKey, Encoding.UTF8.GetBytes(template.ToJsonString(Plain)))));
            }
            else if (!network.LoopbackOnly)
                throw new InvalidDataException("A public host needs a policy it can re-address (local-test-key.json with the encrypted policy, or PolicyPlaintextFile)");
        }
        if (Fixture("app-maintenance.json") is { } maintenancePath && policy is not null)
            maintenance = PythonJson("value", BlockFixture(maintenancePath));
        byte[]? session = null, steamLogin = null;
        string? sessionId = null;
        if (Fixture("session-response.candidate.json") is { } sessionPath && maintenance is not null)
        {
            var fields = JsonNode.Parse(File.ReadAllText(sessionPath))!.AsObject();
            string[] expected = ["session", "session_secret", "game_account_id", "login_name", "user_id", "user_name"];
            if (fields.Count != expected.Length || expected.Any(f => fields[f]?.GetValue<string>() is not { Length: >= 1 and <= 512 }))
                throw new InvalidDataException("Expected six nonempty session string fields");
            session = PythonJson(expected.Select(f => (f, fields[f]!.GetValue<string>())));
            sessionId = fields["session"]!.GetValue<string>();
            var steam = new JsonObject();
            foreach (var f in expected) steam[f] = fields[f]!.GetValue<string>();
            steam["user"] = new JsonObject { ["user_id"] = fields["user_id"]!.GetValue<string>(), ["login_name"] = fields["login_name"]!.GetValue<string>(), ["user_name"] = fields["user_name"]!.GetValue<string>() };
            steam["access_token"] = fields["session"]!.GetValue<string>();
            steam["refresh_token"] = fields["session_secret"]!.GetValue<string>();
            steam["expires_in"] = 86400;
            steam["login_type"] = "steam";
            steam["auth_provider_code"] = "steam";
            steamLogin = Encoding.UTF8.GetBytes(steam.ToJsonString(Plain));
        }
        var authn = session is null ? null : StringField(Fixture("authn-response.candidate.json"), "authn_token", 4096);
        var start = authn is null ? null : StringField(Fixture("secondary-start-response.candidate.json"), "session_id", 512);
        var finish = start is null ? null : StringField(Fixture("secondary-finish-response.candidate.json"), "token", 4096);
        string? launch = null;
        if (options.LaunchAuthorizationFile is { } launchPath)
        {
            var raw = File.ReadAllBytes(launchPath);
            var value = raw.Length <= 4096 ? JsonNode.Parse(raw)?.AsObject() : null;
            launch = value?["authorization"]?.GetValue<string>();
            if (launch is not { Length: 64 } || !launch.All(char.IsAsciiHexDigit) || value?["worldId"]?.GetValue<int>() != 1204)
                throw new InvalidDataException("Invalid launch authorization file");
        }
        return new PlatformResponses
        {
            policyTemplate = template, policyKey = aesKey, policyCustomData = options.CustomData, publicAddress = network.PublicAddress, platformPort = platformPort,
            loginPort = loginPort, chatPort = chatPort,
            Key = keyResponse, Policy = policy, Maintenance = maintenance, Session = session, SessionId = sessionId, SteamLogin = steamLogin,
            Authn = authn, SecondaryStart = start, SecondaryFinish = finish, LaunchAuthorization = launch,
            Geolocation = options.GeolocationFile is { } geo ? File.ReadAllBytes(geo) : null,
        };
    }

    JsonObject? policyTemplate;
    string? policyKey;
    IReadOnlyDictionary<string, string>? policyCustomData;
    string publicAddress = "127.0.0.1";
    int platformPort;
    int? loginPort, chatPort;
    readonly ConcurrentDictionary<string, byte[]> codePolicies = new();

    /// <summary>
    /// The policy for a client launched with a code (bootstrap URL /c/&lt;code&gt;): its api_gate,
    /// login_web and login_api keep the code, so the authn_token request names the launch.
    /// </summary>
    public byte[]? PolicyFor(string? code)
    {
        if (code is null) return Policy;
        if (policyTemplate is null || policyKey is null) return null;
        if (codePolicies.Count > 1024) codePolicies.Clear();
        return codePolicies.GetOrAdd(code, c =>
        {
            var copy = policyTemplate.DeepClone().AsObject();
            PointAtHost(copy, publicAddress, platformPort, loginPort, chatPort, c, policyCustomData);
            return PythonJson("value", Convert.ToBase64String(Encrypt(policyKey, Encoding.UTF8.GetBytes(copy.ToJsonString(Plain)))));
        });
    }

    /// <summary>
    /// Points a policy's addresses at this host: api_gate, login_web and login_api at the bootstrap port
    /// (path kept, launch code prefixed), LoginServerIP at the login port, ChatServerURL at the chat port.
    /// Returns whether anything changed.
    /// </summary>
    public static bool PointAtHost(JsonObject policy, string address, int platformPort, int? loginPort, int? chatPort, string? code,
        IReadOnlyDictionary<string, string>? customData = null)
    {
        var changed = false;
        void Set(JsonObject node, string key, string value)
        {
            if (node[key]?.GetValueKind() == JsonValueKind.String && node[key]!.GetValue<string>() == value) return;
            node[key] = value;
            changed = true;
        }
        foreach (var key in new[] { "api_gate", "login_web", "login_api" })
            if (policy[key]?.GetValueKind() == JsonValueKind.String && Uri.TryCreate(policy[key]!.GetValue<string>(), UriKind.Absolute, out var uri))
                Set(policy, key, $"http://{address}:{platformPort}{(code is null ? "" : "/c/" + code)}{uri.AbsolutePath.TrimEnd('/')}");
        var custom = policy["custom_data"] as JsonObject;
        if (custom is null && customData is { Count: > 0 })
        {
            policy["custom_data"] = custom = new JsonObject();
            changed = true;
        }
        if (custom is not null)
        {
            if (loginPort is { } login) Set(custom, "LoginServerIP", $"{address}:{login}");
            if (chatPort is { } chat) Set(custom, "ChatServerURL", $"http://{address}:{chat}");
            // The configured switches last: they override the fixture's values by design.
            foreach (var (key, value) in customData ?? new Dictionary<string, string>())
                if (key.Length > 0) Set(custom, key, value);
        }
        return changed;
    }

    static JsonObject? Decrypt(string key, byte[] cipher)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes(key);
            return JsonNode.Parse(aes.DecryptCbc(cipher, aes.Key, PaddingMode.PKCS7)) as JsonObject;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException) { return null; }
    }

    static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Plain);

    /// <summary>A flat string object exactly as Python's json.dumps writes it (", "/": " separators, ASCII only).</summary>
    public static byte[] PythonJson(IEnumerable<(string Key, string Value)> fields)
    {
        static string Quote(string s)
        {
            var b = new StringBuilder("\"");
            foreach (var c in s)
                b.Append(c switch
                {
                    '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                    < ' ' or > '~' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                });
            return b.Append('"').ToString();
        }
        return Encoding.ASCII.GetBytes("{" + string.Join(", ", fields.Select(f => Quote(f.Key) + ": " + Quote(f.Value))) + "}");
    }

    static byte[] PythonJson(string key, string value) => PythonJson([(key, value)]);

    static string ValueFixture(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        if (node is not { Count: 1 } || node["value"]?.GetValueKind() != JsonValueKind.String)
            throw new InvalidDataException($"{Path.GetFileName(path)} must contain only a string value");
        return node["value"]!.GetValue<string>();
    }

    static string BlockFixture(string path)
    {
        var value = ValueFixture(path);
        var cipher = Convert.FromBase64String(value);
        if (cipher.Length == 0 || cipher.Length % 16 != 0 || cipher.Length > 1048576)
            throw new InvalidDataException($"{Path.GetFileName(path)} ciphertext must contain 1..65536 AES blocks");
        return value;
    }

    static byte[]? StringField(string? path, string field, int max)
    {
        if (path is null) return null;
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        if (node is not { Count: 1 } || node[field]?.GetValueKind() != JsonValueKind.String || node[field]!.GetValue<string>().Length is < 1 || node[field]!.GetValue<string>().Length > max)
            throw new InvalidDataException($"{Path.GetFileName(path)} must contain only a nonempty {field} string");
        return PythonJson(field, node[field]!.GetValue<string>());
    }

    /// <summary>The observed settings convention: AES-128-CBC, PKCS7, key = IV = 16 printable ASCII characters.</summary>
    public static byte[] Encrypt(string key, byte[] plain)
    {
        if (key.Length != 16 || key.Any(c => c is < ' ' or > '~')) throw new InvalidDataException("Expected exactly 16 printable ASCII key characters");
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(key);
        return aes.EncryptCbc(plain, aes.Key, PaddingMode.PKCS7);
    }
}

/// <summary>
/// Launches awaiting their client, by the address that asked for them (POST /api/client-launches on
/// this host). A client started without a launch code in its bootstrap URL redeems the newest launch
/// of its own address. Codes in the URL are preferred: players behind one address can collide here.
/// </summary>
public sealed class LaunchPairing(TimeProvider clock)
{
    static readonly TimeSpan Remembered = TimeSpan.FromMinutes(15);
    readonly object gate = new();
    readonly Dictionary<IPAddress, (string Authorization, DateTimeOffset Expires)> pending = [];
    /// <summary>The launch an address last took, so a repeated authn_token gets the same ticket.</summary>
    readonly Dictionary<IPAddress, (string Authorization, DateTimeOffset Until)> taken = [];

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public void Register(IPAddress? from, string authorization, DateTimeOffset expires)
    {
        if (from is null) return;
        lock (gate)
        {
            Prune();
            pending[Normalize(from)] = (authorization, expires);
        }
    }

    public string? Resolve(IPAddress? from)
    {
        if (from is null) return null;
        var address = Normalize(from);
        lock (gate)
        {
            Prune();
            if (pending.Remove(address, out var launch))
            {
                taken[address] = (launch.Authorization, clock.GetUtcNow() + Remembered);
                return launch.Authorization;
            }
            return taken.TryGetValue(address, out var last) ? last.Authorization : null;
        }
    }

    void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) pending.Remove(key);
        foreach (var key in taken.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList()) taken.Remove(key);
    }
}

public static partial class PlatformBootstrapEndpoints
{
    static readonly byte[] NotReady = """{"local_probe":true,"ready":false}"""u8.ToArray();
    static readonly string[] Known = ["/MobileAppConfigSdk/app/key", "/MobileAppConfigSdk/app/policy", "/local-api-gate", "/local-login-web", "/local-login-api"];

    // Session lookup by a stored ID (a UUID, or the session this server issued) or with none: a fresh
    // saved folder (the launcher's Saved_A2CorePrivate) asks GET .../sessions/v1.0/ (observed 2026-10-15).
    // The answer is the same placeholder session; identity comes from the launch code at authn_token.
    [GeneratedRegex(@"^/local-api-gate/sessions/v1\.0/(?:[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})?$")]
    private static partial Regex SessionPath();

    /// <summary>A launch code prefix: /c/ and the 64 hex digits of a launch authorization.</summary>
    [GeneratedRegex(@"^/c/([0-9a-fA-F]{64})(/.*)?$")]
    private static partial Regex CodePrefix();

    /// <summary>Answers every request on the bootstrap port in its own branch (no static files or other routes there).</summary>
    public static void UsePlatformBootstrap(this WebApplication app, int port, PlatformResponses responses, string? requestLog)
    {
        var issuedSession = responses.SessionId is { } id ? "/local-api-gate/sessions/v1.0/" + Uri.EscapeDataString(id) : null;
        // A launch authorization becomes a game ticket (12 h within the account session, like the launch itself). Repeated
        // authn_token requests of the same launch get the same ticket for 15 minutes; later ones mint another, which is fine.
        var redeemed = new ConcurrentDictionary<string, (Lazy<Task<byte[]?>> Ticket, DateTimeOffset At)>();
        Task<byte[]?> Redeem(string authorization)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var old in redeemed.Where(r => now - r.Value.At > TimeSpan.FromMinutes(15)).ToList()) redeemed.TryRemove(old);
            return redeemed.GetOrAdd(authorization, a => (new Lazy<Task<byte[]?>>(async () =>
            {
                var handoff = app.Services.GetService<IClientHandoff>();
                if (handoff is null) return null;
                var ticket = await handoff.RedeemLaunchAsync(a, CancellationToken.None);
                // Rejected codes are not remembered (anyone can send them).
                if (ticket is null) redeemed.TryRemove(a, out _);
                return ticket is null ? null : PlatformResponses.PythonJson([("authn_token", ticket.Token)]);
            }), now)).Ticket.Value;
        }
        var pairing = app.Services.GetService<LaunchPairing>();
        var logLock = new object();
        app.MapWhen(context => context.Connection.LocalPort == port, branch => branch.Run(async context =>
        {
            var request = context.Request;
            var path = request.Path.Value ?? "/";
            // /c/<code>/...: a launcher-made bootstrap URL; the code is the launch authorization.
            string? code = null;
            if (CodePrefix().Match(path) is { Success: true } prefixed)
            {
                code = prefixed.Groups[1].Value;
                path = prefixed.Groups[2].Success ? prefixed.Groups[2].Value : "/";
            }
            var method = request.Method;
            (int Status, byte[] Body) reply = (503, NotReady);
            if (method == "GET" && path == Known[0] && responses.Key is { } key) reply = (200, key);
            else if (method == "GET" && path == Known[1] && responses.PolicyFor(code) is { } policy) reply = (200, policy);
            else if (method == "POST" && path == "/MobileAppConfigSdk/app/policy/maintenance" && responses.Maintenance is { } maintenance) reply = (200, maintenance);
            else if (method == "POST" && path == "/local-api-gate/sessions/v1.0/authn_token")
            {
                // The launch code, else the launcher's file (development), else this address's pending launch.
                var launch = code ?? responses.LaunchAuthorization ?? (responses.Authn is null ? pairing?.Resolve(context.Connection.RemoteIpAddress) : null);
                if (launch is not null)
                    reply = await Redeem(launch) is { } token ? (200, token) : (401, """{"error":"Launch authorization rejected"}"""u8.ToArray());
                else if (responses.Authn is { } authn) reply = (200, authn);
            }
            // Steam token login (the client's route when the policy says SteamShowOnlySteamLogin): answered with the same login
            // session object as a session lookup (CANDIDATE: the official reply is unrecorded; the Steam ticket in the body is not read).
            else if (method == "POST" && path == "/local-api-gate/sessions/v1.0/login_sessions;token" && responses.SteamLogin is { } steamSession) reply = (200, steamSession);
            else if (method == "POST" && path == "/local-api-gate/secondary_auth/v1.0/my/auth;start" && responses.SecondaryStart is { } start) reply = (200, start);
            else if (method == "POST" && path == "/local-api-gate/secondary_auth/v1.0/my/auth;finish" && responses.SecondaryFinish is { } finish) reply = (200, finish);
            else if (method == "GET" && path == "/local-api-gate/geolocations/v1.0/me" && responses.Geolocation is { } geo) reply = (200, geo);
            else if (method == "GET" && responses.Session is { } session && (path == issuedSession || SessionPath().IsMatch(path))) reply = (200, session);
            if (requestLog is not null)
            {
                var family = Known.Skip(2).FirstOrDefault(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
                var entry = JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, method, path = family is not null || path.StartsWith("/MobileAppConfigSdk", StringComparison.Ordinal) ? path : "<other>",
                    query = path.StartsWith("/MobileAppConfigSdk", StringComparison.Ordinal) ? request.QueryString.Value : null,
                    route_family = family, launch_code = code is not null, response_status = reply.Status,
                });
                lock (logLock) File.AppendAllText(requestLog, entry + "\n");
            }
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Connection = "close";
            context.Response.ContentLength = reply.Body.Length;
            if (method != "HEAD") await context.Response.Body.WriteAsync(reply.Body);
        }));
    }
}
