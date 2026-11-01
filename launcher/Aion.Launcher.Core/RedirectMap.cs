using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aion.Launcher;

/// <summary>
/// Hostname → IPv4 map for DNS redirect (injected DLL env var or sidecar file). Format:
/// <c>host=ip;host=ip</c> (semicolon-separated, case-insensitive hostnames).
/// </summary>
public static class RedirectMap
{
    public static IReadOnlyDictionary<string, IPAddress> Parse(string? text)
    {
        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (var segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0) throw new FormatException($"Invalid redirect entry (expected host=ip): \"{segment}\"");
            var host = segment[..equals].Trim();
            if (host.Length == 0) throw new FormatException("Redirect entry is missing a hostname.");
            var ipText = segment[(equals + 1)..].Trim();
            if (!IPAddress.TryParse(ipText, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException($"Redirect IP must be IPv4: \"{ipText}\"");
            map[host] = address;
        }
        return map;
    }

    public static string Format(IPAddress server, IEnumerable<string> hostnames) =>
        string.Join(';', hostnames.Select(host => $"{host}={server}"));

    /// <summary>Every NC hostname the launcher sends to <paramref name="server"/>.</summary>
    public static string FormatForServer(IPAddress server) => Format(server, HostsFile.Hostnames);

    public static void WriteMapFile(string path, IPAddress server)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, FormatForServer(server) + Environment.NewLine, Encoding.UTF8);
    }
}

/// <summary>How NC hostnames are pointed at the private server during Play.</summary>
public static class RedirectModeSelection
{
    public const string Inject = "inject";
    public const string Hosts = "hosts";

    public static string Resolve(string? settingsRedirectMode, LauncherConfig config)
    {
        var raw = settingsRedirectMode ?? config.RedirectMode;
        if (raw is null) return Inject;
        return raw.Equals(Hosts, StringComparison.OrdinalIgnoreCase) ? Hosts : Inject;
    }

    public static bool PreferInject(string? settingsRedirectMode, LauncherConfig config) =>
        Resolve(settingsRedirectMode, config) == Inject;
}
