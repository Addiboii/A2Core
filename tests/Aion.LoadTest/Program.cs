using System.Diagnostics;
using Aion.World;
using Aion.Protocol.Native;

// A2Core load test (core-upgrade Stage 2). Drives the real world loop (NpcAi.Tick) offline: N hostile NPCs spread over a map and
// M players that move and are chased/attacked, ticked at a fixed rate for a number of seconds. Reports per-tick time (avg / p50 /
// p99 / max), NPCs processed, messages produced, GC collections and peak managed memory, so later stages (parallel zones, interest
// grid, write-behind) can be judged against a baseline. Runs nothing against the live server; pure in-memory simulation.
//
//   dotnet run -c Release --project tests/Aion.LoadTest -- [npcs] [players] [seconds] [hz] [areaMetres]
// defaults: 2000 npcs, 500 players, 10 s, 20 Hz, 400 m.

int npcCount = Arg(0, 2000), playerCount = Arg(1, 500), seconds = Arg(2, 10), hz = Arg(3, 20);
double area = Arg(4, 400) * 100.0;   // metres -> cm
int Arg(int i, int d) => args.Length > i && int.TryParse(args[i], out var v) ? v : d;

var data = GameData.Embedded;
var rng = new Random(12345);
Console.WriteLine($"A2Core load test: {npcCount} NPCs, {playerCount} players, {seconds}s at {hz} Hz, {area / 100:0} m square.");

// A field of monsters: a mix of normal/elite/hero with real aggro shapes and a basic + special skill (the agrint kit used in checks).
var circle = new AggroShape("Circle", 1000, 0, 0, 1000, "Upper");
var targets = new Dictionary<int, Target>(npcCount);
for (var i = 0; i < npcCount; i++)
{
    var key = 2000 + i;
    var rank = i % 10 == 0 ? ("HeroMonster", 5) : i % 3 == 0 ? ("EliteMonster", 3) : ("NormalMonster", 3);
    targets[key] = new Target
    {
        Key = key, Name = "mob", Hp = 1_000_000, HpMax = 1_000_000, Level = 7, Position = new Vector3d(Rand(), Rand(), 0),
        Aggressive = true, WalkSpeed = 150, RunSpeed = 300, Radius = 120, Aggro = circle, Skills = [1220560, 1220590],
        DamageScale = Combat.DamageScale(rank.Item1, rank.Item2), Rank = NpcTactics.RankOf(rank.Item1),
    };
}
double Rand() => (rng.NextDouble() - 0.5) * area;

// Players: a position and a small random walk each tick.
var players = new (int Key, Vector3d Pos, double Dx, double Dy)[playerCount];
for (var i = 0; i < playerCount; i++)
    players[i] = (100_000 + i, new Vector3d(Rand(), Rand(), 0), (rng.NextDouble() - 0.5) * 400, (rng.NextDouble() - 0.5) * 400);

var ai = new NpcAi(rng, data);
long hits = 0, messages = 0;
List<byte[]> Hit(int victim, int dmg) { hits++; return []; }

var stepMs = 1000 / hz;
var ticks = seconds * hz;
var times = new double[ticks];
var sw = new Stopwatch();
long activeSum = 0;
var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);

// Warm up the JIT and the brains for a second before measuring.
for (var w = 0; w < hz; w++) ai.Tick(targets, Snapshot(), w * stepMs, Hit);

for (var t = 0; t < ticks; t++)
{
    var now = (long)(seconds * 1000) + t * stepMs;
    // move every player a little (bounded to the field)
    for (var i = 0; i < playerCount; i++)
    {
        var p = players[i];
        var x = Math.Clamp(p.Pos.X + p.Dx * stepMs / 1000.0, -area / 2, area / 2);
        var y = Math.Clamp(p.Pos.Y + p.Dy * stepMs / 1000.0, -area / 2, area / 2);
        var dx = x <= -area / 2 || x >= area / 2 ? -p.Dx : p.Dx;
        var dy = y <= -area / 2 || y >= area / 2 ? -p.Dy : p.Dy;
        players[i] = (p.Key, new Vector3d(x, y, 0), dx, dy);
    }
    sw.Restart();
    var replies = ai.Tick(targets, Snapshot(), now, Hit);
    sw.Stop();
    times[t] = sw.Elapsed.TotalMilliseconds;
    messages += replies.Count;
    activeSum += ai.LastTicked;
}

(int Key, Vector3d Position)[] Snapshot()
{
    var s = new (int, Vector3d)[playerCount];
    for (var i = 0; i < playerCount; i++) s[i] = (players[i].Key, players[i].Pos);
    return s;
}

Array.Sort(times);
double P(double q) => times[Math.Clamp((int)(q * ticks), 0, ticks - 1)];
var avg = times.Average();
var budget = (double)stepMs;
Console.WriteLine();
Console.WriteLine($"  tick time:   avg {avg:0.00} ms   p50 {P(0.5):0.00}   p99 {P(0.99):0.00}   max {times[^1]:0.00}   (budget {budget:0} ms/tick at {hz} Hz)");
Console.WriteLine($"  NPCs ticked: {activeSum / ticks} of {npcCount} per tick (interest: idle NPCs far from players are skipped)");
Console.WriteLine($"  messages:    {messages / (double)ticks:0} per tick, {hits} player-hits total");
Console.WriteLine($"  GC:          gen0 {GC.CollectionCount(0) - g0}, gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2} over {ticks} ticks");
Console.WriteLine($"  memory:      {GC.GetTotalMemory(false) / (1024 * 1024)} MB managed, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
var headroom = budget / Math.Max(0.001, P(0.99));
Console.WriteLine();
Console.WriteLine(P(0.99) <= budget
    ? $"  VERDICT: the world loop keeps up ({headroom:0.0}x headroom at p99). Scales to ~{(int)(playerCount * headroom)} players on one core at this NPC density."
    : $"  VERDICT: the world loop is OVER budget at p99 ({P(0.99):0.0} ms > {budget:0} ms). Needs Stage 3 (parallel zones) at this load.");
