using System.Runtime.CompilerServices;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>One expedition room (DungeonRoomInfo): a leader's lobby for a party dungeon until its start.</summary>
public sealed class DungeonRoom(int key, WorldPlayer leader, int dungeon, int level, int race)
{
    /// <summary>OBSERVED: every official room lists five member slots (empty ones too), _limit_member 5.</summary>
    public const int Slots = 5;
    public int Key { get; } = key;
    public WorldPlayer Leader { get; set; } = leader;
    public int Dungeon { get; } = dungeon;
    public int Level { get; } = level;
    public int Race { get; } = race;
    public string Desc { get; set; } = "";
    public bool Public { get; set; } = true;
    /// <summary>EPartyJoinType: 1 Free, 2 Approval.</summary>
    public byte JoinType { get; set; } = 1;
    /// <summary>EDungeonState: 0 waiting in the room, 2 Process once started.</summary>
    public byte State { get; set; }
    /// <summary>The slots 1..5 in order: the player, the EPartyRole chosen, ready.</summary>
    public (WorldPlayer? Player, byte Role, bool Ready)[] Members { get; } = new (WorldPlayer?, byte, bool)[Slots];
    public IEnumerable<WorldPlayer> Players => Members.Where(m => m.Player is not null).Select(m => m.Player!);
    public int SlotOf(WorldPlayer p) => Array.FindIndex(Members, m => m.Player == p);
}

/// <summary>The rooms of one shared world (use under the world gate). Kept beside SocialState so the social code is untouched.</summary>
public sealed class DungeonRoomBoard
{
    static readonly ConditionalWeakTable<SocialState, DungeonRoomBoard> boards = [];
    public static DungeonRoomBoard Of(SocialState social) => boards.GetValue(social, _ => new DungeonRoomBoard());
    int lastKey = 0x0f8900;   // OBSERVED room keys run near 0x0f8900 (0x0f8922, 0x0f8ab5, ...)
    public Dictionary<int, DungeonRoom> Rooms { get; } = [];
    public int NextKey() => ++lastKey;
    public DungeonRoom? RoomOf(WorldPlayer p) => Rooms.Values.FirstOrDefault(r => r.SlotOf(p) >= 0);
}

/// <summary>One DungeonRoomMemberInfo slot as sent (an empty slot: DbId 0).</summary>
public sealed record RoomMemberWire(int Number, long DbId, string Nickname, int PcId, int Level, int EquipItemLevel, bool Ready, bool Login,
    int BornServer, int CurrentServer, byte Role, long CombatPower, long RebirthItems = 0, byte Mentoring = 1, byte Latency = 1, int ConquerorLevel = 0)
{
    public static RoomMemberWire Empty(int number) => new(number, 0, "", 0, 0, 0, false, false, 0, 0, 4, 0, 0, 0, 0);
}

/// <summary>One DungeonRoomInfo as sent.</summary>
public sealed record RoomWire(int Key, string Desc, int Dungeon, int Level, int Race, long Leader, bool Public, byte JoinType, byte State,
    IReadOnlyList<RoomMemberWire> Members, int Limit = DungeonRoom.Slots);

/// <summary>The room messages in the OBSERVED layout (see GameSession.DungeonRooms.cs).</summary>
public static class DungeonRoomPackets
{
    public static void WriteMember(PacketWriter w, RoomMemberWire m)
    {
        var mask = (m.ConquerorLevel != 0 ? 0x01 : 0) | (m.Ready ? 0x02 : 0) | (m.BornServer != 0 ? 0x04 : 0) | (m.CurrentServer != 0 ? 0x08 : 0)
            | (m.RebirthItems != 0 ? 0x10 : 0);
        w.U8(mask).U8(m.Number).I64(m.DbId).String(m.Nickname).I32(m.PcId).I32(m.Level);
        if (m.ConquerorLevel != 0) w.I32(m.ConquerorLevel);
        w.I32(m.EquipItemLevel);
        if (m.Ready) w.Bool(true);
        w.Bool(m.Login);
        if (m.BornServer != 0) w.U16(m.BornServer);
        if (m.CurrentServer != 0) w.U16(m.CurrentServer);
        w.U8(m.Role).I64(m.CombatPower).Varint(0);
        if (m.RebirthItems != 0) w.I64(m.RebirthItems);
        w.U8(m.Mentoring).U8(m.Latency);
    }

    /// <summary>A room; null: the OBSERVED my_room of a player in none (all zero, public set, join type 2).</summary>
    public static void WriteRoom(PacketWriter w, RoomWire? room)
    {
        if (room is null) { w.I32(0).String("").U8(0).I32(0).U8(0).U8(0).I64(0).Bool(true).U8(2).U8(0).Varint(0).Varint(0); return; }
        w.I32(room.Key).String(room.Desc).U8(room.Limit).I32(room.Dungeon).U8(room.Level).U8(room.Race).I64(room.Leader)
            .Bool(room.Public).U8(room.JoinType).U8(room.State).Varint(room.Members.Count);
        foreach (var m in room.Members) WriteMember(w, m);
        w.Varint(0);
    }

    public static byte[] RoomInfo(RoomWire room, byte reason)
    {
        var w = PacketWriter.Opcode(GameSession.DungeonRoomInfoNt);
        WriteRoom(w, room);
        return w.U8(reason).ToArray();
    }

    public static byte[] AddMember(RoomMemberWire m)
    {
        var w = PacketWriter.Opcode(GameSession.DungeonRoomAddMemberNt);
        WriteMember(w, m);
        return w.ToArray();
    }

    public static byte[] List(int dungeon, RoomWire? mine, IReadOnlyList<RoomWire> rooms)
    {
        var w = PacketWriter.Opcode(GameSession.DungeonRoomListRs).U16(0).I32(dungeon);
        WriteRoom(w, mine);
        w.Varint(rooms.Count);
        foreach (var r in rooms) WriteRoom(w, r);
        return w.ToArray();
    }
}

// The expedition window's rooms (DungeonRoom*, the 0x97xx run = usmap 0x3ac1 + 0x5c3f). Transcendence = the Hard party challenge
// dungeons (Dungeon.dat PartyChallengeTier, PartyDungeonLevel.dat: a group's levels 1-4, e.g. Arkanis_G_Group level 1 = 600063).
//
// Wire layout OBSERVED (recording 20261106-115817: 42 room lists 0x9701, 6 room messages 0x9702, 2 AddMember 0x970b; all parse to
// the byte with these rules):
//   DungeonRoomInfo: room key i32 | desc str | limit u8 (5) | dungeon i32 | dungeon level u8 | ERace u8 | leader dbid i64 | public
//     bool | EPartyJoinType u8 | EDungeonState u8 | members varint + DungeonRoomMemberInfo | affix list varint + i32.
//   DungeonRoomMemberInfo: presence mask u8 (bit 0 conqueror level i32, bit 1 ready, bit 2 born server u16, bit 3 current server
//     u16, bit 4 rebirth item count i64: an optional field is present only when not its default) | slot number u8 | dbid i64 |
//     nickname str | pc id i32 | level i32 | [conqueror level] | equip item level i32 | [ready bool, only when true] | login bool
//     | [born server] | [current server] | EPartyRole u8 | combat power i64 | contents tickets map varint | [rebirth count] |
//     EMentoringRole u8 | ENetworkLatencyState u8. An empty slot: mask 0, its number, zeros, role 4 (All).
//   Bools are packed across records (PacketWriter.Bool): the room's public bit opens a byte, the members' bools take its next bits.
//   List_RS: u16 result | dungeon i32 | my room (an empty one when in none: public, join 2) | rooms varint + rooms.
//   RoomInfo_NT: room | EDungeonRoomInfoUpdateReason u8 (2 Join, 4 LeaveMember OBSERVED). AddMember_NT: one member record.
//   InstantJoin_RS `0000 00000000`, Ready_RS `0000 01`, Ready_NT {dbid i64, ready}, Leave_RS `0000`, Kickout_NT `00` OBSERVED.
// Served: List, Create, Join (free rooms), InstantJoin (a free public room of the dungeon with a slot, else a new room of one's
// own), Ready, Start (the leader, every other member ready: all of them move into one copy of the level's dungeon), Leave, Kick,
// EditSetting, Refresh. CANDIDATE (never recorded): Create_RS/Join_RS/Start_RS/Kick_RS/EditSetting_RS bodies (usmap: result, ticket),
// the reasons sent with Create (1) / Kick (5) / start (10 DungeonStateUpdate) / setting change (13), NewLeader_NT. Not served:
// approval rooms' applications, invitations, slot moves, retries. The Transcendence tickets (Contents_Ticket_Party_*) are not
// linked to the dungeons in any table we have and their deduction was never recorded: not taken.
public sealed partial class GameSession
{
    public const ushort DungeonRoomListRq = 0x9700, DungeonRoomListRs = 0x9701, DungeonRoomInfoNt = 0x9702, DungeonRoomCreateRq = 0x9703,
        DungeonRoomCreateRs = 0x9704, DungeonRoomJoinRq = 0x9705, DungeonRoomJoinRs = 0x9706, DungeonRoomAddMemberNt = 0x970b,
        DungeonRoomInstantJoinRq = 0x9712, DungeonRoomInstantJoinRs = 0x9713, DungeonRoomReadyRq = 0x9714, DungeonRoomReadyRs = 0x9715,
        DungeonRoomReadyNt = 0x9716, DungeonRoomStartRq = 0x9717, DungeonRoomStartRs = 0x9718, DungeonRoomNewLeaderNt = 0x9719,
        DungeonRoomDisbandNt = 0x971a, DungeonRoomLeaveRq = 0x971c, DungeonRoomLeaveRs = 0x971d, DungeonRoomEditRq = 0x9720, DungeonRoomEditRs = 0x9721,
        DungeonRoomKickRq = 0x9728, DungeonRoomKickRs = 0x9729, DungeonRoomKickoutNt = 0x972a, DungeonRoomRefreshRq = 0x9735, DungeonRoomRefreshRs = 0x9736;
    /// <summary>EDungeonRoomInfoUpdateReason.</summary>
    public const byte RoomReasonCreate = 1, RoomReasonJoin = 2, RoomReasonLeave = 4, RoomReasonKick = 5, RoomReasonState = 10, RoomReasonUpdate = 13;
    const byte RoleAll = 4;

    DungeonRoomBoard? soloRooms;
    WorldPlayer? soloRoomPlayer;
    /// <summary>The world's rooms; a session outside a shared world (development, checks) keeps a board of its own.</summary>
    DungeonRoomBoard RoomBoard => Social is { } s ? DungeonRoomBoard.Of(s) : soloRooms ??= new DungeonRoomBoard();
    /// <summary>The player as rooms know it (outside a shared world: a stand-in of this session).</summary>
    WorldPlayer? RoomPlayer => worldPlayer ?? (resources is null ? null : soloRoomPlayer ??= new WorldPlayer(this, playerKey) { Appear = () => [] });

    /// <summary>The dungeon a room request names: a party challenge group's level (the request carries the group's first dungeon), else the dungeon itself.</summary>
    static (int Dungeon, int ItemLevel)? RoomDungeon(int dungeonId, int roomLevel) =>
        Tickets.PartyGroupOf(dungeonId) is { } group ? Tickets.PartyLevel(group, Math.Max(1, roomLevel))
        : MapEventData.Dungeon(dungeonId) is { Type: "Party" or "Raid" } ? (dungeonId, 0) : null;

    List<byte[]>? DungeonRoomRequest(byte[] plain, long now)
    {
        var opcode = Opcode(plain);
        if (opcode is < DungeonRoomListRq or > DungeonRoomRefreshRs || resources is null || RoomPlayer is not { } me) return null;
        var board = RoomBoard;
        Prune(board);
        try
        {
            return opcode switch
            {
                DungeonRoomListRq when plain.Length >= 6 => [RoomList(board, BitConverter.ToInt32(plain, 2))],
                DungeonRoomCreateRq => RoomCreate(board, plain),
                DungeonRoomJoinRq when plain.Length >= 6 => RoomJoin(board, plain),
                DungeonRoomInstantJoinRq => RoomInstantJoin(board, plain),
                DungeonRoomReadyRq when plain.Length >= 3 => RoomReady(board, plain[2] != 0),
                DungeonRoomStartRq => RoomStart(board, now),
                DungeonRoomLeaveRq => RoomLeave(board, me, [PacketWriter.Opcode(DungeonRoomLeaveRs).U16(0).ToArray()], RoomReasonLeave),
                DungeonRoomKickRq when plain.Length >= 10 => RoomKick(board, BitConverter.ToInt64(plain, 2)),
                DungeonRoomEditRq => RoomEdit(board, plain),
                DungeonRoomRefreshRq => [PacketWriter.Opcode(DungeonRoomRefreshRs).U16(0).ToArray(), .. board.RoomOf(me) is { } mine ? [RoomInfo(mine, 0)] : Array.Empty<byte[]>()],
                _ => null,
            };
        }
        catch (ProtocolException) { return []; }
    }

    /// <summary>Members who left the world (disconnect, lobby) are dropped from their rooms; empty rooms go.</summary>
    void Prune(DungeonRoomBoard board)
    {
        if (World is not { } world) return;
        foreach (var room in board.Rooms.Values.ToList())
            foreach (var p in room.Players.ToList())
                if (!world.Online.ContainsKey(p.Key) || world.Online[p.Key] != p) LeaveRoom(board, room, p, RoomReasonLeave);
    }

    // ---------- Records ----------

    /// <summary>A member slot as sent: login, this host's server, no conqueror level, no rebirth items.</summary>
    static RoomMemberWire RoomMember(DungeonRoom room, int slot)
    {
        var (p, role, ready) = room.Members[slot];
        if (p is null) return RoomMemberWire.Empty(slot + 1);
        var s = p.Session;
        return new RoomMemberWire(slot + 1, s.DbId, s.StoredName, s.Profile()?.PcId ?? 1, s.level, s.EquipItemLevel(), ready && p != room.Leader, true,
            s.ServerId, s.ServerId, role, s.CombatPower());
    }

    static RoomWire Wire(DungeonRoom room) => new(room.Key, room.Desc, room.Dungeon, room.Level, room.Race, room.Leader.Session.DbId, room.Public,
        room.JoinType, room.State, [.. Enumerable.Range(0, DungeonRoom.Slots).Select(i => RoomMember(room, i))]);

    static byte[] RoomInfo(DungeonRoom room, byte reason) => DungeonRoomPackets.RoomInfo(Wire(room), reason);

    byte[] RoomList(DungeonRoomBoard board, int dungeon) =>
        DungeonRoomPackets.List(dungeon, board.RoomOf(RoomPlayer!) is { } mine ? Wire(mine) : null,
            [.. board.Rooms.Values.Where(r => r.Dungeon == dungeon && r.Public && r.State == 0).OrderBy(r => r.Key).Take(100).Select(Wire)]);

    static byte[] AddMember(DungeonRoom room, WorldPlayer p) => DungeonRoomPackets.AddMember(RoomMember(room, room.SlotOf(p)));

    // ---------- Requests ----------

    /// <summary>A room for the request's dungeon: the dungeon must be a served party dungeon (a challenge group's level or a Party/Raid dungeon).</summary>
    int RoomRefusal(DungeonRoomBoard board, int dungeon, int level)
    {
        if (board.RoomOf(RoomPlayer!) is not null) return EResult.PartyDungeonRoom_Fail_AlreadyInRoom;
        if (RoomDungeon(dungeon, level) is not { } target || MapEventData.Dungeon(target.Dungeon) is null) return EResult.PartyDungeonRoom_Fail_InvalidPartyDungeonLevel;
        if (reloading || dead || instanceKey != 0) return EResult.PartyDungeonRoom_Fail_Playing;
        if (!IsAdmin && Store is not null && target.ItemLevel > 0 && EquipItemLevel() < target.ItemLevel) return EResult.PartyDungeonRoom_Fail_NotEnoughEquipItemLevel;
        return 0;
    }

    DungeonRoom OpenRoom(DungeonRoomBoard board, int dungeon, int level, int race, byte role)
    {
        var room = new DungeonRoom(board.NextKey(), RoomPlayer!, dungeon, level, race is >= 1 and <= 3 ? race : 3);
        room.Members[0] = (RoomPlayer, role, false);
        board.Rooms[room.Key] = room;
        return room;
    }

    /// <summary>DungeonRoomCreate_RQ {dungeon i32, level u8, ERace u8, desc str, public bool, join type u8, wanted roles map, send invite bool, affixes}.</summary>
    List<byte[]> RoomCreate(DungeonRoomBoard board, byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var dungeon = r.I32();
        var level = r.U8();
        var race = r.Remaining > 0 ? r.U8() : 3;
        var desc = "";
        bool isPublic = true;
        byte join = 1;
        try
        {
            desc = r.String();
            isPublic = r.Bool();
            join = r.U8();
        }
        catch (ProtocolException) { }   // a shorter body (our trace: dungeon and level only) keeps the defaults
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomCreateRs).U16(result).I32(0).ToArray();
        if (desc.Length > 40) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidDescLength)];
        if (RoomRefusal(board, dungeon, level) is var refused and not 0) return [Reply(refused)];
        var room = OpenRoom(board, dungeon, level, race, RoleAll);
        room.Desc = desc;
        room.Public = isPublic;
        room.JoinType = join is 1 or 2 ? join : (byte)1;
        Note($"dungeon room {room.Key}: created for {dungeon} level {level}");
        return [Reply(0), RoomInfo(room, RoomReasonCreate)];
    }

    /// <summary>Into a room's first free slot: the joiner gets the room, the others the new member.</summary>
    List<byte[]> EnterRoom(DungeonRoom room, byte role, ushort reply, Func<int, byte[]> replyOf)
    {
        var slot = Array.FindIndex(room.Members, m => m.Player is null);
        if (slot < 0) return [replyOf(EResult.PartyDungeonRoom_Fail_FullRoom)];
        room.Members[slot] = (RoomPlayer, role is >= 1 and <= 4 ? role : RoleAll, false);
        var added = AddMember(room, RoomPlayer!);
        foreach (var other in room.Players.Where(p => p != RoomPlayer)) other.Send(added);
        Note($"dungeon room {room.Key}: joined slot {slot + 1}");
        return [replyOf(0), RoomInfo(room, RoomReasonJoin)];
    }

    /// <summary>DungeonRoomJoin_RQ {room key i32, EPartyRole u8} -> Join_RS {result, join type u8, ticket i32} (CANDIDATE).</summary>
    List<byte[]> RoomJoin(DungeonRoomBoard board, byte[] plain)
    {
        var key = BitConverter.ToInt32(plain, 2);
        var role = plain.Length > 6 ? plain[6] : RoleAll;
        byte join = board.Rooms.TryGetValue(key, out var found) ? found.JoinType : (byte)0;
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomJoinRs).U16(result).U8(join).I32(0).ToArray();
        if (found is null) return [Reply(EResult.PartyDungeonRoom_Fail_NotFoundPartyRoom)];
        if (RoomRefusal(board, found.Dungeon, found.Level) is var refused and not 0) return [Reply(refused)];
        if (found.State != 0) return [Reply(EResult.PartyDungeonRoom_Fail_AlreadyStarted)];
        if (!found.Public) return [Reply(EResult.PartyDungeonRoom_Fail_CannotJoinPrivateRoom)];
        if (found.JoinType != 1) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidJoinType)];
        return EnterRoom(found, role, DungeonRoomJoinRs, Reply);
    }

    /// <summary>DungeonRoomInstantJoin_RQ {dungeon i32, roles (varint count, u8 each), level u8, race u8} (our trace `FF270900 01 02 01 03`):
    /// the OBSERVED reply `0000 00000000`, then the room (reason Join). A free public room of the dungeon with a slot, else a new one.</summary>
    List<byte[]> RoomInstantJoin(DungeonRoomBoard board, byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomInstantJoinRs).U16(result).I32(0).ToArray();
        if (plain.Length < 9) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidPartyDungeonId)];
        var dungeon = BitConverter.ToInt32(plain, 2);
        var roles = plain[6];
        var levelAt = 7 + roles;
        if (levelAt >= plain.Length) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidPartyDungeonId)];
        var role = roles > 0 ? plain[7] : RoleAll;
        var level = plain[levelAt];
        var race = levelAt + 1 < plain.Length ? plain[levelAt + 1] : 3;
        if (RoomRefusal(board, dungeon, level) is var refused and not 0) return [Reply(refused)];
        var open = board.Rooms.Values.Where(r => r.Dungeon == dungeon && r.Level == level && r.Public && r.JoinType == 1 && r.State == 0
            && r.Members.Any(m => m.Player is null)).OrderBy(r => r.Key).FirstOrDefault();
        if (open is not null) return EnterRoom(open, role, DungeonRoomInstantJoinRs, Reply);
        var room = OpenRoom(board, dungeon, level, race, role is >= 1 and <= 4 ? role : RoleAll);
        Note($"dungeon room {room.Key}: quick join opened a room for {dungeon} level {level}");
        return [Reply(0), RoomInfo(room, RoomReasonJoin)];
    }

    /// <summary>DungeonRoomReady_RQ {ready}: the OBSERVED reply `0000 | ready`; the other members get Ready_NT {dbid, ready}.</summary>
    List<byte[]> RoomReady(DungeonRoomBoard board, bool ready)
    {
        if (board.RoomOf(RoomPlayer!) is not { } room) return [PacketWriter.Opcode(DungeonRoomReadyRs).U16(EResult.PartyDungeonRoom_Fail_NotARoomMember).Bool(ready).ToArray()];
        var slot = room.SlotOf(RoomPlayer!);
        room.Members[slot] = room.Members[slot] with { Ready = ready };
        var notice = PacketWriter.Opcode(DungeonRoomReadyNt).I64(DbId).Bool(ready).ToArray();
        foreach (var other in room.Players.Where(p => p != RoomPlayer)) other.Send(notice);
        return [PacketWriter.Opcode(DungeonRoomReadyRs).U16(0).Bool(ready).ToArray()];
    }

    /// <summary>DungeonRoomStart_RQ (the leader, every other member ready): Start_RS {0}, the room's state Process, then every member
    /// moves into one copy of the level's dungeon (the leader's; the others through their own loops).</summary>
    List<byte[]> RoomStart(DungeonRoomBoard board, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomStartRs).U16(result).ToArray();
        if (board.RoomOf(RoomPlayer!) is not { } room) return [Reply(EResult.PartyDungeonRoom_Fail_NotARoomMember)];
        if (room.Leader != RoomPlayer) return [Reply(EResult.PartyDungeonRoom_Fail_YouAreNotLeader)];
        if (room.State != 0) return [Reply(EResult.PartyDungeonRoom_Fail_AlreadyStarted)];
        if (room.Members.Any(m => m.Player is not null && m.Player != RoomPlayer && !m.Ready)) return [Reply(EResult.PartyDungeonRoom_Fail_NotReadyAll)];
        if (RoomDungeon(room.Dungeon, room.Level) is not { } target) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidPartyDungeonLevel)];
        if (reloading || dead || instanceKey != 0) return [Reply(EResult.PartyDungeonRoom_Fail_Playing)];
        var moved = EnterDungeon(target.Dungeon, now);
        if (moved.Count == 0) return [Reply(EResult.PartyDungeonRoom_Fail_NotCreateMap)];
        room.State = 2;
        var copyKey = instanceKey;
        var started = RoomInfo(room, RoomReasonState);
        foreach (var other in room.Players.Where(p => p != RoomPlayer).ToList())
        {
            other.Send(started);
            var session = other.Session;
            _ = session.InvokeAsync(at => (true, session.instanceKey == 0 ? session.EnterDungeon(target.Dungeon, at, copyKey) : []));
        }
        Note($"dungeon room {room.Key}: started -> dungeon {target.Dungeon} ({room.Players.Count()} players)");
        return [Reply(0), started, .. moved];
    }

    /// <summary>A member out of the room: the others see the room again (the reason), a leaving leader hands the room to the next member, an empty room goes.</summary>
    List<byte[]> LeaveRoom(DungeonRoomBoard board, DungeonRoom room, WorldPlayer who, byte reason)
    {
        var slot = room.SlotOf(who);
        if (slot < 0) return [];
        room.Members[slot] = (null, RoleAll, false);
        if (!room.Players.Any()) { board.Rooms.Remove(room.Key); return []; }
        if (room.Leader == who)
        {
            room.Leader = room.Players.First();
            var leaderSlot = room.SlotOf(room.Leader);
            room.Members[leaderSlot] = room.Members[leaderSlot] with { Ready = false };
            var newLeader = PacketWriter.Opcode(DungeonRoomNewLeaderNt).I64(room.Leader.Session.DbId).U8(1).ToArray();
            foreach (var p in room.Players) p.Send(newLeader);
        }
        var info = RoomInfo(room, reason);
        foreach (var p in room.Players) p.Send(info);
        return [];
    }

    List<byte[]> RoomLeave(DungeonRoomBoard board, WorldPlayer who, List<byte[]> replies, byte reason)
    {
        if (board.RoomOf(who) is { } room) LeaveRoom(board, room, who, reason);
        return replies;
    }

    /// <summary>DungeonRoomKickMember_RQ {target dbid i64} (the leader): Kick_RS {0}; the target gets the OBSERVED Kickout_NT `00`.</summary>
    List<byte[]> RoomKick(DungeonRoomBoard board, long target)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomKickRs).U16(result).ToArray();
        if (board.RoomOf(RoomPlayer!) is not { } room) return [Reply(EResult.PartyDungeonRoom_Fail_NotARoomMember)];
        if (room.Leader != RoomPlayer) return [Reply(EResult.PartyDungeonRoom_Fail_YouAreNotLeader)];
        if (room.Players.FirstOrDefault(p => p.Session.DbId == target && p != RoomPlayer) is not { } kicked) return [Reply(EResult.DungeonRoom_Fail_NotDungeonRoomMember)];
        kicked.Send(PacketWriter.Opcode(DungeonRoomKickoutNt).Bool(false).ToArray());
        LeaveRoom(board, room, kicked, RoomReasonKick);
        return [Reply(0)];
    }

    /// <summary>DungeonRoomEditSetting_RQ {desc str, public bool, join type u8, wanted roles map, affixes} (the leader): RS {0}, the room again (reason RoomInfoUpdate).</summary>
    List<byte[]> RoomEdit(DungeonRoomBoard board, byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomEditRs).U16(result).ToArray();
        if (board.RoomOf(RoomPlayer!) is not { } room) return [Reply(EResult.PartyDungeonRoom_Fail_NotARoomMember)];
        if (room.Leader != RoomPlayer) return [Reply(EResult.PartyDungeonRoom_Fail_YouAreNotLeader)];
        var r = new PacketReader(plain) { Offset = 2 };
        var desc = r.String();
        if (desc.Length > 40) return [Reply(EResult.PartyDungeonRoom_Fail_InvalidDescLength)];
        room.Desc = desc;
        room.Public = r.Bool();
        var join = r.U8();
        room.JoinType = join is 1 or 2 ? join : room.JoinType;
        var info = RoomInfo(room, RoomReasonUpdate);
        foreach (var other in room.Players.Where(p => p != RoomPlayer)) other.Send(info);
        return [Reply(0), info];
    }
}
