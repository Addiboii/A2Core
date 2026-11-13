using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The field bosses of the world map's Exploration > Field Boss tab (client WorldMapFieldNamed + PeriodSpawn, tools/data/field-bosses.json
/// from tools/field_boss_data.py): 85 bosses on maps 20 / 22 (Abyss), 1010 / 1011 (Elyos), 1110 / 1111 (Asmodians), each a MapData
/// spawner of world-npcs.json. "Basic" bosses respawn like any monster; "PeriodSpawn" ones (the 12 Abyss bosses) appear only at their
/// PeriodSpawn times: Day at a time of day on listed weekdays (21:00 Sun+Fri, 21:30 Mon+Thu+Sat), Cycle every Cycle minutes from a time
/// of day (M_AR1_BeritraD_01_001: every 3 h from 01:00, one of its 6 MapData points). Times are KST like the shop and ticket resets
/// (CANDIDATE). A killed period boss stays away until its next time; one still alive at that time is not doubled (CANDIDATE).
/// </summary>
public static class FieldBosses
{
    /// <summary>One WorldMapFieldNamed row: the map list's data id, its map, names and the PeriodSpawn id (0 Basic).</summary>
    public sealed record Entry(int Id, int Map, string Name, string Spawner, int Period);

    /// <summary>A PeriodSpawn row: Cycle or Day, the time of day in seconds (KST), the cycle in minutes, weekdays Sunday..Saturday.</summary>
    public sealed record Schedule(int Id, string Name, string Type, int Map, string Spawner, int TimeSeconds, int CycleMinutes, bool[] Days);

    /// <summary>The table times are Korean (UTC+9), as the shop / ticket resets (GlobalSetting 57600 s = 16:00 KST).</summary>
    public const int ScheduleUtcOffsetHours = 9;
    const long DayMs = 86_400_000;

    sealed record FileData(
        [property: JsonPropertyName("named")] List<JsonElement[]> Named,
        [property: JsonPropertyName("periods")] Dictionary<string, JsonElement[]> Periods);

    sealed record Tables(IReadOnlyList<Entry> All, Dictionary<int, List<Entry>> ByMap, Dictionary<int, Schedule> Schedules);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(FieldBosses).Assembly.GetManifestResourceStream("field-bosses.json")
            ?? throw new InvalidOperationException("Missing embedded game data field-bosses.json");
        var file = JsonSerializer.Deserialize<FileData>(stream) ?? throw new InvalidOperationException("Empty game data field-bosses.json");
        var all = file.Named.Select(n => new Entry(n[0].GetInt32(), n[1].GetInt32(), n[2].GetString()!, n[3].GetString()!, n[4].GetInt32())).ToList();
        var schedules = file.Periods.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p =>
        {
            var v = p.Value;
            var days = v[6].GetString()!;
            if (days.Length != 7) throw new InvalidDataException($"field-bosses.json: period {p.Key} days {days}");
            return new Schedule(int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), v[0].GetString()!, v[1].GetString()!, v[2].GetInt32(),
                v[3].GetString()!, v[4].GetInt32(), v[5].GetInt32(), days.Select(c => c == '1').ToArray());
        });
        foreach (var e in all)
            if (e.Period != 0 && !schedules.ContainsKey(e.Period)) throw new InvalidDataException($"field-bosses.json: {e.Name} names missing period {e.Period}");
        return new Tables(all, all.GroupBy(e => e.Map).ToDictionary(g => g.Key, g => g.ToList()), schedules);
    });

    public static IReadOnlyList<Entry> All => tables.Value.All;

    /// <summary>The field bosses listed for a map (WorldMapFieldNamed order by id).</summary>
    public static IReadOnlyList<Entry> On(int map) => tables.Value.ByMap.TryGetValue(map, out var list) ? list : [];

    /// <summary>The field boss whose spawner a map NPC came from, or null.</summary>
    public static Entry? OfSpawner(int map, string spawner) =>
        tables.Value.ByMap.TryGetValue(map, out var list) ? list.FirstOrDefault(e => string.Equals(e.Spawner, spawner, StringComparison.OrdinalIgnoreCase)) : null;

    public static Schedule? ScheduleOf(Entry e) => e.Period != 0 ? tables.Value.Schedules.GetValueOrDefault(e.Period) : null;

    /// <summary>The spawn times of one KST day (Unix ms, ascending): Day once, Cycle from the time every cycle until the day ends.</summary>
    static IEnumerable<long> TimesOn(Schedule s, DateTime kstDate)
    {
        if (!s.Days[(int)kstDate.DayOfWeek]) yield break;
        var dayStart = new DateTimeOffset(kstDate.Date, TimeSpan.Zero).ToUnixTimeMilliseconds() - ScheduleUtcOffsetHours * 3_600_000L;
        if (s.Type == "Cycle" && s.CycleMinutes > 0)
            for (long t = s.TimeSeconds * 1000L; t < DayMs; t += s.CycleMinutes * 60_000L) yield return dayStart + t;
        else yield return dayStart + s.TimeSeconds * 1000L;
    }

    static DateTime KstDate(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms + ScheduleUtcOffsetHours * 3_600_000L).UtcDateTime.Date;

    /// <summary>The latest scheduled spawn at or before <paramref name="now"/> (within 8 days), or null.</summary>
    public static long? LastSpawn(Schedule s, long now)
    {
        var today = KstDate(now);
        for (var back = 0; back <= 8; back++)
            if (TimesOn(s, today.AddDays(-back)).Where(t => t <= now).DefaultIfEmpty(long.MinValue).Max() is var t && t != long.MinValue) return t;
        return null;
    }

    /// <summary>The first scheduled spawn after <paramref name="now"/> (within 8 days), or null.</summary>
    public static long? NextSpawn(Schedule s, long now)
    {
        var today = KstDate(now);
        for (var ahead = 0; ahead <= 8; ahead++)
            if (TimesOn(s, today.AddDays(ahead)).Where(t => t > now).DefaultIfEmpty(long.MaxValue).Min() is var t && t != long.MaxValue) return t;
        return null;
    }

    /// <summary>Which of a period spawner's MapData points the spawn at <paramref name="spawnAt"/> uses (CANDIDATE: in turn, by minute).</summary>
    public static int PointFor(long spawnAt, int count) => count <= 1 ? 0 : (int)((spawnAt / 60_000 % count + count) % count);

    /// <summary>
    /// WorldMapContentsInfo_RS 0x8dd2 (usmap DevPacketData_GameServer_WorldMapContentsInfo_RS, catalogue 1025 + 0x89d1): result u16, map i32,
    /// field named list {data id i32, pos Vec3, is spawned u8, respawn time i64 (Unix ms, 0 while spawned)}, then the boss, rift and
    /// object lists (none here). CANDIDATE: never recorded (the official client asks when the world map opens; no reply was captured).
    /// </summary>
    public static byte[] ContentsInfo(int map, IReadOnlyList<(int DataId, Vector3d Position, bool Spawned, long RespawnAt)> named, int result = 0)
    {
        var w = PacketWriter.Opcode(GameSession.WorldMapContentsInfoRs).U16(result).I32(map).Varint(named.Count);
        foreach (var (id, at, spawned, respawn) in named) w.I32(id).Vec3(at).U8(spawned ? 1 : 0).I64(spawned ? 0 : respawn);
        return w.Varint(0).Varint(0).Varint(0).ToArray();
    }
}

/// <summary>
/// The field bosses' kills, shared by every map of a host (SharedWorld.FieldBosses): the last kill per boss and when it comes back,
/// kept in game.field_boss_kills (migration 091) so a restart neither revives a killed period boss nor forgets a respawn time.
/// Thread-safe (sessions of different maps tick in parallel).
/// </summary>
public sealed class FieldBossState
{
    readonly object gate = new();
    readonly Dictionary<(int Map, int Id), (long KilledAt, long RespawnAt)> kills = [];
    readonly System.Collections.Concurrent.ConcurrentQueue<(int Map, int Id, long KilledAt, long RespawnAt)> unsaved = new();
    int loading;

    /// <summary>The stored kills were read (or there is no store to read them from).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Claims the one load of the stored kills (true for the first caller only).</summary>
    public bool BeginLoad() => !Loaded && Interlocked.Exchange(ref loading, 1) == 0;

    public void Load(IEnumerable<(int Map, int Id, long KilledAt, long RespawnAt)> rows)
    {
        lock (gate)
        {
            foreach (var (map, id, killed, respawn) in rows)
                if (!kills.TryGetValue((map, id), out var known) || known.KilledAt < killed) kills[(map, id)] = (killed, respawn);
            Loaded = true;
        }
    }

    /// <summary>A failed load is retried by the next entry.</summary>
    public void LoadFailed() => Interlocked.Exchange(ref loading, 0);

    public void Killed(FieldBosses.Entry e, long killedAt, long respawnAt)
    {
        lock (gate) kills[(e.Map, e.Id)] = (killedAt, respawnAt);
        unsaved.Enqueue((e.Map, e.Id, killedAt, respawnAt));
    }

    public bool TakeUnsaved(out (int Map, int Id, long KilledAt, long RespawnAt) row) => unsaved.TryDequeue(out row);

    public (long KilledAt, long RespawnAt)? KillOf(FieldBosses.Entry e)
    {
        lock (gate) return kills.TryGetValue((e.Map, e.Id), out var k) ? k : null;
    }

    /// <summary>
    /// Whether the boss is up now and, if not, when it comes: a Basic boss is up unless its respawn time is still ahead; a period boss
    /// is up from its latest scheduled time unless it was killed after that time, else it waits for the next one.
    /// </summary>
    public (bool Spawned, long RespawnAt) Status(FieldBosses.Entry e, long now)
    {
        var kill = KillOf(e);
        if (FieldBosses.ScheduleOf(e) is not { } schedule)
            return kill is { RespawnAt: var due } && due > now ? (false, due) : (true, 0);
        var last = FieldBosses.LastSpawn(schedule, now);
        if (last is { } up && (kill is not { KilledAt: var killed } || killed < up)) return (true, 0);
        return (false, FieldBosses.NextSpawn(schedule, now) ?? long.MaxValue);
    }
}
