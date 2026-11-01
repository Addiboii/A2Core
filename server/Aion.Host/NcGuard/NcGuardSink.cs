using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Aion.Host.NcGuard;

public sealed record NcGuardOptions
{
    public int? Port { get; init; }
    public string? FixtureFile { get; init; }
    public string DataDirectory { get; init; } = "research/local";
    public string[] Hosts { get; init; } = [];
    public int BodyCap { get; init; } = 262144;
    public bool RewriteHosts { get; init; }
    public int? DurationSeconds { get; init; }
    public string? StopFile { get; init; }
    /// <summary>Record each request (headers and body) under DataDirectory. Research hosts only; a public host turns it off.</summary>
    public bool Capture { get; init; } = true;
    /// <summary>Listen address (Aion:Network:BindAddress; loopback by default).</summary>
    public string BindAddress { get; init; } = "127.0.0.1";
}

/// <summary>Fixture server: no upstream traffic and no trust-store installation (clients accept its own certificates).</summary>
public sealed class NcGuardSink : BackgroundService
{
    private sealed record Rule(string Host, string? Method, string? Path, string[]? Prefixes,
        int Status, string ContentType, byte[] Body);
    private readonly NcGuardOptions options;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<NcGuardSink> logger;
    private readonly Dictionary<string, X509Certificate2> certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rule> rules = [];
    private readonly SemaphoreSlim captureLock = new(1);
    private readonly X509Certificate2 authority;
    private readonly byte[] hostsBlock;
    private string? hostsPath;
    private int records;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    public string RunDirectory { get; }

    public NcGuardSink(NcGuardOptions options, IHostApplicationLifetime lifetime, ILogger<NcGuardSink> logger)
    {
        this.logger = logger;
        this.options = options = options.Hosts.Length == 0 ? options with { Hosts =
            ["bladnoch.ncsoft.com", "lagavulin.ncsoft.com", "atp-json.ncsoft.com",
             "sdk.gpa.ncsoft.com", "stun.gpa.ncsoft.com", "nccr-api.global.nc.com"] } : options;
        this.lifetime = lifetime;
        if (options.Port is not (>= 1 and <= 65535) || options.BodyCap is < 0 or > 16777216 ||
            options.DurationSeconds is < 1 or > 86400)
            throw new InvalidDataException("Invalid NCGuard port, capture cap or duration.");
        if (options.Hosts.Length == 0 || options.Hosts.Any(h => Uri.CheckHostName(h) != UriHostNameType.Dns ||
            h.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')))
            throw new InvalidDataException("NCGuard hosts must be DNS names.");
        if (options.FixtureFile is { } fixture)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(fixture));
            foreach (var row in doc.RootElement.GetProperty("rules").EnumerateArray())
            {
                string? Value(string name) => row.TryGetProperty(name, out var value) ? value.GetString() : null;
                var host = Value("host") ?? throw new InvalidDataException("Fixture host missing.");
                if (!options.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Fixture host is outside the configured hosts.");
                var status = row.TryGetProperty("status", out var code) ? code.GetInt32() : 200;
                var contentType = Value("content_type") ?? "application/json";
                if (status is < 200 or > 599 || contentType.Contains('\r') || contentType.Contains('\n'))
                    throw new InvalidDataException("Invalid fixture response metadata.");
                rules.Add(new(host, Value("method"), Value("path"),
                    row.TryGetProperty("paths", out var paths) ? paths.EnumerateArray().Select(p => p.GetString()!).ToArray() : null,
                    status, contentType, Value("body_base64") is { } encoded ? Convert.FromBase64String(encoded) :
                        Encoding.UTF8.GetBytes(Value("body") ?? "")));
            }
        }
        RunDirectory = Path.Combine(Path.GetFullPath(options.DataDirectory), $"tls-mitm-dotnet-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RunDirectory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=AionResearch loopback CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllText(Path.Combine(RunDirectory, "ca.pem"), authority.ExportCertificatePem());
        foreach (var host in options.Hosts.Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var leafKey = RSA.Create(2048);
            var leaf = new CertificateRequest($"CN={host}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
            leaf.CertificateExtensions.Add(san.Build());
            leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var signed = leaf.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            using var withKey = signed.CopyWithPrivateKey(leafKey);
            // Schannel cannot use ephemeral private keys. Default temporary user-key
            // containers are removed on disposal; this does not install a trusted CA.
            certificates[host] = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
            File.WriteAllText(Path.Combine(RunDirectory, host + ".pem"), signed.ExportCertificatePem());
        }
        var marker = Guid.NewGuid().ToString("N");
        hostsBlock = Encoding.UTF8.GetBytes("\n" + string.Join("\n", options.Hosts.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => $"127.0.0.1 {h} # aion-ncguard-{marker}")) + "\n");
        State("prepared");
    }

    public void Configure(KestrelServerOptions server) => server.Listen(IPAddress.Parse(options.BindAddress), options.Port!.Value, listener =>
    {
        listener.Protocols = HttpProtocols.Http1;
        listener.UseHttps(https => https.ServerCertificateSelector = (_, name) =>
            name is not null && certificates.TryGetValue(name, out var cert) ? cert : certificates["localhost"]);
    });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.RewriteHosts)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic hosts changes require Windows.");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            var original = File.ReadAllBytes(path);
            File.WriteAllBytes(Path.Combine(RunDirectory, "hosts-before.bin"), original);
            File.WriteAllBytes(Path.Combine(RunDirectory, "hosts-block.bin"), hostsBlock);
            File.WriteAllBytes(path, [.. original, .. hostsBlock]);
            hostsPath = path;
        }
        lifetime.ApplicationStarted.Register(() => State("running"));
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if ((options.DurationSeconds is { } seconds && (DateTimeOffset.UtcNow - started).TotalSeconds >= seconds) ||
                    (options.StopFile is { } stop && File.Exists(stop)))
                { lifetime.StopApplication(); return; }
                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task HandleAsync(HttpContext context)
    {
        // Route by TLS SNI, like the Python sink. Host headers cannot select another host's fixture.
        var sni = context.Features.Get<ITlsHandshakeFeature>()?.HostName;
        var host = sni is not null && certificates.ContainsKey(sni) ? sni.ToLowerInvariant() : "default";
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        var rule = rules.FirstOrDefault(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase) &&
            (r.Method is null || r.Method == context.Request.Method) && (r.Path is null || r.Path == target) &&
            (r.Prefixes is null || r.Prefixes.Any(p => target.StartsWith(p, StringComparison.Ordinal))));
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = null;
        using var body = new MemoryStream();
        var buffer = new byte[8192]; long received = 0; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            received += count;
            body.Write(buffer, 0, Math.Min(count, options.BodyCap - (int)body.Length));
        }
        var bytes = body.ToArray();
        byte[] response;
        int status;
        string? contentTypeOverride = null;
        if (rule is not null)
        {
            response = rule.Body;
            status = rule.Status;
        }
        else if (NcGuardBuiltinResponses.TryReply(host, context.Request.Method, PathOnly(target), out var builtIn, out contentTypeOverride))
        {
            response = builtIn;
            status = 200;
        }
        else
        {
            response = "loopback-mitm: not answered\n"u8.ToArray();
            status = 404;
            if (string.Equals(host, "sdk.gpa.ncsoft.com", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning("Unhandled GPA SDK request {Method} {Path}", context.Request.Method, PathOnly(target));
        }
        if (!options.Capture)
        {
            Interlocked.Increment(ref records);
            await Respond(context, rule, status, response, contentTypeOverride);
            return;
        }
        await captureLock.WaitAsync(context.RequestAborted);
        try
        {
            var bodyPath = Path.Combine(RunDirectory, "request-bodies.bin");
            await using var output = new FileStream(bodyPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var offset = output.Position;
            await output.WriteAsync(bytes, context.RequestAborted);
            await output.FlushAsync(context.RequestAborted);
            var record = new { n = ++records, utc = DateTimeOffset.UtcNow, host, method = context.Request.Method,
                path = target, request_line = $"{context.Request.Method} {target} {context.Request.Protocol}",
                headers = context.Request.Headers.SelectMany(h => h.Value.Select(v => new[] { h.Key, v })).ToArray(),
                body_offset = offset, body_len = bytes.Length, received_body_len = received, truncated = received > bytes.Length,
                body_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), response_status = status, response_bytes = response.Length };
            await File.AppendAllTextAsync(Path.Combine(RunDirectory, "mitm-records.jsonl"), JsonSerializer.Serialize(record) + "\n", context.RequestAborted);
        }
        finally { captureLock.Release(); }
        await Respond(context, rule, status, response, contentTypeOverride);
    }

    static string PathOnly(string target)
    {
        var q = target.IndexOf('?');
        return q >= 0 ? target[..q] : target;
    }

    private static async Task Respond(HttpContext context, Rule? rule, int status, byte[] response, string? contentTypeOverride = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentTypeOverride ?? rule?.ContentType ?? "text/plain";
        context.Response.ContentLength = response.Length;
        await context.Response.Body.WriteAsync(response, context.RequestAborted);
    }

    private void State(string status) => File.WriteAllText(Path.Combine(RunDirectory, "state.json"),
        JsonSerializer.Serialize(new { status, utc = DateTimeOffset.UtcNow, port = options.Port, hosts = options.Hosts,
            started_utc = startedAt, finished_utc = status == "finished" ? (DateTimeOffset?)DateTimeOffset.UtcNow : null,
            hosts_file_applied = hostsPath is not null, respond = options.FixtureFile is not null, body_cap = options.BodyCap, records }));

    private void RestoreHosts()
    {
        if (hostsPath is not { } path) return;
        var current = File.ReadAllBytes(path);
        var at = current.AsSpan().IndexOf(hostsBlock);
        if (at < 0) throw new IOException($"NCGuard hosts block changed; inspect {RunDirectory} before restoring.");
        File.WriteAllBytes(path, [.. current.AsSpan(0, at), .. current.AsSpan(at + hostsBlock.Length)]);
        hostsPath = null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken); }
        finally { RestoreHosts(); State("finished"); }
    }
    public override void Dispose()
    {
        RestoreHosts();
        foreach (var cert in certificates.Values) cert.Dispose();
        authority.Dispose();
        base.Dispose();
    }
}
