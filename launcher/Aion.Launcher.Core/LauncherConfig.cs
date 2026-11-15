using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>
/// The server this launcher plays on (embedded launcher.json; a launcher.json next to the exe, or the one --config names,
/// overrides it; command-line arguments override single values: <see cref="WithArguments"/>).
/// </summary>
public sealed record LauncherConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("accountUrl")] string AccountUrl,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string SavedDirSuffix,
    // Optional SPKI SHA-256 pins for a self-signed account certificate (CertificatePins; launcher/README.md).
    [property: JsonPropertyName("certificatePins")] string[]? CertificatePins = null)
{
    public static LauncherConfig Load(string? overridePath = null)
    {
        LauncherConfig config;
        if (overridePath is not null && File.Exists(overridePath))
            config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(overridePath)) ?? throw new InvalidDataException("launcher.json");
        else
        {
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json")
                ?? throw new InvalidOperationException("launcher.json is not embedded");
            config = JsonSerializer.Deserialize<LauncherConfig>(stream) ?? throw new InvalidDataException("launcher.json");
        }
        config.Validate();
        return config;
    }

    /// <summary>The arguments that set a value (also accepted as "--name value"); --pin may repeat.</summary>
    public static readonly string[] ArgumentNames = ["--config", "--ip", "--account-url", "--port", "--pin", "--name", "--saved-suffix"];

    /// <summary>The value arguments, by name, from the command line ("--ip=1.2.3.4" or "--ip 1.2.3.4"). Mode arguments
    /// (--uninstall) are skipped; anything else is an error.</summary>
    public static List<(string Name, string Value)> ParseArguments(IReadOnlyList<string> args)
    {
        var parsed = new List<(string, string)>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--uninstall") continue;
            var cut = arg.IndexOf('=');
            var name = (cut < 0 ? arg : arg[..cut]).ToLowerInvariant();
            if (!ArgumentNames.Contains(name)) throw new InvalidDataException($"unknown argument {arg} (use {string.Join(", ", ArgumentNames)})");
            string value;
            if (cut >= 0) value = arg[(cut + 1)..];
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            else throw new InvalidDataException($"{name} needs a value");
            parsed.Add((name, value.Trim()));
        }
        return parsed;
    }

    /// <summary>The settings file the arguments name (--config; it must exist), else <paramref name="fallback"/>.</summary>
    public static string? ConfigPath(IReadOnlyList<string> args, string? fallback)
    {
        if (ParseArguments(args).LastOrDefault(a => a.Name == "--config") is not { Value.Length: > 0 } named) return fallback;
        var path = Path.GetFullPath(named.Value);
        return File.Exists(path) ? path : throw new InvalidDataException($"--config file not found: {path}");
    }

    /// <summary>
    /// This config with the command line's values on top: --ip (the account URL's host follows it unless --account-url is
    /// given), --account-url, --port (the bootstrap port), --pin (repeatable; replaces the file's pins), --name,
    /// --saved-suffix. Validated like the file.
    /// </summary>
    public LauncherConfig WithArguments(IReadOnlyList<string> args)
    {
        var parsed = ParseArguments(args);
        string? Last(string name) => parsed.LastOrDefault(a => a.Name == name).Value;
        var config = this;
        if (Last("--ip") is { } ip)
        {
            config = config with { ServerAddress = ip };
            if (Last("--account-url") is null && Uri.TryCreate(config.AccountUrl, UriKind.Absolute, out var account))
                config = config with { AccountUrl = new UriBuilder(account) { Host = ip }.Uri.ToString().TrimEnd('/') };
        }
        if (Last("--account-url") is { } url) config = config with { AccountUrl = url };
        if (Last("--port") is { } port)
            config = config with { BootstrapPort = int.TryParse(port, out var p) ? p : throw new InvalidDataException("--port must be a number") };
        if (Last("--name") is { } name) config = config with { Name = name };
        if (Last("--saved-suffix") is { } suffix) config = config with { SavedDirSuffix = suffix };
        if (parsed.Where(a => a.Name == "--pin").Select(a => a.Value).ToArray() is { Length: > 0 } pins) config = config with { CertificatePins = pins };
        config.Validate();
        return config;
    }

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>This config as launcher.json (the installed copy keeps the settings it was installed with).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    [JsonIgnore] public IPAddress Server => IPAddress.Parse(ServerAddress);

    public void Validate()
    {
        if (!IPAddress.TryParse(ServerAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("serverAddress must be an IPv4 address");
        if (!Uri.TryCreate(AccountUrl, UriKind.Absolute, out var account) || account.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("accountUrl must be an https URL");
        if (BootstrapPort is < 1 or > 65535) throw new InvalidDataException("bootstrapPort");
        if (SavedDirSuffix.Length is < 1 or > 32 || !SavedDirSuffix.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("savedDirSuffix must be 1-32 letters or digits");
        if (SavedDirSuffix.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("savedDirSuffix must not be the official game's (Steam)");
        if (CertificatePins is { } pins && (pins.Length > 8 || pins.Any(p => Aion.Launcher.CertificatePins.Parse(p) is null)))
            throw new InvalidDataException("certificatePins must be up to 8 SHA-256 public-key pins (base64 or hex)");
    }

    /// <summary>The bootstrap URL for one launch: the launch authorization is the code in the path.</summary>
    public string BootstrapUrl(string authorization) => $"http://{ServerAddress}:{BootstrapPort}/c/{authorization}";
}
