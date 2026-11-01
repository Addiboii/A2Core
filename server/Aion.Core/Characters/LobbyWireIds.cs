namespace Aion.Core.Characters;

/// <summary>Lobby character keys on the wire (SelectCharacter varint, CharSlotSwap i64 dbid).</summary>
public static class LobbyWireIds
{
    /// <summary>Maps LobbyCharDesc._dbid / Common_CharacterSlot._char_dbid to the stored wire_id.</summary>
    public static long ResolveDbId(long wire)
    {
        if (wire > 0 && (wire & 0xFFFF) == 0 && wire >> 16 is > 0 and <= int.MaxValue)
            return wire >> 16;
        if (wire is > 0 and <= int.MaxValue) return wire;
        var shifted = wire >> 16;
        if (shifted is > 0 and <= int.MaxValue) return shifted;
        return wire;
    }

    /// <summary>Resolves a swap/list dbid against known wire ids (shifted key or plain wire).</summary>
    public static bool TryResolveWire(long rawDbId, IEnumerable<long> knownWires, out long wireId)
    {
        wireId = 0;
        var known = knownWires as HashSet<long> ?? knownWires.ToHashSet();
        foreach (var candidate in new[] { ResolveDbId(rawDbId), rawDbId }.Distinct())
        {
            if (candidate is <= 0 or > int.MaxValue || !known.Contains(candidate)) continue;
            wireId = candidate;
            return true;
        }
        return false;
    }
}
