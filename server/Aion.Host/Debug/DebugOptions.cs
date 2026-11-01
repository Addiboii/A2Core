namespace Aion.Host.Debug;

/// <summary>Playtest debug logging (Aion:Debug). Disabled unless <see cref="Enabled"/> is true.</summary>
public sealed record DebugOptions
{
    public bool Enabled { get; init; }
    /// <summary>Directory for debug-YYYYMMDD.log (default: research/local/logs above the host).</summary>
    public string? LogDirectory { get; init; }
    public bool JsonLines { get; init; } = true;
    public bool Packets { get; init; } = true;
    public bool Actions { get; init; } = true;
    public bool Entities { get; init; } = true;
    /// <summary>Max hex bytes per packet body (0 = metadata only).</summary>
    public int MaxPacketHexBytes { get; init; } = 256;
    /// <summary>When set, only these opcodes are logged (hex, no 0x prefix).</summary>
    public ushort[]? IncludeOpcodes { get; init; }
    /// <summary>Extra opcodes to skip (indexed env: ExcludeOpcodes__0, …).</summary>
    public ushort[] ExcludeOpcodes { get; init; } = [];
    /// <summary>Skip movement position reports and the 0x37xx band (default true).</summary>
    public bool ExcludeMovement { get; init; } = true;
    /// <summary>Skip heartbeat / keepalive opcodes (default true).</summary>
    public bool ExcludeHeartbeat { get; init; } = true;
    public bool HttpRequests { get; init; } = true;

    public static ushort[] DefaultExcludedHeartbeats => [0x3600, 0x3601, 0x3602, 0x3651, 0xffa0, 0xffa1, 0xe259];

    public static string ResolveLogDirectory(string? configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var dir = baseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "research", "local", "logs");
            if (Directory.Exists(Path.GetDirectoryName(candidate)!)) return Path.GetFullPath(candidate);
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(baseDirectory, "research", "local", "logs"));
    }
}
