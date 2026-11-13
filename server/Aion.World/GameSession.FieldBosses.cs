using Aion.Protocol.Native;

namespace Aion.World;

// Field bosses on the world map (feature/field-boss-map). User 2026-11-11: "field boss monsters are not available on the map
// (exploration map)": the world map's Exploration > Field Boss tab said "No Field Monsters available to display", as the client's
// WorldMapContentsInfo_RQ 0x8dd1 {map i32} (usmap 0x3a0c = catalogue 1024 + 0x89d1, eleven before the answered WorldMapAbyssRankerInfo_RQ
// 0x8ddc) went unanswered. Answered now with WorldMapContentsInfo_RS 0x8dd2 (FieldBosses.ContentsInfo): every WorldMapFieldNamed boss
// of the asked map at its MapData point, spawned or not and, if not, when it comes back. CANDIDATE: no official reply was ever recorded
// (the recordings hold no 0x8dd2; the client's requests are ciphered there), so the layout is the usmap's and the time is Unix ms.
// The bosses themselves (FieldBosses): PeriodSpawn bosses only appear at their times, a killed one waits for its next time, kills
// survive a restart (game.field_boss_kills, migration 091). Basic bosses keep the ordinary respawn (RespawnMs, OURS). No spawn or
// death notice exists in the message catalogue for field bosses, so none is sent.
public sealed partial class GameSession
{
    public const ushort WorldMapContentsInfoRq = 0x8dd1, WorldMapContentsInfoRs = 0x8dd2;

    FieldBossState? ownFieldBosses;
    /// <summary>The host's field boss kills (a session without a shared world keeps its own).</summary>
    FieldBossState FieldBossKills => options.World?.FieldBossKills ?? (ownFieldBosses ??= new FieldBossState());

    List<byte[]>? FieldBossRequest(byte[] plain, long now)
    {
        if (Opcode(plain) != WorldMapContentsInfoRq || plain.Length < 6) return null;
        var map = BitConverter.ToInt32(plain, 2);
        // The map's live NPCs when it is up (this player's own map, or the shared map someone loaded).
        (IReadOnlyDictionary<int, Target> Targets, IReadOnlyDictionary<int, long> Respawn)? live =
            map == worldMap && instanceKey == 0 && targets is not null ? (targets, respawnAt)
            : options.World?.LoadedMap(map) is { } loaded ? (loaded.Targets, loaded.RespawnAt) : null;
        return [FieldBosses.ContentsInfo(map, FieldBossList(data, FieldBossKills, map, now, live))];
    }

    /// <summary>
    /// The map's field bosses for the map list: data id, where (the living one's place, else its MapData point; a period spawner of
    /// several points shows the point of its current or next time), spawned, and the respawn time (Unix ms) when not.
    /// </summary>
    public static List<(int DataId, Vector3d Position, bool Spawned, long RespawnAt)> FieldBossList(GameData data, FieldBossState state, int map, long now,
        (IReadOnlyDictionary<int, Target> Targets, IReadOnlyDictionary<int, long> Respawn)? live = null)
    {
        var list = new List<(int, Vector3d, bool, long)>();
        var spawns = data.Spawns(map);
        foreach (var e in FieldBosses.On(map))
        {
            var points = spawns.Where(s => string.Equals(s.Name, e.Spawner, StringComparison.OrdinalIgnoreCase))
                .Select(s => new Vector3d(s.Position[0], s.Position[1], s.Position[2])).ToList();
            if (points.Count == 0) continue;
            var (up, due) = state.Status(e, now);
            if (live is { } l)
            {
                var group = l.Targets.Values.Where(t => string.Equals(t.Name, e.Spawner, StringComparison.OrdinalIgnoreCase)).ToList();
                if (group.Count > 0)
                {
                    if (group.FirstOrDefault(t => !t.Dead) is { } alive) { list.Add((e.Id, alive.Position, true, 0)); continue; }
                    up = false;
                    due = group.Select(t => l.Respawn.TryGetValue(t.Key, out var at) ? at : long.MaxValue).Min();
                }
            }
            var point = points[0];
            if (points.Count > 1 && FieldBosses.ScheduleOf(e) is { } schedule)
                point = points[FieldBosses.PointFor(up ? FieldBosses.LastSpawn(schedule, now) ?? 0 : due, points.Count)];
            list.Add((e.Id, point, up, up ? 0 : due));
        }
        return list;
    }

    /// <summary>
    /// A shared map's field bosses as their kills and schedules say: one that is not up waits hidden (HP 0, corpse gone) for its time;
    /// of a period spawner with several points only its time's point stands. Living bosses in a fight are left alone. Returns the keys
    /// that were standing and are now hidden (their 0x3642 is due to anyone who saw them).
    /// </summary>
    public static List<int> PrepareFieldBosses(int map, IDictionary<int, Target> targets, IDictionary<int, long> respawnAt, FieldBossState state, long now)
    {
        var hidden = new List<int>();
        foreach (var e in FieldBosses.On(map))
        {
            var group = targets.Values.Where(t => string.Equals(t.Name, e.Spawner, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Key).ToList();
            if (group.Count == 0) continue;
            var (up, due) = state.Status(e, now);
            var schedule = FieldBosses.ScheduleOf(e);
            var keep = !up ? -1 : schedule is not null && group.Count > 1 ? FieldBosses.PointFor(FieldBosses.LastSpawn(schedule, now) ?? 0, group.Count) : int.MaxValue;
            var until = up && schedule is not null ? FieldBosses.NextSpawn(schedule, now) ?? long.MaxValue : due;
            for (var i = 0; i < group.Count; i++)
            {
                var t = group[i];
                if (keep == int.MaxValue || i == keep || t.InCombat) continue;
                if (!t.Dead) hidden.Add(t.Key);
                else if (!t.CorpseRemoved) hidden.Add(t.Key);
                t.Hp = 0;
                t.CorpseRemoved = true;
                respawnAt[t.Key] = until;
            }
        }
        return hidden;
    }

    /// <summary>After a kill: a field boss of the shared map waits for its next time (period) and the kill is kept.</summary>
    void FieldBossKilled(Target target, long now)
    {
        if (instanceKey != 0 || FieldBosses.OfSpawner(worldMap, target.Name) is not { } e) return;
        target.DiedAt = now;
        var due = FieldBosses.ScheduleOf(e) is { } schedule ? FieldBosses.NextSpawn(schedule, now) ?? long.MaxValue : now + RespawnMs;
        respawnAt[target.Key] = due;
        FieldBossKills.Killed(e, now, due);
        Note($"field boss {e.Name} (map {e.Map}, list id {e.Id}) killed; back at {DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(due, 253402300799000)):u}");
    }

    /// <summary>
    /// A period boss whose respawn came: null to let it stand, else the later time it waits for (another point's boss still stands from
    /// an earlier time, its time has not come, or this time uses another of the spawner's points).
    /// </summary>
    long? FieldBossHeld(Target target, long now)
    {
        if (instanceKey != 0 || targets is null || FieldBosses.OfSpawner(worldMap, target.Name) is not { } e || FieldBosses.ScheduleOf(e) is not { } schedule) return null;
        var next = FieldBosses.NextSpawn(schedule, now) ?? long.MaxValue;
        var group = targets.Values.Where(t => string.Equals(t.Name, e.Spawner, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Key).ToList();
        if (group.Any(t => t != target && !t.Dead)) return next;
        if (!FieldBossKills.Status(e, now).Spawned) return next;
        return group.Count <= 1 || FieldBosses.PointFor(FieldBosses.LastSpawn(schedule, now) ?? now, group.Count) == group.IndexOf(target) ? null : next;
    }

    /// <summary>World entry (0x3622): the stored kills once per host; maps already up are put right (their wrongly standing bosses leave).</summary>
    async Task LoadFieldBossKillsAsync(long now, CancellationToken ct)
    {
        var state = FieldBossKills;
        if (state.Loaded || Store is not { } store || !state.BeginLoad()) return;
        IReadOnlyList<(int Map, int Id, long KilledAt, long RespawnAt)>? rows = null;
        await Stored(async () => { rows = await store.FieldBossKillsAsync(ct); return []; });
        if (rows is null) { state.LoadFailed(); return; }
        state.Load(rows);
        if (options.World is not { } world) return;
        foreach (var map in world.Maps.Where(m => m.Instance == 0).ToList())
        {
            var hidden = PrepareFieldBosses(map.MapId, map.Targets, map.RespawnAt, state, now);
            map.BroadcastAll(hidden.Select(key => GamePackets.Death(key)).ToList());
        }
    }

    /// <summary>Tick: the host's unsaved field boss kills to the store (whichever session ticks first writes them).</summary>
    async Task FlushFieldBossKillsAsync(CancellationToken ct)
    {
        if (Store is not { } store) return;
        var state = FieldBossKills;
        while (state.TakeUnsaved(out var row))
        {
            var (map, id, killed, respawn) = row;
            await Stored(async () => { await store.SaveFieldBossKillAsync(map, id, killed, respawn, ct); return []; });
        }
    }
}
