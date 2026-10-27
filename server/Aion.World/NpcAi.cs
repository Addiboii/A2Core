using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Server-side NPC behaviour: wander, aggro, chase, attack and leash, sent as the official NPC
/// move messages (0x371a-c) and skill messages (0x3802 start, 0x3804 switch-06 hit).
/// From the client data: aggro shape (NpcAggroData by AggroDetectRange), speeds, attack skills
/// and their range/follow range/cooldown/damage effect (Skill.dat, SkillEffect.dat), rotation lock.
/// From the official recording: the 1 s move update, stop-then-start attacks, the hit ~600 ms after
/// the start, and damage ~7.5% of the NPC's level HP times the effect's 90-110% range.
/// Movement follows the map's navigation mesh (<see cref="Nav"/>, the client's Recast tiles): paths
/// around obstacles, heights from its floors (ground, bridges, building floors). Without one: the
/// landscape grid (<see cref="Terrain"/>) for NPCs spawned on it, else the straight slope to the goal. OURS (no client source): leash and active ranges,
/// wander radius and idle times.
/// </summary>
public sealed class NpcAi(Random rng, GameData? data = null)
{
    public const int UpdateMs = 1000;
    /// <summary>The 0x371d delta interval while an NPC moves (OBSERVED: 100 ms).</summary>
    public const int DeltaMs = 100;
    /// <summary>A following NPC farther than this from the player gets the speed boost (OURS; the official toggles it as it falls behind).</summary>
    public const double FollowBoostBeyond = 700.0;
    public const int FollowBoost = 8000;
    public const double LeashRange = 3000.0, ActiveRange = 8000.0;
    public const double WanderRadius = 400.0, Reach = 120.0, TurnResendDegrees = 10.0;
    public const int WanderIdleMinMs = 6000, WanderIdleMaxMs = 15000;
    /// <summary>Official: the NPC's hit (0x3804) followed its skill start (0x3802) by ~600 ms.</summary>
    public const int HitDelayMs = 600;
    /// <summary>Official: NPC hits were ~7.5% of the NPC's level HP (level 3: 13-17 of 199; level 7: 47-54 of 665).</summary>
    public const double DamagePerHp = 0.075;
    /// <summary>The player's collision radius for reach (RecastNavMesh AgentRadius 50).</summary>
    public const double PlayerRadius = 50.0;
    /// <summary>Below the NPC, a player still counts as level with it within this height (stairs, slopes).</summary>
    public const double AggroBelowTolerance = 150.0;
    public const int PlayerKey = 1;
    /// <summary>A spawn this close to the landscape stands on it (spawns sit ~20 above; buildings are far higher).</summary>
    public const double GroundedTolerance = 100.0;

    /// <summary>The current map's ground grid, if extracted; null keeps the slope height model.</summary>
    public Terrain? Terrain { get; set; }

    /// <summary>The current map's navigation mesh, if extracted: paths and floor heights.</summary>
    public NavMesh? Nav { get; set; }

    /// <summary>A chase re-plans its path this often, or when the player moved this far from its goal.</summary>
    public const int RepathMs = 1000;
    public const double RepathDistance = 200.0;

    /// <summary>Scripted (a map event walks the NPC along a named route once) and Follow (it keeps up with a player) are directed moves.</summary>
    public enum Mode { Idle, Wander, Chase, Return, Dead, Patrol, Scripted, Follow }
    /// <summary>A following NPC starts walking beyond FollowFar from the player, runs beyond FollowRun, stops within FollowNear (OURS).</summary>
    public const double FollowNear = 250.0, FollowFar = 450.0, FollowRun = 1200.0;

    /// <summary>A patrol turns toward the next waypoint this close to the current one (official: ~15-20 before each point).</summary>
    public const double ArriveRadius = 18.0;

    public sealed record PendingHit(int SkillId, NpcSkill Skill, int ActionSeq, long DueMs);

    public sealed class Brain(Vector3d home, double homeHeading)
    {
        public Vector3d Home { get; } = home;
        public double HomeHeading { get; } = homeHeading;
        public Mode Mode { get; set; } = Mode.Idle;
        public (double X, double Y) Goal { get; set; }
        public double GoalZ { get; set; }
        public bool Moving { get; set; }
        public bool Run { get; set; }
        /// <summary>The position the client was last told (full 0x371b/0x371c plus the 0x371d deltas since).</summary>
        public Vector3d SentPosition { get; set; }
        public long DeltaAtMs { get; set; }
        /// <summary>The MoveSpeed stat sent for this NPC (0x364b stat 205; speed x (1 + value / 10000), OBSERVED 8000 on a following Robin).</summary>
        public int SpeedBoost { get; set; }
        public double Heading { get; set; } = homeHeading;
        public double SentHeading { get; set; }
        public long LastMs { get; set; }
        public long UpdateAtMs { get; set; }
        public long WanderAtMs { get; set; }
        public bool Engaged { get; set; }
        public long NextAttackMs { get; set; }
        public Dictionary<int, long> Cooldowns { get; } = [];
        public int ActionSeq { get; set; }
        public PendingHit? Pending { get; set; }
        /// <summary>Whether the NPC walks on the landscape (decided from its spawn once a grid is known).</summary>
        public bool? Grounded { get; set; }
        /// <summary>Navmesh waypoints to the goal (the last one is the goal), or null for a straight line.</summary>
        public List<Vector3d>? Path { get; set; }
        public int PathIndex { get; set; }
        public long RepathAtMs { get; set; }
        /// <summary>The player this NPC chases and attacks (entity key; 0 none).</summary>
        public int VictimKey { get; set; }
        /// <summary>Hate by attacker key (damage dealt, at least 1 per hit; OURS): the top one is chased.</summary>
        public Dictionary<int, long> Hate { get; } = [];
        /// <summary>The player considered this tick (nearest, or the victim) for a new chase.</summary>
        internal int CandidateKey { get; set; }
        /// <summary>Patrol: the waypoint walked to and the direction along the route (+1 / -1 on TwoWay routes).</summary>
        public int RouteIndex { get; set; }
        public int RouteStep { get; set; } = 1;
        /// <summary>The route a map event told the NPC to walk (Mode.Scripted).</summary>
        public NpcRoute? Script { get; set; }
        /// <summary>The player a map event told the NPC to follow (Mode.Follow).</summary>
        public int FollowKey { get; set; }
        /// <summary>A companion's followed player while it fights (it follows again when the fight ends).</summary>
        public int ResumeFollowKey { get; set; }
        public long CompanionScanAtMs { get; set; }
        /// <summary>The victim is another NPC (SetAggroNpc fights, or a monster's victim hitting back).</summary>
        public bool VictimIsNpc { get; set; }
        /// <summary>Sent into the fight by a map event (SetAggroNpc): fights wherever it is, then stands there.</summary>
        public bool Directed { get; set; }
    }

    /// <summary>Scripted walks that reached their route's end since the last take: (NPC key, route name).</summary>
    readonly List<(int Key, string Route)> arrivals = [];

    public List<(int Key, string Route)> TakeArrivals()
    {
        var taken = arrivals.ToList();
        arrivals.Clear();
        return taken;
    }

    /// <summary>NPCs killed by other NPCs since the last take (the host runs the kill sequence and counts them).</summary>
    readonly List<(Target Victim, Target Killer)> npcKills = [];
    /// <summary>The targets of the running tick (NPC victims' defense).</summary>
    IReadOnlyDictionary<int, Target>? current;

    public List<(Target Victim, Target Killer)> TakeNpcKills()
    {
        var taken = npcKills.ToList();
        npcKills.Clear();
        return taken;
    }

    Brain BrainOf(Target target)
    {
        if (!brains.TryGetValue(target.Key, out var brain))
        {
            brains[target.Key] = brain = new Brain(target.Position, target.Direction);
            homes.Add(target.Key, target.Position.X, target.Position.Y);
        }
        return brain;
    }

    /// <summary>A map event: walk the route from its first point to its last (whatever its traverse type), then stop there.</summary>
    public List<byte[]> Walk(Target target, NpcRoute route, long now)
    {
        var brain = BrainOf(target);
        if (route.Points.Length == 0 || target.Dead) return [];
        brain.Mode = Mode.Scripted;
        brain.Directed = false;
        brain.Script = route;
        brain.RouteIndex = 0;
        brain.RouteStep = 1;
        brain.Run = route.Run;
        brain.Moving = true;
        brain.Path = null;
        brain.LastMs = now;
        brain.UpdateAtMs = now + UpdateMs;
        brain.Heading = HeadingTo(target.Position, (route.Points[0][0], route.Points[0][1]));
        brain.SentHeading = brain.Heading;
        brain.SentPosition = target.Position;
        brain.DeltaAtMs = now + DeltaMs;
        target.Walking = !route.Run;
        awake.Add(target.Key);
        // The official Robin runs his scripted path with MoveSpeed 8000 (0x364b at the move start, ~875/s observed against
        // his NpcData 500): live 2026-10-19 ours ran at 500 with slow legs.
        var boost = SetBoost(target, brain, route.Run && target.Function == "Companion" ? FollowBoost : 0);
        return [GamePackets.NpcMoveStart(target.Key, target.Position, brain.Heading, route.Run), .. boost];
    }

    /// <summary>How far a companion looks for monsters (its NpcData aggro radius is 1000; OURS: a full circle).</summary>
    public const double CompanionRange = 1000.0;

    /// <summary>The nearest living hostile NPC within a companion's range, checked twice a second while it is free.</summary>
    Target? CompanionPrey(Target target, Brain brain, long now)
    {
        if (target.Function != "Companion" || target.Hostile || !target.Aggressive || current is null) return null;
        if (brain.Mode is not (Mode.Idle or Mode.Follow or Mode.Wander) || now < brain.CompanionScanAtMs) return null;
        brain.CompanionScanAtMs = now + 500;
        Target? best = null;
        var bestDistance = CompanionRange;
        foreach (var other in current.Values)
        {
            if (other.Key == target.Key || !other.Hostile || other.Dead || other.CorpseRemoved) continue;
            var d = Distance(target.Position, Flat(other.Position));
            if (d < bestDistance) { bestDistance = d; best = other; }
        }
        return best;
    }

    /// <summary>A map event: follow the player (or stop following and stand).</summary>
    public List<byte[]> Follow(Target target, int playerKey, bool follow, long now)
    {
        var brain = BrainOf(target);
        if (follow)
        {
            brain.Mode = Mode.Follow;
            brain.Directed = false;
            brain.FollowKey = playerKey;
            brain.VictimKey = playerKey;
            awake.Add(target.Key);
            return [];
        }
        if (brain.Mode != Mode.Follow) return [];
        brain.Mode = Mode.Idle;
        brain.FollowKey = 0;
        brain.VictimKey = 0;
        brain.WanderAtMs = long.MaxValue;
        return brain.Moving ? Stop(target, brain, brain.Heading, now) : [];
    }

    /// <summary>A map event: the NPC turns to a heading (0x364e CharRotate, OBSERVED after NpcRotate directives).</summary>
    public List<byte[]> Face(Target target, double heading, long now)
    {
        var brain = BrainOf(target);
        var from = brain.Heading;
        brain.Heading = heading;
        brain.SentHeading = heading;
        target.Direction = heading;
        return [GamePackets.CharRotate(target.Key, (float)heading, (float)from, 1)];
    }

    /// <summary>A map event (SetAggroPc): the NPC attacks the player now, as if it had detected them.</summary>
    public List<byte[]> ForceAggro(Target target, int playerKey, long now)
    {
        var brain = BrainOf(target);
        if (target.Dead || !target.Hostile) return [];
        brain.CandidateKey = playerKey;
        brain.Script = null;
        target.Walking = false;
        awake.Add(target.Key);
        return StartChase(target, brain, now);
    }

    /// <summary>A map event (SetAggroNpc): the NPC attacks another NPC now, whatever their relationships (a companion joins a fight).</summary>
    public List<byte[]> ForceAggroNpc(Target target, Target victim, long now)
    {
        var brain = BrainOf(target);
        if (target.Dead || victim.Dead || target.RunSpeed == 0 && target.WalkSpeed == 0) return [];
        brain.Hate[victim.Key] = brain.Hate.GetValueOrDefault(victim.Key) + 1;
        brain.CandidateKey = victim.Key;
        brain.VictimIsNpc = true;
        brain.Directed = true;
        brain.Script = null;
        brain.FollowKey = 0;
        target.Walking = false;
        awake.Add(target.Key);
        return StartChase(target, brain, now);
    }

    /// <summary>
    /// An NPC's hit on another NPC: the victim's HP (0x8d00); it turns on its attacker (its hate table) unless a script
    /// directs or an abnormal holds it; a kill waits for the host (TakeNpcKills). Damage as against players (CANDIDATE).
    /// </summary>
    List<byte[]> HitNpc(IReadOnlyDictionary<int, Target> targets, int victimKey, Target attacker, int damage, long now)
    {
        if (!targets.TryGetValue(victimKey, out var victim) || victim.Dead) return [];
        if (!victim.Immune) victim.Hp = Math.Max(0, victim.Hp - damage);
        var replies = new List<byte[]> { GamePackets.NpcHp(victim.Key, victim.Hp) };
        LastAttacker = attacker.Key;
        if (victim.Dead) { npcKills.Add((victim, attacker)); return replies; }
        if (!victim.Held && (!brains.TryGetValue(victim.Key, out var mind) || mind.Mode is not (Mode.Scripted or Mode.Follow)))
            replies.AddRange(Engage(targets, victim, now, attacker.Key, damage));
        return replies;
    }

    /// <summary>Walks the scripted route; at its end the NPC stops and the arrival is recorded.</summary>
    List<byte[]> ScriptedStep(Target target, Brain brain, long now)
    {
        var route = brain.Script!;
        var points = route.Points;
        var speed = brain.Run ? target.RunSpeed : target.WalkSpeed;
        if (speed <= 0) speed = Math.Max(target.RunSpeed, target.WalkSpeed);
        if (speed <= 0) { brain.Mode = Mode.Idle; return []; }
        speed *= 1 + brain.SpeedBoost / 10000.0;
        var budget = speed * Math.Max(0, now - brain.LastMs) / 1000.0;
        brain.LastMs = now;
        for (var guard = 0; guard < points.Length + 2 && budget > 0; guard++)
        {
            var aim = points[brain.RouteIndex];
            var distance = Distance(target.Position, (aim[0], aim[1]));
            if (distance <= ArriveRadius)
            {
                if (brain.RouteIndex >= points.Length - 1)
                {
                    target.Position = new Vector3d(aim[0], aim[1], Height(brain, aim[0], aim[1], aim[2]));
                    target.Walking = false;
                    brain.Mode = Mode.Idle;
                    brain.Script = null;
                    brain.WanderAtMs = long.MaxValue;
                    arrivals.Add((target.Key, route.Name));
                    return [.. SetBoost(target, brain, 0), .. Stop(target, brain, brain.Heading, now)];
                }
                brain.RouteIndex++;
                continue;
            }
            var travel = Math.Min(budget, distance);
            brain.Heading = HeadingTo(target.Position, (aim[0], aim[1]));
            var radians = brain.Heading * Math.PI / 180;
            double x = target.Position.X + Math.Cos(radians) * travel, y = target.Position.Y + Math.Sin(radians) * travel;
            var slope = target.Position.Z + (aim[2] - target.Position.Z) * travel / Math.Max(1, distance);
            target.Position = new Vector3d(x, y, Height(brain, x, y, slope));
            target.Direction = brain.Heading;
            budget -= travel;
        }
        return MoveUpdate(target, brain, now);
    }

    /// <summary>Keeps up with the followed player: starts walking (running when far) beyond FollowFar, stops within FollowNear.</summary>
    List<byte[]> FollowStep(Target target, Brain brain, Vector3d? player, long now)
    {
        if (player is not { } p) return brain.Moving ? [.. SetBoost(target, brain, 0), .. Stop(target, brain, brain.Heading, now)] : [];
        var distance = Distance(target.Position, Flat(p));
        // The official following Robin always runs (flag 5) and has his speed raised while behind (live 2026-10-19: ours
        // walked at 126/s and fell far behind a running player).
        var boost = SetBoost(target, brain, (distance > FollowBoostBeyond || target.Function == "Companion") && target.RunSpeed > 0 ? FollowBoost : 0);
        if (!brain.Moving)
        {
            if (distance <= FollowFar) return boost;
            return [.. boost, .. Go(target, brain, Flat(p), p.Z, target.RunSpeed > 0, now)];
        }
        if (now >= brain.RepathAtMs || Distance(p, brain.Goal) > RepathDistance)
        {
            brain.Goal = Flat(p);
            brain.GoalZ = p.Z;
            brain.Path = PathTo(target, brain.Goal, p.Z);
            brain.PathIndex = 0;
            brain.RepathAtMs = now + RepathMs;
        }
        if (Step(target, brain, Flat(p), p.Z, FollowNear, now)) return [.. boost, .. SetBoost(target, brain, 0), .. Stop(target, brain, HeadingTo(target.Position, Flat(p)), now)];
        return [.. boost, .. MoveUpdate(target, brain, now)];
    }

    readonly Dictionary<int, Brain> brains = [];
    /// <summary>Brains by spawn point, for the NPCs near a player.</summary>
    readonly SpatialGrid homes = new();
    /// <summary>NPCs busy without a player near (chasing, running home, wandering, a hit pending): ticked anyway.</summary>
    readonly HashSet<int> awake = [];

    public IReadOnlyDictionary<int, Brain> Brains => brains;
    /// <summary>How many NPCs the last tick ran (research and checks).</summary>
    public int LastTicked { get; private set; }

    long WanderDelay(long now) => now + rng.Next(WanderIdleMinMs, WanderIdleMaxMs + 1);

    public static double HeadingTo(Vector3d origin, (double X, double Y) goal) =>
        GamePackets.PyMod(Math.Atan2(goal.Y - origin.Y, goal.X - origin.X) * 180 / Math.PI, 360);

    static double Distance(Vector3d a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static (double, double) Flat(Vector3d v) => (v.X, v.Y);
    static double AngleDelta(double a, double b) => Math.Abs(GamePackets.PyMod(a - b + 180, 360) - 180);

    void Sync(IReadOnlyDictionary<int, Target> targets)
    {
        // Targets are only ever added, so an unchanged count means nothing new.
        if (targets.Count == brains.Count) return;
        foreach (var target in targets.Values)
            if (!brains.ContainsKey(target.Key))
            {
                brains[target.Key] = new Brain(target.Position, target.Direction);
                homes.Add(target.Key, target.Position.X, target.Position.Y);
            }
    }

    /// <summary>
    /// An idle NPC with no player within ActiveRange (plus its wander radius) does nothing in a tick: no detection
    /// (aggro shapes are smaller), no wander (only near a player), no pending hit. Those are skipped; a map of
    /// 16000 spawns runs only the ones around its players.
    /// </summary>
    public const double SleepRange = ActiveRange + WanderRadius;

    static bool Busy(Brain brain) => brain.Mode is Mode.Chase or Mode.Return or Mode.Wander or Mode.Patrol or Mode.Scripted or Mode.Follow || brain.Pending is not null;

    /// <summary>
    /// Whether the NPC's aggro shape contains the player: a circle, or an arc of Angle degrees
    /// centred on the NPC's facing plus an all-round inner circle; the player must be at most
    /// Height above it (ZAxis Upper) and not far below. No shape: never aggressive.
    /// </summary>
    public static bool Detects(Target target, double heading, Vector3d player)
    {
        if (target.Aggro is not { } shape) return false;
        var dz = player.Z - target.Position.Z;
        if (dz > shape.Height || dz < -(shape.ZAxis == "Upper" ? AggroBelowTolerance : shape.Height)) return false;
        var distance = Distance(target.Position, Flat(player));
        if (distance > shape.Radius) return false;
        if (shape.Shape != "ArcCircle" || distance <= shape.Inner) return true;
        return AngleDelta(HeadingTo(target.Position, Flat(player)), heading) <= shape.Angle / 2;
    }

    /// <summary>
    /// A hit monster (any aggro type) chases whoever has hurt it most (its hate table, OURS); a monster running
    /// home ignores hits until it is back.
    /// </summary>
    public List<byte[]> Engage(IReadOnlyDictionary<int, Target> targets, Target target, long now, int attackerKey = PlayerKey, int damage = 1)
    {
        Sync(targets);
        if (!brains.TryGetValue(target.Key, out var brain) || target.Dead || target.RunSpeed == 0 || brain.Mode == Mode.Return) return [];
        awake.Add(target.Key);
        brain.Hate[attackerKey] = brain.Hate.GetValueOrDefault(attackerKey) + Math.Max(1, damage);
        brain.CandidateKey = TopHate(brain) ?? attackerKey;
        return StartChase(target, brain, now);
    }

    static int? TopHate(Brain brain) => brain.Hate.Count == 0 ? null : brain.Hate.MaxBy(h => h.Value).Key;

    /// <summary>The NPC died: it leaves combat (official 0x8d21 off after the kill) and drops a pending hit.</summary>
    /// <summary>A victim's defense by entity key (null: no reduction).</summary>
    public Func<int, int>? DefenseOf { get; set; }

    /// <summary>The NPC whose hit was applied last (the killer when that hit was fatal).</summary>
    public int LastAttacker { get; private set; }

    public List<byte[]> Killed(Target target)
    {
        if (!brains.TryGetValue(target.Key, out var brain)) return [];
        brain.Pending = null;
        brain.Cooldowns.Clear();
        if (!brain.Engaged) return [];
        brain.Engaged = false;
        return [GamePackets.CombatState(target.Key, false)];
    }

    static List<byte[]> StartChase(Target target, Brain brain, long now)
    {
        var replies = new List<byte[]>();
        brain.VictimKey = brain.CandidateKey != 0 ? brain.CandidateKey : PlayerKey;
        if (!brain.Engaged)
        {
            // Official aggro start, all in one tick: NpcFirstAggro_NT 0x8d52, the NPC's target (0x3835, the player), a stop
            // (0x371a), 0x8d21 on (recording 2026-10-19).
            brain.Engaged = true;
            replies.Add(GamePackets.NpcFirstAggro(target.Key));
            replies.Add(GamePackets.TargetUpdate(target.Key, brain.VictimKey));
            replies.AddRange(Stop(target, brain, brain.Heading, now));
            replies.Add(GamePackets.CombatState(target.Key, true));
        }
        brain.Mode = Mode.Chase;
        brain.Moving = false;  // the next tick starts a run (0x371b) towards the player
        brain.LastMs = now;
        return replies;
    }

    /// <summary>Navmesh waypoints from the NPC to the goal, or null (no mesh, or no path).</summary>
    List<Vector3d>? PathTo(Target target, (double X, double Y) goal, double goalZ) =>
        Nav?.FindPath(target.Position, new Vector3d(goal.X, goal.Y, goalZ));

    (double X, double Y) Aim(Brain brain) =>
        brain.Path is { } path && brain.PathIndex < path.Count - 1 ? (path[brain.PathIndex].X, path[brain.PathIndex].Y) : brain.Goal;

    List<byte[]> Go(Target target, Brain brain, (double X, double Y) goal, double goalZ, bool run, long now, List<Vector3d>? path = null)
    {
        brain.Goal = goal;
        brain.GoalZ = goalZ;
        brain.Run = run;
        brain.Path = path ?? PathTo(target, goal, goalZ);
        brain.PathIndex = 0;
        brain.RepathAtMs = now + RepathMs;
        brain.Heading = HeadingTo(target.Position, Aim(brain));
        brain.SentHeading = brain.Heading;
        brain.Moving = true;
        brain.LastMs = now;
        brain.UpdateAtMs = now + UpdateMs;
        brain.SentPosition = target.Position;
        brain.DeltaAtMs = now + DeltaMs;
        return [GamePackets.NpcMoveStart(target.Key, target.Position, brain.Heading, run)];
    }

    static List<byte[]> Stop(Target target, Brain brain, double heading, long now)
    {
        brain.Moving = false;
        brain.Heading = heading;
        brain.LastMs = now;
        return [GamePackets.NpcStop(target.Key, target.Position, heading)];
    }

    /// <summary>Height at (x, y): the ground for NPCs on the landscape, else <paramref name="fallback"/>.</summary>
    double Height(Brain brain, double x, double y, double fallback)
    {
        // The floor on the NPC's own level (a bridge, not the ground under it).
        if (Nav?.FloorAt(x, y, fallback, 250) is { } floor) return floor;
        if (Terrain is not { } terrain) return fallback;
        brain.Grounded ??= terrain.GroundAt(brain.Home.X, brain.Home.Y) is { } home && Math.Abs(brain.Home.Z - home) <= GroundedTolerance;
        return brain.Grounded == true && terrain.GroundAt(x, y) is { } ground ? ground : fallback;
    }

    /// <summary>
    /// Advance a moving NPC towards goal; true when within stopAt of it. Height: the ground, or
    /// the straight slope from the current position to goalZ.
    /// </summary>
    bool Step(Target target, Brain brain, (double X, double Y) goal, double goalZ, double stopAt, long now)
    {
        var elapsed = Math.Max(0, now - brain.LastMs) / 1000.0;
        brain.LastMs = now;
        var budget = (brain.Run ? target.RunSpeed : target.WalkSpeed) * (1 + brain.SpeedBoost / 10000.0) * elapsed;
        // Walk the waypoints in order; stopAt applies to the final goal only.
        while (true)
        {
            var final = brain.Path is not { } path || brain.PathIndex >= path.Count - 1;
            var aim = final ? goal : (brain.Path![brain.PathIndex].X, brain.Path[brain.PathIndex].Y);
            var aimZ = final ? goalZ : brain.Path![brain.PathIndex].Z;
            var distance = Distance(target.Position, aim);
            var remaining = distance - (final ? stopAt : 0);
            if (remaining <= 0)
            {
                if (final) return true;
                brain.PathIndex++;
                continue;
            }
            var travel = Math.Min(remaining, budget);
            var heading = HeadingTo(target.Position, aim);
            var radians = heading * Math.PI / 180;
            var slope = distance > 0 ? target.Position.Z + (aimZ - target.Position.Z) * travel / distance : aimZ;
            double x = target.Position.X + Math.Cos(radians) * travel, y = target.Position.Y + Math.Sin(radians) * travel;
            target.Position = new Vector3d(x, y, Height(brain, x, y, slope));
            brain.Heading = heading;
            budget -= travel;
            if (travel < remaining) return false;
            if (final) return true;
            brain.PathIndex++;
            if (budget <= 1e-6) return false;
        }
    }

    /// <summary>A moving NPC's 0x371c: on a turn of more than TurnResendDegrees (waypoint corners) or every UpdateMs.</summary>
    List<byte[]> MoveUpdate(Target target, Brain brain, long now)
    {
        if (AngleDelta(brain.Heading, brain.SentHeading) > TurnResendDegrees || now >= brain.UpdateAtMs) return FullMove(target, brain, now);
        if (now < brain.DeltaAtMs) return [];
        // Between the full positions the official server moves the NPC in 100 ms deltas (live 2026-10-19: with the 1 s
        // positions alone running NPCs skipped forward once a second). The deltas add up exactly: they are taken from the
        // position the client was told, so rounding never drifts.
        brain.DeltaAtMs = now + DeltaMs;
        int dx = (int)Math.Round(target.Position.X - brain.SentPosition.X), dy = (int)Math.Round(target.Position.Y - brain.SentPosition.Y),
            dz = (int)Math.Round(target.Position.Z - brain.SentPosition.Z);
        if (dx == 0 && dy == 0 && dz == 0) return [];
        if (Math.Abs(dx) > 127 || Math.Abs(dy) > 127 || Math.Abs(dz) > 127) return FullMove(target, brain, now);
        brain.SentPosition = new Vector3d(brain.SentPosition.X + dx, brain.SentPosition.Y + dy, brain.SentPosition.Z + dz);
        return [GamePackets.NpcMoveDelta(target.Key, dx, dy, dz, brain.Heading, brain.Run)];
    }

    List<byte[]> FullMove(Target target, Brain brain, long now)
    {
        brain.SentHeading = brain.Heading;
        brain.UpdateAtMs = now + UpdateMs;
        brain.DeltaAtMs = now + DeltaMs;
        brain.SentPosition = target.Position;
        return [GamePackets.NpcMove(target.Key, target.Position, brain.Heading, brain.Run)];
    }

    /// <summary>The MoveSpeed stat for a following NPC: raised while it is behind (OBSERVED on Robin: 8000 / 0, 0x364b stat 205).</summary>
    static List<byte[]> SetBoost(Target target, Brain brain, int boost)
    {
        if (brain.SpeedBoost == boost) return [];
        brain.SpeedBoost = boost;
        return [PlayerPackets.MoveSpeedUpdate(target.Key, [(205, boost)])];
    }

    List<byte[]> Reset(Target target, Brain brain, long now)
    {
        var replies = new List<byte[]>();
        brain.Pending = null;
        brain.Cooldowns.Clear();
        if (brain.Engaged)
        {
            brain.Engaged = false;
            replies.Add(GamePackets.CombatState(target.Key, false));
        }
        if (target.Hp != target.HpMax)
        {
            target.Hp = target.HpMax;
            replies.Add(GamePackets.NpcHp(target.Key, target.Hp));
        }
        brain.Mode = Mode.Idle;
        brain.VictimKey = 0;
        brain.Hate.Clear();
        brain.WanderAtMs = WanderDelay(now);
        return replies;
    }

    /// <summary>The NPC's basic attack: the first damaging skill of its ActiveSkillList.</summary>
    (int Id, NpcSkill Skill)? Basic(Target target)
    {
        if (data is null) return null;
        foreach (var id in target.Skills)
            if (data.NpcSkill(id) is { } skill) return (id, skill);
        return null;
    }

    /// <summary>
    /// The skill to use now: a special (a later damaging skill) whose own cooldown is over and
    /// whose range reaches the player, else the basic attack when ready and in reach.
    /// </summary>
    (int Id, NpcSkill Skill)? Choose(Target target, Brain brain, double distance, long now)
    {
        if (data is null || brain.Pending is not null || now < brain.NextAttackMs) return null;
        var basic = Basic(target);
        foreach (var id in target.Skills)
        {
            if (data.NpcSkill(id) is not { } skill || basic is { } b && b.Id == id) continue;
            // A special first becomes ready one cooldown into the fight; the basic attack opens.
            if (!brain.Cooldowns.TryGetValue(id, out var readyAt))
            {
                brain.Cooldowns[id] = now + skill.Cooldown;
                continue;
            }
            if (now < readyAt || distance > AttackRange(target, skill)) continue;
            return (id, skill);
        }
        return basic is { } ready && now >= brain.Cooldowns.GetValueOrDefault(ready.Id) && distance <= AttackRange(target, ready.Skill) ? ready : null;
    }

    /// <summary>Official order: a stop facing the player, then the skill start; the hit follows the cast and animation.</summary>
    List<byte[]> Attack(Target target, Brain brain, (int Id, NpcSkill Skill) skill, double facing, long now)
    {
        var replies = new List<byte[]>();
        if (!target.NoRotation) replies.AddRange(Stop(target, brain, facing, now));
        else brain.Moving = false;
        brain.ActionSeq = brain.ActionSeq % 255 + 1;
        var lands = now + skill.Skill.Cast + HitDelayMs;
        brain.Pending = new PendingHit(skill.Id, skill.Skill, brain.ActionSeq, lands);
        brain.Cooldowns[skill.Id] = now + Math.Max(skill.Skill.Cooldown, skill.Skill.Cast + HitDelayMs);
        brain.NextAttackMs = lands;
        replies.Add(GamePackets.SkillStart(skill.Id, brain.ActionSeq, target.NoRotation ? brain.Heading : facing, target.Position,
            targetKey: Victim(brain), actorKey: target.Key, cooldown: skill.Skill.Cooldown));
        return replies;
    }

    /// <summary>Centre distance at which the NPC's skill reaches the player.</summary>
    static double AttackRange(Target target, NpcSkill? skill) => (skill?.Range ?? Reach) + target.Radius + PlayerRadius;

    int Damage(Target target, NpcSkill skill)
    {
        var low = Math.Min(skill.DamageMin, skill.DamageMax);
        var high = Math.Max(skill.DamageMin, skill.DamageMax);
        var factor = (low + rng.NextDouble() * (high - low)) / 10000.0;
        // The NPC's level's Grade 3 HP, not its own HP: the recording of 2026-10-19 has the lake cave boss (hero, level 5, 3000 HP)
        // hitting 21-31 with its ordinary attacks (399 x 7.5 % = 30) and level-4 monsters 11-30; the skills' factors spread that,
        // and the rank scales it (Combat.DamageScale).
        return Math.Max(1, (int)Math.Round(Combat.HpForLevel(target.Level) * DamagePerHp * target.DamageScale * factor));
    }

    /// <summary>A due hit lands when the player is still within reach plus the skill's follow range.</summary>
    List<byte[]> Resolve(Target target, Brain brain, Vector3d? player, long now, Func<int, List<byte[]>>? hitPlayer)
    {
        if (brain.Pending is not { } hit || now < hit.DueMs) return [];
        brain.Pending = null;
        if (player is not { } p || hitPlayer is null) return [];
        if (Distance(target.Position, Flat(p)) > AttackRange(target, hit.Skill) + hit.Skill.Follow) return [];
        var victim = Victim(brain);
        // The victim's defense reduces the hit (the CANDIDATE 1000/(1000+defense) used for players' hits).
        var defense = brain.VictimIsNpc && current?.GetValueOrDefault(victim) is { } prey ? prey.Defense : DefenseOf?.Invoke(victim) ?? 0;
        var damage = Math.Max(1, (int)Math.Round(Damage(target, hit.Skill) * 1000.0 / (1000 + Math.Max(0, defense))));
        LastAttacker = target.Key;
        return [GamePackets.NpcSkillHit(victim, target.Key, hit.SkillId, hit.ActionSeq, hit.Skill.Effect, damage), .. hitPlayer(damage)];
    }

    static int Victim(Brain brain) => brain.VictimKey != 0 ? brain.VictimKey : PlayerKey;

    List<byte[]> TickOne(Target target, Brain brain, Vector3d? player, long now, Func<int, List<byte[]>>? hitPlayer)
    {
        if (target.Dead)
        {
            if (brain.Mode != Mode.Dead)
            {
                brain.Mode = Mode.Dead; brain.Moving = false; brain.Engaged = false; brain.Pending = null; brain.VictimKey = 0; brain.Hate.Clear();
                target.Position = brain.Home;   // the respawn (0x3641) uses the spawn point
                target.Direction = brain.HomeHeading;
                brain.Heading = brain.HomeHeading;
            }
            return [];
        }
        if (brain.Mode == Mode.Dead)
        {
            brain.Mode = Mode.Idle;
            brain.WanderAtMs = WanderDelay(now);
        }
        // Held by a scripted abnormal (stun, hold, stone): it stops where it is and does nothing until released.
        if (target.Held)
        {
            brain.Pending = null;
            return brain.Moving ? Stop(target, brain, brain.Heading, now) : [];
        }
        // Directed by a map event: the walk, nothing else.
        if (brain.Mode == Mode.Scripted && brain.Script is not null) return ScriptedStep(target, brain, now);
        // A companion (NpcData Function Companion, aggressive: Robin in the HQ001 layer) attacks the monsters near it on its
        // own (OBSERVED: the official Robin ran to the Cherubim and cast first, #10020; live 2026-10-19 ours stood by).
        if (CompanionPrey(target, brain, now) is { } prey)
        {
            var resume = brain.Mode == Mode.Follow ? brain.FollowKey : brain.ResumeFollowKey;
            var engaged = ForceAggroNpc(target, prey, now);
            brain.ResumeFollowKey = resume;
            return engaged;
        }
        if (brain.Mode == Mode.Follow) return FollowStep(target, brain, player, now);
        var replies = Resolve(target, brain, player, now, hitPlayer);
        if (target.WalkSpeed == 0 && target.RunSpeed == 0) return replies;
        // Official: of 42 friendly NPCs in the recording only one moved, a patrol walking its MapData waypoint set;
        // townsfolk stand at their MapData spot and facing (their idle animation is in the appear). A friendly NPC
        // in a fight with another NPC (SetAggroNpc, or hit by one) chases like a monster until it returns home.
        if (!target.Hostile && !(brain.VictimIsNpc && brain.Mode is Mode.Chase or Mode.Return))
        {
            if (target.Route is not null) replies.AddRange(Patrol(target, brain, now));
            return replies;
        }
        if (brain.Mode is Mode.Idle or Mode.Wander or Mode.Patrol && target.Aggressive && target.RunSpeed != 0
            && player is { } seen && Detects(target, brain.Heading, seen))
        {
            target.Walking = false;
            replies.AddRange(StartChase(target, brain, now));
        }
        // Roaming monsters walk their route when not fighting.
        if (target.Route is not null && brain.Mode is Mode.Idle or Mode.Wander or Mode.Patrol)
        {
            replies.AddRange(Patrol(target, brain, now));
            return replies;
        }

        if (brain.Mode == Mode.Chase)
        {
            // A companion fights wherever it is (Robin is far from his spawn after his scripted run; live 2026-10-19 the leash
            // ended each chase at once and he only raised his bow): its fight ends when the prey is gone. So does a friendly
            // NPC a map event sent in (HQ002's Salros, Function None, is 2300 from his spawn when told to fight: live
            // 2026-10-24 he ran home at once and left the fight).
            var companion = !target.Hostile && (target.Function == "Companion" || brain.Directed);
            if (player is not { } p || !companion && (Distance(target.Position, Flat(brain.Home)) > LeashRange || Distance(p, Flat(brain.Home)) > LeashRange))
            {
                // A companion's fight is over: it follows its player again instead of walking home.
                if (brain.ResumeFollowKey != 0 && (player is null || brain.VictimIsNpc))
                {
                    brain.Mode = Mode.Follow;
                    brain.FollowKey = brain.VictimKey = brain.ResumeFollowKey;
                    brain.ResumeFollowKey = 0;
                    brain.VictimIsNpc = false;
                    brain.Pending = null;
                    return brain.Moving ? Stop(target, brain, brain.Heading, now) : [];
                }
                if (companion)
                {
                    brain.Mode = Mode.Idle;
                    brain.VictimKey = 0;
                    brain.VictimIsNpc = false;
                    brain.Directed = false;
                    brain.Pending = null;
                    brain.WanderAtMs = long.MaxValue;
                    return [.. SetBoost(target, brain, 0), .. brain.Moving ? Stop(target, brain, brain.Heading, now) : []];
                }
                brain.Mode = Mode.Return;
                brain.VictimKey = 0;
                brain.Pending = null;
                replies.AddRange(Go(target, brain, Flat(brain.Home), brain.Home.Z, true, now));
                return replies;
            }
            var basic = Basic(target);
            var reach = AttackRange(target, basic?.Skill);
            var stopAt = Math.Max(target.Radius, reach - 20);   // close in to just inside melee reach
            var distance = Distance(target.Position, Flat(p));
            var facing = HeadingTo(target.Position, Flat(p));
            // A ready skill that reaches the player is used at once, ranged ones while still closing in.
            if (Choose(target, brain, distance, now) is { } skill)
            {
                replies.AddRange(Attack(target, brain, skill, facing, now));
                return replies;
            }
            if (!brain.Moving)
            {
                // Close in whenever the player is out of reach: holding still within the follow range
                // (which only keeps a started hit landing) left mobs staring without attacking.
                if (distance > reach)
                {
                    if (brain.Pending is null) replies.AddRange(Go(target, brain, Flat(p), p.Z, true, now));
                    return replies;
                }
                if (!target.NoRotation && AngleDelta(facing, brain.Heading) > 30) replies.AddRange(Stop(target, brain, facing, now));
                return replies;
            }
            // The player moves: re-plan every RepathMs, or at once when they left the path's goal.
            if (now >= brain.RepathAtMs || Distance(p, brain.Goal) > RepathDistance)
            {
                brain.Goal = Flat(p);
                brain.GoalZ = p.Z;
                brain.Path = PathTo(target, brain.Goal, p.Z);
                brain.PathIndex = 0;
                brain.RepathAtMs = now + RepathMs;
            }
            if (Step(target, brain, Flat(p), p.Z, stopAt, now))
            {
                replies.AddRange(Stop(target, brain, HeadingTo(target.Position, Flat(p)), now));
                return replies;
            }
            replies.AddRange(MoveUpdate(target, brain, now));
            return replies;
        }

        if (brain.Mode is Mode.Return or Mode.Wander)
        {
            if (Step(target, brain, brain.Goal, brain.GoalZ, 0, now))
            {
                target.Position = new Vector3d(brain.Goal.X, brain.Goal.Y, Height(brain, brain.Goal.X, brain.Goal.Y, brain.GoalZ));
                replies.AddRange(Stop(target, brain, brain.Mode == Mode.Return ? brain.HomeHeading : brain.Heading, now));
                if (brain.Mode == Mode.Return) replies.AddRange(Reset(target, brain, now));
                else
                {
                    brain.Mode = Mode.Idle;
                    brain.WanderAtMs = WanderDelay(now);
                }
            }
            else replies.AddRange(MoveUpdate(target, brain, now));
            return replies;
        }

        replies.AddRange(Idle(target, brain, player, now, WanderRadius));
        return replies;
    }

    /// <summary>
    /// A patrol: walks its waypoint set without pausing, at its walk speed (run on run routes), turning toward the next
    /// point within ArriveRadius; Circular routes loop, TwoWay routes go back and forth, OneWay routes stop at the end.
    /// Official messages: 0x371c walk every second (no 0x371b start / 0x371a stop), the appear says move_state Walk.
    /// It starts at the route point nearest to where it stands (the spawn, or where it came back to after a fight).
    /// </summary>
    List<byte[]> Patrol(Target target, Brain brain, long now)
    {
        var route = target.Route!;
        var points = route.Points;
        var speed = route.Run ? target.RunSpeed : target.WalkSpeed;
        if (points.Length == 0 || speed <= 0) return [];
        if (brain.Mode != Mode.Patrol)
        {
            brain.Mode = Mode.Patrol;
            var nearest = 0;
            for (var i = 1; i < points.Length; i++)
                if (Distance(target.Position, (points[i][0], points[i][1])) < Distance(target.Position, (points[nearest][0], points[nearest][1]))) nearest = i;
            brain.RouteIndex = nearest;
            brain.RouteStep = 1;
            brain.Run = route.Run;
            brain.Moving = true;
            brain.LastMs = now;
            brain.Path = null;
            target.Walking = !route.Run;
            return [];
        }
        var budget = speed * Math.Max(0, now - brain.LastMs) / 1000.0;
        brain.LastMs = now;
        for (var guard = 0; guard < points.Length * 2 + 2 && budget > 0; guard++)
        {
            var aim = points[brain.RouteIndex];
            var distance = Distance(target.Position, (aim[0], aim[1]));
            if (distance <= ArriveRadius)
            {
                if (!NextPoint(route, brain))
                {
                    // A OneWay route ends: the NPC stays where it arrived.
                    target.Walking = false;
                    brain.Mode = Mode.Idle;
                    brain.WanderAtMs = long.MaxValue;
                    return Stop(target, brain, brain.Heading, now);
                }
                continue;
            }
            var travel = Math.Min(budget, distance - ArriveRadius / 2);
            brain.Heading = HeadingTo(target.Position, (aim[0], aim[1]));
            var radians = brain.Heading * Math.PI / 180;
            double x = target.Position.X + Math.Cos(radians) * travel, y = target.Position.Y + Math.Sin(radians) * travel;
            var slope = target.Position.Z + (aim[2] - target.Position.Z) * travel / Math.Max(1, distance);
            target.Position = new Vector3d(x, y, Height(brain, x, y, slope));
            target.Direction = brain.Heading;
            budget -= travel;
        }
        return MoveUpdate(target, brain, now);
    }

    /// <summary>The next waypoint of the route; false at the end of a OneWay route.</summary>
    static bool NextPoint(NpcRoute route, Brain brain)
    {
        var count = route.Points.Length;
        if (count < 2) return false;
        switch (route.Traverse)
        {
            case "Circular":
                brain.RouteIndex = (brain.RouteIndex + 1) % count;
                return true;
            case "TwoWay":
                var next = brain.RouteIndex + brain.RouteStep;
                if (next < 0 || next >= count) brain.RouteStep = -brain.RouteStep;
                brain.RouteIndex += brain.RouteStep;
                return true;
            default:
                if (brain.RouteIndex >= count - 1) return false;
                brain.RouteIndex++;
                return true;
        }
    }

    /// <summary>Idle and wander: an occasional short walk around the spawn point, only near the player.</summary>
    List<byte[]> Idle(Target target, Brain brain, Vector3d? player, long now, double radius)
    {
        var replies = new List<byte[]>();
        if (brain.Mode == Mode.Wander)
        {
            if (Step(target, brain, brain.Goal, brain.GoalZ, 0, now))
            {
                target.Position = new Vector3d(brain.Goal.X, brain.Goal.Y, Height(brain, brain.Goal.X, brain.Goal.Y, brain.GoalZ));
                replies.AddRange(Stop(target, brain, brain.Heading, now));
                brain.Mode = Mode.Idle;
                brain.WanderAtMs = WanderDelay(now);
            }
            else replies.AddRange(MoveUpdate(target, brain, now));
            return replies;
        }
        if (brain.WanderAtMs == 0) brain.WanderAtMs = WanderDelay(now);
        if (now >= brain.WanderAtMs && target.WalkSpeed != 0 && player is { } p && Distance(target.Position, Flat(p)) <= ActiveRange)
        {
            var angle = rng.NextDouble() * 2 * Math.PI;
            var reach = radius / 4 + rng.NextDouble() * (radius - radius / 4);
            (double X, double Y) goal = (brain.Home.X + Math.Cos(angle) * reach, brain.Home.Y + Math.Sin(angle) * reach);
            var goalZ = brain.Home.Z;
            List<Vector3d>? path = null;
            if (Nav is { } nav)
            {
                // Only walkable, reachable spots: off the mesh (a rock, a wall) the walk is skipped.
                if (nav.FloorAt(goal.X, goal.Y, brain.Home.Z) is not { } floor || (path = PathTo(target, goal, floor)) is null)
                {
                    brain.WanderAtMs = WanderDelay(now);
                    return replies;
                }
                goalZ = floor;
            }
            brain.Mode = Mode.Wander;
            replies.AddRange(Go(target, brain, goal, goalZ, false, now, path));
        }
        return replies;
    }

    /// <summary>
    /// Movement and attack messages for every NPC this tick. <paramref name="hitPlayer"/> applies an
    /// NPC hit's damage to the player and returns the player's update messages (null: NPCs only
    /// threaten). A null <paramref name="player"/> (not in the world, or invisible) is ignored.
    /// </summary>
    public List<byte[]> Tick(IReadOnlyDictionary<int, Target> targets, Vector3d? player, long now, Func<int, List<byte[]>>? hitPlayer = null) =>
        Tick(targets, player is { } p ? [(PlayerKey, p)] : [], now, hitPlayer is null ? null : (_, damage) => hitPlayer(damage));

    /// <summary>
    /// Several players (shared map): each NPC follows its victim while chasing; otherwise it
    /// considers the nearest player (detection, wandering). A victim who left (or hid) ends the
    /// chase as a lost player does. <paramref name="hitPlayer"/> gets the victim's key and damage.
    /// </summary>
    public List<byte[]> Tick(IReadOnlyDictionary<int, Target> targets, IReadOnlyList<(int Key, Vector3d Position)> players, long now,
        Func<int, int, List<byte[]>>? hitPlayer)
    {
        Sync(targets);
        current = targets;
        var replies = new List<byte[]>();
        // The busy NPCs and those placed near a player, in key order.
        var active = new SortedSet<int>(awake);
        foreach (var p in players) active.UnionWith(homes.Near(p.Position.X, p.Position.Y, SleepRange));
        LastTicked = active.Count;
        foreach (var key in active)
        {
            if (!brains.TryGetValue(key, out var brain) || !targets.TryGetValue(key, out var target)) continue;
            (int Key, Vector3d Position)? chosen = null;
            if (brain.VictimKey != 0 && brain.Mode is Mode.Chase or Mode.Follow)
            {
                chosen = Locate(players, targets, brain, brain.VictimKey);
                // The victim left (hid, or died): the next one on the hate table, if any is still here.
                while (chosen is null && brain.Hate.Remove(brain.VictimKey) && TopHate(brain) is { } next)
                {
                    brain.VictimKey = next;
                    chosen = Locate(players, targets, brain, next);
                }
            }
            else
            {
                brain.VictimIsNpc = false;
                var best = double.MaxValue;
                foreach (var p in players)
                {
                    var d = Distance(target.Position, Flat(p.Position));
                    if (d < best) { best = d; chosen = p; }
                }
            }
            brain.CandidateKey = chosen?.Key ?? 0;
            var victim = chosen?.Key ?? 0;
            var npcVictim = brain.VictimIsNpc ? brain.VictimKey : 0;
            replies.AddRange(TickOne(target, brain, chosen?.Position, now,
                chosen is null ? null
                : npcVictim != 0 ? damage => HitNpc(targets, npcVictim, target, damage, now)
                : hitPlayer is null ? null : damage => hitPlayer(brain.VictimKey != 0 ? brain.VictimKey : victim, damage)));
            if (Busy(brain)) awake.Add(key);
            else awake.Remove(key);
        }
        return replies;
    }

    /// <summary>Where a victim is now: a player here, or a living NPC (then the brain fights an NPC); null when gone.</summary>
    static (int Key, Vector3d Position)? Locate(IReadOnlyList<(int Key, Vector3d Position)> players, IReadOnlyDictionary<int, Target> targets, Brain brain, int key)
    {
        foreach (var p in players)
            if (p.Key == key) { brain.VictimIsNpc = false; return p; }
        if (targets.TryGetValue(key, out var prey) && !prey.Dead && !prey.CorpseRemoved) { brain.VictimIsNpc = true; return (prey.Key, prey.Position); }
        return null;
    }
}
