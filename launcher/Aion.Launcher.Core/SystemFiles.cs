using System.Net;
using System.Text;

namespace Aion.Launcher;

/// <summary>
/// The client's platform settings for our private saved folder
/// (%LOCALAPPDATA%\AION2\Saved_&lt;suffix&gt;\Config\Windows\GamePlatform.ini): the bootstrap URL with this
/// launch's code, and an updater profile that points nowhere. The official game's Saved_Steam folder
/// is never touched.
/// </summary>
public static class GamePlatformIni
{
    public const string Section = "/Script/Aion2.AionGamePlatformSettings";

    public static string PathFor(string localAppData, string suffix) =>
        Path.Combine(localAppData, "AION2", "Saved_" + suffix, "Config", "Windows", "GamePlatform.ini");

    /// <summary>The file with our section replaced (other sections, e.g. the game's own user settings, kept).</summary>
    public static string WithBootstrap(string? existing, string bootstrapUrl)
    {
        if (bootstrapUrl.Any(c => c is '"' or '\r' or '\n')) throw new ArgumentException("Invalid bootstrap URL");
        var kept = new StringBuilder();
        var skipping = false;
        foreach (var line in (existing ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) skipping = trimmed[1..^1].Equals(Section, StringComparison.OrdinalIgnoreCase);
            if (!skipping) kept.Append(line).Append("\r\n");
        }
        var text = kept.ToString().TrimEnd('\r', '\n');
        // The updater profile (NcUpdater, normally NCSoft's update service) points at loopback, where
        // nothing answers; the firewall rules block the rest.
        return (text.Length > 0 ? text + "\r\n\r\n" : "") + $"[{Section}]\r\n"
            + $"NcPlatformNetworks=((\"LIVE\",(ConfigurationServerUrl=\"{bootstrapUrl}\")))\r\n"
            + "NcUpdaterConfigType=LOCAL\r\n"
            + "NcUpdaterConfigs=((\"LOCAL\",(ServerUrl=\"127.0.0.1\",GameId_Windows=\"A2_WW_L_GA_PURPLE\")))\r\n";
    }

    public static void Write(string path, string bootstrapUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        File.WriteAllText(path, WithBootstrap(existing, bootstrapUrl), new UTF8Encoding(false));
    }
}

/// <summary>
/// Hosts-file lines that send the client's built-in NCSoft hostnames (NCGuard, telemetry) to our
/// server, between marker comments so they can be removed exactly. Present only while the private
/// client runs, so the official game is not redirected otherwise.
/// </summary>
public static class HostsFile
{
    public static readonly string[] Hostnames =
    [
        "bladnoch.ncsoft.com", "lagavulin.ncsoft.com", "atp-json.ncsoft.com",
        "sdk.gpa.ncsoft.com", "stun.gpa.ncsoft.com", "nccr-api.global.nc.com",
    ];
    const string Begin = "# >>> private server (added by the launcher; removed when the game closes)";
    const string End = "# <<< private server";
    // Markers written by launchers before 0.2; still recognised so their leftovers are removed.
    const string LegacyBegin = "# >>> AION2 private server (added by AionLauncher; removed when the game closes)";
    const string LegacyEnd = "# <<< AION2 private server";

    public static string SystemPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool HasBlock(string text) =>
        text.Contains(Begin, StringComparison.Ordinal) || text.Contains(LegacyBegin, StringComparison.Ordinal);

    public static string Without(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var output = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.Trim() is Begin or LegacyBegin) { inside = true; continue; }
            if (inside && line.Trim() is End or LegacyEnd) { inside = false; continue; }
            if (!inside) output.Add(line);
        }
        return string.Join("\r\n", output).TrimEnd('\r', '\n') + "\r\n";
    }

    public static string With(string text, IPAddress server)
    {
        var block = new StringBuilder().Append(Begin).Append("\r\n");
        foreach (var host in Hostnames) block.Append(server).Append(' ').Append(host).Append("\r\n");
        block.Append(End).Append("\r\n");
        return Without(text) + block;
    }
}

/// <summary>
/// Windows Firewall rules for the private client's programs: block every remote address except
/// loopback and our server, both directions, so the copy never talks to NCSoft. Rules carry one
/// name, so they are removed together.
/// </summary>
public static class FirewallRules
{
    public const string RuleName = "Private game server";
    /// <summary>The rule name launchers before 0.2 used; deleted alongside the current one.</summary>
    public const string LegacyRuleName = "AION2 private server";

    /// <summary>IPv4 ranges outside loopback (127.0.0.0/8) and the server, plus all IPv6 except ::1.</summary>
    public static IReadOnlyList<string> BlockedRanges(IPAddress server)
    {
        uint ip = ToUInt(server);
        var allowed = new List<(uint From, uint To)> { (0x7F000000, 0x7FFFFFFF), (ip, ip) };
        allowed.Sort((a, b) => a.From.CompareTo(b.From));
        var ranges = new List<string>();
        ulong next = 0;
        foreach (var (from, to) in allowed)
        {
            if (from > next) ranges.Add($"{FromUInt((uint)next)}-{FromUInt(from - 1)}");
            next = Math.Max(next, (ulong)to + 1);
        }
        if (next <= uint.MaxValue) ranges.Add($"{FromUInt((uint)next)}-255.255.255.255");
        ranges.Add("::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff");
        return ranges;
    }

    /// <summary>netsh commands (arguments) that replace the rules for these programs.</summary>
    public static IEnumerable<string> Commands(IEnumerable<string> programs, IPAddress server)
    {
        yield return $"advfirewall firewall delete rule name=\"{RuleName}\"";
        yield return $"advfirewall firewall delete rule name=\"{LegacyRuleName}\"";
        var remote = string.Join(",", BlockedRanges(server));
        foreach (var program in programs)
            foreach (var direction in new[] { "out", "in" })
                yield return $"advfirewall firewall add rule name=\"{RuleName}\" dir={direction} action=block program=\"{program}\" remoteip={remote} profile=any enable=yes";
    }

    static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }
    static string FromUInt(uint value) => $"{value >> 24}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
}
