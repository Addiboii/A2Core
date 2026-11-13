using Aion.Protocol.Native;

namespace Aion.World;

// Requests of the community and PvP menus that have no file of their own (feature/systems-2): one hook in HandleCoreAsync
// (after the legion's) and one in the session tick. Expedition: GameSession.Expedition.cs; Abyss: GameSession.Abyss.cs; legion:
// GameSession.Guild.cs / GameSession.Legion.cs; arena and battlefield: GameSession.Arena.cs; mail: GameSession.Mail.cs.
//
// Season: TakePrevSeasonRankingReward_RQ 0x8d59 {contents type u8} (catalogue 904 + 0x89d1, between the OBSERVED 0x8d57 and
//   0x8d58 MyPrevSeasonRanking_NT, which the official server sends empty "00" at every entry) -> RS 0x8d5a: answered with the
//   season rankings since feature/rank-system (GameSession.Ranking.cs).
//
// Shugo festival (FieldEvent.dat kind ShugoFesta, EventSchedule.dat 1101-1109 / 2101-2109). Client tables: every group runs
// every day at every full hour (24 times a day, ActiveTimeList 00:00..23:00; the table times are KST like the other schedules,
// which for whole hours changes nothing), each event Prepare for ReadyDuration (180 s: the entry window, "admission period"),
// then Progress for its ProgressTime (175-300 s). OBSERVED (official recordings 20261106-105448, -115817, -145442/-145451,
// 20261107-010322, -010735, -210132; 0x6000-0x6008 = catalogue 549-557 + 0x5ddb):
//   - FieldEventInfo_NT 0x6000 {varint count, entries} after the appearance at every entry: the player's race's 9 ShugoFesta
//     events that are in Prepare or Progress now ("00" outside the windows, 9 entries at 01:04:52, 4 at 01:07:36).
//   - FieldEventChanged_NT 0x6001 {entry} for each of them at HH:00 (Prepare), at open + 180 s (Progress) and at its end (gone).
//   - an entry: {mask u8 3, id varint, state u8, open i64, start i64, end i64}; Prepare carries (open, open, open + 180 s),
//     Progress (open, start, start + ProgressTime; Racing + 12 s: 312 s twice); gone: {mask 0, id, state 0, 2100-01-01 KST}.
//   - FieldEventEntranceTeleport_RS 0x6008 {result u16 0, event id varint} (11:01:11, TrapBoard 210901, in Prepare), then the
//     official server moves the player to a dungeon server (0x3647, 0x3616); ours teleports to the event's map.
// CANDIDATE: the request bodies (0x6002 ranking, 0x6007 entrance: the event id, varint or i32), the refusal outside Prepare
// (Telpo_InvalidFieldEventState, the client's "admission period has ended"), the Light events' order past 3, 2, 7, 8, the
// empty ranking reply, the DungeonState times inside the event map (aligned with the event's start and end). Not built: the
// Dimensional Invasion events (220101 OBSERVED at 12:30 for an Asmodian, none at 01:30 for an Elyos: which one runs is not
// known), the mini-game rules, scores and the reward mail (Post.dat 10000000 + event id).
public sealed partial class GameSession
{
    public const ushort TakePrevSeasonRankingRewardRq = 0x8d59, TakePrevSeasonRankingRewardRs = 0x8d5a;
    public const ushort FieldEventInfoNt = 0x6000, FieldEventChangedNt = 0x6001, FieldEventRankingRq = 0x6002, FieldEventRankingRs = 0x6003,
        FieldEventEntranceRq = 0x6007, FieldEventEntranceRs = 0x6008;
    /// <summary>EFieldEventStateType.</summary>
    public const byte FieldEventNone = 0, FieldEventPrepare = 1, FieldEventProgress = 2, FieldEventEnd = 3;
    /// <summary>The open time of an event that is gone (OBSERVED 0x03bb2ad57580: 2100-01-01 00:00 KST).</summary>
    public const long FieldEventNeverMs = 4_102_412_400_000;
    /// <summary>Racing runs 12 s past its ProgressTime (OBSERVED 312 s for 300, 210301 and 110301).</summary>
    public const long FieldEventRacingExtraMs = 12_000;
    /// <summary>The order the official server lists an Asmodian's events in (OBSERVED: group 3, 5, 1, 4, 8, 7, 2, 6, 9); the
    /// Elyos one is OBSERVED as 3, 2, 7, 8 for the four still running, the rest CANDIDATE.</summary>
    static readonly int[] DarkFestaOrder = [3, 5, 1, 4, 8, 7, 2, 6, 9], LightFestaOrder = [3, 2, 7, 8, 5, 1, 4, 6, 9];

    async Task<List<byte[]>?> Systems2RequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        await Task.CompletedTask;
        if (ExpeditionRequest(plain, now) is { } expedition) return expedition;
        if (AbyssRequest(plain, now) is { } abyss) return abyss;
        var op = Opcode(plain);
        switch (op)
        {
            case FieldEventRankingRq when plain.Length >= 3:
                return [FestivalRanking(FieldEventIdOf(plain), now)];   // the run's points (GameSession.FestivalGames.cs)
            case FieldEventEntranceRq when plain.Length >= 3:
                return FieldEventEntrance(FieldEventIdOf(plain), now);
        }
        return null;
    }

    /// <summary>Session tick: the week's Abyss Points saved when they changed; the festival's state changes.</summary>
    async Task<List<byte[]>> Systems2TickAsync(long now, CancellationToken ct)
    {
        await SaveAbyssWeekAsync(ct);
        return [.. FieldEventTick(now), .. await FestivalMailAsync(ct)];
    }

    /// <summary>A request's event id: i32 when the body is 4 bytes, else varint (the RS carries it as a varint, OBSERVED).</summary>
    static int FieldEventIdOf(byte[] plain)
    {
        if (plain.Length == 6) return BitConverter.ToInt32(plain, 2);
        int value = 0, shift = 0;
        for (var i = 2; i < plain.Length && shift < 35; i++, shift += 7)
        {
            value |= (plain[i] & 0x7f) << shift;
            if (plain[i] < 0x80) break;
        }
        return value;
    }

    static readonly Dictionary<string, Systems2Tables.EventSchedule?> scheduleOfGroup = [];

    static Systems2Tables.EventSchedule? ScheduleOf(string group)
    {
        lock (scheduleOfGroup)
        {
            if (!scheduleOfGroup.TryGetValue(group, out var schedule))
                scheduleOfGroup[group] = schedule = Systems2Tables.Embedded.EventSchedules.FirstOrDefault(s => s.Events.Any(x => x.Category == "FieldEvent" && x.Key == group));
            return schedule;
        }
    }

    /// <summary>An event's Progress length: ProgressTime, Racing 12 s more (OBSERVED).</summary>
    public static long FieldEventProgressMs(Systems2Tables.FieldEvent e) =>
        e.ProgressSeconds * 1000L + (e.Group.EndsWith("_Racing", StringComparison.Ordinal) ? FieldEventRacingExtraMs : 0);

    /// <summary>
    /// A field event's state at <paramref name="now"/> from EventSchedule.dat (the schedule whose event list names the event's
    /// group): the latest start at or before now on a listed day (KST, FieldBosses.ScheduleUtcOffsetHours); Prepare from the
    /// open for ReadyDuration, then Progress until <c>End</c>, then End. Start = open + ReadyDuration.
    /// </summary>
    public static (byte State, long Open, long Start, long End) FieldEventState(Systems2Tables.FieldEvent e, long now)
    {
        if (ScheduleOf(e.Group) is not { TimesMs.Count: > 0 } schedule) return (FieldEventNone, 0, 0, 0);
        const long offset = FieldBosses.ScheduleUtcOffsetHours * 3_600_000L;
        var kstDay = DateTimeOffset.FromUnixTimeMilliseconds(now + offset).UtcDateTime.Date;
        foreach (var back in new[] { 0, 1 })
        {
            var date = kstDay.AddDays(-back);
            if (!schedule.Days.Contains(date.DayOfWeek.ToString())) continue;
            var dayMs = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds() - offset;
            foreach (var t in schedule.TimesMs.OrderByDescending(t => t))
            {
                var open = dayMs + t;
                if (open > now) continue;
                var start = open + e.ReadySeconds * 1000L;
                var end = start + FieldEventProgressMs(e);
                return (now < start ? FieldEventPrepare : now < end ? FieldEventProgress : FieldEventEnd, open, start, end);
            }
        }
        return (FieldEventNone, 0, 0, 0);
    }

    /// <summary>The next open of the event's schedule after <paramref name="now"/> (KST days), 0 when it has none.</summary>
    public static long FieldEventNextOpen(Systems2Tables.FieldEvent e, long now)
    {
        if (ScheduleOf(e.Group) is not { TimesMs.Count: > 0 } schedule) return 0;
        const long offset = FieldBosses.ScheduleUtcOffsetHours * 3_600_000L;
        var kstDay = DateTimeOffset.FromUnixTimeMilliseconds(now + offset).UtcDateTime.Date;
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var date = kstDay.AddDays(ahead);
            if (!schedule.Days.Contains(date.DayOfWeek.ToString())) continue;
            var dayMs = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds() - offset;
            foreach (var t in schedule.TimesMs.OrderBy(t => t))
                if (dayMs + t > now) return dayMs + t;
        }
        return 0;
    }

    /// <summary>The festival events the official server lists for a race (FieldEvent kind ShugoFesta, ids race x 100000 + ...), in its order.</summary>
    public static IReadOnlyList<Systems2Tables.FieldEvent> FestaEventsOf(int race)
    {
        var order = race == 2 ? DarkFestaOrder : LightFestaOrder;
        return Systems2Tables.Embedded.FieldEvents.Where(e => e.Kind == "ShugoFesta" && e.Id / 100_000 == race)
            .OrderBy(e => Rank(Array.IndexOf(order, e.Id / 100 % 100))).ThenBy(e => e.Id).ToList();
        static int Rank(int index) => index >= 0 ? index : 99;
    }

    /// <summary>One FieldEventInfo entry: a running event {3, id, state, open, start, end}, a gone one {0, id, 0, never} (OBSERVED).</summary>
    static PacketWriter FieldEventEntry(PacketWriter w, Systems2Tables.FieldEvent e, (byte State, long Open, long Start, long End) run) =>
        run.State switch
        {
            FieldEventPrepare => w.U8(3).Varint(e.Id).U8(FieldEventPrepare).I64(run.Open).I64(run.Open).I64(run.Start),
            FieldEventProgress => w.U8(3).Varint(e.Id).U8(FieldEventProgress).I64(run.Open).I64(run.Start).I64(run.End),
            _ => w.U8(0).Varint(e.Id).U8(FieldEventNone).I64(FieldEventNeverMs),
        };

    /// <summary>FieldEventInfo_NT 0x6000 at the entry: the race's festival events running now (OBSERVED "00" when none).</summary>
    public static byte[] FieldEventInfo(int race, long now) =>
        FieldEventInfo(FestaEventsOf(race).Select(e => (e, FieldEventState(e, now))).Where(x => x.Item2.State is FieldEventPrepare or FieldEventProgress).ToList());

    /// <summary>FieldEventInfo_NT 0x6000 {varint count, entries} of these runs.</summary>
    public static byte[] FieldEventInfo(IReadOnlyList<(Systems2Tables.FieldEvent Event, (byte State, long Open, long Start, long End) Run)> running)
    {
        var w = PacketWriter.Opcode(FieldEventInfoNt).Varint(running.Count);
        foreach (var (e, run) in running) FieldEventEntry(w, e, run);
        return w.ToArray();
    }

    /// <summary>FieldEventChanged_NT 0x6001 {entry} (OBSERVED at the open, the start and the end of each event).</summary>
    public static byte[] FieldEventChanged(Systems2Tables.FieldEvent e, (byte State, long Open, long Start, long End) run) =>
        FieldEventEntry(PacketWriter.Opcode(FieldEventChangedNt), e, run).ToArray();

    /// <summary>What this session last told the client per festival event (state, open); null until the entry's list went out.</summary>
    Dictionary<int, (byte State, long Open)>? fieldEventsShown;

    /// <summary>At the entry (after the appearance): the list, and from now on the changes.</summary>
    byte[] FieldEventInfoAtEntry(long now)
    {
        fieldEventsShown = [];
        foreach (var e in FestaEventsOf(profileRace))
        {
            var run = FieldEventState(e, now);
            if (run.State is FieldEventPrepare or FieldEventProgress) fieldEventsShown[e.Id] = (run.State, run.Open);
        }
        return FieldEventInfo(profileRace, now);
    }

    /// <summary>The tick: FieldEventChanged_NT for each event whose state changed since the client heard of it.</summary>
    List<byte[]> FieldEventTick(long now)
    {
        if (fieldEventsShown is not { } shown) return [];
        List<byte[]>? changes = null;
        foreach (var e in FestaEventsOf(profileRace))
        {
            var run = FieldEventState(e, now);
            var running = run.State is FieldEventPrepare or FieldEventProgress;
            var known = shown.TryGetValue(e.Id, out var last);
            if (running ? known && last == (run.State, run.Open) : !known) continue;
            if (running) shown[e.Id] = (run.State, run.Open);
            else shown.Remove(e.Id);
            (changes ??= []).Add(FieldEventChanged(e, run));
        }
        return changes ?? [];
    }

    /// <summary>The festival event whose own map this is (FieldEvent.dat MapId), if any.</summary>
    public static Systems2Tables.FieldEvent? FieldEventOfMap(int mapId) => Systems2Tables.Embedded.FieldEvents.FirstOrDefault(e => e.Map == mapId);

    /// <summary>
    /// FieldEventEntranceTeleport_RQ {event id}: into a Direct-entrance (Shugo festival) event's map during its entry window
    /// (Prepare; staff at any time). RS 0x6008 {result u16, event id varint} (OBSERVED on success), then the move.
    /// </summary>
    List<byte[]> FieldEventEntrance(int eventId, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(FieldEventEntranceRs).U16(result).Varint(eventId).ToArray();
        if (Systems2Tables.Embedded.FieldEventOf(eventId) is not { Entrance: "Direct" } e) return [Reply(EResult.Telpo_InvalidFieldEvent)];
        // Event ids 1xxxxx are the Elyos (Light) groups, 2xxxxx the Asmodian (Dark) ones.
        if (eventId / 100_000 != profileRace) return [Reply(EResult.Telpo_NotFieldEventUser)];
        var (state, _, _, _) = FieldEventState(e, now);
        if (!IsAdmin && state != FieldEventPrepare)
        {
            Note($"field event {e.Name}: entry refused, state {state} (entry only while it prepares)");
            return [Reply(EResult.Telpo_InvalidFieldEventState)];
        }
        if (instanceKey != 0 || dead || resources is null) return [Reply(EResult.Telpo_InvalidFieldEventState)];
        var map = e.Map != 0 ? e.Map : e.EntranceMap;
        var points = data.MapPoints(map);
        var at = points.Where(p => p.Key.Contains("Respawn", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("OnYourMark", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault() ?? data.MapStart(map)?.Position;
        if (at is not { } point || !data.KnownMap(map)) return [Reply(EResult.Telpo_InvalidFieldEvent)];
        // Everyone entering this event in this hour plays in one copy (fix/shugo-games; before, each player had a copy of
        // their own); the way back is where the player came from (Dungeon.dat ExitType LastPosition: before, Poeta).
        var (_, open, _, _) = FieldEventState(e, now);
        instanceKey = FestivalInstanceKey(e.Id, open);
        instanceReturn = (worldMap, position ?? point);
        Note($"field event {e.Name}: entered map {map} (copy {instanceKey:x})");
        return [Reply(0), .. Teleport(map, point, now)];
    }

    /// <summary>
    /// A field event's NPC entry (EnterFieldEvent NPCs, the Interact-entrance events on the world maps) into an event map that
    /// runs on a schedule: only during its entry window (Prepare), staff at any time (CANDIDATE: the official refusal is unrecorded).
    /// </summary>
    bool FieldEventEntryClosed(int mapId, long now) =>
        !IsAdmin && FieldEventOfMap(mapId) is { } e && ScheduleOf(e.Group) is not null && FieldEventState(e, now).State != FieldEventPrepare;
}
