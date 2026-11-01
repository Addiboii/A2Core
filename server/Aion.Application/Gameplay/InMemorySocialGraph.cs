using Aion.Core.Characters;

namespace Aion.Application.Gameplay;

/// <summary>In-memory friends, requests and blocks (same rules as PostgresGameplayStore social tables).</summary>
public sealed class InMemorySocialGraph
{
    public const int FriendLimit = 100;

    readonly HashSet<(Guid Owner, Guid Friend)> friends = [];
    readonly HashSet<(Guid From, Guid To)> requests = [];
    readonly HashSet<(Guid Owner, Guid Blocked)> blocks = [];
    readonly Dictionary<(Guid Owner, Guid Friend), string> memos = [];

    static SocialRelation Row(CharacterState state, string kind, string memo = "") =>
        new(state.Summary.CharacterId, state.Summary.Name, state.Progress.Level, state.CreationId, state.Race, state.WireId, kind, memo);

    public IReadOnlyList<SocialRelation> List(Guid owner, Func<Guid, CharacterState?> resolve)
    {
        var list = new List<SocialRelation>();
        foreach (var (_, friend) in friends.Where(p => p.Owner == owner))
            if (resolve(friend) is { } s)
                list.Add(Row(s, "friend", memos.GetValueOrDefault((owner, friend), "")));
        foreach (var (from, to) in requests.Where(p => p.From == owner))
            if (resolve(to) is { } s) list.Add(Row(s, "sent"));
        foreach (var (from, to) in requests.Where(p => p.To == owner))
            if (resolve(from) is { } s) list.Add(Row(s, "received"));
        foreach (var (_, blocked) in blocks.Where(p => p.Owner == owner))
            if (resolve(blocked) is { } s) list.Add(Row(s, "blocked"));
        return list;
    }

    public bool BlockedBetween(Guid a, Guid b) => blocks.Contains((a, b)) || blocks.Contains((b, a));

    public (string Result, SocialRelation? Target) FriendRequest(Guid character, string targetName, Func<string, CharacterState?> byName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Length > 64) return ("not_found", null);
        var name = targetName.Trim();
        if (byName(name) is not { } target) return ("not_found", null);
        if (target.Summary.CharacterId == character) return ("self", Row(target, ""));
        if (BlockedBetween(character, target.Summary.CharacterId)) return ("blocked", Row(target, ""));
        if (friends.Contains((character, target.Summary.CharacterId))) return ("already_friends", Row(target, "friend"));
        if (requests.Contains((character, target.Summary.CharacterId))) return ("already_requested", Row(target, "sent"));
        if (friends.Count(p => p.Owner == character) >= FriendLimit) return ("full", Row(target, ""));
        if (requests.Remove((target.Summary.CharacterId, character)))
        {
            friends.Add((character, target.Summary.CharacterId));
            friends.Add((target.Summary.CharacterId, character));
            return ("accepted", Row(target, "friend"));
        }
        requests.Add((character, target.Summary.CharacterId));
        return ("requested", Row(target, "sent"));
    }

    public bool FriendRespond(Guid character, Guid from, bool accept)
    {
        if (!requests.Remove((from, character))) return false;
        if (!accept) return true;
        if (friends.Count(p => p.Owner == character) >= FriendLimit) return false;
        friends.Add((character, from));
        friends.Add((from, character));
        return true;
    }

    public bool FriendCancel(Guid character, Guid to) => requests.Remove((character, to));

    public bool FriendDelete(Guid character, Guid friend)
    {
        memos.Remove((character, friend));
        return friends.Remove((character, friend)) | friends.Remove((friend, character));
    }

    public bool FriendMemo(Guid character, Guid friend, string memo)
    {
        if (!friends.Contains((character, friend))) return false;
        memos[(character, friend)] = memo;
        return true;
    }

    public SocialRelation? Block(Guid character, string targetName, Func<string, CharacterState?> byName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Length > 64) return null;
        if (byName(targetName.Trim()) is not { } target || target.Summary.CharacterId == character) return null;
        var id = target.Summary.CharacterId;
        friends.Remove((character, id));
        friends.Remove((id, character));
        requests.Remove((character, id));
        requests.Remove((id, character));
        memos.Remove((character, id));
        memos.Remove((id, character));
        blocks.Add((character, id));
        return Row(target, "blocked");
    }

    public bool Unblock(Guid character, Guid blocked) => blocks.Remove((character, blocked));
}
