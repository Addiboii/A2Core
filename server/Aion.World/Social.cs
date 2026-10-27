using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A party on this host (in memory; lost on restart). Members in join order; the leader leads.</summary>
public sealed class Party(long key, WorldPlayer leader)
{
    public const int Limit = 4;   // _limit_member: our CANDIDATE AION2 party size
    public long Key { get; } = key;
    public WorldPlayer Leader { get; set; } = leader;
    public List<WorldPlayer> Members { get; } = [leader];
    public IEnumerable<WorldPlayer> Others(WorldPlayer of) => Members.Where(m => m != of);
}

/// <summary>A pending party invitation: who invited whom into which party, until when.</summary>
public sealed record PartyInvite(long PartyKey, int InviterKey, long ExpiresMs);

/// <summary>A player trade between two players on this host (in memory until it completes in the store).</summary>
public sealed class Exchange(long id, WorldPlayer requester, WorldPlayer target)
{
    public long Id { get; } = id;
    public WorldPlayer Requester { get; } = requester;
    public WorldPlayer Target { get; } = target;
    public bool Accepted { get; set; }
    /// <summary>Each side's offer: item wire key -> count.</summary>
    public Dictionary<int, Dictionary<long, long>> Offers { get; } = new() { [requester.Key] = [], [target.Key] = [] };
    public HashSet<int> Locked { get; } = [];
    public HashSet<int> Confirmed { get; } = [];
    public WorldPlayer Other(WorldPlayer of) => of == Requester ? Target : Requester;
    public bool Has(WorldPlayer p) => p == Requester || p == Target;
}

/// <summary>A duel between two players: challenged, counting down, fighting, over.</summary>
public sealed class Duel(int roomKey, WorldPlayer challenger, WorldPlayer accepter)
{
    public const long CountdownMs = 3000, LimitMs = 120_000;
    public int RoomKey { get; } = roomKey;
    public WorldPlayer Challenger { get; } = challenger;
    public WorldPlayer Accepter { get; } = accepter;
    public DuelState State { get; set; } = DuelState.InWaiting;
    public long NextMs { get; set; }
    public WorldPlayer Other(WorldPlayer of) => of == Challenger ? Accepter : Challenger;
    public bool Has(WorldPlayer p) => p == Challenger || p == Accepter;
}

/// <summary>Social state of the shared world (use under the gate).</summary>
public sealed class SocialState
{
    long lastId;
    public long NextId() => ++lastId;
    public Dictionary<long, Party> Parties { get; } = [];
    /// <summary>Pending invitations by invited player key.</summary>
    public Dictionary<int, PartyInvite> Invites { get; } = [];
    public Dictionary<long, Exchange> Exchanges { get; } = [];
    public Dictionary<int, Duel> Duels { get; } = [];
    /// <summary>Arena matches by room key and the players waiting for one.</summary>
    public Dictionary<int, ArenaMatch> Arenas { get; } = [];
    public List<ArenaQueueEntry> ArenaQueue { get; } = [];

    public Party? PartyOf(WorldPlayer p) => Parties.Values.FirstOrDefault(x => x.Members.Contains(p));
    public Exchange? ExchangeOf(WorldPlayer p) => Exchanges.Values.FirstOrDefault(x => x.Has(p));
    public Duel? DuelOf(WorldPlayer p) => Duels.Values.FirstOrDefault(x => x.Has(p));
}
