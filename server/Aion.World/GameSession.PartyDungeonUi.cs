using System.Text;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Expedition UI / dungeon room matchmaking (usmap; bodies mostly CANDIDATE — no official captures for 0x3A61/0x3AC*).
// Flow (OURS): create or join a waiting room → optional apply/invite → PartyDungeonEnter or Start moves every member into
// the same instance copy (SharedWorld instance key = room key) and forms a party; each member spends an expedition ticket.
public sealed partial class GameSession
{
    public const ushort PartyDungeonEnterRq = 0x3A61, PartyDungeonEnterRs = 0x3A62, BookmarkedContentListNt = 0x3A66, PartyDungeonChallengeNt = 0x3A67,
        DungeonRoomListRq = 0x3AC1, DungeonRoomListRs = 0x3AC2, DungeonRoomInfoNt = 0x3AC3,
        DungeonRoomCreateRq = 0x3AC4, DungeonRoomCreateRs = 0x3AC5, DungeonRoomJoinRq = 0x3AC6, DungeonRoomJoinRs = 0x3AC7,
        DungeonRoomApplyNt = 0x3AC8, DungeonRoomApplyResponseRq = 0x3AC9, DungeonRoomApplyResponseRs = 0x3ACA, DungeonRoomApplyResponseNt = 0x3ACB,
        DungeonRoomAddMemberNt = 0x3ACC, DungeonRoomInviteRq = 0x3ACD, DungeonRoomInviteRs = 0x3ACE, DungeonRoomInviteNt = 0x3ACF,
        DungeonRoomInviteResponseRq = 0x3AD0, DungeonRoomInviteResponseRs = 0x3AD1, DungeonRoomLeaveRq = 0x3ADD, DungeonRoomLeaveRs = 0x3ADE,
        DungeonRoomLeaveNt = 0x3ADC, DungeonRoomKickMemberRq = 0x3AE9, DungeonRoomKickMemberRs = 0x3AEA, DungeonRoomKickoutNt = 0x3AEB,
        DungeonRoomInstantJoinRq = 0x3AD3, DungeonRoomInstantJoinRs = 0x3AD4,
        DungeonRoomReadyRq = 0x3AD5, DungeonRoomReadyRs = 0x3AD6, DungeonRoomReadyNt = 0x3AD7,
        DungeonRoomStartRq = 0x3AD8, DungeonRoomStartRs = 0x3AD9, DungeonRoomDisbandNt = 0x3ADB, DungeonRoomNewLeaderNt = 0x3ADA,
        DungeonRoomDisconnectMemberNt = 0x3ADF,
        DungeonRoomEditSettingRq = 0x3AE1, DungeonRoomEditSettingRs = 0x3AE2, DungeonRoomSettingUpdatedNt = 0x3AE3,
        DungeonRoomRetryRq = 0x3AEF, DungeonRoomRetryRs = 0x3AF0, DungeonRoomAskRetryNt = 0x3AF1,
        DungeonRoomRetryResponseRq = 0x3AF2, DungeonRoomRetryResponseRs = 0x3AF3, DungeonRoomRetryResponseNt = 0x3AF4, DungeonRoomRetryFailNt = 0x3AF5;

    const int RoomFailed = 1;
    const byte RoomInfoCreate = 0, RoomInfoJoin = 1, RoomInfoLeave = 2, RoomInfoKick = 3, RoomInfoDisband = 4;
    const byte ApplyAccept = 0, ApplyRefuse = 1;
    const byte InviteAccept = 0, RetryAccept = 0, RetryRefuse = 1;
    const long RoomInviteMs = 30_000, RoomApplyMs = 30_000;

    /// <summary>Solo create path before PartyDungeonEnter (no room).</summary>
    int pendingPartyDungeonId;

    static byte[] BookmarkedContentList() => PacketWriter.Opcode(BookmarkedContentListNt).Varint(0).ToArray();

    static byte[] PartyDungeonChallengeList() => PacketWriter.Opcode(PartyDungeonChallengeNt).Varint(0).ToArray();

    DungeonRoom? MyRoom => worldPlayer is null ? null : Social?.DungeonRoomOf(worldPlayer);

    static string ReadFString(PacketReader reader)
    {
        var length = checked((int)reader.Varint());
        return Encoding.UTF8.GetString(reader.Take(length));
    }

    static List<int> ReadAffixList(PacketReader reader)
    {
        var count = checked((int)reader.Varint());
        var list = new List<int>(count);
        for (var i = 0; i < count; i++) list.Add(reader.I32());
        return list;
    }

    static void SkipWantedRole(PacketReader reader)
    {
        var count = checked((int)reader.Varint());
        for (var i = 0; i < count; i++) { reader.U8(); reader.U8(); }
    }

    (string Desc, bool Public, byte JoinType, List<int> Affixes)? ParseEditSetting(byte[] plain)
    {
        if (plain.Length < 3) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var desc = ReadFString(reader);
            var pub = reader.Bool();
            var join = reader.U8();
            if (reader.Remaining > 0) SkipWantedRole(reader);
            if (reader.Remaining > 0) reader.Bool();
            var affixes = reader.Remaining > 0 ? ReadAffixList(reader) : [];
            return (desc, pub, join, affixes);
        }
        catch (ProtocolException) { return null; }
    }

    (int ContentDungeonId, byte DungeonLevel, byte Race)? ParseInstantJoin(byte[] plain)
    {
        if (plain.Length < 7) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var dungeonId = reader.I32();
            if (reader.Remaining > 0) SkipWantedRole(reader);
            var level = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var race = reader.Remaining > 0 ? reader.U8() : (byte)0;
            return (dungeonId, level, race);
        }
        catch (ProtocolException) { return null; }
    }

    (int ContentDungeonId, byte DungeonLevel, byte Race, string Desc, bool Public, byte JoinType, List<int> Affixes)? ParseCreate(byte[] plain)
    {
        if (plain.Length < 7) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var dungeonId = reader.I32();
            var level = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var race = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var desc = reader.Remaining > 0 ? ReadFString(reader) : "";
            var pub = reader.Remaining > 0 && reader.Bool();
            var join = reader.Remaining > 0 ? reader.U8() : PartyOptions.JoinFree;
            if (reader.Remaining > 0) SkipWantedRole(reader);
            if (reader.Remaining > 0) reader.Bool();
            var affixes = reader.Remaining > 0 ? ReadAffixList(reader) : [];
            return (dungeonId, level, race, desc, pub, join, affixes);
        }
        catch (ProtocolException) { return null; }
    }

    static void WriteInvitation(PacketWriter w, InvitationInfo i) =>
        w.I32(i.PcId).I32(i.Level).I32(i.EquipItemLevel).String(i.Nickname).I32(i.ServerId).I32(i.ConquerorLevel).I64(i.CombatPower).U8(i.MentoringRole);

    void WriteDungeonRoomMember(PacketWriter w, WorldPlayer member, byte number, bool ready)
    {
        var session = member.Session;
        w.U8(number).I64(session.DbId).String(session.StoredName).I32(session.Profile()?.PcId ?? 1).I32(session.level).I32(0)
            .I32(session.EquipItemLevel()).Bool(ready).Bool(true).I32(ServerId).I32(ServerId).U8(0).I64(0).Varint(0).I64(0).U8(0).U8(0);
    }

    void WriteDungeonRoomInfo(PacketWriter w, DungeonRoom room, byte dungeonState = 0)
    {
        w.I32(room.RoomKey).String(room.Desc).U8((byte)Math.Min(room.Limit, 255)).I32(room.ContentDungeonId).U8(room.DungeonLevel)
            .U8(room.Race).I64(room.Leader.Session.DbId).Bool(room.Public).U8(room.JoinType).U8(dungeonState)
            .Varint((uint)room.Members.Count);
        for (var i = 0; i < room.Members.Count; i++) WriteDungeonRoomMember(w, room.Members[i], (byte)i, room.Ready.Contains(room.Members[i].Key));
        w.Varint((uint)room.AffixList.Count);
        foreach (var affix in room.AffixList) w.I32(affix);
    }

    byte[] DungeonRoomInfoPacket(DungeonRoom room, byte reason)
    {
        var w = PacketWriter.Opcode(DungeonRoomInfoNt);
        WriteDungeonRoomInfo(w, room);
        return w.U8(reason).ToArray();
    }

    static int TicketFailId(DungeonInfo room) => Expeditions.TicketFor(room)?.Id ?? 0;

    bool RoomEntryAllowed(DungeonInfo room, long now, out int ticketId, bool allowInCopy = false)
    {
        ticketId = 0;
        if ((!allowInCopy && instanceKey != 0) || resources is null || reloading || dead) return false;
        if (MapEventData.Dungeon(room.Map) is null || room.Type != "Party") return false;
        if (!IsAdmin && !MeetsEntry(room, now, out _)) { ticketId = TicketFailId(room); return false; }
        return true;
    }

    void BroadcastRoom(DungeonRoom room, byte[] packet, WorldPlayer? except = null)
    {
        foreach (var member in room.Members)
            if (member != except) member.Send(packet);
    }

    void BroadcastRoomInfo(DungeonRoom room, byte reason, WorldPlayer? except = null) =>
        BroadcastRoom(room, DungeonRoomInfoPacket(room, reason), except);

    /// <summary>0x3AC1 DungeonRoomList_RQ {dungeon id, level}: waiting rooms for the expedition window.</summary>
    List<byte[]> DungeonRoomList(byte[] plain)
    {
        if (plain.Length < 7) return [];
        var contentId = BitConverter.ToInt32(plain, 2);
        var level = plain.Length > 6 ? plain[6] : (byte)0;
        var resolved = Expeditions.ResolveDungeonId(contentId, level);
        if (MapEventData.Dungeon(resolved) is not { Type: "Party" })
            return [DungeonRoomListReply(RoomFailed, contentId, null, [])];
        var myRoom = MyRoom;
        var rooms = Social?.DungeonRooms.Values
            .Where(r => r.Public && r.ContentDungeonId == contentId && r.DungeonLevel == level && r.ResolvedDungeonId == resolved)
            .ToList() ?? [];
        return [DungeonRoomListReply(0, contentId, myRoom, rooms)];
    }

    byte[] DungeonRoomListReply(int result, int contentId, DungeonRoom? mine, IReadOnlyList<DungeonRoom> rooms)
    {
        var w = PacketWriter.Opcode(DungeonRoomListRs).I32(result).I32(contentId);
        if (mine is null) WriteEmptyDungeonRoomInfo(w); else WriteDungeonRoomInfo(w, mine);
        w.Varint((uint)rooms.Count);
        foreach (var room in rooms) WriteDungeonRoomInfo(w, room);
        return w.ToArray();
    }

    static void WriteEmptyDungeonRoomInfo(PacketWriter w) =>
        w.I32(0).String("").U8(0).I32(0).U8(0).U8(0).I64(0).Bool(false).U8(0).U8(0).Varint(0).Varint(0);

    /// <summary>0x3AC4 DungeonRoomCreate_RQ: open a waiting room for an expedition tier.</summary>
    List<byte[]> DungeonRoomCreate(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomCreateRs).I32(result).I32(ticketId).ToArray();
        if (ParseCreate(plain) is not { } settings || Social is not { } social || worldPlayer is null)
            return [Reply(RoomFailed)];
        var resolved = Expeditions.ResolveDungeonId(settings.ContentDungeonId, settings.DungeonLevel);
        if (MapEventData.Dungeon(resolved) is not { } room || MyRoom is not null)
            return [Reply(RoomFailed)];
        if (!Expeditions.AffixesAllowed(settings.ContentDungeonId, settings.DungeonLevel, settings.Affixes))
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(room, now, out var ticketId))
            return [Reply(RoomFailed, ticketId)];
        var key = checked((int)social.NextId());
        var dungeonRoom = new DungeonRoom(key, worldPlayer, settings.ContentDungeonId, settings.DungeonLevel, resolved)
        {
            Desc = settings.Desc, Public = settings.Public, JoinType = settings.JoinType, Race = settings.Race,
        };
        dungeonRoom.AffixList.AddRange(settings.Affixes);
        social.DungeonRooms[key] = dungeonRoom;
        return [Reply(0), DungeonRoomInfoPacket(dungeonRoom, RoomInfoCreate)];
    }

    byte[] SettingUpdatedPacket(DungeonRoom room)
    {
        var w = PacketWriter.Opcode(DungeonRoomSettingUpdatedNt).String(room.Desc).Bool(room.Public).U8(room.JoinType).Varint(0).Bool(false);
        w.Varint((uint)room.AffixList.Count);
        foreach (var affix in room.AffixList) w.I32(affix);
        return w.ToArray();
    }

    /// <summary>0x3AD3 auto-match: join a public free room or open one.</summary>
    List<byte[]> DungeonRoomInstantJoin(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInstantJoinRs).I32(result).I32(ticketId).ToArray();
        if (ParseInstantJoin(plain) is not { } req || Social is not { } social || worldPlayer is null || MyRoom is not null)
            return [Reply(RoomFailed)];
        var resolved = Expeditions.ResolveDungeonId(req.ContentDungeonId, req.DungeonLevel);
        if (MapEventData.Dungeon(resolved) is not { Type: "Party" } dungeon)
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(dungeon, now, out var ticketId))
            return [Reply(RoomFailed, ticketId)];
        var room = social.DungeonRooms.Values
            .Where(r => r.Public && r.JoinType == PartyOptions.JoinFree && r.ContentDungeonId == req.ContentDungeonId
                && r.DungeonLevel == req.DungeonLevel && r.ResolvedDungeonId == resolved && r.Members.Count < r.Limit)
            .OrderByDescending(r => r.Members.Count)
            .FirstOrDefault();
        if (room is not null)
            return AddRoomMember(room, worldPlayer, now, [Reply(0)]);
        var key = checked((int)social.NextId());
        room = new DungeonRoom(key, worldPlayer, req.ContentDungeonId, req.DungeonLevel, resolved)
        {
            Public = true, JoinType = PartyOptions.JoinFree, Race = req.Race,
        };
        social.DungeonRooms[key] = room;
        return [Reply(0), DungeonRoomInfoPacket(room, RoomInfoCreate)];
    }

    /// <summary>0x3AD5 toggle ready in the expedition waiting room.</summary>
    List<byte[]> DungeonRoomReady(byte[] plain)
    {
        byte[] Reply(int result, bool ready) => PacketWriter.Opcode(DungeonRoomReadyRs).I32(result).I32(ready ? 1 : 0).ToArray();
        if (plain.Length < 3 || MyRoom is not { } room || worldPlayer is null)
            return [Reply(RoomFailed, false)];
        var ready = plain[2] != 0;
        if (ready) room.Ready.Add(playerKey); else room.Ready.Remove(playerKey);
        var notice = PacketWriter.Opcode(DungeonRoomReadyNt).I64(DbId).I32(ready ? 1 : 0).ToArray();
        BroadcastRoom(room, notice, worldPlayer);
        return [Reply(0, ready), notice];
    }

    /// <summary>0x3AE1 leader edits room settings.</summary>
    List<byte[]> DungeonRoomEditSetting(byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomEditSettingRs).I32(result).ToArray();
        if (ParseEditSetting(plain) is not { } settings || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        if (!Expeditions.AffixesAllowed(room.ContentDungeonId, room.DungeonLevel, settings.Affixes))
            return [Reply(RoomFailed)];
        room.Desc = settings.Desc;
        room.Public = settings.Public;
        room.JoinType = settings.JoinType;
        room.AffixList.Clear();
        room.AffixList.AddRange(settings.Affixes);
        room.Ready.Clear();
        var updated = SettingUpdatedPacket(room);
        BroadcastRoom(room, updated);
        BroadcastRoomInfo(room, RoomInfoCreate);
        return [Reply(0), updated];
    }

    /// <summary>0x3AC6 DungeonRoomJoin_RQ: enter a public room or apply when approval is required.</summary>
    List<byte[]> DungeonRoomJoin(byte[] plain, long now)
    {
        byte[] Reply(int result, byte joinType = PartyOptions.JoinFree, int ticketId = 0) =>
            PacketWriter.Opcode(DungeonRoomJoinRs).I32(result).U8(joinType).I32(ticketId).ToArray();
        if (plain.Length < 6 || Social is not { } social || worldPlayer is null || MyRoom is not null)
            return [Reply(RoomFailed)];
        var roomKey = BitConverter.ToInt32(plain, 2);
        if (!social.DungeonRooms.TryGetValue(roomKey, out var room))
            return [Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(dungeon, now, out var ticketId))
            return [Reply(RoomFailed, room.JoinType, ticketId)];
        if (room.Members.Count >= room.Limit)
            return [Reply(RoomFailed, room.JoinType)];
        if (room.JoinType == PartyOptions.JoinApproval)
        {
            room.PendingApply[playerKey] = now + RoomApplyMs;
            var applyW = PacketWriter.Opcode(DungeonRoomApplyNt).I32(room.RoomKey).I64(DbId);
            WriteInvitation(applyW, Invitation());
            var apply = applyW.I64(now + RoomApplyMs).ToArray();
            room.Leader.Send(apply);
            return [Reply(0, room.JoinType)];
        }
        return AddRoomMember(room, worldPlayer, now, [Reply(0, room.JoinType)]);
    }

    /// <summary>0x3AC9 leader answers a join application.</summary>
    List<byte[]> DungeonRoomApplyResponse(byte[] plain, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomApplyResponseRs).I32(result).ToArray();
        if (plain.Length < 14 || Social is not { } social || worldPlayer is null || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        var roomKey = BitConverter.ToInt32(plain, 2);
        if (room.RoomKey != roomKey) return [Reply(RoomFailed)];
        var applicantDbId = BitConverter.ToInt64(plain, 6);
        var accept = plain.Length > 14 ? plain[14] == ApplyAccept : false;
        var applicant = World?.Online.Values.FirstOrDefault(p => p.Session.DbId == applicantDbId);
        if (applicant is null || !room.PendingApply.Remove(applicant.Key))
            return [Reply(RoomFailed)];
        var notice = PacketWriter.Opcode(DungeonRoomApplyResponseNt).I32(room.RoomKey).U8(accept ? ApplyAccept : ApplyRefuse).ToArray();
        applicant.Send(notice);
        if (!accept) return [Reply(0)];
        if (room.Members.Count >= room.Limit || MyRoom is not null && applicant != worldPlayer)
            return [Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon || !applicant.Session.RoomEntryAllowed(dungeon, now, out _))
            return [Reply(RoomFailed)];
        return AddRoomMember(room, applicant, now, [Reply(0)]);
    }

    /// <summary>0x3ACD invite a player by name into the leader's room.</summary>
    List<byte[]> DungeonRoomInvite(byte[] plain, long now)
    {
        byte[] Fail(int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInviteRs).I32(RoomFailed).I32(ServerId).String("").I32(0).I32(0).I64(0).I32(ticketId).ToArray();
        if (Social is not { } social || worldPlayer is null || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Fail()];
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var serverId = reader.I32();
            var name = ReadFString(reader);
            _ = reader.Remaining > 0 ? reader.U8() : (byte)0;
            if (World?.FindOnline(CharacterNames.Stored(name)) is not { } invitee || invitee == worldPlayer || MyRoom is not null && room.Has(invitee))
                return [Fail()];
            if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
                return [Fail()];
            if (!invitee.Session.RoomEntryAllowed(dungeon, now, out var inviteTicket))
                return [Fail(inviteTicket)];
            var expires = now + RoomInviteMs;
            social.DungeonRoomInvites[invitee.Key] = new DungeonRoomInvitePending(room.RoomKey, expires);
            room.PendingInvite[invitee.Key] = expires;
            var inviteW = PacketWriter.Opcode(DungeonRoomInviteNt).I32(room.RoomKey).I32(room.ContentDungeonId).U8(room.DungeonLevel).I64(DbId);
            WriteInvitation(inviteW, Invitation());
            invitee.Send(inviteW.I64(expires).ToArray());
            return [PacketWriter.Opcode(DungeonRoomInviteRs).I32(0).I32(serverId).String(invitee.Name).I32(invitee.Key).I32(invitee.Session.Profile()?.PcId ?? 1)
                .I64(expires).I32(0).ToArray()];
        }
        catch (ProtocolException) { return [Fail()]; }
    }

    /// <summary>0x3AD0 answer a dungeon-room invitation.</summary>
    List<byte[]> DungeonRoomInviteResponse(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInviteResponseRs).I32(result).I32(ticketId).ToArray();
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null)
            return [Reply(RoomFailed)];
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var roomKey = reader.I32();
            var accept = reader.U8() == InviteAccept;
            if (!social.DungeonRoomInvites.Remove(playerKey, out var pending) || pending.RoomKey != roomKey || pending.ExpiresMs < now)
                return [Reply(RoomFailed)];
            if (!social.DungeonRooms.TryGetValue(roomKey, out var room))
                return [Reply(RoomFailed)];
            room.PendingInvite.Remove(playerKey);
            if (!accept) return [Reply(0)];
            if (MyRoom is not null || room.Members.Count >= room.Limit)
                return [Reply(RoomFailed)];
            if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
                return [Reply(RoomFailed)];
            if (!RoomEntryAllowed(dungeon, now, out var inviteFailTicket))
                return [Reply(RoomFailed, inviteFailTicket)];
            return AddRoomMember(room, worldPlayer, now, [Reply(0)]);
        }
        catch (ProtocolException) { return [Reply(RoomFailed)]; }
    }

    List<byte[]> AddRoomMember(DungeonRoom room, WorldPlayer member, long now, List<byte[]> replies)
    {
        room.Members.Add(member);
        room.Ready.Remove(member.Key);
        var addW = PacketWriter.Opcode(DungeonRoomAddMemberNt);
        WriteDungeonRoomMember(addW, member, (byte)(room.Members.Count - 1), false);
        var added = addW.ToArray();
        BroadcastRoom(room, added, member);
        replies.Add(added);
        replies.Add(DungeonRoomInfoPacket(room, RoomInfoJoin));
        BroadcastRoomInfo(room, RoomInfoJoin, member);
        return replies;
    }

    /// <summary>0x3ADD leave the expedition waiting room.</summary>
    List<byte[]> DungeonRoomLeave()
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomLeaveRs).I32(result).ToArray();
        if (MyRoom is not { } room) return [Reply(RoomFailed)];
        return LeaveDungeonRoom(room, RoomInfoLeave, [Reply(0)]);
    }

    /// <summary>0x3AE9 leader removes a member.</summary>
    List<byte[]> DungeonRoomKick(byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomKickMemberRs).I32(result).ToArray();
        if (plain.Length < 10 || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        var targetDbId = BitConverter.ToInt64(plain, 2);
        var kicked = room.Members.FirstOrDefault(m => m.Session.DbId == targetDbId && m != leader);
        if (kicked is null) return [Reply(RoomFailed)];
        kicked.Send(PacketWriter.Opcode(DungeonRoomKickoutNt).Bool(false).ToArray());
        return LeaveDungeonRoom(room, RoomInfoKick, [Reply(0)], kicked);
    }

    /// <summary>0x3AD8 or 0x3A61: move everyone in the room into the same dungeon copy.</summary>
    List<byte[]> PartyDungeonEnter(long now) => DungeonRoomStart(now);

    List<byte[]> DungeonRoomStart(long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomStartRs).I32(result).ToArray();
        byte[] EnterReply(int result) => PacketWriter.Opcode(PartyDungeonEnterRs).I32(result).ToArray();
        if (MyRoom is not { Leader: var leader } room)
        {
            if (pendingPartyDungeonId != 0)
            {
                var solo = EnterDungeon(pendingPartyDungeonId, now);
                pendingPartyDungeonId = 0;
                return solo.Count > 0 ? [EnterReply(0), .. solo] : [EnterReply(RoomFailed)];
            }
            return [EnterReply(RoomFailed), Reply(RoomFailed)];
        }
        if (leader != worldPlayer) return [EnterReply(RoomFailed), Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
            return [EnterReply(RoomFailed), Reply(RoomFailed)];
        foreach (var member in room.Members)
            if (!member.Session.RoomEntryAllowed(dungeon, now, out _))
                return [EnterReply(RoomFailed), Reply(RoomFailed)];
        EnsurePartyFromRoom(room);
        var replies = new List<byte[]> { Reply(0), EnterReply(0) };
        var copyKey = room.RoomKey;
        foreach (var member in room.Members.ToList())
        {
            var moved = member.Session.EnterDungeon(room.ResolvedDungeonId, now, copyKey, consumeTicket: true);
            if (moved.Count == 0) return [EnterReply(RoomFailed), Reply(RoomFailed)];
            member.Send(moved);
            if (member == worldPlayer) replies.AddRange(moved);
        }
        DisbandDungeonRoom(room, RoomInfoDisband);
        return replies;
    }

    void EnsurePartyFromRoom(DungeonRoom room)
    {
        if (Social is not { } social) return;
        if (social.PartyOf(room.Leader) is { } existing && room.Members.All(existing.Members.Contains))
        {
            if (existing.Leader != room.Leader)
            {
                existing.Leader = room.Leader;
                foreach (var member in existing.Members) member.Send(SocialPackets.PartyNewLeader(room.Leader.Session.DbId));
            }
            return;
        }
        foreach (var member in room.Members)
            if (social.PartyOf(member) is { } other) DisbandParty(other, PartyUpdateReason.LeaveMemberBySystem);
        var party = new Party(social.NextId(), room.Leader) { JoinType = room.JoinType };
        party.Members.Clear();
        foreach (var member in room.Members) party.Members.Add(member);
        social.Parties[party.Key] = party;
        var info = SocialPackets.PartyInfo(new PartyInfo
        {
            Key = party.Key, LeaderDbId = party.Leader.Session.DbId, LimitMember = Party.Limit, JoinType = party.JoinType,
            Members = party.Members.Select((m, i) => m.Session.MemberInfo((byte)i)).ToList(),
        }, PartyUpdateReason.Create);
        foreach (var member in party.Members) member.Send(info);
    }

    List<byte[]> LeaveDungeonRoom(DungeonRoom room, byte reason, List<byte[]> replies, WorldPlayer? who = null)
    {
        var leaving = who ?? worldPlayer;
        if (leaving is null) return replies;
        room.Members.Remove(leaving);
        room.Ready.Remove(leaving.Key);
        room.PendingApply.Remove(leaving.Key);
        leaving.Send(PacketWriter.Opcode(DungeonRoomLeaveNt).I32(room.RoomKey).ToArray());
        if (room.Members.Count == 0)
        {
            DisbandDungeonRoom(room, reason);
            return replies;
        }
        if (room.Leader == leaving)
        {
            room.Leader = room.Members[0];
            BroadcastRoom(room, PacketWriter.Opcode(DungeonRoomNewLeaderNt).I64(room.Leader.Session.DbId).U8(0).ToArray());
        }
        BroadcastRoomInfo(room, reason);
        return replies;
    }

    void DisbandDungeonRoom(DungeonRoom room, byte reason)
    {
        if (Social is not { } social) return;
        var disband = PacketWriter.Opcode(DungeonRoomDisbandNt).U8(reason).ToArray();
        foreach (var member in room.Members.ToList()) member.Send(disband);
        social.DungeonRooms.Remove(room.RoomKey);
    }

    /// <summary>0x3AEF party leader asks to re-run the expedition (everyone must accept).</summary>
    List<byte[]> DungeonRoomRetry(long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomRetryRs).I32(result).I32(ticketId).ToArray();
        if (Social is not { } social || worldPlayer is null || Social.PartyOf(worldPlayer) is not { Leader: var leader } party || leader != worldPlayer)
            return [Reply(RoomFailed)];
        if (MapEventData.DungeonOfMap(worldMap) is not { Type: "Party" } dungeon)
            return [Reply(RoomFailed)];
        var dungeonId = MapEventData.DungeonIdOfMap(worldMap);
        foreach (var member in party.Members)
            if (!member.Session.RoomEntryAllowed(dungeon, now, out var ticketId, allowInCopy: true))
                return [Reply(RoomFailed, ticketId)];
        social.DungeonRetries.Remove(party.Key);
        var pending = new DungeonRetryPending(leader, dungeonId, checked((int)social.NextId()), now + RoomInviteMs);
        pending.Responses[leader.Key] = RetryAccept;
        social.DungeonRetries[party.Key] = pending;
        var replies = new List<byte[]> { Reply(0) };
        var askW = PacketWriter.Opcode(DungeonRoomAskRetryNt).I64(leader.Session.DbId);
        WriteInvitation(askW, Invitation());
        var ask = askW.I32(pending.CopyKey).I64(pending.ExpiresMs).ToArray();
        foreach (var member in party.Members)
        {
            if (member == leader) continue;
            member.Send(ask);
        }
        if (party.Members.Count == 1) return FinishDungeonRetry(party, pending, now, replies);
        return replies;
    }

    /// <summary>0x3AF2 answer an in-dungeon retry vote.</summary>
    List<byte[]> DungeonRoomRetryResponse(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomRetryResponseRs).I32(result).I32(ticketId).ToArray();
        if (plain.Length < 3 || Social is not { } social || worldPlayer is null || Social.PartyOf(worldPlayer) is not { } party)
            return [Reply(RoomFailed)];
        if (!social.DungeonRetries.TryGetValue(party.Key, out var pending) || pending.ExpiresMs < now)
            return [Reply(RoomFailed)];
        var accept = plain[2] == RetryAccept;
        pending.Responses[playerKey] = accept ? RetryAccept : RetryRefuse;
        var notice = PacketWriter.Opcode(DungeonRoomRetryResponseNt).I64(DbId).I32(accept ? RetryAccept : RetryRefuse).ToArray();
        foreach (var member in party.Members) member.Send(notice);
        if (!accept)
        {
            social.DungeonRetries.Remove(party.Key);
            var fail = PacketWriter.Opcode(DungeonRoomRetryFailNt).I32(RoomFailed).ToArray();
            foreach (var member in party.Members) member.Send(fail);
            return [Reply(0), notice, fail];
        }
        if (party.Members.Any(m => !pending.Responses.TryGetValue(m.Key, out var r) || r != RetryAccept))
            return [Reply(0), notice];
        return FinishDungeonRetry(party, pending, now, [Reply(0), notice]);
    }

    List<byte[]> FinishDungeonRetry(Party party, DungeonRetryPending pending, long now, List<byte[]> replies)
    {
        if (Social is { } social) social.DungeonRetries.Remove(party.Key);
        if (MapEventData.Dungeon(pending.DungeonId) is not { } dungeon) return replies;
        // A retry is a new run: the revives of a Hard Expedition start over (the Oct 28 2026 fix "resurrection count did not reset on retry").
        foreach (var member in party.Members) member.Session.copy?.Revives.Clear();
        foreach (var member in party.Members.ToList())
        {
            if (!member.Session.RoomEntryAllowed(dungeon, now, out _, allowInCopy: true))
                return [PacketWriter.Opcode(DungeonRoomRetryFailNt).I32(RoomFailed).ToArray()];
            var moved = member.Session.EnterDungeon(pending.DungeonId, now, pending.CopyKey, consumeTicket: true);
            if (moved.Count == 0)
                return [PacketWriter.Opcode(DungeonRoomRetryFailNt).I32(RoomFailed).ToArray()];
            member.Send(moved);
            if (member == worldPlayer) replies.AddRange(moved);
        }
        return replies;
    }

    /// <summary>Disconnect / lobby: leave waiting rooms cleanly.</summary>
    void LeaveDungeonRoomSocial(long now)
    {
        if (Social is { } social)
        {
            social.DungeonRoomInvites.Remove(playerKey);
            if (worldPlayer is not null && social.PartyOf(worldPlayer) is { } party) social.DungeonRetries.Remove(party.Key);
        }
        if (MyRoom is not { } room) return;
        var disconnect = PacketWriter.Opcode(DungeonRoomDisconnectMemberNt).I64(DbId).ToArray();
        var hadOthers = room.Members.Count > 1;
        if (room.Leader == worldPlayer)
            foreach (var (key, _) in room.PendingApply.ToList())
                World?.Online.GetValueOrDefault(key)?.Send(PacketWriter.Opcode(DungeonRoomApplyResponseNt).I32(room.RoomKey).U8(ApplyRefuse).ToArray());
        LeaveDungeonRoom(room, RoomInfoLeave, []);
        if (hadOthers && Social?.DungeonRooms.ContainsKey(room.RoomKey) == true)
            BroadcastRoom(room, disconnect);
    }
}
