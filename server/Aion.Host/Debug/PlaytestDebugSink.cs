using System.Text;
using System.Text.Json;
using Aion.Host.Logging;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Debug;

/// <summary>Buffered playtest log: JSON lines to debug-YYYYMMDD.log (local date), important action lines at Information on the console.</summary>
public sealed class PlaytestDebugSink : IPlaytestDebugSink, IAsyncDisposable
{
    static readonly HashSet<ushort> SecretOpcodes = [0x3613, 0x3905];
    /// <summary>Logged whole whatever MaxPacketHexBytes says: the skill list, its additions, the hotbar, and ride mount replies.</summary>
    static readonly HashSet<ushort> FullHexOpcodes =
    [
        0x3615, 0x3626, GameSession.CharSlotSwapRq, GameSession.CharSlotSwapRs,
        GameSession.CharCustomizingListRq, GameSession.CharCustomizingListRs, GameSession.BroadcastCharCustomizingUpdateNt,
        0x5100, 0x5101, 0x5600,
        RidePackets.RideInit, RidePackets.GetOnRq, RidePackets.GetOnRs, RidePackets.GetOnNt,
        RidePackets.GetOffRq, RidePackets.GetOffRs, RidePackets.GetOffNt,
    ];
    static readonly HashSet<string> ConsoleActionKinds = new(StringComparer.Ordinal)
    {
        "skill_cast", "damage", "kill", "death", "resurrect", "teleport", "quest_accept", "quest_progress",
        "quest_complete", "level_up", "skill_point", "npc_talk", "npc_talk_refused", "map_event", "item_use", "item_use_refused", "item_gain",
    };

    readonly DebugOptions options;
    readonly OpcodeCatalog opcodes;
    readonly ILogger<PlaytestDebugSink> logger;
    readonly ChannelFileLogWriter file;
    readonly HashSet<ushort> exclude;
    readonly HashSet<ushort>? includeOnly;

    public bool Packets => options.Packets;
    public bool Actions => options.Actions;
    public bool Entities => options.Entities;
    public string LogPath => file.Path;

    public PlaytestDebugSink(DebugOptions options, OpcodeCatalog opcodes, ILogger<PlaytestDebugSink> logger)
    {
        this.options = options;
        this.opcodes = opcodes;
        this.logger = logger;
        exclude = new HashSet<ushort>(options.ExcludeOpcodes);
        if (options.ExcludeHeartbeat)
            foreach (var opcode in DebugOptions.DefaultExcludedHeartbeats) exclude.Add(opcode);
        includeOnly = options.IncludeOpcodes is { Length: > 0 } list ? list.ToHashSet() : null;
        var directory = DebugOptions.ResolveLogDirectory(options.LogDirectory, AppContext.BaseDirectory);
        var logPath = Path.Combine(directory, PlaytestLogPaths.DebugFileName(DateTime.Now));
        file = new ChannelFileLogWriter(logPath);
    }

    public void Packet(in PlaytestPacketRecord record)
    {
        if (!options.Packets || !ShouldLogPacket(record.Opcode)) return;
        file.Enqueue(FormatPacket(record));
    }

    public void Action(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        if (!options.Actions) return;
        var line = FormatAction(kind, fields);
        file.Enqueue(line);
        if (ConsoleActionKinds.Contains(kind))
            logger.LogInformation("Playtest {Kind} {Detail}", kind, Compact(fields));
    }

    public void EntitySpawn(int entityKey, int datId, string? name)
    {
        if (!options.Entities) return;
        file.Enqueue(FormatAction("entity_spawn", new Dictionary<string, object?>
        {
            ["entityKey"] = entityKey,
            ["datId"] = datId,
            ["name"] = name ?? "",
        }));
    }

    bool ShouldLogPacket(ushort opcode)
    {
        if (includeOnly is not null && !includeOnly.Contains(opcode)) return false;
        if (exclude.Contains(opcode)) return false;
        if (options.ExcludeMovement && opcode is >= 0x3700 and <= 0x3718) return false;
        return true;
    }

    string FormatPacket(in PlaytestPacketRecord record)
    {
        if (options.JsonLines)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("t", "packet");
                json.WriteString("utc", DateTimeOffset.UtcNow.ToString("O"));
                json.WriteString("channel", record.Channel);
                json.WriteString("dir", record.Outbound ? "out" : "in");
                json.WriteString("opcode", opcodes.Label(record.Opcode));
                json.WriteNumber("length", record.Length);
                if (record.CharacterKey is { } key) json.WriteNumber("characterKey", key);
                json.WriteNumber("ms", record.NowMs);
                if (!SecretOpcodes.Contains(record.Opcode) && options.MaxPacketHexBytes != 0)
                {
                    var take = FullHexOpcodes.Contains(record.Opcode) ? record.Body.Length : Math.Min(record.Body.Length, options.MaxPacketHexBytes);
                    json.WriteString("hex", Convert.ToHexString(record.Body.Span[..take]));
                    if (record.Body.Length > take) json.WriteBoolean("hexTruncated", true);
                }
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        var sb = new StringBuilder();
        sb.Append(record.NowMs).Append(' ').Append(record.Channel).Append(' ')
            .Append(record.Outbound ? "OUT" : "IN ").Append(opcodes.Label(record.Opcode))
            .Append(' ').Append(record.Length).Append('B');
        if (record.CharacterKey is { } ck) sb.Append(" char=").Append(ck);
        return sb.ToString();
    }

    static string FormatAction(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("t", "action");
            json.WriteString("utc", DateTimeOffset.UtcNow.ToString("O"));
            json.WriteString("kind", kind);
            json.WriteStartObject("fields");
            foreach (var (key, value) in fields) WriteField(json, key, value);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteField(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(key); break;
            case string s: json.WriteString(key, s); break;
            case bool b: json.WriteBoolean(key, b); break;
            case int i: json.WriteNumber(key, i); break;
            case long l: json.WriteNumber(key, l); break;
            case uint u: json.WriteNumber(key, u); break;
            case double d: json.WriteNumber(key, d); break;
            case int[] ints: json.WriteString(key, string.Join(',', ints)); break;
            case IEnumerable<int> seq: json.WriteString(key, string.Join(',', seq)); break;
            default: json.WriteString(key, value.ToString()); break;
        }
    }

    static string Compact(IReadOnlyDictionary<string, object?> fields)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in fields)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(key).Append('=').Append(value);
        }
        return sb.ToString();
    }

    public ValueTask DisposeAsync() => file.DisposeAsync();
}
