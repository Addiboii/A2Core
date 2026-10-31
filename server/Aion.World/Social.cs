using Aion.Protocol.Native;

namespace Aion.World;

// PartyOptions lives in SocialPackets.cs

/// <summary>A party on this host (in memory; lost on restart). Members in join order; the leader leads.</summary>
public sealed class Party(long key, WorldPlayer leader)
{
    public const int Limit = 4;   // _limit_member: our CANDIDATE AION2 party size
    public long Key { get; } = key;
    public WorldPlayer Leader { get; set; } = leader;
    public List<WorldPlayer> Members { get; } = [leader];
    public byte Distribution { get; set; } = PartyOptions.DistributionSequence;
    public byte SpecialDistribution { get; set; } = PartyOptions.SpecialNormal;
    public byte SpecialDistributionGrade { get; set; } = 2;
    public byte JoinType { get; set; } = PartyOptions.JoinFree;
    /// <summary>Round-robin index for <see cref="PartyOptions.DistributionSequence"/>.</summary>
    public int SequenceIndex { get; set; }
    /// <summary>When set, this party belongs to a raid/alliance force.</summary>
    public long? ForceKey { get; set; }
    public IEnumerable<WorldPlayer> Others(WorldPlayer of) => Members.Where(m => m != of);
}

/// <summary>Raid/alliance grouping several parties (in memory).</summary>
public sealed class Force(long key, WorldPlayer commander, Party leadParty)
{
    public long Key { get; } = key;
    public WorldPlayer Commander { get; set; } = commander;
    public List<Party> Parties { get; } = [leadParty];
    public IEnumerable<WorldPlayer> AllMembers => Parties.SelectMany(p => p.Members);
}

/// <summary>An in-progress party dice roll for one dropped item (social state, under the world gate).</summary>
public sealed class PartyDiceRoll
{
    public long DiceUid { get; init; }
    public long PartyKey { get; init; }
    public int ItemId { get; init; }
    public int Count { get; init; }
    public long ExpiresAtMs { get; init; }
    public int KillerKey { get; init; }
    public Vector3d Center { get; init; }
    public Vector3d At { get; init; }
    /// <summary>Member entity keys still able to respond (online, in range at roll start).</summary>
    public HashSet<int> Eligible { get; } = [];
    /// <summary>Roll value per member key; absent = not yet answered.</summary>
    public Dictionary<int, int?> Responses { get; } = [];
    public bool Resolved { get; set; }
}

/// <summary>Kinah auction for one special item (shared social state).</summary>
public sealed class PartyBidSession
{
    public long BidUid { get; init; }
    public long PartyKey { get; init; }
    public int ItemId { get; init; }
    public int Count { get; init; }
    public long ExpiresAtMs { get; init; }
    public HashSet<int> Eligible { get; } = [];
    public int? HighBidderKey { get; set; }
    public long HighPrice { get; set; }
    /// <summary>Kinah escrow per member key (deducted from bag on bid).</summary>
    public Dictionary<int, long> Escrow { get; } = [];
    public bool Resolved { get; set; }
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
    /// <summary>Kina each side offered (GameCash on ExchangeItemListModify_RQ).</summary>
    public Dictionary<int, long> CashOffers { get; } = new() { [requester.Key] = 0, [target.Key] = 0 };
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

/// <summary>Social state of the shared world (use under the gate). Legion hall/decoration state lives in Postgres + <see cref="GameSession"/> guild packets, not here.</summary>
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
    public Dictionary<long, PartyDiceRoll> DiceRolls { get; } = [];
    public Dictionary<long, PartyBidSession> BidSessions { get; } = [];
    public Dictionary<long, Force> Forces { get; } = [];
    public Dictionary<int, DungeonRoom> DungeonRooms { get; } = [];
    /// <summary>Pending dungeon-room invites by invited player key.</summary>
    public Dictionary<int, DungeonRoomInvitePending> DungeonRoomInvites { get; } = [];
    /// <summary>Pending in-dungeon retry votes by party key.</summary>
    public Dictionary<long, DungeonRetryPending> DungeonRetries { get; } = [];
    long nextDiceUid;
    public long NextDiceUid() => ++nextDiceUid;
    public long NextBidUid() => ++nextDiceUid;

    public Party? PartyOf(WorldPlayer p) => Parties.Values.FirstOrDefault(x => x.Members.Contains(p));
    public Force? ForceOf(WorldPlayer p) =>
        Parties.Values.FirstOrDefault(x => x.Members.Contains(p) && x.ForceKey is { } fk) is { ForceKey: { } key }
            && Forces.TryGetValue(key, out var force) ? force : null;
    public Exchange? ExchangeOf(WorldPlayer p) => Exchanges.Values.FirstOrDefault(x => x.Has(p));
    public Duel? DuelOf(WorldPlayer p) => Duels.Values.FirstOrDefault(x => x.Has(p));
    public DungeonRoom? DungeonRoomOf(WorldPlayer p) => DungeonRooms.Values.FirstOrDefault(r => r.Has(p));
}
