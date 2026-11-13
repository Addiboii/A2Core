using Aion.Protocol.Native;

namespace Aion.World;

// The daily dungeons' run (DailyDungeons.cs has the tables and the messages):
// - the countdown: DungeonState_NT 0x6100 Process from the run's start to its deadline (the OBSERVED form of the other timed dungeons;
//   the length is CANDIDATE, see DailyDungeons.cs), Prepare first where the dungeon has a ready time (Dungeon_Daily_0001's event
//   starts on DungeonState Prepare);
// - the monsters: the spawners of the dungeon's waves (WaveStepData orders) are not placed at the map load; the wave's first order
//   spawns when the event starts the wave and each next order when the one before is dead (MapEventRun waves). DungeonWave_0001's map
//   also holds the spawners of other wave levels (S1_DungeonWave_0001_Multi_Monster_Wave_L_1..20) that no wave of this client names:
//   never placed. Before, all 30 wave spawners stood at the entry (600 monsters) and no order followed another;
// - the points: each kill / object use worth DungeonPoint.dat points adds them while the run is on, DungeonPoint_NT 0x6104 shows the
//   total (CANDIDATE form);
// - the end: at the deadline, or when the waves are cleared (DungeonWave_0001's Sub event) or the event says DungeonClear: the
//   monsters go, WaveDungeonCompleteResult_NT 0x6102 (CANDIDATE form) with the points and the reward of the highest RewardDungeon tier
//   reached, DungeonCompleteResult_NT 0x6101 (OBSERVED form, with the reward map as the official sealed-dungeon clear), the state
//   Complete then End until CompleteStateHoldingTime (30 s), then out. The reward is granted, the clear achievements count, the best
//   run is kept (setting "daily_dungeon_records") and sent as WaveDungeonInfo_NT 0x6103 (OBSERVED form) at every entry.
// CANDIDATE: a run that reaches the deadline is a completed run (succeeded, paid by its points: the client's result window shows
// the points and the reward, its "time bonus" line is 0: no bonus rule in the client data); grade 1 (the official records say 1).
public sealed partial class GameSession
{
    /// <summary>The grade a finished run records (OBSERVED: 1 in both official records; meaning unknown, CANDIDATE).</summary>
    public const int DailyGrade = 1;

    static DailyDungeons Dailies => DailyDungeons.Embedded;

    /// <summary>The character's best run per daily dungeon (setting "daily_dungeon_records" {dungeon: [grade, seconds, points]}).</summary>
    readonly Dictionary<int, DailyRecord> dailyRecords = [];
    bool dailyRecordsDirty;

    void LoadDailyRecords(Dictionary<string, int[]>? stored)
    {
        dailyRecords.Clear();
        foreach (var (key, v) in stored ?? [])
            if (int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var id) && v is [var grade, var seconds, var points])
                dailyRecords[id] = new DailyRecord(id, grade, seconds, points);
    }

    async Task FlushDailyRecordsAsync(CancellationToken ct)
    {
        if (!dailyRecordsDirty) return;
        dailyRecordsDirty = false;
        await SaveSettingAsync("daily_dungeon_records", dailyRecords.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p => new[] { p.Value.Grade, p.Value.DurationS, p.Value.Point }), ct);
    }

    /// <summary>WaveDungeonInfo_NT at the world entry: this character's records (`00` with none, as before).</summary>
    byte[] WaveDungeonInfoAtEntry() => DailyDungeons.WaveDungeonInfo(dailyRecords.Values);

    internal IReadOnlyDictionary<int, DailyRecord> DailyRecordsForChecks => dailyRecords;

    /// <summary>Checks: the player's kill of an NPC (HP 0 and the kill's whole sequence: credit, map events, points, loot).</summary>
    internal List<byte[]> KillForChecks(Target target, long now)
    {
        target.Hp = 0;
        return AfterNpcDamage(target, 0, 1, now);
    }

    /// <summary>The daily dungeon this copy runs, if any.</summary>
    DailyDungeon? DailyOfCopy => copy is { } c ? Dailies.Of(c.DungeonId) : null;

    /// <summary>When the run's countdown started (after the ready time).</summary>
    static long ProcessStart(DungeonCopy c) => c.ProcessAtMs > 0 ? c.ProcessAtMs : c.StartedMs;

    /// <summary>
    /// The map's wave spawners stay unplaced until their order comes (daily dungeon maps): every spawner of a wave the map's events
    /// name, and the wave-level spawners of the map no wave of this client names (DungeonWave_0001's Wave_L_1..20).
    /// </summary>
    internal static HashSet<string> DailyDormantSpawners(int map, IEnumerable<string> spawnNames)
    {
        var dormant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Dailies.OfMap(map) is null) return dormant;
        foreach (var name in ScriptNames(map))
            if (MapEventData.Wave(name) is { } orders)
                foreach (var order in orders) dormant.UnionWith(order);
        foreach (var name in spawnNames)
            if (name.Contains("_Multi_Monster_Wave_", StringComparison.OrdinalIgnoreCase)) dormant.Add(name);
        return dormant;
    }

    /// <summary>A kill in the host's daily copy while the run is on: its points.</summary>
    void DailyKilled(Target target, long now) => DailyPoints(d => DailyDungeons.KillPoints(d, target.NpcId), now);

    /// <summary>An object used in the host's daily copy (orbs, coins: UseEnvObj points).</summary>
    void DailyUsedObject(int envId, long now)
    {
        if (CopyOwner is { } owner) { owner.DailyUsedObject(envId, now); return; }
        DailyPoints(d => DailyDungeons.UsePoints(d, envId), now);
    }

    void DailyPoints(Func<DailyDungeon, int> worth, long now)
    {
        if (copy is not { Cleared: false, Failed: false, Phase: "Process" } c || c.Host != this || Dailies.Of(c.DungeonId) is not { } d) return;
        if (c.DeadlineMs > 0 && now >= c.DeadlineMs) return;
        var points = worth(d);
        if (points <= 0) return;
        c.Points += points;
        mapEventOut.Add(DailyDungeons.DungeonPoint(c.DungeonId, c.Points));
    }

    /// <summary>The host's tick: the deadline ends the run; so does a cleared wave event (DungeonWave_0001's waves all down).</summary>
    void DailyTick(long now)
    {
        if (copy is not { Cleared: false, Failed: false } c || c.Host != this || Dailies.Of(c.DungeonId) is not { } d || c.Phase != "Process") return;
        if (c.DeadlineMs > 0 && now >= c.DeadlineMs) { DailyEnd(d, c, c.DeadlineMs, timeUp: true); return; }
        if (mapEventRuns.Any(r => r.Status == MapEventRun.Complete && r.Definition.Stages.SelectMany(s => s.Steps).SelectMany(s => s.Objectives).Any(o => o.Type == "ClearWave")))
            DailyEnd(d, c, now, timeUp: false);
    }

    /// <summary>The run ends: the monsters go, the result with the points and the reward, Complete then End for the hold, then out.</summary>
    void DailyEnd(DailyDungeon d, DungeonCopy c, long now, bool timeUp, string? mapEvent = null)
    {
        if (c.Cleared) return;
        c.Cleared = true;
        var duration = Math.Clamp(now - ProcessStart(c), 0, d.LimitS > 0 ? d.LimitS * 1000L : long.MaxValue);
        const int bonus = 0;   // CANDIDATE: no time-bonus rule in the client data
        var total = c.Points + bonus;
        var tier = DailyDungeons.Reward(d, profileRace, total);
        var items = tier?.Items.ToList() ?? [];
        Note($"daily dungeon {d.Name}: {(timeUp ? "time up" : "cleared")} after {duration / 1000} s, {total} points, reward {(tier is null ? "none" : $"tier {tier.RowId} ({tier.NeedPoint})")}");
        // The waves stop and the monsters still standing go (0x3642 act 7); the map's objects (the exit) stay.
        foreach (var run in mapEventRuns) run.StopWaves();
        foreach (var t in targets?.Values.Where(t => t.Key != Combat.TrainingDummyKey && t.Hostile && !(t.Dead && t.CorpseRemoved)).ToList() ?? [])
            DespawnTarget(t, kill: false);
        if (items.Count > 0) pendingRewards.Add(items);
        var hold = (d.HoldS > 0 ? d.HoldS : 30) * 1000L;
        mapEventOut.Add(DailyDungeons.WaveDungeonCompleteResult(c.DungeonId, DailyGrade, duration, total, bonus, items));
        mapEventOut.Add(DungeonCompleteResult(c.DungeonId, true, items));
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateComplete, now, now));
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, now, now + hold));
        instanceExitAt ??= now + hold;
        // The best run (most points, then the shorter) is the record the dungeon list shows.
        var seconds = (int)(duration / 1000);
        if (!dailyRecords.TryGetValue(d.Id, out var best) || total > best.Point || total == best.Point && seconds < best.DurationS)
        {
            dailyRecords[d.Id] = new DailyRecord(d.Id, DailyGrade, seconds, total);
            dailyRecordsDirty = true;
            mapEventOut.Add(WaveDungeonInfoAtEntry());
        }
        if (MapEventData.Dungeon(d.Id) is { } room)
        {
            var name = mapEvent ?? MapEventData.EventsOn(worldMap).FirstOrDefault()?.Name ?? room.Name;
            if (!clearedEvents.Contains(name)) clearedEvents.Add(name);
            mapEventOut.AddRange(AchieveDungeonClear(room, name, 1));
            mapEventOut.AddRange(AchieveDungeonClearLimits(room, now - c.StartedMs, c.Deaths));
        }
    }
}
