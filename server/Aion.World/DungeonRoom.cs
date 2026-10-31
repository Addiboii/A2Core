using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Waiting expedition room (in memory; lost on restart). Members share one dungeon copy when they enter.</summary>
public sealed class DungeonRoom(int roomKey, WorldPlayer leader, int contentDungeonId, byte dungeonLevel, int resolvedId)
{
    public int RoomKey { get; } = roomKey;
    public WorldPlayer Leader { get; set; } = leader;
    public int ContentDungeonId { get; } = contentDungeonId;
    public byte DungeonLevel { get; } = dungeonLevel;
    public int ResolvedDungeonId { get; } = resolvedId;
    public string Desc { get; set; } = "";
    public bool Public { get; set; } = true;
    public byte JoinType { get; set; } = PartyOptions.JoinFree;
    public byte Race { get; set; }
    public List<int> AffixList { get; } = [];
    public List<WorldPlayer> Members { get; } = [leader];
    public HashSet<int> Ready { get; } = [];
    public Dictionary<int, long> PendingApply { get; } = [];
    public Dictionary<int, long> PendingInvite { get; } = [];
    public int Limit => MapEventTables.Embedded.Dungeon(ResolvedDungeonId)?.MaxMember is { } cap and > 0 ? cap : Party.Limit;

    public IEnumerable<WorldPlayer> Others(WorldPlayer of) => Members.Where(m => m != of);
    public bool Has(WorldPlayer p) => Members.Contains(p);
}

public sealed record DungeonRoomInvitePending(int RoomKey, long ExpiresMs);

/// <summary>Party vote to re-enter the same expedition after a run (in-instance retry).</summary>
public sealed class DungeonRetryPending(WorldPlayer leader, int dungeonId, int copyKey, long expiresMs)
{
    public WorldPlayer Leader { get; } = leader;
    public int DungeonId { get; } = dungeonId;
    public int CopyKey { get; } = copyKey;
    public long ExpiresMs { get; } = expiresMs;
    public Dictionary<int, byte> Responses { get; } = [];
}
