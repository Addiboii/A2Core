using System.Runtime.CompilerServices;
using Aion.Protocol.Native;

namespace Aion.World;

// Expedition / party finder (feature/systems-2). No official recording holds any of these (record-checklist section 7):
// every layout is the usmap's, every opcode the catalogue index + the late-run base 0x8ed6 (the run of the OBSERVED
// 0x9330/0x9331 world-entry lists) or the party base 0x8df3, results u16 (CANDIDATE throughout).
//   PartyRecruit_RQ 0x9334 (no body) -> PartyRecruit_RS 0x9335 {result, expire i64}: the leader (alone: a party is made) opens the
//     party to joiners until the expiry; the members hear PartyRecruit_NT 0x933a {expire}. PartyRecruitCancel 0x9336/0x9337 closes it.
//   PartyJoin_RQ 0x9338 {party key i64} -> PartyJoin_RS 0x9339 {result}: anyone joins a recruiting party that has room (the
//     party key comes with the client's recruit chat link); the joiner gets PartyInfo_NT (Join), the others PartyAddMember_NT.
//   PartyInfoUpdate_RQ 0x9201 {distribution, special, grade, join type u8} -> PartyInfoUpdate_RS 0x9202 {result} and
//     PartyInfoUpdate_NT 0x9203 to the members; join type EPartyJoinType Free joins at once, Approval refuses PartyJoin.
//   PartyRecommend_RQ 0x9228 {type u8, candidates [i64]} -> PartyRecommend_RS 0x9229 {result, type, [PartyRecommendCharInfo]}:
//     Normal = players on this map in no party (GlobalSetting party_invite_recommend_list_max_count 10), Friend = friends online
//     (party_invite_friend_list_max_count 30), Guild = legion members online (party_invite_legion_list_max_count 30), RecentParty none.
//   ForceRecruit (alliances of parties) is not built: refused.
public sealed partial class GameSession
{
    public const ushort PartyRecruitRq = 0x9334, PartyRecruitRs = 0x9335, PartyRecruitCancelRq = 0x9336, PartyRecruitCancelRs = 0x9337,
        PartyJoinRq = 0x9338, PartyJoinRs = 0x9339, PartyRecruitNt = 0x933a, ForceRecruitRq = 0x9383, ForceRecruitRs = 0x9384,
        ForceRecruitCancelRq = 0x9385, ForceRecruitCancelRs = 0x9386;
    /// <summary>CANDIDATE: how long a recruitment stays open (no table value; GlobalSetting party_auto_delay is 20 s between automatic posts).</summary>
    public const long PartyRecruitMs = 600_000;
    const byte PartyJoinFree = 1, PartyJoinApproval = 2;
    /// <summary>EPartyRecommendType.</summary>
    const byte RecommendNormal = 1, RecommendFriend = 2, RecommendGuild = 3, RecommendRecent = 4;

    /// <summary>Finder state of a party (recruiting until, join type), kept beside the shared world's party.</summary>
    sealed class PartyFinder
    {
        public long RecruitUntil;
        public byte JoinType = PartyJoinFree;
        public (byte Distribution, byte Special, byte Grade) Loot;
    }
    static readonly ConditionalWeakTable<Party, PartyFinder> partyFinders = new();
    static PartyFinder FinderOf(Party party) => partyFinders.GetOrCreateValue(party);

    List<byte[]>? ExpeditionRequest(byte[] plain, long now) => Opcode(plain) switch
    {
        PartyRecruitRq => PartyRecruit(now),
        PartyRecruitCancelRq => PartyRecruitCancel(),
        PartyJoinRq when plain.Length >= 10 => PartyJoin(BitConverter.ToInt64(plain, 2), now),
        ForceRecruitRq => [PacketWriter.Opcode(ForceRecruitRs).U16(Failed).I64(0).ToArray()],
        ForceRecruitCancelRq => [PacketWriter.Opcode(ForceRecruitCancelRs).U16(Failed).ToArray()],
        _ => null,
    };

    List<byte[]> PartyRecruit(long now)
    {
        byte[] Reply(int result, long expire) => PacketWriter.Opcode(PartyRecruitRs).U16(result).I64(expire).ToArray();
        if (Social is not { } s || worldPlayer is null || instanceKey != 0) return [Reply(Failed, 0)];
        var replies = new List<byte[]>();
        if (s.PartyOf(worldPlayer) is not { } party)
        {
            party = new Party(s.NextId(), worldPlayer);
            s.Parties[party.Key] = party;
            replies.Add(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create));
        }
        if (party.Leader != worldPlayer || party.Members.Count >= Party.Limit) return [Reply(Failed, 0)];
        var finder = FinderOf(party);
        finder.RecruitUntil = now + PartyRecruitMs;
        var notice = PacketWriter.Opcode(PartyRecruitNt).I64(finder.RecruitUntil).ToArray();
        foreach (var m in party.Others(worldPlayer)) m.Send(notice);
        replies.Insert(0, Reply(0, finder.RecruitUntil));
        replies.Add(notice);
        return replies;
    }

    List<byte[]> PartyRecruitCancel()
    {
        if (Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party || party.Leader != worldPlayer)
            return [PacketWriter.Opcode(PartyRecruitCancelRs).U16(Failed).ToArray()];
        FinderOf(party).RecruitUntil = 0;
        var notice = PacketWriter.Opcode(PartyRecruitNt).I64(0).ToArray();
        foreach (var m in party.Others(worldPlayer)) m.Send(notice);
        return [PacketWriter.Opcode(PartyRecruitCancelRs).U16(0).ToArray(), notice];
    }

    List<byte[]> PartyJoin(long partyKey, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(PartyJoinRs).U16(result).ToArray();
        if (Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not null || instanceKey != 0) return [Reply(Failed)];
        if (!s.Parties.TryGetValue(partyKey, out var party) || party.Members.Count >= Party.Limit) return [Reply(Failed)];
        var finder = FinderOf(party);
        if (finder.RecruitUntil < now || finder.JoinType == PartyJoinApproval || party.Leader.Session.profileRace != profileRace) return [Reply(Failed)];
        party.Members.Add(worldPlayer);
        var member = MemberInfo((byte)(party.Members.Count - 1));
        foreach (var other in party.Others(worldPlayer)) other.Send(SocialPackets.PartyAddMember(member));
        if (party.Members.Count >= Party.Limit) finder.RecruitUntil = 0;
        return [Reply(0), SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Join), .. Achieve("JoinParty")];
    }

    /// <summary>PartyInfoUpdate_RQ {distribution, special distribution, its grade, join type}: the leader's party settings.</summary>
    List<byte[]> PartyInfoUpdate(byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SocialOpcodes.PartyInfoUpdateRs).U16(result).ToArray();
        if (plain.Length < 6 || Social is not { } s || worldPlayer is null || s.PartyOf(worldPlayer) is not { } party || party.Leader != worldPlayer) return [Reply(Failed)];
        var finder = FinderOf(party);
        finder.Loot = (plain[2], plain[3], plain[4]);
        finder.JoinType = plain[5] is PartyJoinFree or PartyJoinApproval ? plain[5] : PartyJoinFree;
        var notice = PacketWriter.Opcode(SocialOpcodes.PartyInfoUpdateNt).I32(Party.Limit).U8(plain[2]).U8(plain[3]).U8(plain[4]).ToArray();
        foreach (var m in party.Others(worldPlayer)) m.Send(notice);
        return [Reply(0), notice];
    }

    /// <summary>PartyRecommend_RQ {type u8, candidates [i64]}: players to invite (see the file header).</summary>
    List<byte[]> PartyRecommend(byte[] plain)
    {
        var type = plain.Length >= 3 ? plain[2] : RecommendNormal;
        var picks = new List<GameSession>();
        if (World is { } world && worldPlayer is not null)
        {
            IEnumerable<WorldPlayer> pool = type switch
            {
                RecommendNormal => worldPlayer.Map?.Players.Values.Where(p => p != worldPlayer) ?? [],
                RecommendFriend => social.Where(r => r.Kind == "friend").Select(r => world.FindOnline(r.CharacterId)).OfType<WorldPlayer>(),
                RecommendGuild => guild?.Members.Where(m => m.CharacterId != CharacterId).Select(m => world.FindOnline(m.CharacterId)).OfType<WorldPlayer>() ?? [],
                _ => [],
            };
            // GlobalSetting party_invite_recommend_list_max_count 10; party_invite_friend_list_max_count / party_invite_legion_list_max_count 30.
            var limit = type is RecommendFriend or RecommendGuild ? 30 : (int)Systems2Tables.Embedded.Global("party_invite_recommend_list_max_count");
            picks = pool.Where(p => !p.Hidden && p.Session.profileRace == profileRace && Social?.PartyOf(p) is null && p.Session.resources is not null)
                .Select(p => p.Session).Take(Math.Max(1, limit)).ToList();
        }
        var w = PacketWriter.Opcode(SocialOpcodes.PartyRecommendRs).U16(0).U8(type).Varint(picks.Count);
        foreach (var p in picks)
            w.I64(p.DbId).I32(p.ServerId).I32(p.ServerId).String(p.StoredName).I32(p.Profile()?.PcId ?? 1).String(p.guild?.Name ?? "").U16(p.guild?.Emblem ?? 0)
                .I32(p.level).I32(0).Bool(true);
        return [w.ToArray()];
    }
}
