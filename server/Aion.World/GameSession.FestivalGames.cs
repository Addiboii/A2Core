using Aion.Protocol.Native;

namespace Aion.World;

// The Shugo festival games inside their event maps (fix/shugo-games; FestivalGames.cs has the data and the messages).
// Before (live 2026-11-13, Axel3 in TrapBoard 500036): every spawner of the map stood at the entry (all 8 trap boards at once,
// 740 floor tiles, the 178 bonus orbs), nothing ran the rounds, the ranking stayed empty (0x6003 with no lines, polled 104
// times), the end was a failed run (0x6101 succeeded 0) and the way out went to Poeta (no return point was kept).
// Now:
// - one copy per event run: everyone who enters the same event in the same hour plays in one map (shared instance key);
//   the way back is where the player entered from (LastPosition, Dungeon.dat ExitType);
// - the game's own spawners wait (trap boards, round bonus orbs, the EventFunc phase spawners) until the game starts them;
// - the run starts at the event's start (DungeonState Process, already) with the contents rules below, ends at the event's end
//   (or the map event's DungeonClear: Racing, Defence): 0x6101 succeeded with the reward, Complete then End for
//   CompleteStateHoldingTime (41 s), then out; the reward is the cube's RewardCommon of the player's level band by mail
//   (Post.dat 10000000 + event id, UseContents MonsterCube_FieldEvent) to everyone who scored;
// - points: Overlap point items (bubbles, balloons, Od), kills (Defence, Pang, TreasureHunter), and per contents type
//   (TrapBoard rounds, Platformer checkpoints, Racing laps and the finish order, Dilemma rounds); the ranking reply lists them.
// Contents types:
// - TrapBoard: FieldEventTrapBoard.dat rounds (8): Ready (ReadyTime, the last ReadyCount seconds counted), Preview (the right
//   tiles: TrapBoardAnswer_NT), Answer, Result; the round's board spawns at its Ready and the one before goes; at the Result the
//   wrong tiles go (whoever stands on one falls) and each player on a right tile scores (abnormal 90001; wrong 90002: EventFunc
//   FE_Trapboard_Answer / _Wronganswer); the round's BonusEnv orbs come with it.
// - Racing: RacingStart_NT (the map event's 3 s, then StartCount), the lap triggers in order (Lap1_Trigger_1 the start / finish
//   line: lap 2, RacingCharLap_NT; Lap1_Trigger_2; Lap2_Trigger_1 the finish), RacingFinish_NT at the first finish, EndCount later
//   RacingEnd_NT. The game itself ends with the event (312 s OBSERVED in the schedule): the map event's last step waits on
//   UnableComplete, its DungeonClear never comes.
// - Platformer: PlatformerState_NT Ready -> Start after StartWait + StartCountdown -> WaitFinish EndCountdown before the end ->
//   Finish; the CheckPointList volumes reached in order (PlatformerPlayerInfo_NT); the moving platforms (Vessel NPCs on their
//   Vessel waypoint sets) travel their routes.
// - PlatformerDilemma: the rounds of FieldEventPlatformerDilemma.dat with the EventFunc holds (Start 14 s, Ready 6.5 s, Result 5 s,
//   End 10 s), PlatformerDilemmaState_NT per state; at a round's result a player on an answer board scores (abnormal 9001x), off
//   the boards 90010; the round's bonus Od (FE_Dilemma_*_<n>Round_BonusOd_Spawn).
// - Masquerade: after WaitTime the roles (MasqueradePlayerInfo_NT; a quarter Seekers, at least one with two players), the phases.
// - Shooting (Pang): ShootingStart_NT after StartCountdown, the Pang spawn phases, kills score by colour.
// - GetContribution (JumpJump, SpecterEvasion, OdBubble, TreasureHunter) and Defence: the map event's own script plus the phases.
// CANDIDATE (nothing recorded inside an event map): every message form, the phase times (the EventFunc groups run in table order,
// spread evenly over the play time: what triggers them is server-only data), the point values, the right tiles (a random path),
// the masquerade roles, the Dilemma's state lengths, the Racing lap rule, the platform motion (straight lines at the waypoint
// set's BaseMoveSpeed). Not built: the Masquerade capture, the Specter's touch, laser hits taking points, the Key reward (the
// contents ticket opens it), the moving Pangs and the race pets.
public sealed partial class GameSession
{
    static FestivalGames Festivals => FestivalGames.Embedded;

    /// <summary>The copy key of an event run: all its players share one map (above the arena rooms, below the world layers).</summary>
    public static int FestivalInstanceKey(int eventId, long open) => 0x6000_0000 | (int)(open / 3_600_000 % 0x400) << 18 | eventId % 0x40000;

    /// <summary>The last finished run's ranking per event (the ranking asked outside the map). Under the world lock.</summary>
    static readonly Dictionary<int, (int Version, List<FestivalRank> Ranks)> lastFestivalRanks = [];

    /// <summary>Spawners the festival game of this map starts itself (not placed at the map load).</summary>
    internal static IEnumerable<string> FestivalDormant(int map) => Festivals.OfMap(map)?.Dormant() ?? [];

    /// <summary>The run of the copy this player is in, while it plays.</summary>
    FestivalRun? Festival => copy?.Festival;

    internal FestivalRun? FestivalForChecks => copy?.Festival;
    /// <summary>Checks: the player stands here (a position report's festival part: checkpoints, laps).</summary>
    internal List<byte[]> FestivalAtForChecks(Vector3d at, long now)
    {
        position = at;
        if (worldPlayer is { } wp) wp.Position = at;
        var replies = MapEventPosition(at, now);
        replies.AddRange(FestivalPosition(at, now));
        return replies;
    }
    /// <summary>Checks: the use of a placed object (as the interaction's completion).</summary>
    internal Task<List<byte[]>> UseObjectForChecks(int index, long now) => CompleteUseAsync(index, CancellationToken.None, now);
    /// <summary>Checks: an object kept out of sight in this copy (dormant spawner, removed).</summary>
    internal bool HiddenForChecks(int index) => MapEventHidden(index, WorldObjects.ObjectsOn(worldMap)[index]);
    internal bool DormantForChecks(string spawner) => (CopyOwner ?? this).dormantSpawners.Contains(spawner);
    internal int MapForChecks => worldMap;
    internal static FestivalRun? NewFestivalRunForChecks(int map) => NewFestivalRun(map, 0, 1);

    /// <summary>A new copy of an event map: its run, timed by the event's schedule.</summary>
    static FestivalRun? NewFestivalRun(int mapId, long start, long end)
    {
        if (Festivals.OfMap(mapId) is not { } game) return null;
        var run = new FestivalRun(game, start, end);
        // A spawn entry with several EnvObjData ids (EnvObjIdList [green Od, yellow Od], [racing item 1, item 2], ...) places ONE of
        // them per position (as an NpcIdList picks one NPC); the world object data places all of them on the same spot (live
        // 2026-11-13: two objects stacked at every such point). One per position is kept, chosen at random (CANDIDATE: the pick rule).
        var rng = new Random();
        foreach (var group in WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i))
                     .Where(x => game.Spawners.TryGetValue(x.o.Name, out var s) && s.Envs.Length > 1)
                     .GroupBy(x => (x.o.Name, X: Math.Round(x.o.Position[0]), Y: Math.Round(x.o.Position[1]), Z: Math.Round(x.o.Position[2]))))
        {
            var keep = rng.Next(group.Count());
            foreach (var (x, n) in group.Select((x, n) => (x, n))) if (n != keep) run.Rejected.Add(x.i);
        }
        return run;
    }

    // ---------- The copy's players and messages ----------

    /// <summary>Everyone in the copy, the host first.</summary>
    IEnumerable<GameSession> FestivalPlayers() => CopyMembers().Prepend(this);

    FestivalScore ScoreOf(FestivalRun run)
    {
        if (!run.Scores.TryGetValue(playerKey, out var s))
            run.Scores[playerKey] = s = new FestivalScore { Key = playerKey };
        s.Name = StoredName;
        s.DbId = DbId;
        s.Server = ServerId;
        s.Guild = guild?.Name ?? "";
        s.Emblem = guild?.Emblem ?? 0;
        s.Character = CharacterId;
        s.Level = level;
        return s;
    }

    void AddPoints(FestivalRun run, int points, string why)
    {
        if (points == 0 || run.Finished) return;
        var s = ScoreOf(run);
        s.Points = Math.Max(0, s.Points + points);
        run.Version++;
        s.Last = why;
    }

    /// <summary>An object goes for the whole copy (a taken bubble, a wrong tile at a result).</summary>
    void HideForCopy(int index)
    {
        var host = CopyOwner ?? this;
        host.removedObjects.Add(index);
        foreach (var s in host.FestivalPlayers())
            if (s.shownObjects.Remove(index))
            {
                var gone = GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index, 4, 3);
                if (s == this) festivalOut.Add(gone); else s.worldPlayer?.Send(gone);
            }
    }

    /// <summary>Packets for this player alone (not the copy), sent with the tick's replies.</summary>
    readonly List<byte[]> festivalOut = [];

    /// <summary>An abnormal on one player of the copy (EventFunc AddAbnormal on the player).</summary>
    void FestivalBuff(GameSession s, int abnormal, int ms)
    {
        var packets = s.AddScriptedBuff(abnormal, ms, mapEventNow);
        if (s == this) festivalOut.AddRange(packets); else s.worldPlayer?.Send(packets);
    }

    // ---------- Play events (any player of the copy) ----------

    /// <summary>A point item walked into (Overlap) or a treasure box opened: its points, once for the copy.</summary>
    List<byte[]> FestivalUsedObject(int index, WorldObject o, long now)
    {
        if (Festival is not { Started: true, Finished: false } run || run.Game.PickupPoints(o.Env) is not (> 0 and var points)) return [];
        if (!run.Taken.Add(index)) return [];
        mapEventNow = now;
        AddPoints(run, points, o.Name);
        // Gone for the others too (the taker's own despawn follows the object's bDespawn).
        var host = CopyOwner ?? this;
        host.removedObjects.Add(index);
        foreach (var s in host.FestivalPlayers().Where(s => s != this))
            if (s.shownObjects.Remove(index)) s.worldPlayer?.Send(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index, 4, 3));
        return TakeFestivalOut();
    }

    /// <summary>A kill by this player: its points (Defence, Pang, TreasureHunter).</summary>
    void FestivalKilled(Target target, long now)
    {
        if (Festival is not { Started: true, Finished: false } run || run.Game.KillPoints(target.NpcId) is not (> 0 and var points)) return;
        mapEventNow = now;
        AddPoints(run, points, target.Name);
    }

    /// <summary>A position report: platformer checkpoints and racing laps (in order).</summary>
    List<byte[]> FestivalPosition(Vector3d at, long now)
    {
        if (Festival is not { Started: true, Finished: false } run) return [];
        mapEventNow = now;
        var host = CopyOwner ?? this;
        if (run.Game.Platformer is { } p && now >= run.PlatformerGoMs)
        {
            var s = ScoreOf(run);
            var next = s.Checkpoint < p.Checkpoints.Length ? p.Checkpoints[s.Checkpoint] : null;
            if (next is not null && next.Contains(at))
            {
                s.Checkpoint++;
                s.Toward = 0;
                AddPoints(run, FestivalGames.CheckpointPoints, next.Name);
                host.mapEventOut.Add(FestivalGames.PlatformerPlayerInfo(s.DbId, s.Checkpoint));
            }
            else if (next is not null)
            {
                // The way towards the next checkpoint (the rule: rank by checkpoints, then the distance left), up to 999.
                var from = s.Checkpoint == 0 ? run.StartPosition ?? at : p.Checkpoints[s.Checkpoint - 1].Centre;
                double whole = Math.Sqrt(from.DistanceSquared(next.Centre)), left = Math.Sqrt(at.DistanceSquared(next.Centre));
                var toward = whole <= 0 ? 0 : (int)Math.Clamp(999 * (1 - left / whole), 0, 999);
                if (toward > s.Toward) { AddPoints(run, toward - s.Toward, "towards " + next.Name); s.Toward = toward; }
            }
        }
        if (run.Game.Racing is { } r && now >= run.RaceGoMs)
        {
            var s = ScoreOf(run);
            if (!s.Finished && s.Stage < r.Triggers.Length && r.Triggers[s.Stage].Contains(at)) RacePassed(run, r, s, now);
        }
        var replies = host == this ? [] : host.DrainMapEvents();
        if (replies.Count > 0) host.worldPlayer?.Send(replies);
        return TakeFestivalOut();
    }

    void RacePassed(FestivalRun run, RacingGame r, FestivalScore s, long now)
    {
        var host = CopyOwner ?? this;
        var passed = r.Triggers[s.Stage].Name;
        s.Stage++;
        if (s.Stage == r.Triggers.Length)
        {
            // The finish: the order's points; the first finish ends the race EndCount later.
            s.Finished = true;
            var place = run.Scores.Values.Count(x => x.Finished);
            AddPoints(run, FestivalGames.FinishPoints(place), $"finish {place}");
            if (place == 1)
            {
                run.RaceEndMs = now + r.EndCount * 1000L;
                host.mapEventOut.Add(FestivalGames.RacingFinish(r.Id, run.RaceEndMs, s.Server, s.Name));
            }
            return;
        }
        // A lap's first trigger is the start / finish line: the map event's Lap1_Trigger_1 clears lap 1's items and places lap 2's
        // tornados and boards, Lap1_Trigger_2 clears the rest of lap 1, Lap2_Trigger_1 is the finish. Crossing it starts the next lap.
        if (passed.EndsWith("_Trigger_1", StringComparison.Ordinal) && s.Lap < r.Laps)
        {
            s.Lap++;
            AddPoints(run, FestivalGames.LapPoints, $"lap {s.Lap}");
            host.mapEventOut.Add(FestivalGames.RacingCharLap(r.Id, s.DbId, s.Lap));
        }
    }

    List<byte[]> TakeFestivalOut()
    {
        if (festivalOut.Count == 0) return [];
        var taken = festivalOut.ToList();
        festivalOut.Clear();
        return taken;
    }

    /// <summary>
    /// FieldEventRanking_RQ: the run's lines (most points first) while the player is in its map, else the last finished run of the
    /// event; empty when there is none (as before).
    /// </summary>
    byte[] FestivalRanking(int eventId, long now)
    {
        if (Festival is { } run && run.Game.Event == eventId)
            return FestivalGames.Ranking(eventId, run.Version, now + FestivalRun.RankingPollMs, run.Ranks());
        if (lastFestivalRanks.TryGetValue(eventId, out var last))
            return FestivalGames.Ranking(eventId, last.Version, now + 60_000, last.Ranks);
        return FestivalGames.Ranking(eventId, 0, now + 60_000, []);
    }

    // ---------- The host's tick ----------

    /// <summary>The run's timeline: the start, the contents' states, the phases, the platforms, the end.</summary>
    List<byte[]> FestivalTick(long now)
    {
        if (copy is not { } c || c.Host != this || c.Festival is not { Finished: false } run) return TakeFestivalOut();
        mapEventNow = now;
        if (c.Phase != "Process" || now < run.StartMs) return TakeFestivalOut();
        if (!run.Started) FestivalStart(run, now);
        FestivalPhases(run, now);
        if (run.Game.TrapBoard is { } trap) TrapBoardTick(run, trap, now);
        if (run.Game.Platformer is { } p) PlatformerTick(run, p, now);
        if (run.Game.Racing is { } r && run.RaceEndMs > 0 && now >= run.RaceEndMs && !run.RaceEnded)
        {
            run.RaceEnded = true;
            mapEventOut.Add(FestivalGames.RacingEnd(r.Id));
        }
        if (run.Game.Dilemma is { } d) DilemmaTick(run, d, now);
        if (run.Game.Masquerade is { } m && !run.RolesGiven && now >= run.StartMs + m.WaitS * 1000L) MasqueradeRoles(run, m);
        VesselTick(run, now);
        if (now >= run.EndMs) FestivalFinish(run, run.EndMs);
        return TakeFestivalOut();
    }

    void FestivalStart(FestivalRun run, long now)
    {
        run.Started = true;
        run.StartPosition = position;
        foreach (var s in FestivalPlayers()) s.ScoreOf(run);
        var g = run.Game;
        Note($"festival {g.Name} ({g.Game}): the game starts ({FestivalPlayers().Count()} players, until {DateTimeOffset.FromUnixTimeMilliseconds(run.EndMs):HH:mm:ss})");
        if (g.Racing is { } r)
        {
            // The map event's Process step waits 3 s (Delay 3000) before the start talk, then the count.
            run.RaceGoMs = now + 3_000 + r.StartCount * 1000L;
            mapEventOut.Add(FestivalGames.RacingStart(r.Id, run.RaceGoMs));
        }
        if (g.Platformer is { } p)
        {
            run.PlatformerGoMs = now + (p.StartWaitS + p.StartCountdownS) * 1000L;
            mapEventOut.Add(FestivalGames.PlatformerState(p.Id, FestivalGames.PlatformerReady, run.PlatformerGoMs));
        }
        if (g.Shooting is { } sh) mapEventOut.Add(FestivalGames.ShootingStart(sh.Id, now + sh.StartCountdownS * 1000L));
        // The phases: from the start (after the shooting's count) to the end (before its closing count), in table order.
        var first = now + (g.Shooting?.StartCountdownS ?? g.Masquerade?.WaitS ?? 0) * 1000L;   // after the count / the masquerade's wait
        var last = run.EndMs - (g.Shooting?.EndCountdownS ?? 0) * 1000L - 5_000;
        var phases = g.Phases ?? [];
        for (var i = 0; i < phases.Length; i++)
            run.PhaseAt.Add(phases.Length <= 1 ? first : first + (last - first) * i / Math.Max(1, phases.Length));
        if (g.TrapBoard is { } trap) run.RoundAt = now;
        if (g.Dilemma is { } d)
        {
            run.DilemmaStep = 0;
            run.DilemmaAt = now;
        }
    }

    /// <summary>The EventFunc phases whose time has come.</summary>
    void FestivalPhases(FestivalRun run, long now)
    {
        var phases = run.Game.Phases ?? [];
        while (run.NextPhase < phases.Length && run.NextPhase < run.PhaseAt.Count && now >= run.PhaseAt[run.NextPhase])
        {
            foreach (var a in phases[run.NextPhase].Acts) FestivalAct(a);
            run.NextPhase++;
        }
    }

    /// <summary>One EventFunc row on the copy: spawners, captions, messages, abnormals on every player.</summary>
    void FestivalAct(string[] a)
    {
        if (a.Length < 2) return;
        switch (a[0])
        {
            case "StartSpawner": DoSpawn(a[1]); break;
            case "EndSpawner": DoDespawn(a[1], kill: false); break;
            case "PlayNpcTalk": mapEventOut.Add(GamePackets.Caption(a[1])); break;
            case "ShowMessage": mapEventOut.Add(GamePackets.Message(a[1])); break;
            case "AddAbnormal" when int.TryParse(a[1], out var id):
                var ms = a.Length > 2 && int.TryParse(a[2], out var d) ? d : -1;
                foreach (var s in FestivalPlayers()) FestivalBuff(s, id, ms);
                break;
            case "RemoveAbnormal" when int.TryParse(a[1], out var gone):
                foreach (var s in FestivalPlayers())
                {
                    var packets = s.RemoveScriptedBuffs(gone);
                    if (s == this) festivalOut.AddRange(packets); else s.worldPlayer?.Send(packets);
                }
                break;
        }
    }

    // ---------- TrapBoard ----------

    void TrapBoardTick(FestivalRun run, TrapBoardGame trap, long now)
    {
        while (run.Round < trap.Rounds.Length)
        {
            var r = trap.Rounds[run.Round];
            long ready = run.RoundAt, preview = ready + r.ReadyS * 1000L, answer = preview + r.PreviewS * 1000L,
                result = answer + r.AnswerS * 1000L, over = result + r.ResultS * 1000L;
            switch (run.RoundState)
            {
                case 0:
                    // The round's board (the one before goes), its bonus orbs, the right tiles.
                    if (run.Round > 0 && trap.Rounds[run.Round - 1].Spawner is { Length: > 0 } before && before != r.Spawner) DoDespawn(before, kill: false);
                    if (run.Round > 0 && trap.Rounds[run.Round - 1].Bonus is { Length: > 0 } oldBonus) DoDespawn(oldBonus, kill: false);
                    if (r.Spawner.Length > 0)
                    {
                        foreach (var (index, _) in SpawnerObjects(r.Spawner)) run.Taken.Remove(index);
                        DoSpawn(r.Spawner);
                    }
                    if (r.Bonus.Length > 0) DoSpawn(r.Bonus);
                    run.Answers = FestivalGames.TrapPath(r.Grid[0], r.Grid[1], rng);
                    run.CountedDown = 0;
                    mapEventOut.Add(FestivalGames.TrapBoardState(r.Id, FestivalGames.TrapReady, preview));
                    if (r.Talk is [var talk, ..] && talk != "None") mapEventOut.Add(GamePackets.Caption(talk));
                    run.RoundState = 1;
                    continue;
                case 1:
                    // The last ReadyCount seconds counted (EventFunc FE_Trapboard_Time_Count_3s / 2s / 1s).
                    for (var left = r.ReadyCount; left >= 1; left--)
                        if (now >= preview - left * 1000L && run.CountedDown < r.ReadyCount - left + 1 && left <= 3)
                        {
                            run.CountedDown = r.ReadyCount - left + 1;
                            mapEventOut.Add(GamePackets.Message($"MSG_SHUGOFESTA_TRAPBOARD_TIME_{left}S"));
                        }
                    if (now < preview) return;
                    mapEventOut.Add(FestivalGames.TrapBoardAnswer(r.Id, FestivalGames.TrapPreview, run.Answers, answer));
                    run.RoundState = 2;
                    continue;
                case 2:
                    if (now < answer) return;
                    mapEventOut.Add(FestivalGames.TrapBoardState(r.Id, FestivalGames.TrapAnswer, result));
                    if (r.Talk.Length > 2 && r.Talk[2] != "None") mapEventOut.Add(GamePackets.Caption(r.Talk[2]));
                    run.RoundState = 3;
                    continue;
                case 3:
                    if (now < result) return;
                    mapEventOut.Add(FestivalGames.TrapBoardState(r.Id, FestivalGames.TrapResult, over));
                    if (r.Talk.Length > 3 && r.Talk[3] != "None") mapEventOut.Add(GamePackets.Caption(r.Talk[3]));
                    TrapResult(run, trap, r);
                    run.RoundState = 4;
                    continue;
                default:
                    if (now < over) return;
                    run.Round++;
                    run.RoundState = 0;
                    run.RoundAt = over;
                    continue;
            }
        }
    }

    /// <summary>A round's result: the wrong tiles go, each player on a right tile scores.</summary>
    void TrapResult(FestivalRun run, TrapBoardGame trap, TrapRound r)
    {
        var tiles = SpawnerObjects(r.Spawner).ToList();
        foreach (var s in FestivalPlayers())
        {
            var at = s.position;
            var cell = at is { } p ? TileUnder(tiles, p) : -1;
            var right = cell >= 0 && run.Answers.Contains(cell);
            if (right) { s.AddPoints(run, FestivalGames.TrapRoundPoints(r.Round), $"round {r.Round} right tile"); FestivalBuff(s, trap.Right[0], trap.Right[1]); }
            else FestivalBuff(s, trap.Wrong[0], trap.Wrong[1]);
        }
        for (var i = 0; i < tiles.Count; i++)
            if (!run.Answers.Contains(i)) HideForCopy(tiles[i].Index);
    }

    /// <summary>The board tile (its place in the spawner) under a position, -1 off the board (half a tile: TrapBoard GridUnitSize 180 + margin 20).</summary>
    internal static int TileUnder(IReadOnlyList<(int Index, WorldObject Object)> tiles, Vector3d at)
    {
        var best = -1;
        var bestDistance = 110.0 * 110.0;
        for (var i = 0; i < tiles.Count; i++)
        {
            var o = tiles[i].Object.At;
            double dx = o.X - at.X, dy = o.Y - at.Y;
            if (Math.Abs(o.Z - at.Z) > 400) continue;
            var d = dx * dx + dy * dy;
            if (d <= bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    // ---------- Platformer ----------

    void PlatformerTick(FestivalRun run, PlatformerGame p, long now)
    {
        if (run.PlatformerState < FestivalGames.PlatformerStart && now >= run.PlatformerGoMs)
        {
            run.PlatformerState = FestivalGames.PlatformerStart;
            mapEventOut.Add(FestivalGames.PlatformerState(p.Id, FestivalGames.PlatformerStart, run.PlatformerGoMs));
        }
        if (run.PlatformerState < FestivalGames.PlatformerWaitFinish && now >= run.EndMs - p.EndCountdownS * 1000L)
        {
            run.PlatformerState = FestivalGames.PlatformerWaitFinish;
            mapEventOut.Add(FestivalGames.PlatformerState(p.Id, FestivalGames.PlatformerWaitFinish, run.EndMs));
        }
    }

    // ---------- PlatformerDilemma ----------

    /// <summary>The Dilemma's states in turn: the game's Ready (Start hold), per round Ready, PrepareStep1, PrepareStep2, Progress (the
    /// answer count), RoundResult (Result hold); then Finished (End hold).</summary>
    void DilemmaTick(FestivalRun run, DilemmaGame d, long now)
    {
        const long PrepareMs = 10_000;   // CANDIDATE: the two prepare steps' length is not in the client data
        while (now >= run.DilemmaAt && !run.DilemmaDone)
        {
            var step = run.DilemmaStep;
            var round = step == 0 ? 0 : (step - 1) / 5 + 1;
            var stage = step == 0 ? 0 : (step - 1) % 5;
            if (round > d.Rounds.Length)
            {
                run.DilemmaDone = true;
                mapEventOut.Add(FestivalGames.DilemmaState(DungeonStateProcess, FestivalGames.PlatformerFinish, FestivalGames.DilemmaFinished, 0, d.Rounds.Length,
                    run.EndMs, run.EndMs));
                foreach (var s in FestivalPlayers()) FestivalBuff(s, d.HoldAbnormal, d.HoldsMs.GetValueOrDefault("End", 10_000));
                return;
            }
            var r = round > 0 ? d.Rounds[round - 1] : null;
            var length = step == 0 ? d.HoldsMs.GetValueOrDefault("Start", 14_000)
                : stage switch { 0 => d.HoldsMs.GetValueOrDefault("Ready", 6_500), 1 or 2 => PrepareMs, 3 => r!.AnswerS * 1000, _ => d.HoldsMs.GetValueOrDefault("Result", 5_000) };
            var end = run.DilemmaAt + length;
            var state = step == 0 ? FestivalGames.DilemmaReady : stage switch
            {
                0 => FestivalGames.DilemmaReady, 1 => FestivalGames.DilemmaPrepare1, 2 => FestivalGames.DilemmaPrepare2, 3 => FestivalGames.DilemmaProgress,
                _ => FestivalGames.DilemmaRoundResult,
            };
            mapEventOut.Add(FestivalGames.DilemmaState(DungeonStateProcess, step == 0 ? FestivalGames.PlatformerReady : FestivalGames.PlatformerStart, state,
                r?.Id ?? 0, round, run.EndMs, end));
            if (step == 0)
                foreach (var s in FestivalPlayers()) FestivalBuff(s, d.HoldAbnormal, (int)length);
            else if (stage == 0)
            {
                if (r!.Talk is [var talk, ..] && talk != "None") mapEventOut.Add(GamePackets.Caption(talk));
                foreach (var s in FestivalPlayers()) FestivalBuff(s, d.HoldAbnormal, (int)length);
                foreach (var spawner in d.Bonus.GetValueOrDefault(round.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? []) DoSpawn(spawner);
            }
            else if (stage == 3 && r!.Talk.Length > 1 && r.Talk[1] != "None") mapEventOut.Add(GamePackets.Caption(r.Talk[1]));
            else if (stage == 4)
            {
                DilemmaResult(run, d, round);
                if (r!.Talk.Length > 2 && r.Talk[2] != "None") mapEventOut.Add(GamePackets.Caption(r.Talk[2]));
                foreach (var s in FestivalPlayers()) FestivalBuff(s, d.HoldAbnormal, (int)length);
            }
            run.DilemmaStep++;
            run.DilemmaAt = end;
        }
    }

    /// <summary>A round's result: a player on an answer board (an AnswerArea NPC's spot) scores, the others get the wrong mark.</summary>
    void DilemmaResult(FestivalRun run, DilemmaGame d, int round)
    {
        const double BoardReach = 500;   // CANDIDATE: the answer area's size (the AnswerArea NPC's own area is not in the tables)
        var boards = d.Boards.SelectMany(SpawnerNpcs).Where(t => !t.Dead).Select(t => t.Position).ToList();
        foreach (var s in FestivalPlayers())
        {
            var on = s.position is { } at && boards.Any(b => Math.Abs(b.Z - at.Z) < 500 && (b.X - at.X) * (b.X - at.X) + (b.Y - at.Y) * (b.Y - at.Y) <= BoardReach * BoardReach);
            if (on)
            {
                s.AddPoints(run, FestivalGames.DilemmaRoundPoints(round), $"round {round} board");
                FestivalBuff(s, d.Right[Math.Clamp(round - 1, 0, d.Right.Length - 1)], 1000);
            }
            else FestivalBuff(s, d.Wrong, 5000);
        }
    }

    // ---------- Masquerade ----------

    void MasqueradeRoles(FestivalRun run, MasqueradeGame m)
    {
        run.RolesGiven = true;
        var players = FestivalPlayers().ToList();
        var seekers = players.Count >= 2 ? Math.Max(1, players.Count / 4) : 0;
        var order = players.OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            var role = i < seekers ? FestivalGames.MasqueradeSeeker : FestivalGames.MasqueradeHider;
            var score = order[i].ScoreOf(run);
            score.Role = role;
            mapEventOut.Add(FestivalGames.MasqueradePlayerInfo(m.Id, score.DbId, role));
        }
    }

    // ---------- Moving platforms ----------

    void VesselTick(FestivalRun run, long now)
    {
        if (run.Game.Vessels is not { Length: > 0 } vessels || now < run.VesselAt) return;
        var full = now >= run.VesselFullAt;
        run.VesselAt = now + 100;
        if (full) run.VesselFullAt = now + 1_000;
        foreach (var v in vessels)
            foreach (var t in SpawnerNpcs(v.Spawner).Where(t => !t.Dead))
            {
                var at = v.At(now - run.StartMs);
                var sent = run.VesselSent.TryGetValue(t.Key, out var last) ? last : t.Position;
                t.Position = at;
                int dx = (int)Math.Round(at.X - sent.X), dy = (int)Math.Round(at.Y - sent.Y), dz = (int)Math.Round(at.Z - sent.Z);
                if (full || Math.Abs(dx) > 127 || Math.Abs(dy) > 127 || Math.Abs(dz) > 127)
                {
                    run.VesselSent[t.Key] = at;
                    mapEventOut.Add(GamePackets.NpcMove(t.Key, at, t.Direction));
                }
                else if (dx != 0 || dy != 0 || dz != 0)
                {
                    run.VesselSent[t.Key] = new Vector3d(sent.X + dx, sent.Y + dy, sent.Z + dz);
                    mapEventOut.Add(GamePackets.NpcMoveDelta(t.Key, dx, dy, dz, t.Direction, run: false));
                }
            }
    }

    // ---------- The end ----------

    /// <summary>
    /// The run ends (the event's end, or the map event's DungeonClear): the result with each player's reward, Complete then End
    /// for the hold, the way out after it; the reward mail to everyone who scored.
    /// </summary>
    void FestivalFinish(FestivalRun run, long now)
    {
        if (run.Finished || copy is not { } c) return;
        run.Finished = true;
        c.Cleared = true;
        var g = run.Game;
        if (g.Platformer is { } p) mapEventOut.Add(FestivalGames.PlatformerState(p.Id, FestivalGames.PlatformerFinish, now));
        if (g.Racing is { } r && !run.RaceEnded) { run.RaceEnded = true; mapEventOut.Add(FestivalGames.RacingEnd(r.Id)); }
        var hold = (g.HoldS > 0 ? g.HoldS : 41) * 1000L;
        var ranks = run.Ranks();
        lastFestivalRanks[g.Event] = (run.Version, ranks);
        Note($"festival {g.Name}: over, {ranks.Count} ranked ({string.Join(", ", ranks.Take(5).Select(x => $"{x.Rank}. {x.Name} {x.Points}"))})");
        foreach (var s in FestivalPlayers())
        {
            var score = s.ScoreOf(run);
            var eligible = score.Points > 0 && (g.Platformer is null || score.Checkpoint >= 1);   // HighHigh: "reach the first checkpoint"
            var items = eligible && g.RewardFor(score.Level) is { Common: { Length: > 0 } common } ? RollRewards(common, s.Rng, s.profileRace) : [];
            if (eligible && items.Count > 0 && g.Post > 0 && score.Character is { } to) s.festivalMail.Add((to, g.Post, items));
            List<byte[]> result =
            [
                DungeonCompleteResult(c.DungeonId, true, items),
                DungeonState(c.DungeonId, DungeonStateComplete, now, now),
                DungeonState(c.DungeonId, DungeonStateEnd, now, now + hold),
            ];
            if (s == this) festivalOut.AddRange(result); else s.worldPlayer?.Send(result);
            s.instanceExitAt ??= now + hold;
        }
        // Players who scored and left before the end still get their mail.
        foreach (var gone in run.Scores.Values.Where(x => x.Points > 0 && x.Character is not null && !FestivalPlayers().Any(s => s.playerKey == x.Key)))
            if (g.Platformer is null || gone.Checkpoint >= 1)
                if (g.RewardFor(gone.Level) is { Common: { Length: > 0 } common } && g.Post > 0)
                    run.Mail.Add((gone.Character!.Value, g.Post, RollRewards(common, rng, g.Event / 100_000)));
    }

    /// <summary>This player's reward mail from a finished run (sent by its own session, as its own character's store).</summary>
    readonly List<(Guid To, int Post, List<(int ItemId, long Count)> Items)> festivalMail = [];

    /// <summary>The async tick: this player's reward mail, and as the host the mail of players who left before the end (RecvPost_NT
    /// to recipients online).</summary>
    async Task<List<byte[]>> FestivalMailAsync(CancellationToken ct)
    {
        if (Store is not { } store) return [];
        var mail = festivalMail.ToList();
        festivalMail.Clear();
        if (copy is { Festival: { Mail.Count: > 0 } run } c && c.Host == this) { mail.AddRange(run.Mail); run.Mail.Clear(); }
        if (mail.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var (to, post, items) in mail)
        {
            var sent = await StoredValue(() => store.SendPostAsync(to, post, "", items, PostTabReward, ct), null);
            if (sent is null) continue;
            var notice = WritePost(PacketWriter.Opcode(PostRecvNt), sent).ToArray();
            if (to == CharacterId) replies.Add(notice);
            else World?.FindOnline(to)?.Send(notice);
        }
        return replies;
    }
}

/// <summary>A player's standing in a festival run.</summary>
public sealed class FestivalScore
{
    public int Key { get; init; }
    public string Name { get; set; } = "";
    public long DbId { get; set; }
    public int Server { get; set; }
    public string Guild { get; set; } = "";
    public int Emblem { get; set; }
    public Guid? Character { get; set; }
    public int Level { get; set; }
    public int Points { get; set; }
    public int Checkpoint { get; set; }
    public int Toward { get; set; }
    public int Stage { get; set; }
    public int Lap { get; set; } = 1;
    public bool Finished { get; set; }
    public byte Role { get; set; }
    /// <summary>What scored last (the end's note).</summary>
    public string Last { get; set; } = "";
}

/// <summary>One event run's game in a copy (shared by its players; the host's tick drives it).</summary>
public sealed class FestivalRun(FestivalGame game, long startMs, long endMs)
{
    /// <summary>The ranking's next poll (CANDIDATE: the client asked every ~2 s in the map, live 2026-11-13).</summary>
    public const long RankingPollMs = 3_000;
    public FestivalGame Game { get; } = game;
    public long StartMs { get; } = startMs;
    public long EndMs { get; } = endMs;
    public bool Started { get; set; }
    public bool Finished { get; set; }
    public int Version { get; set; }
    public Vector3d? StartPosition { get; set; }
    public Dictionary<int, FestivalScore> Scores { get; } = [];
    /// <summary>The other objects of a multi-object spawn entry at each position (one per position stays): never shown.</summary>
    public HashSet<int> Rejected { get; } = [];
    /// <summary>Point objects taken in this run (world object indexes; a respawned spawner gives its objects again).</summary>
    public HashSet<int> Taken { get; } = [];
    public List<long> PhaseAt { get; } = [];
    public int NextPhase { get; set; }
    // TrapBoard
    public int Round { get; set; }
    public int RoundState { get; set; }
    public long RoundAt { get; set; }
    public int CountedDown { get; set; }
    public SortedSet<int> Answers { get; set; } = [];
    // Racing / Platformer
    public long RaceGoMs { get; set; }
    public long RaceEndMs { get; set; }
    public bool RaceEnded { get; set; }
    public long PlatformerGoMs { get; set; }
    public byte PlatformerState { get; set; } = FestivalGames.PlatformerReady;
    // Dilemma / Masquerade
    public int DilemmaStep { get; set; }
    public long DilemmaAt { get; set; } = long.MaxValue;
    public bool DilemmaDone { get; set; }
    public bool RolesGiven { get; set; }
    // Moving platforms
    public long VesselAt { get; set; }
    public long VesselFullAt { get; set; }
    public Dictionary<int, Vector3d> VesselSent { get; } = [];
    /// <summary>Reward mail waiting for the host's async tick: character, Post.dat id, items.</summary>
    public List<(Guid To, int Post, List<(int ItemId, long Count)> Items)> Mail { get; } = [];

    /// <summary>The ranking: most points first (ties: who got there first keeps the order of joining), players with points only.</summary>
    public List<FestivalRank> Ranks() =>
        Scores.Values.Where(s => s.Points > 0).OrderByDescending(s => s.Points).Select((s, i) => new FestivalRank(i + 1, s.Points, s.Server, s.DbId, s.Name, s.Guild, s.Emblem)).ToList();
}
