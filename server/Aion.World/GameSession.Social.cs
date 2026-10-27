using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Friends (stored), parties, player trades and duels (in the shared world). Layouts and opcodes:
/// SocialPackets / research/social-protocol.md, all CANDIDATE (no official samples beyond the empty
/// world-entry lists). Each first request of a kind is logged by the listener with its raw body.
/// </summary>
public sealed partial class GameSession
{
    const int ServerId = 1204;
    const int Failed = 1;   // OURS: the client's failure codes are unknown; 0 is success
    /// <summary>Trade partners must stand within this distance (cm). OURS.</summary>
    public const double ExchangeRange = 1500;

    /// <summary>Friends, requests and blocks as last loaded from the store.</summary>
    List<SocialRelation> social = [];
    SocialState? Social => World?.Social;
    SharedWorld? World => options.World;
    long DbId => selected?.WireId ?? playerKey;
    string StoredName => selected?.Summary.Name ?? character?.Name ?? CharacterName ?? "";
    long partyStatusAt;
    (long Hp, int Mp) partyPointsSent;

    /// <summary>Whether a request opcode is social (including the alternative duel opcodes).</summary>
    public static bool IsSocialRequest(int header) =>
        SocialOpcodes.ClientRequests.Contains((ushort)header)
        || header is >= 0 and <= 0xffff && SocialOpcodes.ClientRequests.Contains((ushort)(header + (SocialOpcodes.LateBase - SocialOpcodes.LateAlternativeBase)));

    async Task<List<byte[]>?> SocialRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var header = Opcode(plain);
        if (!IsSocialRequest(header) || resources is null) return null;
        // A duel request under the alternative base is handled as the primary one.
        if (!SocialOpcodes.ClientRequests.Contains(header)) header = (ushort)(header + (SocialOpcodes.LateBase - SocialOpcodes.LateAlternativeBase));
        return header switch
        {
            SocialOpcodes.FriendRequestRq => await FriendRequestAsync(plain, ct),
            SocialOpcodes.FriendRequestAcceptRq or SocialOpcodes.FriendRequestRejectRq => await FriendAnswerAsync(plain, header == SocialOpcodes.FriendRequestAcceptRq, ct),
            SocialOpcodes.FriendRequestCancelRq => await FriendRemoveAsync(plain, cancel: true, ct),
            SocialOpcodes.FriendDeleteRq => await FriendRemoveAsync(plain, cancel: false, ct),
            SocialOpcodes.FriendBlockRq => await FriendBlockAsync(plain, ct),
            SocialOpcodes.FriendBlockCancelRq => await FriendUnblockAsync(plain, ct),
            SocialOpcodes.FriendMemoUpdateRq => await FriendMemoAsync(plain, ct),
            SocialOpcodes.PartyCreateRq => PartyCreate(),
            SocialOpcodes.PartyInviteRq => PartyInvite(plain, now),
            SocialOpcodes.PartyInviteResponseRq => PartyInviteResponse(plain, now),
            SocialOpcodes.PartyLeaveRq => PartyLeave(PartyUpdateReason.LeaveMember, [SocialPackets.PartyLeaveReply(0)]),
            SocialOpcodes.PartyKickoutRq => PartyKick(plain),
            SocialOpcodes.PartySetLeaderRq => PartySetLeader(plain),
            SocialOpcodes.PartyDisbandRq => PartyDisband(),
            SocialOpcodes.PartyRecentMemberRq => [SocialPackets.PartyRecentMembers(0, [])],
            SocialOpcodes.ExchangeRequestRq => ExchangeRequest(plain, now),
            SocialOpcodes.ExchangeAcceptRq => ExchangeAccept(plain),
            SocialOpcodes.ExchangeRefuseRq or SocialOpcodes.ExchangeCancelRq => ExchangeEnd(plain, header == SocialOpcodes.ExchangeRefuseRq),
            SocialOpcodes.ExchangeItemListModifyRq => ExchangeModify(plain),
            SocialOpcodes.ExchangeItemListLockRq => ExchangeLock(plain),
            SocialOpcodes.ExchangeConfirmRq => await ExchangeConfirmAsync(plain, ct),
            SocialOpcodes.DuelCallOutRq => DuelCallOut(plain, now),
            SocialOpcodes.DuelCallOutAcceptRq or SocialOpcodes.DuelCallOutRejectRq => DuelAnswer(plain, header == SocialOpcodes.DuelCallOutAcceptRq, now),
            _ => [],
        };
    }

    // ---------- Friends ----------

    async Task LoadSocialAsync(CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character) social = [.. await store.SocialAsync(character, ct)];
    }

    FriendInfo FriendEntry(SocialRelation r, FriendRelation relation) =>
        new(ServerId, r.WireId, r.Name, (byte)r.Race, (byte)r.CreationId, r.Level, 0, relation, World?.FindOnline(r.CharacterId) is not null, 0, r.Memo);

    FriendInfo MyEntry(FriendRelation relation) =>
        new(ServerId, DbId, StoredName, (byte)(Profile()?.Race ?? 1), (byte)(character?.CreationId ?? 1), level, 0, relation, true);

    FriendUpdateInfo MyUpdate(bool online) => new(ServerId, DbId, StoredName, (byte)(character?.CreationId ?? 1), level, 0, online);

    static FriendRelation Relation(string kind) => kind switch { "sent" => FriendRelation.Send, "received" => FriendRelation.Recv, _ => FriendRelation.Friend };

    /// <summary>World entry: the friend list (0xe274) and the trade counter (0xe33d), as the official world entry.</summary>
    List<byte[]> SocialWorldEntry() =>
    [
        SocialPackets.PersonalExchangeInfo(0),
        SocialPackets.FriendList(social.Where(r => r.Kind != "blocked").Select(r => FriendEntry(r, Relation(r.Kind))).ToList(),
            social.Where(r => r.Kind == "blocked").Select(r => new FriendBlockInfo(ServerId, r.WireId, r.Name)).ToList()),
    ];

    /// <summary>Online friends hear this character come online or go offline (FriendInfoUpdate_NT).</summary>
    void TellFriends(bool online)
    {
        if (World is not { } world) return;
        foreach (var friend in social.Where(r => r.Kind == "friend"))
            world.FindOnline(friend.CharacterId)?.Send(SocialPackets.FriendInfoUpdate(MyUpdate(online)));
    }

    WorldPlayer? OnlineBy(long wireId) => social.FirstOrDefault(r => r.WireId == wireId) is { } r ? World?.FindOnline(r.CharacterId) : null;

    async Task<List<byte[]>> FriendRequestAsync(byte[] plain, CancellationToken ct)
    {
        if (SocialRequests.ParseServerAndName(plain) is not { } target) return [];
        var name = CharacterNames.Stored(target.Name);
        if (Store is not { } store || CharacterId is not { } me) return [SocialPackets.FriendRequestReply(Failed, ServerId, target.Name)];
        var (result, other) = await StoredValue(() => store.FriendRequestAsync(me, name, ct));
        if (other is null || result is not ("requested" or "accepted")) return [SocialPackets.FriendRequestReply(Failed, ServerId, target.Name)];
        await LoadSocialAsync(ct);
        var online = World?.FindOnline(other.CharacterId);
        if (result == "requested")
        {
            online?.Send(SocialPackets.FriendRequestNotice(MyEntry(FriendRelation.Recv)));
            return [SocialPackets.FriendRequestReply(0, ServerId, other.Name), SocialPackets.FriendRequestNotice(FriendEntry(other, FriendRelation.Send))];
        }
        online?.Send([SocialPackets.FriendRequestAcceptNotice(DbId), SocialPackets.FriendInfoUpdate(MyUpdate(true))]);
        return [SocialPackets.FriendRequestReply(0, ServerId, other.Name), SocialPackets.FriendRequestNotice(FriendEntry(other, FriendRelation.Friend))];
    }

    async Task<List<byte[]>> FriendAnswerAsync(byte[] plain, bool accept, CancellationToken ct)
    {
        if (SocialRequests.ParseDbId(plain) is not { } dbId) return [];
        await LoadSocialAsync(ct);
        byte[] Reply(int result) => accept ? SocialPackets.FriendRequestAcceptReply(result, dbId) : SocialPackets.FriendRequestRejectReply(result, dbId);
        if (Store is not { } store || CharacterId is not { } me || social.FirstOrDefault(r => r.WireId == dbId && r.Kind == "received") is not { } from)
            return [Reply(Failed)];
        if (!await StoredValue(() => store.FriendRespondAsync(me, from.CharacterId, accept, ct))) return [Reply(Failed)];
        var online = World?.FindOnline(from.CharacterId);
        await LoadSocialAsync(ct);
        if (!accept)
        {
            online?.Send(SocialPackets.FriendInfoDelete(DbId));
            return [Reply(0)];
        }
        online?.Send([SocialPackets.FriendRequestAcceptNotice(DbId), SocialPackets.FriendInfoUpdate(MyUpdate(true))]);
        return [Reply(0), SocialPackets.FriendRequestNotice(FriendEntry(from, FriendRelation.Friend)), .. Achieve("AddFriend")];
    }

    async Task<List<byte[]>> FriendRemoveAsync(byte[] plain, bool cancel, CancellationToken ct)
    {
        if (SocialRequests.ParseDbId(plain) is not { } dbId) return [];
        await LoadSocialAsync(ct);
        byte[] Reply(int result) => cancel ? SocialPackets.FriendRequestCancelReply(result, dbId) : SocialPackets.FriendDeleteReply(result, dbId);
        if (Store is not { } store || CharacterId is not { } me || social.FirstOrDefault(r => r.WireId == dbId && r.Kind == (cancel ? "sent" : "friend")) is not { } other)
            return [Reply(Failed)];
        var done = await StoredValue(() => cancel ? store.FriendCancelAsync(me, other.CharacterId, ct) : store.FriendDeleteAsync(me, other.CharacterId, ct));
        if (!done) return [Reply(Failed)];
        World?.FindOnline(other.CharacterId)?.Send(SocialPackets.FriendInfoDelete(DbId));
        await LoadSocialAsync(ct);
        return [Reply(0), SocialPackets.FriendInfoDelete(dbId)];
    }

    async Task<List<byte[]>> FriendBlockAsync(byte[] plain, CancellationToken ct)
    {
        if (SocialRequests.ParseServerAndName(plain) is not { } target) return [];
        if (Store is not { } store || CharacterId is not { } me) return [SocialPackets.FriendBlockReply(Failed, ServerId, 0, target.Name)];
        var wasFriend = social.FirstOrDefault(r => string.Equals(r.Name, CharacterNames.Stored(target.Name), StringComparison.OrdinalIgnoreCase));
        if (await StoredValue(() => store.BlockAsync(me, CharacterNames.Stored(target.Name), ct)) is not { } blocked)
            return [SocialPackets.FriendBlockReply(Failed, ServerId, 0, target.Name)];
        if (wasFriend is not null) World?.FindOnline(blocked.CharacterId)?.Send(SocialPackets.FriendInfoDelete(DbId));
        await LoadSocialAsync(ct);
        return [SocialPackets.FriendBlockReply(0, ServerId, blocked.WireId, blocked.Name)];
    }

    async Task<List<byte[]>> FriendUnblockAsync(byte[] plain, CancellationToken ct)
    {
        if (SocialRequests.ParseFriendBlockCancel(plain) is not { } info) return [];
        await LoadSocialAsync(ct);
        if (Store is not { } store || CharacterId is not { } me || social.FirstOrDefault(r => r.WireId == info.CharDbId && r.Kind == "blocked") is not { } blocked
            || !await StoredValue(() => store.UnblockAsync(me, blocked.CharacterId, ct)))
            return [SocialPackets.FriendBlockCancelReply(Failed, info)];
        await LoadSocialAsync(ct);
        return [SocialPackets.FriendBlockCancelReply(0, new FriendBlockInfo(ServerId, blocked.WireId, blocked.Name))];
    }

    async Task<List<byte[]>> FriendMemoAsync(byte[] plain, CancellationToken ct)
    {
        if (SocialRequests.ParseFriendMemo(plain) is not { } memo) return [];
        await LoadSocialAsync(ct);
        if (Store is not { } store || CharacterId is not { } me || social.FirstOrDefault(r => r.WireId == memo.FriendDbId && r.Kind == "friend") is not { } friend
            || !await StoredValue(() => store.FriendMemoAsync(me, friend.CharacterId, memo.Memo, ct)))
            return [SocialPackets.FriendMemoReply(Failed, memo.FriendDbId, memo.Memo)];
        await LoadSocialAsync(ct);
        return [SocialPackets.FriendMemoReply(0, memo.FriendDbId, memo.Memo)];
    }

    /// <summary>A store call whose failure (stale session, database) is a failed request, recorded like other storage errors.</summary>
    async Task<T?> StoredValue<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is InvalidOperationException or GameplayException or ArgumentException)
        {
            PersistenceErrors.Add(error.GetType().Name);
            return default;
        }
    }

    // ---------- Party ----------

    PartyMember MemberInfo(byte number) => new()
    {
        Number = number, Key = playerKey, DbId = DbId, Nickname = StoredName, PcId = Profile()?.PcId ?? 1,
        Hp = resources?.Hp ?? 1, HpMax = resources?.HpMax ?? 1, Mp = resources?.Mp ?? 0, MpMax = resources?.MpMax ?? 0,
        MapKey = worldMap, Position = position ?? default, Level = level,
    };

    static PartyInfo Info(Party party) => new()
    {
        Key = party.Key, LeaderDbId = party.Leader.Session.DbId, LimitMember = Party.Limit,
        Members = party.Members.Select((m, i) => m.Session.MemberInfo((byte)i)).ToList(),
    };

    InvitationInfo Invitation() => new(Profile()?.PcId ?? 1, level, 0, StoredName, ServerId);

    List<byte[]> PartyCreate()
    {
        if (Social is not { } s || worldPlayer is null) return [SocialPackets.PartyCreateReply(Failed)];
        if (s.PartyOf(worldPlayer) is not null) return [SocialPackets.PartyCreateReply(Failed)];
        var party = new Party(s.NextId(), worldPlayer);
        s.Parties[party.Key] = party;
        return [SocialPackets.PartyCreateReply(0), SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create)];
    }

    List<byte[]> PartyInvite(byte[] plain, long now)
    {
        if (SocialRequests.ParsePartyInvite(plain) is not { } target) return [];
        byte[] Fail() => SocialPackets.PartyInviteReply(Failed, ServerId, target.Name);
        if (Social is not { } s || worldPlayer is null || World?.FindOnline(CharacterNames.Stored(target.Name)) is not { } invitee || invitee == worldPlayer) return [Fail()];
        if (s.PartyOf(invitee) is not null) return [Fail()];
        var replies = new List<byte[]>();
        var party = s.PartyOf(worldPlayer);
        if (party is null)
        {
            party = new Party(s.NextId(), worldPlayer);
            s.Parties[party.Key] = party;
            replies.Add(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create));
        }
        if (party.Leader != worldPlayer || party.Members.Count >= Party.Limit) return [Fail()];
        var expires = now + 30_000;
        s.Invites[invitee.Key] = new PartyInvite(party.Key, playerKey, expires);
        invitee.Send(SocialPackets.PartyInviteNotice(party.Key, DbId, Invitation(), expires));
        replies.Insert(0, SocialPackets.PartyInviteReply(0, ServerId, invitee.Name));
        return replies;
    }

    List<byte[]> PartyInviteResponse(byte[] plain, long now)
    {
        if (SocialRequests.ParsePartyInviteResponse(plain) is not { Count: > 0 } answers || Social is not { } s || worldPlayer is null)
            return [SocialPackets.PartyInviteResponseReply(Failed)];
        var answer = answers[0];
        if (!s.Invites.Remove(playerKey, out var invite) || invite.ExpiresMs < now || !s.Parties.TryGetValue(invite.PartyKey, out var party))
            return [SocialPackets.PartyInviteResponseReply(Failed)];
        var inviter = World!.Online.GetValueOrDefault(invite.InviterKey);
        var accepted = answer.Response == PartyInviteAnswer.Accept && s.PartyOf(worldPlayer) is null && party.Members.Count < Party.Limit;
        inviter?.Send(SocialPackets.PartyInviteResponseNotice(DbId, ServerId, StoredName, accepted ? PartyInviteAnswer.Accept : answer.Response));
        if (!accepted)
        {
            // A party of one left by a refusal ends.
            if (party.Members.Count == 1) DisbandParty(party, PartyUpdateReason.LeaveMember);
            return [SocialPackets.PartyInviteResponseReply(0)];
        }
        party.Members.Add(worldPlayer);
        var member = MemberInfo((byte)(party.Members.Count - 1));
        foreach (var other in party.Others(worldPlayer)) other.Send(SocialPackets.PartyAddMember(member));
        return [SocialPackets.PartyInviteResponseReply(0), SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Join), .. Achieve("JoinParty")];
    }

    void DisbandParty(Party party, PartyUpdateReason reason)
    {
        foreach (var m in party.Members) m.Send(SocialPackets.PartyDisband(reason));
        Social?.Parties.Remove(party.Key);
    }

    /// <summary>
    /// Leaving (also on disconnect): the leaver gets PartyDisband_NT (safer than the unconfirmed
    /// PartyLeave_NT), the others the updated PartyInfo_NT; a new leader if the leader left; a party
    /// of one ends.
    /// </summary>
    List<byte[]> PartyLeave(PartyUpdateReason reason, List<byte[]> replies)
    {
        if (Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party) return replies.Count > 0 ? [SocialPackets.PartyLeaveReply(Failed)] : [];
        party.Members.Remove(worldPlayer);
        replies.Add(SocialPackets.PartyDisband(reason));
        if (party.Members.Count <= 1)
        {
            DisbandParty(party, reason);
            return replies;
        }
        if (party.Leader == worldPlayer)
        {
            party.Leader = party.Members[0];
            foreach (var m in party.Members) m.Send(SocialPackets.PartyNewLeader(party.Leader.Session.DbId));
        }
        foreach (var m in party.Members) m.Send(SocialPackets.PartyInfo(Info(party), reason));
        return replies;
    }

    List<byte[]> PartyKick(byte[] plain)
    {
        if (SocialRequests.ParseDbId(plain) is not { } dbId || Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party
            || party.Leader != worldPlayer || party.Members.FirstOrDefault(m => m.Session.DbId == dbId && m != worldPlayer) is not { } kicked)
            return [SocialPackets.PartyKickoutReply(Failed)];
        party.Members.Remove(kicked);
        kicked.Send([SocialPackets.PartyKickout(false), SocialPackets.PartyDisband(PartyUpdateReason.KickMember)]);
        if (party.Members.Count <= 1)
        {
            DisbandParty(party, PartyUpdateReason.KickMember);
            return [SocialPackets.PartyKickoutReply(0)];
        }
        foreach (var m in party.Others(worldPlayer)) m.Send(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.KickMember));
        return [SocialPackets.PartyKickoutReply(0), SocialPackets.PartyInfo(Info(party), PartyUpdateReason.KickMember)];
    }

    List<byte[]> PartySetLeader(byte[] plain)
    {
        if (SocialRequests.ParseDbId(plain) is not { } dbId || Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party
            || party.Leader != worldPlayer || party.Members.FirstOrDefault(m => m.Session.DbId == dbId) is not { } leader)
            return [SocialPackets.PartySetLeaderReply(Failed)];
        party.Leader = leader;
        foreach (var m in party.Others(worldPlayer)) m.Send(SocialPackets.PartyNewLeader(dbId));
        return [SocialPackets.PartySetLeaderReply(0), SocialPackets.PartyNewLeader(dbId)];
    }

    List<byte[]> PartyDisband()
    {
        if (Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party || party.Leader != worldPlayer)
            return [SocialPackets.PartyDisbandReply(Failed)];
        foreach (var m in party.Others(worldPlayer)) m.Send(SocialPackets.PartyDisband(PartyUpdateReason.LeaveMember));
        s.Parties.Remove(party.Key);
        return [SocialPackets.PartyDisbandReply(0), SocialPackets.PartyDisband(PartyUpdateReason.LeaveMember)];
    }

    /// <summary>Party members see this one's HP/MP when it changes, and its map and position every 2 s.</summary>
    void PartyTick(long now)
    {
        if (Social is not { } s || worldPlayer is null || resources is null || s.PartyOf(worldPlayer) is not { } party || party.Members.Count < 2) return;
        if ((resources.Hp, resources.Mp) != partyPointsSent)
        {
            partyPointsSent = (resources.Hp, resources.Mp);
            var points = SocialPackets.PartyMemberHpMp(new PartyMemberPoints(playerKey, resources.Hp, resources.HpMax, resources.Mp, resources.MpMax));
            foreach (var m in party.Others(worldPlayer)) m.Send(points);
        }
        if (now < partyStatusAt) return;
        partyStatusAt = now + 2000;
        var status = SocialPackets.PartyMemberStatus(new PartyMemberStatus
        {
            DbId = DbId, PcId = Profile()?.PcId ?? 1, MapKey = worldMap, Position = position ?? default, Level = level,
        });
        foreach (var m in party.Others(worldPlayer)) m.Send(status);
    }

    // ---------- Player trade ----------

    List<byte[]> ExchangeRequest(byte[] plain, long now)
    {
        if (SocialRequests.ParseExchangeRequest(plain) is not { } targetKey) return [];
        byte[] Fail() => SocialPackets.ExchangeRequestReply(Failed, targetKey, 0);
        if (Social is not { } s || worldPlayer?.Map is not { } map || !map.Players.TryGetValue(targetKey, out var target) || target == worldPlayer
            || Store is null || target.Session.Store is null || s.ExchangeOf(worldPlayer) is not null || s.ExchangeOf(target) is not null
            || Distance2D(worldPlayer.Position, target.Position) > ExchangeRange)
            return [Fail()];
        var exchange = new Exchange(s.NextId(), worldPlayer, target);
        s.Exchanges[exchange.Id] = exchange;
        target.Send(SocialPackets.ExchangeRequested(playerKey, Invitation(), exchange.Id, now + 30_000));
        return [SocialPackets.ExchangeRequestReply(0, targetKey, exchange.Id)];
    }

    static double Distance2D(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    Exchange? MyExchange(long id) =>
        Social?.Exchanges.GetValueOrDefault(id) is { } e && worldPlayer is not null && e.Has(worldPlayer) ? e : null;

    List<byte[]> ExchangeAccept(byte[] plain)
    {
        if (SocialRequests.ParseExchangeAnswer(plain) is not { } answer) return [];
        if (MyExchange(answer.ExchangeId) is not { } e || e.Target != worldPlayer || e.Accepted) return [SocialPackets.ExchangeAcceptReply(Failed, answer.ExchangeId)];
        e.Accepted = true;
        e.Requester.Send(SocialPackets.ExchangeAccepted(e.Id));
        return [SocialPackets.ExchangeAcceptReply(0, e.Id)];
    }

    List<byte[]> ExchangeEnd(byte[] plain, bool refuse)
    {
        var id = refuse ? SocialRequests.ParseExchangeAnswer(plain)?.ExchangeId : SocialRequests.ParseExchangeId(plain);
        if (id is not { } exchangeId) return [];
        byte[] Reply(int result) => refuse ? SocialPackets.ExchangeRefuseReply(result, exchangeId) : SocialPackets.ExchangeCancelReply(result, exchangeId);
        if (MyExchange(exchangeId) is not { } e) return [Reply(Failed)];
        CancelExchange(e, refuse ? 1 : 0);
        return [Reply(0)];
    }

    void CancelExchange(Exchange e, int reason)
    {
        Social?.Exchanges.Remove(e.Id);
        e.Other(worldPlayer!).Send(SocialPackets.ExchangeCanceled(e.Id, reason));
    }

    List<byte[]> ExchangeModify(byte[] plain)
    {
        if (SocialRequests.ParseExchangeItemModify(plain) is not { } m) return [];
        byte[] Reply(int result) => SocialPackets.ExchangeItemListModifyReply(result, m.ExchangeId, m.Type, m.Item, m.GameCash);
        if (MyExchange(m.ExchangeId) is not { Accepted: true } e) return [Reply(Failed)];
        var offer = e.Offers[playerKey];
        if (m.Type == ExchangeModification.Remove) offer.Remove(m.Item.ItemKey);
        else
        {
            // Only bag items held in that number and not soul-bound (the store checks again when it completes).
            if (!storedItems.TryGetValue(m.Item.ItemKey, out var row) || row.Location != "bag" || m.Item.Count < 1 || m.Item.Count > row.Quantity
                || SoulBindCount(row) > 0 || offer.Count >= 16 && !offer.ContainsKey(m.Item.ItemKey))
                return [Reply(Failed)];
            offer[m.Item.ItemKey] = m.Item.Count;
        }
        // A changed offer unlocks both sides.
        e.Locked.Clear();
        e.Confirmed.Clear();
        var itemId = storedItems.TryGetValue(m.Item.ItemKey, out var item) ? item.TemplateId : 0;
        e.Other(worldPlayer!).Send(SocialPackets.ExchangePeerItemListModified(e.Id, m.Type, new ExchangeItemDetail(m.Item.ItemKey, itemId, m.Item.Count), m.GameCash));
        return [Reply(0)];
    }

    List<byte[]> ExchangeLock(byte[] plain)
    {
        if (SocialRequests.ParseExchangeLock(plain) is not { } l) return [];
        if (MyExchange(l.ExchangeId) is not { Accepted: true } e) return [SocialPackets.ExchangeItemListLockReply(Failed, l.ExchangeId, l.Locked)];
        if (l.Locked) e.Locked.Add(playerKey);
        else
        {
            e.Locked.Remove(playerKey);
            e.Confirmed.Clear();
        }
        e.Other(worldPlayer!).Send(SocialPackets.ExchangePeerItemListLock(e.Id, l.Locked));
        return [SocialPackets.ExchangeItemListLockReply(0, e.Id, l.Locked)];
    }

    /// <summary>Both locked, both confirmed: the items change hands in one store transaction, then both inventories update.</summary>
    async Task<List<byte[]>> ExchangeConfirmAsync(byte[] plain, CancellationToken ct)
    {
        if (SocialRequests.ParseExchangeId(plain) is not { } id) return [];
        if (MyExchange(id) is not { Accepted: true } e || e.Locked.Count < 2) return [SocialPackets.ExchangeConfirmReply(Failed, id)];
        e.Confirmed.Add(playerKey);
        var other = e.Other(worldPlayer!);
        if (e.Confirmed.Count < 2)
        {
            other.Send(SocialPackets.ExchangePeerConfirmed(e.Id));
            return [SocialPackets.ExchangeConfirmReply(0, e.Id)];
        }
        Social!.Exchanges.Remove(e.Id);
        var partner = other.Session;
        List<(Guid, long)> Gives(GameSession side) =>
            e.Offers[side.playerKey].Select(o => side.storedItems.TryGetValue(o.Key, out var row) ? (row.InstanceId, o.Value) : (Guid.Empty, 0L)).ToList();
        var mine = Gives(this);
        var theirs = Gives(partner);
        var done = Store is { } store && CharacterId is { } me && partner.Store is { } otherStore && partner.CharacterId is { } them
            && mine.Concat(theirs).All(g => g.Item1 != Guid.Empty)
            && await StoredValue(() => store.ExchangeAsync(me, mine, otherStore, them, theirs, ct));
        if (!done)
        {
            other.Send(SocialPackets.ExchangeCanceled(e.Id, 2));
            return [SocialPackets.ExchangeConfirmReply(Failed, e.Id), SocialPackets.ExchangeCanceled(e.Id, 2)];
        }
        other.Send([.. await partner.InventoryChangesAsync(ct), SocialPackets.ExchangePeerConfirmed(e.Id), SocialPackets.ExchangeCompleted(e.Id)]);
        return [SocialPackets.ExchangeConfirmReply(0, e.Id), .. await InventoryChangesAsync(ct), SocialPackets.ExchangeCompleted(e.Id)];
    }

    // ---------- Duel ----------

    List<byte[]> DuelCallOut(byte[] plain, long now)
    {
        if (SocialRequests.ParseDuelCallOut(plain) is not { } targetKey) return [];
        byte[] Fail() => SocialPackets.DuelCallOutReply(Failed, 0, 0);
        if (Social is not { } s || worldPlayer?.Map is not { } map || !map.Players.TryGetValue(targetKey, out var target) || target == worldPlayer
            || s.DuelOf(worldPlayer) is not null || s.DuelOf(target) is not null || Distance2D(worldPlayer.Position, target.Position) > ExchangeRange * 2)
            return [Fail()];
        var duel = new Duel((int)s.NextId(), worldPlayer, target) { NextMs = now + 30_000 };
        s.Duels[duel.RoomKey] = duel;
        target.Send(SocialPackets.DuelCallOut(duel.RoomKey, playerKey, Invitation(), duel.NextMs));
        return [SocialPackets.DuelCallOutReply(0, duel.RoomKey, duel.NextMs)];
    }

    DuelStatus Status(Duel d, long now) =>
        new(d.RoomKey, d.RoomKey, d.State, d.Challenger.Key, d.Challenger.Name, d.Accepter.Key, d.Accepter.Name, now);

    List<byte[]> DuelAnswer(byte[] plain, bool accept, long now)
    {
        if (SocialRequests.ParseDuelAnswer(plain) is not { } answer) return [];
        if (Social?.Duels.GetValueOrDefault(answer.RoomKey) is not { State: DuelState.InWaiting } duel || duel.Accepter != worldPlayer)
            return [accept ? SocialPackets.DuelAcceptReply(Failed, answer.RoomKey, answer.ChallengerKey) : SocialPackets.DuelRejectReply(Failed, answer.RoomKey)];
        if (!accept)
        {
            Social.Duels.Remove(duel.RoomKey);
            duel.Challenger.Send(SocialPackets.DuelRejected(duel.RoomKey, playerKey, StoredName));
            return [SocialPackets.DuelRejectReply(0, duel.RoomKey)];
        }
        duel.State = DuelState.InCountDown;
        duel.NextMs = now + Duel.CountdownMs;
        var status = SocialPackets.DuelStateUpdate(Status(duel, now));
        duel.Challenger.Send([SocialPackets.DuelAccepted(duel.RoomKey, playerKey), status]);
        return [SocialPackets.DuelAcceptReply(0, duel.RoomKey, duel.Challenger.Key), status];
    }

    /// <summary>Countdown to battle, time limit, and an unanswered challenge expiring (run by the challenger's tick).</summary>
    void DuelTick(long now)
    {
        if (Social is not { } s || worldPlayer is null || s.DuelOf(worldPlayer) is not { } duel || duel.Challenger != worldPlayer || now < duel.NextMs) return;
        switch (duel.State)
        {
            case DuelState.InWaiting:
                s.Duels.Remove(duel.RoomKey);
                duel.Challenger.Send(SocialPackets.DuelCancel(duel.RoomKey, duel.Challenger.Name, duel.Accepter.Name));
                duel.Accepter.Send(SocialPackets.DuelCancel(duel.RoomKey, duel.Challenger.Name, duel.Accepter.Name));
                break;
            case DuelState.InCountDown:
                duel.State = DuelState.InBattle;
                duel.NextMs = now + Duel.LimitMs;
                var status = SocialPackets.DuelStateUpdate(Status(duel, now));
                duel.Challenger.Send(status);
                duel.Accepter.Send(status);
                break;
            case DuelState.InBattle:
                EndDuel(duel, DuelCompleteReason.TimeOut, null, now);
                break;
        }
    }

    /// <summary>The duel ends (a winner, a draw on time-out, or a duelist leaving): both get the state and the result; onlookers the completion.</summary>
    void EndDuel(Duel duel, DuelCompleteReason reason, WorldPlayer? winner, long now)
    {
        Social?.Duels.Remove(duel.RoomKey);
        duel.State = DuelState.InComplete;
        var loser = winner is null ? null : duel.Other(winner);
        var result = SocialPackets.DuelResult(duel.RoomKey, reason, winner?.Key ?? 0, loser?.Key ?? 0, winner?.Name ?? "", loser?.Name ?? "");
        foreach (var p in new[] { duel.Challenger, duel.Accepter }) p.Send([SocialPackets.DuelStateUpdate(Status(duel, now)), result]);
        duel.Challenger.Map?.Broadcast(duel.Challenger, [SocialPackets.DuelComplete(duel.RoomKey, reason, winner?.Key ?? 0, winner?.Name ?? "", loser?.Name ?? "")]);
    }

    /// <summary>A cast at a duel opponent in battle: PvP damage (Skill.dat PvP rows) to the opponent; at 1 HP the duel is lost.</summary>
    List<byte[]> DuelHits(int skill, int targetKey, long now)
    {
        if (Social is not { } s || worldPlayer is null || s.DuelOf(worldPlayer) is not { State: DuelState.InBattle } duel) return [];
        var opponent = duel.Other(worldPlayer);
        if (opponent.Key != targetKey || opponent.Session.resources is not { } theirs) return [];
        var replies = new List<byte[]>();
        var seq = 0;
        // The opponent's defense counts as it does against NPC hits (it was ignored before).
        foreach (var hit in Combat.SkillHits(data, skill, className, level, equippedWeapon, opponent.Session.Defense, rng, pvp: true))
        {
            theirs.Hp = Math.Max(1, theirs.Hp - hit.Amount);
            replies.Add(GamePackets.SkillHit(opponent.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq));
        }
        if (replies.Count == 0) return [];
        opponent.Send([.. replies, theirs.Update()]);
        if (theirs.Hp <= 1) EndDuel(duel, DuelCompleteReason.Die, worldPlayer, now);
        return replies;
    }

    /// <summary>Leaving the world (lobby, disconnect): parties, trades, duels and invitations end; friends see this one go offline.</summary>
    void LeaveSocial(long now)
    {
        if (Social is not { } s || worldPlayer is null) return;
        TellFriends(false);
        PartyLeave(PartyUpdateReason.LeaveMemberBySystem, []);
        if (s.ExchangeOf(worldPlayer) is { } exchange) CancelExchange(exchange, 3);
        if (s.DuelOf(worldPlayer) is { } duel) EndDuel(duel, DuelCompleteReason.MapOut, duel.Other(worldPlayer), now);
        LeaveArena(now);
        s.Invites.Remove(playerKey);
    }
}
