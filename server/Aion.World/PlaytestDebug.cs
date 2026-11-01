namespace Aion.World;

/// <summary>Optional playtest debug sink (Aion:Debug). Off unless the host registers an implementation.</summary>
public interface IPlaytestDebugSink
{
    bool Packets { get; }
    bool Actions { get; }
    bool Entities { get; }

    void Packet(in PlaytestPacketRecord record);
    void Action(string kind, IReadOnlyDictionary<string, object?> fields);
    void EntitySpawn(int entityKey, int datId, string? name);
}

/// <summary>One native login or game message (body may be truncated by the sink).</summary>
public readonly record struct PlaytestPacketRecord(
    string Channel,
    bool Outbound,
    ushort Opcode,
    ReadOnlyMemory<byte> Body,
    int Length,
    int? CharacterKey,
    long NowMs);

public static class PlaytestDebug
{
    public static IPlaytestDebugSink? Sink { get; set; }

    public static void Action(string kind, params (string Key, object? Value)[] fields)
    {
        if (Sink is not { Actions: true }) return;
        var map = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (key, value) in fields) map[key] = value;
        Sink.Action(kind, map);
    }

    public static void EntityOnce(HashSet<int> seen, int entityKey, int datId, string? name)
    {
        if (Sink is not { Entities: true } || !seen.Add(entityKey)) return;
        Sink.EntitySpawn(entityKey, datId, name);
    }
}
