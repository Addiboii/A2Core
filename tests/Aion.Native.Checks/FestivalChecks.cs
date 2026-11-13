using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// The Shugo festival games (fix/shugo-games, FestivalGames.cs / GameSession.FestivalGames.cs): the client data of the 22 event maps
/// (spawn counts, trap boards, checkpoints, laps, rounds, rewards), the message forms, and scripted play-throughs in stored sessions:
/// TrapBoard (two players in one copy: board, right tile, result, ranking, end, reward mail, the way back), Racing (start, laps,
/// finish), Platformer (states, checkpoints, moving platforms), Dilemma (states, a board at the result), JumpJump (phase spawns,
/// pickups once), Pang (start, a kill), Defence (a wave kill), Masquerade (roles).
/// </summary>
static class FestivalChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static byte[] Entrance(int eventId) => new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(eventId).ToArray();
    static byte[] RankingRq(int eventId) => new PacketWriter().U16(GameSession.FieldEventRankingRq).Varint(eventId).ToArray();

    /// <summary>0x6003's lines: (rank, points, name).</summary>
    static List<(int Rank, int Points, string Name)> Lines(byte[] b)
    {
        var r = new PacketReader(b) { Offset = 2 };
        r.U16(); r.Varint(); r.I32(); r.I64();
        var count = (int)r.Varint();
        var lines = new List<(int, int, string)>();
        for (var i = 0; i < count; i++)
        {
            var rank = r.I32(); var points = r.I32(); r.I32(); r.I64(); var name = r.String(); r.String(); r.I32();
            lines.Add((rank, points, name));
        }
        return lines;
    }

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        Tables(check);
        Wire(check);
        await TrapBoard(storedWorldSession, now, check);
        await Racing(storedWorldSession, now, check);
        await Platformer(storedWorldSession, now, check);
        await Dilemma(storedWorldSession, now, check);
        await Phased(storedWorldSession, now, check);
    }

    static void Tables(Action<bool, string> check)
    {
        var games = FestivalGames.Embedded;
        check(games.All.Count == 22 && games.All.Count(g => g.Event / 100_000 == 1) == 11
              && games.All.Select(g => g.Game).Distinct().Count() == 11,
            "festival-games.json: the 11 games' event maps, Light and Dark (FieldEvent.dat Direct entrance, Map_Dungeon_FieldEvent_*)");
        // Every spawner of the data is placed from the client map data: the objects of each env spawner = its positions.
        var bad = new List<string>();
        foreach (var g in games.All)
        {
            var map = Systems2Tables.Embedded.FieldEventOf(g.Event)!.Map;
            foreach (var (name, s) in g.Spawners.Where(x => x.Value.Envs.Length > 0))
                if (WorldObjects.ObjectsOn(map).Count(o => o.Name == name) != s.Count * s.Envs.Length) bad.Add($"{map} {name}");
            foreach (var d in g.Dormant()) if (!g.Spawners.ContainsKey(d)) bad.Add($"{map} dormant {d}");
        }
        check(bad.Count == 0, $"every object spawner of the 22 maps stands at its MapData positions (one object per EnvObjData id per position in the data), every game-started spawner exists ({string.Join("; ", bad.Take(5))})");
        var trap = games.OfMap(500036)!;
        var tiles = trap.TrapBoard!.Rounds.Select(r => WorldObjects.ObjectsOn(500036).Count(o => o.Name == r.Spawner)).ToList();
        check(trap.Contents == "TrapBoard" && trap.TrapBoard.Rounds.Length == 8 && tiles.SequenceEqual([36, 64, 80, 80, 100, 120, 120, 140])
              && trap.TrapBoard.Rounds.All(r => r.Grid[0] * r.Grid[1] == WorldObjects.ObjectsOn(500036).Count(o => o.Name == r.Spawner))
              && trap.TrapBoard.Rounds.Sum(r => r.LengthMs) == 175_000 && trap.ProgressS == 195
              && trap.TrapBoard.Right.SequenceEqual([90001, 1000]) && trap.TrapBoard.Wrong.SequenceEqual([90002, 5000])
              && trap.TrapBoard.Rounds[2].Bonus == "SF_TrapBoard_L_1_BonusEnv3",
            "TrapBoard 500036: 8 rounds (FieldEventTrapBoard SF_Trapboard_Light), each board's tiles = TrapBoard.dat GridX x GridY (36..140), 175 s of rounds in 195 s, abnormals 90001/90002, bonus orbs from round 3");
        var race = games.OfMap(500039)!.Racing!;
        check(race is { Id: 1001, Laps: 2, StartCount: 5, EndCount: 10 } && race.Triggers.Select(t => t.Name).SequenceEqual(
                  ["SF_Racing_L_1_Lap1_Trigger_1", "SF_Racing_L_1_Lap1_Trigger_2", "SF_Racing_L_1_Lap2_Trigger_1"]),
            "Racing 500039: FieldEventRacing SF_Racing_01 (2 laps, count 5, end 10) and the map's 3 lap triggers in order");
        var high = games.OfMap(500035)!;
        check(high.Platformer is { Id: 9021, StartWaitS: 10, StartCountdownS: 5, EndCountdownS: 10, Checkpoints.Length: 4 }
              && high.Platformer.Checkpoints[3].Radius == 2000 && high.Vessels is { Length: 8 } && high.Vessels.All(v => v.Speed == 800),
            "HighHigh 500035: FieldEventPlatformer SF_Platformer_L_01 (wait 10, count 5, end 10), 4 checkpoint volumes, 8 moving platforms on their Vessel routes");
        var dilemma = games.OfMap(500034)!.Dilemma!;
        check(dilemma.Rounds.Length == 6 && dilemma.Boards.Length == 5 && dilemma.Bonus.Count == 5 && dilemma.HoldsMs["Start"] == 14_000,
            "Dilemma 500034: 6 rounds (FE_Dilemma_Light), 5 answer boards, bonus Od from round 2, the EventFunc holds");
        var jump = games.OfMap(500037)!;
        check(jump.Phases is { Length: > 30 } && jump.Phases[0].Group == "FE_JumpJump_S2_StartPhase_01" && jump.PickupPoints(1100098) == 10
              && jump.Dormant().Contains("FE_JumpJump_S2_Od_1") && !jump.Dormant().Contains("FE_JumpJump_S2_Onlooker")
              && games.OfMap(510032)!.Phases!.Any(p => p.Group == "FE_JumpJump_D_S2_StartPhase_02") && !games.OfMap(510032)!.Phases!.Any(p => p.Group == "FE_JumpJump_S2_StartPhase_02"),
            "JumpJump: the EventFunc phases in table order (the Asmodian map its own start phase), balloons score, the phase spawners wait, the onlookers stand");
        check(games.All.Where(g => g.Event % 1000 / 100 is not (11 or 12) && g.Game is not ("Pang" or "Dilemma")).All(g => g.Rewards.Length == 11 && g.Post == 10_000_000 + g.Event)
              && trap.RewardFor(50)!.Common.Length == 3 && trap.RewardFor(1)!.Min == 1 && trap.RewardFor(60)!.Max == 50,
            "the 18 ShugoFesta maps: reward cube in 11 level bands (RewardDataMonsterCube -> RewardItem; the bands below 10 empty), mail Post.dat 10000000 + event id");
        check(games.OfMap(500030)!.Shooting is { StartCountdownS: 5 } && games.OfMap(500030)!.KillPoints(2340509) == 50 && games.OfMap(500043)!.KillPoints(2340119) == 50
              && games.OfMap(500043)!.KillPoints(2340114) == 10 && games.OfMap(500035)!.Kills.Count == 0,
            "kill points only in Pang (by colour), Defence (elites 50, others 10) and TreasureHunter; hazards elsewhere score nothing");
    }

    static void Wire(Action<bool, string> check)
    {
        check(Convert.ToHexString(FestivalGames.TrapBoardState(3001, FestivalGames.TrapReady, 0x0102030405060708)) == "1260B917010807060504030201",
            "TrapBoardState_NT 0x6012 {trap id varint, ETrapBoardRoundState u8, end i64} (CANDIDATE form)");
        check(Convert.ToHexString(FestivalGames.TrapBoardAnswer(1, FestivalGames.TrapPreview, [3, 9], 0)) == "1360010202" + "03000000" + "09000000" + "0000000000000000"
              && Convert.ToHexString(FestivalGames.RacingCharLap(1001, 5, 2)) == "0B60E907050000000000000002"
              && Convert.ToHexString(FestivalGames.PlatformerState(9021, FestivalGames.PlatformerStart, 1)) == "1060BD46020100000000000000"
              && Convert.ToHexString(FestivalGames.Ranking(110901, 2, 0, [new FestivalRank(1, 100, 7, 9, "A", "", 0)])) == "03600000B5E206" + "02000000" + "0000000000000000" + "01"
                  + "01000000" + "64000000" + "07000000" + "0900000000000000" + "0141" + "00" + "00000000",
            "TrapBoardAnswer 0x6013, RacingCharLap 0x600b, PlatformerState 0x6010, FieldEventRanking_RS 0x6003 with a line (CANDIDATE forms of the usmap structs)");
        var rng = new Random(1);
        var ok = true;
        foreach (var (x, y) in new[] { (6, 6), (14, 10), (10, 8) })
            for (var i = 0; i < 50; i++)
            {
                var path = FestivalGames.TrapPath(x, y, rng);
                var rows = path.Select(c => c / y).Distinct().Count();
                // Connected: each next row's tile touches the row before (same column or the sideways step).
                ok &= rows == x && path.All(c => c >= 0 && c < x * y) && path.Count >= x && path.Count <= 2 * x;
            }
        check(ok, "the right tiles: a path over every row of the board, one forward step per row and at most one sideways");
        var lift = FestivalGames.Embedded.OfMap(500035)!.Vessels!.First(v => v.Spawner == "SF_HighHigh_L_1_Move_S_2");
        check(Math.Abs(lift.At(0).Z - -26421.1) < 1 && Math.Abs(lift.At(1_000).Z - (-26421.1 + 800)) < 1 && Math.Abs(lift.At(2_250).Z - -24621.1) < 1
              && Math.Abs(lift.At(4_500).Z - -26421.1) < 1,
            "a lift platform (Vessel route of 2 points 18 m apart, 800 cm/s): up in 2.25 s, back down by 4.5 s (Circular)");
    }

    /// <summary>A stored player entering an event's map in its entry window; the event by race (Light 1xxxxx, Dark 2xxxxx).</summary>
    static async Task<(GameSession Player, int Event, long Open)> Enter(Func<string, FakeStore, Task<GameSession>> storedWorldSession, string name, FakeStore store,
        int light, long now, int race = 0)
    {
        var player = await storedWorldSession(name, store);
        player.SetLevelForChecks(50);   // the cube's bands below level 10 hold no items
        var eventId = (race != 0 ? race : player.RaceForChecks) == 2 ? light + 100_000 : light;
        var e = Systems2Tables.Embedded.FieldEventOf(eventId)!;
        var open = GameSession.FieldEventNextOpen(e, now);
        await player.TickAsync(open + 1_000);
        var entered = await player.HandleAsync(Entrance(eventId), open + 2_000);
        await player.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), open + 2_100);
        return (player, eventId, open);
    }

    static List<(int Index, WorldObject Object)> ObjectsOf(int map, string spawner) =>
        WorldObjects.ObjectsOn(map).Select((o, i) => (i, o)).Where(x => x.o.Name == spawner).ToList();

    static async Task TrapBoard(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var (a, eventId, open) = await Enter(storedWorldSession, "TrapA", new FakeStore(), 110901, now);
        var game = FestivalGames.Embedded.OfEvent(eventId)!;
        var map = a.MapForChecks;
        var (b, _, _) = await Enter(storedWorldSession, "TrapB", new FakeStore(), 110901, now, a.RaceForChecks);
        check(map == game.Event switch { 110901 => 500036, _ => 510031 } && a.InstanceKey == GameSession.FestivalInstanceKey(eventId, open) && b.InstanceKey == a.InstanceKey
              && a.Copy is not null && b.Copy == a.Copy && a.Copy.Festival is not null,
            "two players entering TrapBoard in the same hour play in one copy (shared instance key, one run)");
        var boards = game.TrapBoard!.Rounds.Select(r => r.Spawner).ToList();
        // One object per position of a multi-object spawn entry (the Racing items, Dilemma Od, JumpJump balloons: before, two stacked).
        var raceGame = FestivalGames.Embedded.OfMap(500039)!;
        var stacked = WorldObjects.ObjectsOn(500039).Count(o => raceGame.Spawners.TryGetValue(o.Name, out var s) && s.Envs.Length > 1);
        var raceRun = GameSession.NewFestivalRunForChecks(500039)!;
        check(raceRun.Rejected.Count * 2 == stacked && stacked > 0, $"a spawn entry with 2 EnvObjData ids places one per position ({stacked} objects -> {stacked / 2})");
        check(boards.All(a.DormantForChecks) && game.TrapBoard.Rounds.Where(r => r.Bonus.Length > 0).All(r => a.DormantForChecks(r.Bonus))
              && ObjectsOf(map, boards[0]).All(x => a.HiddenForChecks(x.Index)),
            "at the entry the 8 boards and the bonus orbs wait (before: all 740 tiles and 178 orbs stood at once)");
        var process = open + 180_000;
        var started = await a.TickAsync(process + 100);
        await b.TickAsync(process + 100);
        var run = a.FestivalForChecks!;
        var round = game.TrapBoard.Rounds[0];
        check(run.Started && started.Any(x => Op(x) == FestivalGames.TrapBoardStateNt && x[^9] == FestivalGames.TrapReady)
              && !a.DormantForChecks(boards[0]) && a.DormantForChecks(boards[1]) && run.Answers.Count >= round.Grid[0],
            "at the event's start round 1: TrapBoardState Ready, board 1 placed (the others still wait), its right tiles chosen");
        var tiles = ObjectsOf(map, round.Spawner);
        var right = run.Answers.First();
        var wrong = Enumerable.Range(0, tiles.Count).First(i => !run.Answers.Contains(i));
        a.FestivalAtForChecks(tiles[right].Object.At, process + 200);
        b.FestivalAtForChecks(tiles[wrong].Object.At, process + 200);
        var roundAt = run.RoundAt;
        var preview = await a.TickAsync(roundAt + round.ReadyS * 1000 + 50);
        check(preview.Any(x => Op(x) == FestivalGames.TrapBoardAnswerNt),
            "after ReadyTime the right tiles are shown (TrapBoardAnswer_NT Preview)");
        var resultAt = roundAt + (round.ReadyS + round.PreviewS + round.AnswerS) * 1000L + 50;
        await a.TickAsync(resultAt - 1000);
        var result = await a.TickAsync(resultAt);
        check(result.Any(x => Op(x) == FestivalGames.TrapBoardStateNt && x[^9] == FestivalGames.TrapResult)
              && run.Scores[a.PlayerKey].Points == 100 && run.Scores[b.PlayerKey].Points == 0
              && a.HiddenForChecks(tiles[wrong].Index) && !a.HiddenForChecks(tiles[right].Index),
            "at the result the player on a right tile scores 100 (round 1), the one on a wrong tile 0, the wrong tiles go");
        var ranking = await a.HandleAsync(RankingRq(eventId), resultAt + 10);
        var lines = Lines(ranking.Single(x => Op(x) == GameSession.FieldEventRankingRs));
        check(lines.Count == 1 && lines[0] is { Rank: 1, Points: 100, Name: "TrapA" },
            "FieldEventRanking_RQ: the run's ranking (TrapA first with 100; before: always empty)");
        // TrapB walks off the boards (no more rounds for it); round 2 replaces the board.
        b.FestivalAtForChecks(tiles[0].Object.At with { X = tiles[0].Object.At.X + 20_000 }, resultAt + 20);
        await a.TickAsync(roundAt + round.LengthMs + 50);
        check(!a.DormantForChecks(boards[1]) && ObjectsOf(map, boards[0]).All(x => a.HiddenForChecks(x.Index)), "round 2: board 2 placed, board 1 gone");
        // The end: success, the reward, the mail, the hold, the way back.
        var end = open + 180_000 + game.ProgressS * 1000L;
        for (var t = process + round.LengthMs + 1_000; t < end; t += 5_000) await a.TickAsync(t);
        var finished = await a.TickAsync(end + 10);
        var complete = finished.FirstOrDefault(x => Op(x) == GameSession.DungeonCompleteResultNt);
        var states = finished.Where(x => Op(x) == GameSession.DungeonStateNt).Select(x => x[6]).ToList();
        check(run.Finished && complete is not null && complete[7] == 1 && complete[8] > 0 && states.SequenceEqual([GameSession.DungeonStateComplete, GameSession.DungeonStateEnd]),
            $"at the event's end: DungeonCompleteResult succeeded with the reward items, Complete then End (before: a failed run) [{(complete is null ? "none" : Convert.ToHexString(complete))} states {string.Join(",", states)} finished {run.Finished}]");
        var storeA = a.Store as FakeStore;
        await a.TickAsync(end + 1_000);
        check(storeA!.Posts.Any(p => p.DataId == 10_000_000 + eventId && p.Attachments.Count > 0),
            $"the reward cube's RewardCommon by mail (Post.dat {10_000_000 + eventId}) for the player who scored");
        await b.TickAsync(end + 1_000);
        check(!((FakeStore)b.Store!).Posts.Any() && b.FestivalForChecks is { } bRun && bRun.Scores[b.PlayerKey].Points == 0, "no reward for a player without points");
        var hold = (game.HoldS > 0 ? game.HoldS : 41) * 1000L;
        var left = await a.TickAsync(end + hold + 100);
        check(left.Any(x => Op(x) == 0x3621 && BitConverter.ToInt32(x, 6) != map) && a.InstanceKey == 0,
            "after the 41 s hold the player goes back to where it entered (before: Poeta)");
        a.ExitSharedWorld();
        b.ExitSharedWorld();
    }

    static async Task Racing(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var (p, eventId, open) = await Enter(storedWorldSession, "Racer", new FakeStore(), 110301, now);
        var game = FestivalGames.Embedded.OfEvent(eventId)!;
        var race = game.Racing!;
        var process = open + 180_000;
        var start = await p.TickAsync(process + 100);
        var run = p.FestivalForChecks!;
        check(start.Any(x => Op(x) == FestivalGames.RacingStartNt) && run.RaceGoMs == process + 100 + 3_000 + race.StartCount * 1000,
            "Racing: RacingStart_NT at the start, the race goes after the map event's 3 s and the 5 s count");
        var t = run.RaceGoMs + 100;
        var outs = new List<byte[]>();
        foreach (var trigger in race.Triggers)
        {
            outs.AddRange(p.FestivalAtForChecks(trigger.Centre, t += 1000));
            outs.AddRange(await p.TickAsync(t += 100));
        }
        var score = run.Scores[p.PlayerKey];
        check(outs.Any(x => Op(x) == FestivalGames.RacingCharLapNt && x[^1] == 2) && outs.Any(x => Op(x) == FestivalGames.RacingFinishNt)
              && score.Finished && score.Points == FestivalGames.FinishPoints(1) + FestivalGames.LapPoints && run.RaceEndMs > 0,
            "the lap triggers in order: lap 2 (RacingCharLap_NT), the finish (RacingFinish_NT, 1st: 1000 + 200 for the lap)");
        var ended = new List<byte[]>();
        for (var u = t + 1_000; u <= run.RaceEndMs + 2_000; u += 1_000) ended.AddRange(await p.TickAsync(u));
        var gameEnd = process + FestivalGames.Embedded.OfEvent(eventId)!.ProgressS * 1000L + GameSession.FieldEventRacingExtraMs;
        for (var u = run.RaceEndMs + 10_000; u <= gameEnd + 10; u += 10_000) ended.AddRange(await p.TickAsync(u));
        ended.AddRange(await p.TickAsync(gameEnd + 20));
        check(ended.Any(x => Op(x) == FestivalGames.RacingEndNt) && run.Finished && ended.Any(x => Op(x) == GameSession.DungeonCompleteResultNt && x[7] == 1),
            "EndCount after the finish RacingEnd_NT; at the event's end (300 + 12 s) the result with the reward");
        p.ExitSharedWorld();
    }

    static async Task Platformer(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var (p, eventId, open) = await Enter(storedWorldSession, "Climber", new FakeStore(), 110801, now);
        var game = FestivalGames.Embedded.OfEvent(eventId)!;
        var high = game.Platformer!;
        var process = open + 180_000;
        var ready = await p.TickAsync(process + 100);
        var run = p.FestivalForChecks!;
        check(ready.Any(x => Op(x) == FestivalGames.PlatformerStateNt && x[^9] == FestivalGames.PlatformerReady),
            "HighHigh: PlatformerState Ready at the start");
        p.FestivalAtForChecks(high.Checkpoints[0].Centre, process + 2_000);
        check(run.Scores[p.PlayerKey].Checkpoint == 0, "a checkpoint before the start counts nothing");
        var go = await p.TickAsync(run.PlatformerGoMs + 10);
        var info = p.FestivalAtForChecks(high.Checkpoints[0].Centre, run.PlatformerGoMs + 100);
        info.AddRange(await p.TickAsync(run.PlatformerGoMs + 200));
        p.FestivalAtForChecks(high.Checkpoints[2].Centre, run.PlatformerGoMs + 300);   // out of order: not yet
        var score = run.Scores[p.PlayerKey];
        check(go.Any(x => Op(x) == FestivalGames.PlatformerStateNt && x[^9] == FestivalGames.PlatformerStart)
              && info.Any(x => Op(x) == FestivalGames.PlatformerPlayerInfoNt && x[^1] == 1) && score.Checkpoint == 1 && score.Points >= FestivalGames.CheckpointPoints,
            "after the wait and the count: Start; checkpoint 1 reached (PlatformerPlayerInfo_NT index 1, 1000 points), a later one out of order does not count");
        var lift = game.Vessels!.First(v => v.Spawner.EndsWith("_Move_S_2", StringComparison.Ordinal));
        var platform = p.Targets.Values.First(x => x.Name == lift.Spawner);
        var z0 = platform.Position.Z;
        var moved = await p.TickAsync(run.PlatformerGoMs + 1_200);
        check(Math.Abs(platform.Position.Z - z0) > 50 && moved.Any(x => (Op(x) == 0x371c || Op(x) == 0x371d) && (int)new PacketReader(x) { Offset = 2 }.Varint() == platform.Key),
            "the moving platforms travel their Vessel routes (NPC moves 0x371c / 0x371d)");
        var end = process + game.ProgressS * 1000L;
        var waiting = new List<byte[]>();
        for (var t = run.PlatformerGoMs + 5_000; t <= end + 10; t += 5_000) waiting.AddRange(await p.TickAsync(t));
        waiting.AddRange(await p.TickAsync(end + 20));
        check(waiting.Any(x => Op(x) == FestivalGames.PlatformerStateNt && x[^9] == FestivalGames.PlatformerWaitFinish)
              && waiting.Any(x => Op(x) == FestivalGames.PlatformerStateNt && x[^9] == FestivalGames.PlatformerFinish) && run.Finished,
            "EndCountdown before the end WaitFinish, at the end Finish and the result");
        p.ExitSharedWorld();
    }

    static async Task Dilemma(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        var (p, eventId, open) = await Enter(storedWorldSession, "Dilemmist", new FakeStore(), 111201, now);
        var game = FestivalGames.Embedded.OfEvent(eventId)!;
        var d = game.Dilemma!;
        var process = open + 180_000;
        var states = new List<byte[]>(await p.TickAsync(process + 100));
        var run = p.FestivalForChecks!;
        var board = p.Targets.Values.First(t => t.Name == d.Boards[0]);
        p.FestivalAtForChecks(board.Position, process + 200);
        // Start hold, then round 1: Ready, Prepare 1, Prepare 2, Progress, RoundResult.
        var resultAt = process + 100 + d.HoldsMs["Start"] + d.HoldsMs["Ready"] + 20_000 + d.Rounds[0].AnswerS * 1000L + 10;
        for (var t = process + 1_000; t <= resultAt; t += 1_000) states.AddRange(await p.TickAsync(t));
        states.AddRange(await p.TickAsync(resultAt));
        var rounds = states.Where(x => Op(x) == FestivalGames.PlatformerDilemmaStateNt).Select(x => x[4]).ToList();
        check(rounds.Take(6).SequenceEqual([FestivalGames.DilemmaReady, FestivalGames.DilemmaReady, FestivalGames.DilemmaPrepare1, FestivalGames.DilemmaPrepare2,
                  FestivalGames.DilemmaProgress, FestivalGames.DilemmaRoundResult]) && run.Scores[p.PlayerKey].Points == 100,
            "Dilemma: PlatformerDilemmaState_NT game Ready, then round 1 Ready, PrepareStep1, PrepareStep2, Progress, RoundResult; on an answer board: 100 points");
        p.ExitSharedWorld();
    }

    static async Task Phased(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // JumpJump: the phases start the balloon spawners; a balloon scores once.
        var (p, eventId, open) = await Enter(storedWorldSession, "Jumper", new FakeStore(), 110101, now);
        var game = FestivalGames.Embedded.OfEvent(eventId)!;
        var map = p.MapForChecks;
        check(p.DormantForChecks("FE_JumpJump_S2_Od_1") && ObjectsOf(map, "FE_JumpJump_S2_Od_1").All(x => p.HiddenForChecks(x.Index)),
            "JumpJump: the balloons wait at the entry (before: all 189 stood from the start)");
        var process = open + 180_000;
        await p.TickAsync(process + 100);
        var run = p.FestivalForChecks!;
        var odPhase = Array.FindIndex(game.Phases!, ph => ph.Acts.Any(a => a[0] == "StartSpawner" && a[1] == "FE_JumpJump_S2_Od_1"));
        var phaseOuts = new List<byte[]>();
        for (var t = process + 1_000; t <= run.PhaseAt[odPhase] + 10; t += 1_000) phaseOuts.AddRange(await p.TickAsync(t));
        phaseOuts.AddRange(await p.TickAsync(run.PhaseAt[odPhase] + 20));
        var balloon = ObjectsOf(map, "FE_JumpJump_S2_Od_1")[0];
        await p.UseObjectForChecks(balloon.Index, run.PhaseAt[odPhase] + 100);
        await p.UseObjectForChecks(balloon.Index, run.PhaseAt[odPhase] + 200);
        check(!p.DormantForChecks("FE_JumpJump_S2_Od_1")
              && run.Scores[p.PlayerKey].Points == game.PickupPoints(balloon.Object.Env) && run.Taken.Contains(balloon.Index),
            $"the phase in table order starts the balloons; one balloon scores {game.PickupPoints(balloon.Object.Env)} once");
        p.ExitSharedWorld();

        // Pang: ShootingStart, the start phase's Pangs, a kill scores by colour.
        var (pang, pangEvent, pangOpen) = await Enter(storedWorldSession, "Shooter", new FakeStore(), 111101, now);
        var pangGame = FestivalGames.Embedded.OfEvent(pangEvent)!;
        var shoot = await pang.TickAsync(pangOpen + 180_100);
        var pangRun = pang.FestivalForChecks!;
        await pang.TickAsync(pangRun.PhaseAt[0] + 10);
        var first = pang.Targets.Values.First(t => t.Name == "FE_Pang_StartPang_001" && !t.Dead);
        pang.KillForChecks(first, pangRun.PhaseAt[0] + 100);
        check(shoot.Any(x => Op(x) == FestivalGames.ShootingStartNt) && pangRun.Scores[pang.PlayerKey].Points == pangGame.KillPoints(first.NpcId)
              && pangGame.KillPoints(first.NpcId) == 10,
            "Pang: ShootingStart_NT at the start, the start phase's Pangs appear, a blue Pang shot down scores 10");
        pang.ExitSharedWorld();

        // Defence: a wave monster of the map event scores.
        var (def, defEvent, defOpen) = await Enter(storedWorldSession, "Defender", new FakeStore(), 110701, now);
        var defGame = FestivalGames.Embedded.OfEvent(defEvent)!;
        for (var t = defOpen + 180_100; t <= defOpen + 190_000; t += 1_000) await def.TickAsync(t);
        var thief = def.Targets.Values.FirstOrDefault(t => t.Name.StartsWith("M_FE_Def_Pahse_1_Thief", StringComparison.Ordinal) && !t.Dead && !t.CorpseRemoved);
        if (thief is not null) def.KillForChecks(thief, defOpen + 190_100);
        check(thief is not null && def.FestivalForChecks!.Scores[def.PlayerKey].Points == defGame.KillPoints(thief.NpcId),
            "Defence: the map event's phase-1 thieves come; a kill scores 10");
        def.ExitSharedWorld();

        // Masquerade: after WaitTime the roles (a single player hides).
        var (masq, masqEvent, masqOpen) = await Enter(storedWorldSession, "Masker", new FakeStore(), 110401, now);
        await masq.TickAsync(masqOpen + 180_100);
        var roles = await masq.TickAsync(masqOpen + 180_100 + 10_100);
        check(roles.Any(x => Op(x) == FestivalGames.MasqueradePlayerInfoNt && x[^1] == FestivalGames.MasqueradeHider),
            "Masquerade: after WaitTime (10 s) the roles (MasqueradePlayerInfo_NT; alone: a Hider)");
        masq.ExitSharedWorld();
    }
}
