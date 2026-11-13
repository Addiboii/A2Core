using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

// The Shugo festival mini-games (fix/shugo-games): the client data of each event map (tools/festival_games.py ->
// tools/data/festival-games.json) and the game messages. GameSession.FestivalGames.cs runs them.
//
// NOT RECORDED: every official recording ends at the move to the dungeon server (rec 20261106-105448 #80582: 0x6008, then
// 0x3647 and 0x3616 to the festival's server), so nothing inside an event map is OBSERVED. The messages are the usmap structs
// of catalogue 549-572 (FieldEventInfo_NT .. FieldEventPlatformerDilemmaState_NT). Live opcodes: 549-557 are OBSERVED at
// 0x6000-0x6008 (+0x5ddb), 573+ at 0x6100+ (+0x5ec3); the run of 558-572 is taken as the 0x6000 block's continuation
// 0x6009-0x6017 (CANDIDATE: the block that starts at 0x6000 with catalogue 549 runs up to the 0x6100 block of catalogue 573;
// the other reading, 0x60f1-0x60ff, is what DailyDungeons.cs assumed for 569/570). Field widths follow the OBSERVED FieldEvent
// family (result u16, event ids varint, times i64) and the usmap for the rest (i64 character dbids, u8 enums, u8-length strings):
// CANDIDATE throughout.

public sealed record FestivalSpawner([property: JsonPropertyName("npcs")] int[] Npcs, [property: JsonPropertyName("envs")] int[] Envs,
    [property: JsonPropertyName("n")] int Count, [property: JsonPropertyName("tb")] int TrapBoard);

/// <summary>An EventFunc.dat group the game runs: its rows {action, value1..4}.</summary>
public sealed record FestivalPhase([property: JsonPropertyName("group")] string Group, [property: JsonPropertyName("acts")] string[][] Acts);

/// <summary>A MapData trigger volume (lap triggers, platformer checkpoints): a rotated box (half extents) or a cylinder.</summary>
public sealed record FestivalVolume([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("at")] double[] At,
    [property: JsonPropertyName("dir")] double Dir, [property: JsonPropertyName("shape")] string Shape, [property: JsonPropertyName("box")] double[] Box,
    [property: JsonPropertyName("radius")] double Radius, [property: JsonPropertyName("height")] double Height)
{
    public Vector3d Centre => new(At[0], At[1], At[2]);

    public bool Contains(Vector3d p)
    {
        double dx = p.X - At[0], dy = p.Y - At[1], dz = p.Z - At[2];
        if (Math.Abs(dz) > Math.Max(Height, 500)) return false;
        if (Shape != "Box" || Box.Length < 2 || Box[0] <= 0) return Math.Sqrt(dx * dx + dy * dy) <= Math.Max(Radius, 100);
        var r = -Dir * Math.PI / 180;
        double lx = dx * Math.Cos(r) - dy * Math.Sin(r), ly = dx * Math.Sin(r) + dy * Math.Cos(r);
        return Math.Abs(lx) <= Box[0] && Math.Abs(ly) <= Box[1];
    }
}

public sealed record TrapRound([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("board")] string Board, [property: JsonPropertyName("board_id")] int BoardId,
    [property: JsonPropertyName("grid")] int[] Grid, [property: JsonPropertyName("spawner")] string Spawner,
    [property: JsonPropertyName("bonus")] string Bonus, [property: JsonPropertyName("ready_s")] int ReadyS,
    [property: JsonPropertyName("ready_count")] int ReadyCount, [property: JsonPropertyName("preview_s")] int PreviewS,
    [property: JsonPropertyName("answer_s")] int AnswerS, [property: JsonPropertyName("result_s")] int ResultS,
    [property: JsonPropertyName("talk")] string[] Talk)
{
    public int LengthMs => (ReadyS + PreviewS + AnswerS + ResultS) * 1000;
}

public sealed record TrapBoardGame([property: JsonPropertyName("rounds")] TrapRound[] Rounds,
    [property: JsonPropertyName("right")] int[] Right, [property: JsonPropertyName("wrong")] int[] Wrong);

public sealed record RacingGame([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("laps")] int Laps,
    [property: JsonPropertyName("start_count")] int StartCount, [property: JsonPropertyName("end_count")] int EndCount,
    [property: JsonPropertyName("track")] string Track, [property: JsonPropertyName("triggers")] FestivalVolume[] Triggers);

public sealed record PlatformerGame([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("start_wait_s")] int StartWaitS,
    [property: JsonPropertyName("start_countdown_s")] int StartCountdownS, [property: JsonPropertyName("end_countdown_s")] int EndCountdownS,
    [property: JsonPropertyName("checkpoints")] FestivalVolume[] Checkpoints);

public sealed record DilemmaRound([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("answer_s")] int AnswerS,
    [property: JsonPropertyName("talk")] string[] Talk);

public sealed record DilemmaGame([property: JsonPropertyName("rounds")] DilemmaRound[] Rounds, [property: JsonPropertyName("boards")] string[] Boards,
    [property: JsonPropertyName("doubles")] string[] Doubles, [property: JsonPropertyName("bonus")] Dictionary<string, string[]> Bonus,
    [property: JsonPropertyName("holds_ms")] Dictionary<string, int> HoldsMs, [property: JsonPropertyName("hold_abnormal")] int HoldAbnormal,
    [property: JsonPropertyName("right")] int[] Right, [property: JsonPropertyName("wrong")] int Wrong);

public sealed record MasqueradeGame([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("wait_s")] int WaitS);

public sealed record ShootingGame([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("start_countdown_s")] int StartCountdownS,
    [property: JsonPropertyName("end_countdown_s")] int EndCountdownS);

/// <summary>A moving platform: a Vessel NPC and the MapData Vessel waypoint set that starts at its spawn.</summary>
public sealed record FestivalVessel([property: JsonPropertyName("spawner")] string Spawner, [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("points")] double[][] Points, [property: JsonPropertyName("traverse")] string Traverse,
    [property: JsonPropertyName("speed")] double Speed)
{
    /// <summary>Where the platform is after <paramref name="ms"/> of travel: along the points at Speed cm/s, Circular back to the
    /// first point, TwoWay back and forth.</summary>
    public Vector3d At(long ms)
    {
        var pts = Points.Select(p => new Vector3d(p[0], p[1], p[2])).ToList();
        if (pts.Count < 2 || Speed <= 0) return pts.Count > 0 ? pts[0] : default;
        var legs = new List<(Vector3d From, Vector3d To, double Length)>();
        for (var i = 0; i + 1 < pts.Count; i++) legs.Add((pts[i], pts[i + 1], Math.Sqrt(pts[i].DistanceSquared(pts[i + 1]))));
        if (Traverse == "TwoWay") for (var i = pts.Count - 1; i > 0; i--) legs.Add((pts[i], pts[i - 1], Math.Sqrt(pts[i].DistanceSquared(pts[i - 1]))));
        else legs.Add((pts[^1], pts[0], Math.Sqrt(pts[^1].DistanceSquared(pts[0]))));
        var total = legs.Sum(l => l.Length);
        if (total <= 0) return pts[0];
        var travel = Speed * ms / 1000.0 % total;
        foreach (var (from, to, length) in legs)
        {
            if (travel <= length && length > 0)
            {
                var f = travel / length;
                return new Vector3d(from.X + (to.X - from.X) * f, from.Y + (to.Y - from.Y) * f, from.Z + (to.Z - from.Z) * f);
            }
            travel -= length;
        }
        return pts[0];
    }
}

/// <summary>A reward cube level band: RewardItem rows {item, chance in 1e8, min, max} of RewardCommon and RewardKey.</summary>
public sealed record FestivalReward([property: JsonPropertyName("min")] int Min, [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("common")] long[][] Common, [property: JsonPropertyName("key")] long[][] Key);

/// <summary>One event map's game (FieldEvent.dat row, its map's spawns and the contents tables).</summary>
public sealed record FestivalGame(
    [property: JsonPropertyName("event")] int Event, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("game")] string Game, [property: JsonPropertyName("contents")] string Contents,
    [property: JsonPropertyName("value")] string Value, [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("progress_s")] int ProgressS, [property: JsonPropertyName("ready_s")] int ReadyS,
    [property: JsonPropertyName("hold_s")] int HoldS, [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("cube")] string Cube, [property: JsonPropertyName("post")] int Post,
    [property: JsonPropertyName("respawn")] string[] Respawn,
    [property: JsonPropertyName("spawners")] Dictionary<string, FestivalSpawner> Spawners,
    [property: JsonPropertyName("pickups")] Dictionary<string, int> Pickups,
    [property: JsonPropertyName("kills")] Dictionary<string, int> Kills,
    [property: JsonPropertyName("rewards")] FestivalReward[] Rewards,
    [property: JsonPropertyName("phases")] FestivalPhase[]? Phases = null,
    [property: JsonPropertyName("trapboard")] TrapBoardGame? TrapBoard = null,
    [property: JsonPropertyName("racing")] RacingGame? Racing = null,
    [property: JsonPropertyName("platformer")] PlatformerGame? Platformer = null,
    [property: JsonPropertyName("dilemma")] DilemmaGame? Dilemma = null,
    [property: JsonPropertyName("masquerade")] MasqueradeGame? Masquerade = null,
    [property: JsonPropertyName("shooting")] ShootingGame? Shooting = null,
    [property: JsonPropertyName("vessels")] FestivalVessel[]? Vessels = null)
{
    /// <summary>Points for picking up this object (EnvObjData id), 0 for none.</summary>
    public int PickupPoints(int env) => Pickups.GetValueOrDefault(env.ToString(System.Globalization.CultureInfo.InvariantCulture));
    /// <summary>Points for killing this NPC (NpcData id), 0 for none.</summary>
    public int KillPoints(int npc) => Kills.GetValueOrDefault(npc.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The reward band of a character level (the highest band below it when above every band).</summary>
    public FestivalReward? RewardFor(int level) =>
        Rewards.FirstOrDefault(r => level >= r.Min && level <= r.Max) ?? Rewards.Where(r => level > r.Max).OrderBy(r => r.Max).LastOrDefault();

    /// <summary>
    /// Spawners the game starts itself (so not placed at the map load): the trap boards and their bonus orbs, the Dilemma round
    /// bonus Od, every StartSpawner of the game's EventFunc phases.
    /// </summary>
    public IEnumerable<string> Dormant()
    {
        foreach (var r in TrapBoard?.Rounds ?? [])
        {
            if (r.Spawner.Length > 0) yield return r.Spawner;
            if (r.Bonus.Length > 0) yield return r.Bonus;
        }
        // Every board spawner of the map, also those no round of this contents names.
        foreach (var (name, s) in Spawners) if (s.TrapBoard != 0 || name.Contains("_BonusEnv", StringComparison.Ordinal)) yield return name;
        if (Dilemma is { } dilemma) foreach (var names in dilemma.Bonus.Values) foreach (var n in names) yield return n;
        foreach (var p in Phases ?? []) foreach (var a in p.Acts) if (a.Length > 1 && a[0] == "StartSpawner") yield return a[1];
    }
}

public sealed class FestivalGames
{
    // Live opcodes (see the head of this file; 0x6003 is GameSession.FieldEventRankingRs).
    public const ushort FieldEventUserListNt = 0x6004, FieldEventEnterUserNt = 0x6005, FieldEventLeaveUserNt = 0x6006, FieldEventOfflineNt = 0x6009,
        RacingStartNt = 0x600a, RacingCharLapNt = 0x600b, RacingFinishNt = 0x600c, RacingEndNt = 0x600d,
        MasqueradePlayerInfoNt = 0x600e, MasqueradeCaptureMsgNt = 0x600f, PlatformerStateNt = 0x6010, PlatformerPlayerInfoNt = 0x6011,
        TrapBoardStateNt = 0x6012, TrapBoardAnswerNt = 0x6013, ShootingStartNt = 0x6016, PlatformerDilemmaStateNt = 0x6017;
    /// <summary>ETrapBoardRoundState (usmap).</summary>
    public const byte TrapReady = 1, TrapPreview = 2, TrapAnswer = 3, TrapResult = 4;
    /// <summary>EPlatformerState (usmap; the Dilemma's game state too).</summary>
    public const byte PlatformerReady = 1, PlatformerStart = 2, PlatformerWaitFinish = 3, PlatformerFinish = 4;
    /// <summary>EDilemmaRoundState (usmap).</summary>
    public const byte DilemmaReady = 1, DilemmaPrepare1 = 2, DilemmaPrepare2 = 3, DilemmaProgress = 4, DilemmaRoundResult = 5, DilemmaFinished = 6;
    /// <summary>EMasqueradeRole (usmap).</summary>
    public const byte MasqueradeHider = 1, MasqueradeSeeker = 2;

    sealed record File([property: JsonPropertyName("maps")] Dictionary<string, FestivalGame> Maps);

    readonly Dictionary<int, FestivalGame> byMap;

    FestivalGames(Dictionary<int, FestivalGame> maps) => byMap = maps;

    static readonly Lazy<FestivalGames> embedded = new(() =>
    {
        using var stream = typeof(FestivalGames).Assembly.GetManifestResourceStream("festival-games.json")
            ?? throw new InvalidOperationException("festival-games.json is not embedded");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("festival-games.json");
        return new FestivalGames(file.Maps.ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value));
    });

    public static FestivalGames Embedded => embedded.Value;
    public IReadOnlyCollection<FestivalGame> All => byMap.Values;
    public FestivalGame? OfMap(int map) => byMap.GetValueOrDefault(map);
    public FestivalGame? OfEvent(int eventId) => byMap.Values.FirstOrDefault(g => g.Event == eventId);

    // ---------- Messages (CANDIDATE forms, see the head of the file) ----------

    /// <summary>FieldEventRanking_RS 0x6003 {result u16, event varint, version i32, next ranking time i64, [{ranking i32, point i32,
    /// server i32, char dbid i64, nickname, guild name, emblem i32}]}.</summary>
    public static byte[] Ranking(int eventId, int version, long nextMs, IReadOnlyList<FestivalRank> ranks)
    {
        var w = PacketWriter.Opcode(GameSession.FieldEventRankingRs).U16(0).Varint(eventId).I32(version).I64(nextMs).Varint(ranks.Count);
        foreach (var r in ranks) w.I32(r.Rank).I32(r.Points).I32(r.Server).I64(r.DbId).String(r.Name).String(r.Guild).I32(r.Emblem);
        return w.ToArray();
    }

    public static byte[] RacingStart(int racingId, long startMs) => PacketWriter.Opcode(RacingStartNt).Varint(racingId).I64(startMs).ToArray();
    public static byte[] RacingCharLap(int racingId, long dbId, int lap) => PacketWriter.Opcode(RacingCharLapNt).Varint(racingId).I64(dbId).U8((byte)lap).ToArray();
    public static byte[] RacingFinish(int racingId, long endMs, int winnerServer, string winner) =>
        PacketWriter.Opcode(RacingFinishNt).Varint(racingId).I64(endMs).I32(winnerServer).String(winner).ToArray();
    public static byte[] RacingEnd(int racingId) => PacketWriter.Opcode(RacingEndNt).Varint(racingId).ToArray();
    public static byte[] MasqueradePlayerInfo(int id, long dbId, byte role) => PacketWriter.Opcode(MasqueradePlayerInfoNt).Varint(id).I64(dbId).U8(role).ToArray();
    public static byte[] MasqueradeCapture(int id, string hider, string seeker) => PacketWriter.Opcode(MasqueradeCaptureMsgNt).Varint(id).String(hider).String(seeker).ToArray();
    public static byte[] PlatformerState(int id, byte state, long startMs) => PacketWriter.Opcode(PlatformerStateNt).Varint(id).U8(state).I64(startMs).ToArray();
    public static byte[] PlatformerPlayerInfo(long dbId, int checkpoint) => PacketWriter.Opcode(PlatformerPlayerInfoNt).I64(dbId).U8((byte)checkpoint).ToArray();
    public static byte[] TrapBoardState(int trapId, byte state, long endMs) => PacketWriter.Opcode(TrapBoardStateNt).Varint(trapId).U8(state).I64(endMs).ToArray();
    public static byte[] TrapBoardAnswer(int trapId, byte state, IReadOnlyCollection<int> answers, long endMs)
    {
        var w = PacketWriter.Opcode(TrapBoardAnswerNt).Varint(trapId).U8(state).Varint(answers.Count);
        foreach (var a in answers) w.I32(a);
        return w.I64(endMs).ToArray();
    }
    public static byte[] ShootingStart(int id, long startMs) => PacketWriter.Opcode(ShootingStartNt).Varint(id).I64(startMs).ToArray();
    public static byte[] DilemmaState(byte dungeonState, byte gameState, byte roundState, int roundId, int roundNo, long gameEndMs, long roundEndMs) =>
        PacketWriter.Opcode(PlatformerDilemmaStateNt).U8(dungeonState).U8(gameState).U8(roundState).Varint(roundId).I32(roundNo).I64(gameEndMs).I64(roundEndMs).ToArray();

    // ---------- Rules (CANDIDATE: the client data has no score table) ----------

    /// <summary>TrapBoard: points for standing on a right tile at a round's result (round x 100).</summary>
    public static int TrapRoundPoints(int round) => round * 100;
    /// <summary>Platformer: points per checkpoint reached (index x 1000), plus up to 999 for the way towards the next one.</summary>
    public const int CheckpointPoints = 1000;
    /// <summary>Racing: the finish order's points (1st 1000, then 100 less per place, at least 100) and 200 per lap started.</summary>
    public static int FinishPoints(int place) => Math.Max(100, 1100 - 100 * place);
    public const int LapPoints = 200;
    /// <summary>Dilemma: points for standing on an answer board at a round's result (round x 100).</summary>
    public static int DilemmaRoundPoints(int round) => round * 100;

    /// <summary>
    /// A TrapBoard round's right tiles: a path over the board from its first row to its last (grid GridX x GridY, tiles in spawn
    /// order: GridY tiles per row, OBSERVED in Map_Dungeon_FieldEvent_TrapBoard MapData: runs of GridY positions 200 cm apart),
    /// one step forward per row and at most one sideways (CANDIDATE: the official board's answers are server-only).
    /// </summary>
    public static SortedSet<int> TrapPath(int gridX, int gridY, Random rng)
    {
        var path = new SortedSet<int>();
        if (gridX <= 0 || gridY <= 0) return path;
        var col = rng.Next(gridY);
        for (var row = 0; row < gridX; row++)
        {
            path.Add(row * gridY + col);
            var next = Math.Clamp(col + rng.Next(-1, 2), 0, gridY - 1);
            if (next != col && row + 1 < gridX) path.Add(row * gridY + next);   // the sideways step, then forward
            col = next;
        }
        return path;
    }
}

/// <summary>One line of the festival ranking.</summary>
public sealed record FestivalRank(int Rank, int Points, int Server, long DbId, string Name, string Guild, int Emblem);
