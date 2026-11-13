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
    /// <summary>How far past a skill's reach its hit still lands (a step; OURS, user rule 2026-11-07: no damage from out of reach).</summary>
    public const double LandSlack = 100.0;
    /// <summary>Official: NPC hits were ~7.5% of the NPC's level HP (level 3: 13-17 of 199; level 7: 47-54 of 665).</summary>
    public const double DamagePerHp = 0.075;
    /// <summary>The player's collision radius for reach (RecastNavMesh AgentRadius 50).</summary>
    public const double PlayerRadius = 50.0;
    /// <summary>Below the NPC, a player still counts as level with it within this height (stairs, slopes).</summary>
    public const double AggroBelowTolerance = 150.0;
    public const int PlayerKey = 1;
    /// <summary>A spawn this close to the landscape stands on it (spawns sit ~20 above; buildings are far higher).</summary>
    public const double GroundedTolerance = 100.0;

    /// <summary>server-rules.xml monster_ai (user 2026-11-13): bosses fight only by client data and map events.</summary>
    public bool BossOfficial { get; set; } = ServerRules.Embedded.BossOfficialAi;
    /// <summary>server-rules.xml monster_ai: the other monsters fight by our tactics (NpcTactics, OURS).</summary>
    public bool FunMonsters { get; set; } = ServerRules.Embedded.FunMonsters;

    /// <summary>
    /// Whether this NPC fights by our invented tactics (NpcTactics, OURS: scored random skill pick, generic 75/50/25 % phases,
    /// enrage below 30 %, random target switch, call for help, flee / kite, catch-up speed, aiming ahead): a boss
    /// (NpcTactics.IsBoss) only with boss_official off, every other monster only with fun_monsters on.
    /// </summary>
    public bool Ours(Target target) => NpcTactics.IsBoss(target) ? !BossOfficial : FunMonsters;

    /// <summary>The current map's ground grid, if extracted; null keeps the slope height model.</summary>
    public Terrain? Terrain { get; set; }

    /// <summary>The current map's navigation mesh, if extracted: paths and floor heights.</summary>
    public NavMesh? Nav { get; set; }

    /// <summary>A chase re-plans its path this often, or when the player moved this far from its goal.</summary>
    public const int RepathMs = 1000;
    /// <summary>
    /// Polygon budget of a chase's partial path search (measured 2026-11-08 on Poeta 1000 and 210001, 400 goals within 30 m each:
    /// the same paths as 4096 polygons, worst search 1.5 ms instead of 5.6 ms; an off-mesh goal now searches instead of failing).
    /// </summary>
    public const int PartialPathNodes = 1024;
    /// <summary>
    /// Jumps (user rule 2026-11-08, OURS distances): a chasing ground monster within ChaseMaxCm of its target jumps an obstacle in
    /// front of it (a gap, a ledge, a low rock) when floor on the far side is within JumpCm forward, JumpUpCm up and JumpDownCm
    /// down, and walking cannot reach the target or the way round is much longer; otherwise it goes round (the navmesh path).
    /// </summary>
    public const double JumpCm = 500, JumpUpCm = 250, JumpDownCm = 800;
    public const int JumpCooldownMs = 2000;
    /// <summary>
    /// Melee spacing (user rule 2026-11-08): a melee monster with another monster of its pack in front of it (between it and the
    /// victim) or standing on it moves to a free spot round the victim (SpotCandidates around, bodies apart by SpotSpacing of their
    /// radii) before it attacks; at most SpotMs per move, a new move after SpotCooldownMs.
    /// </summary>
    public const int SpotCandidates = 16, SpotMs = 1500, SpotCooldownMs = 2000;
    public const double SpotSpacing = 0.9;
    public const double RepathDistance = 200.0;

    /// <summary>Scripted (a map event walks the NPC along a named route once) and Follow (it keeps up with a player) are directed moves.</summary>
    public enum Mode { Idle, Wander, Chase, Return, Dead, Patrol, Scripted, Follow }
    /// <summary>A following NPC starts walking beyond FollowFar from the player, runs beyond FollowRun, stops within FollowNear (OURS).</summary>
    public const double FollowNear = 250.0, FollowFar = 450.0, FollowRun = 1200.0;

    /// <summary>A patrol turns toward the next waypoint this close to the current one (official: ~15-20 before each point).</summary>
    public const double ArriveRadius = 18.0;

    /// <summary>A started hit: when it lands; a telegraphed skill also its warned spots and their radius (only there it hits).</summary>
    public sealed record PendingHit(int SkillId, NpcSkill Skill, int ActionSeq, long DueMs, IReadOnlyList<Vector3d>? Spots = null, double SpotRadius = 0);

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
        /// <summary>When it last hit its victim (or began the chase): the leash without hits (ServerRules.LeashNoHitMs).</summary>
        public long LastHitMs { get; set; } = long.MinValue;
        /// <summary>Where the current chase began (the first aggro), and how far from it the monster follows (user rule 2026-11-08).</summary>
        public Vector3d? ChaseAnchor { get; set; }
        public double ChaseLimit { get; set; }
        /// <summary>The monster has landed a hit on its victim in this chase: it may follow ServerRules.ChaseAfterHitCm further.</summary>
        public bool HitVictim { get; set; }
        /// <summary>When a chasing boss lost sight of every target (null while it has one): it waits ServerRules.BossLostGraceMs.</summary>
        public long? LostSinceMs { get; set; }
        /// <summary>A self-destructing monster's blast time (its skill start), the skill and action seq, and whether its end was sent.</summary>
        public long? SelfDestructAt { get; set; }
        public (int Skill, int Seq)? SelfDestructSkill { get; set; }
        public bool SelfDestructEnded { get; set; }
        /// <summary>No new jump before this time (JumpCooldownMs).</summary>
        public long JumpReadyMs { get; set; }
        /// <summary>A flying monster's height above the ground at its spawn, kept while it moves (null until known).</summary>
        public double? Hover { get; set; }
        public Dictionary<int, long> Cooldowns { get; } = [];
        public int ActionSeq { get; set; }
        public PendingHit? Pending { get; set; }
        /// <summary>A cast in progress: the skill start (0x3802) to send when its casting time is over.</summary>
        public (byte[] Start, long DueMs)? PendingStart { get; set; }
        /// <summary>Whether the NPC walks on the landscape (decided from its spawn once a grid is known).</summary>
        public bool? Grounded { get; set; }
        /// <summary>Navmesh waypoints to the goal (the last one is the goal), or null for a straight line.</summary>
        public List<Vector3d>? Path { get; set; }
        /// <summary>The path ends short of the goal (unreachable goal: the nearest reachable spot); the NPC stops at its end.</summary>
        public bool PathPartial { get; set; }
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
        /// <summary>Boss HP phases already run (their hp_percent), and the phase skills still to cast (boss-ai.xml).</summary>
        public HashSet<int> PhasesDone { get; } = [];
        public Queue<int> PhaseSkills { get; } = new();
        /// <summary>When this fight's specials first become ready (0: not in a fight yet).</summary>
        public long OpeningAtMs { get; set; }

        // Tactics (NpcTactics, OURS).
        /// <summary>The last special (non-basic) skill used: not repeated at once.</summary>
        public int LastSpecial { get; set; }
        /// <summary>The victim's last seen position and time, and its speed (units/s): leading and cast timing.</summary>
        public Vector3d? VictimSeen { get; set; }
        public long VictimSeenMs { get; set; }
        public (double X, double Y) VictimVelocity { get; set; }
        /// <summary>Chasing out of reach since (catch-up boost), long.MinValue while in reach.</summary>
        public long OutOfReachSinceMs { get; set; } = long.MinValue;
        /// <summary>NpcTactics.Phases already passed this fight (bits), and a forced mechanic due (NPCs without boss-ai.xml phases).</summary>
        public int TacticPhases { get; set; }
        public bool PhaseDue { get; set; }
        public bool Enraged { get; set; }
        /// <summary>Running (for help, or a ranged fighter stepping back) until; a run for help happens once per fight.</summary>
        public long FleeUntilMs { get; set; }
        public bool Fled { get; set; }
        public bool Kiting { get; set; }
        public long KiteAtMs { get; set; }
        public long HateCheckAtMs { get; set; }
        /// <summary>Engaged this tick (or reached a phase, or ended a run for help): calls its kind after the tick.</summary>
        public bool CallHelp { get; set; }
        /// <summary>Monsters on the same victim this tick (surrounding instead of stacking).</summary>
        public int PackSize { get; set; }
        public int PackSlot { get; set; }
        /// <summary>A free spot round the victim a crowded melee monster is moving to before it attacks; when the next may be chosen.</summary>
        public (double X, double Y)? Spot { get; set; }
        public long SpotUntilMs { get; set; }
        public long SpotReadyMs { get; set; }
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

    /// <summary>The lowest HP a target may drop to (a map event still needs it), set by the host; null: no limit.</summary>
    public Func<Target, int>? HpFloor { get; set; }
    /// <summary>NPCs hit by other NPCs since the last take (the host checks the map events' HP objectives).</summary>
    readonly List<Target> npcHits = [];

    /// <summary>The skill of the last NPC hit on a player (its abnormals go on the victim: a boss's stun).</summary>
    public int LastSkill { get; private set; }

    readonly List<(Target Caster, int Skill, int Victim)> casts = [];

    /// <summary>The skills NPCs started since the last call (boss mechanics hook on them: BossMechanicTable).</summary>
    public List<(Target Caster, int Skill, int Victim)> TakeCasts()
    {
        var taken = casts.ToList();
        casts.Clear();
        return taken;
    }

    /// <summary>A monster skill's damage from its client-table ratio (as Damage), for helpers that hit for their boss.</summary>
    public int SkillDamage(Target caster, int ratioMin, int ratioMax)
    {
        var low = Math.Min(ratioMin, ratioMax); var high = Math.Max(ratioMin, ratioMax);
        var factor = (low + rng.NextDouble() * (high - low)) / 10000.0;
        return Math.Max(1, (int)Math.Round(Combat.HpForLevel(caster.Level) * DamagePerHp * caster.DamageScale * factor));
    }

    public List<Target> TakeNpcHits()
    {
        var taken = npcHits.Distinct().ToList();
        npcHits.Clear();
        return taken;
    }

    public List<(Target Victim, Target Killer)> TakeNpcKills()
    {
        var taken = npcKills.ToList();
        npcKills.Clear();
        return taken;
    }

    /// <summary>NPCs other NPCs hit since the last take (the host reports their HP to the map events: HpRemaining steps).</summary>
    readonly HashSet<int> npcHurt = [];

    public List<int> TakeNpcHurt()
    {
        var taken = npcHurt.ToList();
        npcHurt.Clear();
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
        // The route's own speed bonus (MapData AdditionalMoveStat): the official Robin runs his scripted path with MoveSpeed 8000
        // (0x364b at the move start, ~875/s observed against his NpcData 500) and his route says 8000. Before, every Companion got
        // 8000 on any run route: quest partners on routes without a bonus ran 80 % too fast and were gone before the talk (user
        // report 2026-11-09), other NPCs on boosted routes too slow.
        var boost = SetBoost(target, brain, route.Boost);
        return [GamePackets.NpcMoveStart(target.Key, target.Position, brain.Heading, route.Run), .. boost];
    }

    /// <summary>How far a companion looks for monsters (its NpcData aggro radius is 1000; OURS: a full circle).</summary>
    public const double CompanionRange = 1000.0;

    /// <summary>
    /// The ChangeBT trees whose NPC looks for monsters to fight: Def_Companion_NPC_01 (14 uses: Griscilla in Dungeon_D_0007, Ulgorn and
    /// Nemon in Dungeon_D_0009 after their NoCombat walk). CANDIDATE reading of the names (the trees are client/server AI assets we do not
    /// have): Def_Companion_NPC_NoCombat (10), Def_Citizen_01 (7), Def_Citizen_Melee_01 (2) and def_idle (1) seek no fight.
    /// </summary>
    public static bool FightingBehaviour(string tree) => string.Equals(tree, "Def_Companion_NPC_01", StringComparison.OrdinalIgnoreCase);

    /// <summary>The nearest living hostile NPC within a companion's range, checked twice a second while it is free.</summary>
    Target? CompanionPrey(Target target, Brain brain, long now)
    {
        // A map event's ChangeBT decides (Def_Companion_NPC_01 fights, NoCombat / Citizen / idle do not), else the NpcData companion.
        var fights = target.Behaviour is { } tree ? FightingBehaviour(tree) : target.Function == "Companion" && target.Aggressive;
        if (!fights || target.Hostile || current is null) return null;
        if (brain.Mode is not (Mode.Idle or Mode.Follow or Mode.Wander) || now < brain.CompanionScanAtMs) return null;
        brain.CompanionScanAtMs = now + 500;
        Target? best = null;
        var bestDistance = CompanionRange;
        foreach (var other in current.Values)
        {
            if (other.Key == target.Key || !other.Hostile || other.Dead || other.CorpseRemoved || other.Untouchable) continue;
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
        // Stopped while fighting (Munihelle's StopFollowPc comes with the watchstone's kill, before the companion's fight has
        // ended): it must not take up following again afterwards (live 2026-10-28 Elvida kept following, no talk).
        brain.ResumeFollowKey = 0;
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
    /// <paramref name="others"/>: the rest of the victim's spawner, fought next (their hate is 0, below the first's).
    public List<byte[]> ForceAggroNpc(Target target, Target victim, long now, IEnumerable<Target>? others = null)
    {
        var brain = BrainOf(target);
        if (target.Dead || victim.Dead || target.RunSpeed == 0 && target.WalkSpeed == 0) return [];
        // A following companion sent into a fight follows its player again afterwards (Munihelle step 7: FollowPc, then
        // SetAggroNpc at the Drakan; the order dropped the follow and she stood idle).
        if (brain.Mode == Mode.Follow && brain.FollowKey != 0) brain.ResumeFollowKey = brain.FollowKey;
        brain.Hate[victim.Key] = brain.Hate.GetValueOrDefault(victim.Key) + 1;
        foreach (var other in others ?? []) if (other.Key != victim.Key && !other.Dead) brain.Hate.TryAdd(other.Key, 0);
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
        // A helper (Function Companion: Elvida, Robin) only assists: its hits do HelperDamagePercent (server-rules.xml, provisional)
        // and do not count on a hit gauge (the totem is the player's to destroy).
        if (attacker.Function == "Companion") damage = victim.HitGauge ? 0 : Math.Max(1, damage * ServerRules.Embedded.HelperDamagePercent / 100);
        else if (victim.HitGauge) damage = damage > 0 ? 1 : 0;
        if (!victim.Immune) victim.Hp = Math.Max(HpFloor?.Invoke(victim) ?? 0, victim.Hp - damage);
        npcHits.Add(victim);
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
            target.Position = new Vector3d(x, y, target.Air ? AirHeight(brain, x, y, slope) : Height(brain, x, y, slope));
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
            brain.PathPartial = EndsShort(brain.Path, brain.Goal);
            brain.PathIndex = 0;
            brain.RepathAtMs = now + RepathMs;
        }
        if (Step(target, brain, Flat(p), p.Z, FollowNear, now)) return [.. boost, .. SetBoost(target, brain, 0), .. Stop(target, brain, HeadingTo(target.Position, Flat(p)), now)];
        return [.. boost, .. MoveUpdate(target, brain, now)];
    }

    readonly Dictionary<int, Brain> brains = [];
    /// <summary>The chasing monsters on each victim this tick (melee spacing).</summary>
    readonly Dictionary<int, List<Target>> packMembers = [];

    /// <summary>
    /// Another monster of the pack stands on this one (bodies overlap and it is nearer the victim) or in front of it (between it and
    /// the victim, within their bodies' width): a melee monster then looks for space.
    /// </summary>
    static bool Crowded(Target me, Vector3d victim, List<Target> pack)
    {
        double vx = victim.X - me.Position.X, vy = victim.Y - me.Position.Y, mine = Math.Sqrt(vx * vx + vy * vy);
        if (mine < 1) return false;
        double ux = vx / mine, uy = vy / mine;
        foreach (var other in pack)
        {
            if (other == me || other.Dead) continue;
            var room = (me.Radius + other.Radius) * SpotSpacing;
            double ox = other.Position.X - me.Position.X, oy = other.Position.Y - me.Position.Y;
            var theirs = Distance(other.Position, Flat(victim));
            if (ox * ox + oy * oy < room * room && theirs <= mine) return true;
            var along = ox * ux + oy * uy;
            var side = Math.Abs(-ox * uy + oy * ux);
            if (along > 0 && along < mine && side < room) return true;
        }
        return false;
    }

    /// <summary>
    /// A free spot round the victim within attack reach: SpotCandidates points on a ring, nearest to the monster's own side first,
    /// away from every other pack member's body (or the spot it is moving to) and on the floor. Null when the ring is full.
    /// </summary>
    (double X, double Y)? FreeSpot(Target me, Vector3d victim, List<Target> pack, double ring)
    {
        var bearing = Math.Atan2(me.Position.Y - victim.Y, me.Position.X - victim.X);
        for (var k = 1; k < SpotCandidates; k++)
        {
            var step = (k + 1) / 2 * (k % 2 == 0 ? -1 : 1);   // 1, -1, 2, -2, ... slots from its own side
            var angle = bearing + step * 2 * Math.PI / SpotCandidates;
            (double X, double Y) spot = (victim.X + Math.Cos(angle) * ring, victim.Y + Math.Sin(angle) * ring);
            var free = true;
            foreach (var other in pack)
            {
                if (other == me || other.Dead) continue;
                var room = (me.Radius + other.Radius) * SpotSpacing;
                var at = SpotOf(other) ?? (other.Position.X, other.Position.Y);
                if ((at.X - spot.X) * (at.X - spot.X) + (at.Y - spot.Y) * (at.Y - spot.Y) < room * room) { free = false; break; }
            }
            if (free && (Nav is null || Nav.FloorAt(spot.X, spot.Y, victim.Z, 250) is not null)) return spot;
        }
        return null;
    }

    /// <summary>The spot a pack member is moving to (so two monsters do not pick the same one), or null.</summary>
    (double X, double Y)? SpotOf(Target t) => brains.TryGetValue(t.Key, out var b) ? b.Spot : null;
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
        // A monster that cannot move (NpcData speeds 0: the poison grass, the bombs) fights back from where it stands when it has a
        // skill; before 2026-11-12 it ignored every hit.
        if (!brains.TryGetValue(target.Key, out var brain) || target.Dead || target.RunSpeed == 0 && !Stationary(target) || brain.Mode == Mode.Return) return [];
        awake.Add(target.Key);
        brain.Hate[attackerKey] = brain.Hate.GetValueOrDefault(attackerKey) + Math.Max(1, damage);
        brain.CandidateKey = TopHate(brain) ?? attackerKey;
        return StartChase(target, brain, now, detected: false);
    }

    static int? TopHate(Brain brain) => brain.Hate.Count == 0 ? null : brain.Hate.MaxBy(h => h.Value).Key;

    /// <summary>The NPC died: it leaves combat (official 0x8d21 off after the kill) and drops a pending hit.</summary>
    /// <summary>A victim's defense by entity key (null: no reduction).</summary>
    public Func<int, int>? DefenseOf { get; set; }
    /// <summary>
    /// A player victim's answer to a monster hit, by key and attacker: chances to dodge (the hit lands for 0) and block (BlockReduction
    /// less), the damage taken multiplier (damage reductions) and extra defense (boss defense against Hero monsters).
    /// </summary>
    public Func<int, Target, (double Dodge, double Block, double Taken, int ExtraDefense)>? AvoidanceOf { get; set; }
    /// <summary>A player victim's block cut (its weapon/shield Decrease Block), by key.</summary>
    public Func<int, double>? BlockCutOf { get; set; }
    /// <summary>Where the attacker hits a player victim from (EAttackAngle: 1 behind, 2 front) and whether it blocks with a shield.</summary>
    public Func<int, Target, (byte Angle, bool Shield)>? HitSideOf { get; set; }

    /// <summary>The NPC whose hit was applied last (the killer when that hit was fatal).</summary>
    public int LastAttacker { get; private set; }

    /// <summary>The skill of the NPC hit applied last on a player (CharDie_NT names it).</summary>
    public int LastAttackSkill { get; private set; }

    public List<byte[]> Killed(Target target)
    {
        // OBSERVED: official sends the dead NPC's 0x8d21 off after its 0x8d04 almost always (668 of 691), also for a monster
        // killed before it fought back (we sent it only for engaged ones).
        if (brains.TryGetValue(target.Key, out var brain))
        {
            brain.Pending = null;
            brain.Cooldowns.Clear();
            brain.PhasesDone.Clear();
            brain.PhaseSkills.Clear();
            brain.OpeningAtMs = 0;
            brain.Engaged = false;
        }
        target.InCombat = false;
        return [GamePackets.CombatState(target.Key, false)];
    }

    /// <summary>
    /// The fight begins. <paramref name="detected"/>: the monster noticed the player itself (its aggro shape, or a map event);
    /// otherwise it was hit first (provoked).
    /// </summary>
    static List<byte[]> StartChase(Target target, Brain brain, long now, bool detected = true)
    {
        var replies = new List<byte[]>();
        brain.VictimKey = brain.CandidateKey != 0 ? brain.CandidateKey : PlayerKey;
        if (!brain.Engaged)
        {
            // Official aggro start, all in one tick: NpcFirstAggro_NT 0x8d52, the NPC's target (0x3835, the player), a stop
            // (0x371a), 0x8d21 on (recording 2026-10-19). OFFICIAL (20 recordings 2026-11-06/07): only a monster that noticed the
            // player itself sends 0x8d52 / 0x3835 (Aggro_A: 114 starts); a monster hit first starts with 0x8d21 alone (AggroNormal
            // 556 of 556 combat starts, none with 0x8d52, 546 without 0x3835), and so do the Revenge_A bombs (47 of 47, rec
            // 20261107-013918 #17897). A monster that cannot move sends no stop (the poison grass rec 20261106-144624 #29055: 0x8d21,
            // then its cast).
            brain.Engaged = true;
            brain.CallHelp = target.Hostile;
            brain.ChaseAnchor = target.Position;   // the chase distance counts from here (its limit is drawn on the first chase tick)
            brain.ChaseLimit = 0;
            brain.HitVictim = false;
            target.InCombat = true;
            if (detected && target.AggroType != "Revenge_A")
            {
                replies.Add(GamePackets.NpcFirstAggro(target.Key));
                replies.Add(GamePackets.TargetUpdate(target.Key, brain.VictimKey));
            }
            if (!Immobile(target)) replies.AddRange(Stop(target, brain, brain.Heading, now));
            // The berserk time: boss-ai.xml, else the client's NpcData bUseBossBerserk / BossBerserkInvokeTime (377 bosses, 300 s
            // for most); the wire offset as OBSERVED once (BossAi.BerserkOffsetMs), CANDIDATE for the client-table bosses.
            var known = BossAi.Of(target.NpcId) is { BerserkMs: > 0 } boss ? boss : null;
            var berserkMs = known?.BerserkMs ?? (ClientCombat.Embedded.Boss(target.NpcId)?.BerserkS ?? 0) * 1000L;
            var berserkOffset = known?.BerserkOffsetMs ?? BossClient.Embedded.BerserkOffsetMs;
            target.BerserkAt = berserkMs > 0 ? now + berserkMs : 0;
            replies.Add(berserkMs > 0
                ? GamePackets.CombatState(target.Key, true, now + berserkOffset + berserkMs)
                : GamePackets.CombatState(target.Key, true));
        }
        if (brain.Mode != Mode.Chase) brain.LastHitMs = now;
        brain.Mode = Mode.Chase;
        brain.Moving = false;  // the next tick starts a run (0x371b) towards the player
        brain.LastMs = now;
        return replies;
    }

    /// <summary>Navmesh waypoints from the NPC to the goal, or null (no mesh, or no path).</summary>
    /// <summary>Line of sight from the NPC to the player on the navmesh (true without a mesh).</summary>
    bool Sight(Target target, Vector3d player) => Nav?.LineOfSight(target.Position, player) ?? true;

    /// <summary>
    /// Navmesh waypoints to the goal. Partial (default): an unreachable goal (a player on a rock, across a gap) gives the path to
    /// the nearest reachable spot, so the NPC goes as close as it can instead of walking straight through the wall.
    /// </summary>
    List<Vector3d>? PathTo(Target target, (double X, double Y) goal, double goalZ, bool partial = true) => partial
        ? Nav?.FindPathPartial(target.Position, new Vector3d(goal.X, goal.Y, goalZ), PartialPathNodes)
        : Nav?.FindPath(target.Position, new Vector3d(goal.X, goal.Y, goalZ));

    /// <summary>Whether a path stops short of its goal (more than 1 cm away).</summary>
    static bool EndsShort(List<Vector3d>? path, (double X, double Y) goal) =>
        path is { Count: > 0 } && Distance(path[^1], goal) > 1;

    /// <summary>A destination moved back along the floor to the last walkable point before a wall or edge (unchanged without a mesh).</summary>
    (double X, double Y) Reachable(Target target, (double X, double Y) goal) =>
        Nav?.MoveAlong(target.Position, new Vector3d(goal.X, goal.Y, target.Position.Z)) is { } at ? (at.X, at.Y) : goal;

    (double X, double Y) Aim(Brain brain) =>
        brain.Path is { } path && brain.PathIndex < path.Count - 1 ? (path[brain.PathIndex].X, path[brain.PathIndex].Y) : brain.Goal;

    List<byte[]> Go(Target target, Brain brain, (double X, double Y) goal, double goalZ, bool run, long now, List<Vector3d>? path = null)
    {
        brain.Goal = goal;
        brain.GoalZ = goalZ;
        brain.Run = run;
        brain.Path = path ?? PathTo(target, goal, goalZ);
        brain.PathPartial = EndsShort(brain.Path, goal);
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
    /// A flying monster's height at (x, y): the ground under it plus its spawn's height above the ground, so it rises over hills,
    /// ledges and bridges instead of flying into them (user rule 2026-11-08); the straight slope where no ground is known.
    /// </summary>
    double AirHeight(Brain brain, double x, double y, double fallback)
    {
        double? Ground(double gx, double gy, double near) => Nav?.FloorAt(gx, gy, near, 5000) ?? Terrain?.GroundAt(gx, gy);
        brain.Hover ??= Ground(brain.Home.X, brain.Home.Y, brain.Home.Z) is { } home ? Math.Max(0, brain.Home.Z - home) : null;
        return brain.Hover is { } hover && Ground(x, y, fallback - hover) is { } ground ? ground + hover : fallback;
    }

    /// <summary>The walking length of a path from the NPC's position.</summary>
    static double PathLength(Vector3d from, List<Vector3d>? path)
    {
        if (path is not { Count: > 0 }) return 0;
        double length = 0; var at = from;
        foreach (var point in path) { length += Math.Sqrt((point.X - at.X) * (point.X - at.X) + (point.Y - at.Y) * (point.Y - at.Y)); at = point; }
        return length;
    }

    /// <summary>
    /// A chasing ground monster's jump over the obstacle in front of it (JumpCm): when its target is within ChaseMaxCm, the straight
    /// line to it is blocked close ahead, and walking cannot reach it or goes the long way round (more than twice the straight
    /// distance plus 5 m), it lands on the far side's floor. Null when it should walk (round) instead.
    /// </summary>
    List<byte[]>? TryJump(Target target, Brain brain, Vector3d victim, long now)
    {
        if (target.Air || Nav is not { } nav || now < brain.JumpReadyMs) return null;
        var direct = Distance(target.Position, Flat(victim));
        if (direct > ServerRules.Embedded.ChaseMaxCm || direct < 50) return null;
        // The walking way (the planned path, or a fresh one when none is planned yet): reachable and not much longer means walk.
        var path = brain.Path;
        var partial = brain.PathPartial;
        if (path is null) { path = PathTo(target, Flat(victim), victim.Z); partial = EndsShort(path, Flat(victim)); }
        if (!partial && path is { Count: > 0 } && PathLength(target.Position, path) <= direct * 2 + 500) return null;
        var blocked = nav.MoveAlong(target.Position, victim);
        var clear = Distance(blocked, (target.Position.X, target.Position.Y));
        if (clear >= Math.Min(direct, JumpCm) - 30) return null;   // nothing in the way within a jump
        brain.JumpReadyMs = now + JumpCooldownMs;
        if (nav.JumpTarget(target.Position, victim, clear + 25, Math.Min(JumpCm, direct), JumpUpCm, JumpDownCm) is not { } land) return null;
        target.Position = land;
        var heading = HeadingTo(land, Flat(victim));
        brain.Heading = heading;
        brain.Path = null;
        brain.PathPartial = false;
        brain.Moving = false;
        return [GamePackets.NpcStop(target.Key, land, heading)];
    }

    /// <summary>
    /// Advance a moving NPC towards goal; true when within stopAt of it. Height: the ground, or
    /// the straight slope from the current position to goalZ.
    /// </summary>
    bool Step(Target target, Brain brain, (double X, double Y) goal, double goalZ, double stopAt, long now)
    {
        var elapsed = Math.Max(0, now - brain.LastMs) / 1000.0;
        brain.LastMs = now;
        var budget = (brain.Run ? target.RunSpeed : target.WalkSpeed) * (1 + brain.SpeedBoost / 10000.0) * elapsed
            * Math.Max(0.1, 1 + target.ModPercent("MoveSpeed", tickNow) / 100.0);   // Slow (-40%) and other speed debuffs
        // Walk the waypoints in order; stopAt applies to the final goal only.
        while (true)
        {
            var final = brain.Path is not { } path || brain.PathIndex >= path.Count - 1;
            // A partial path's last leg goes to its own end (the nearest reachable spot), not on through the wall to the goal.
            var shortEnd = final && brain.PathPartial && brain.Path is { Count: > 0 };
            var aim = shortEnd ? (brain.Path![^1].X, brain.Path[^1].Y) : final ? goal : (brain.Path![brain.PathIndex].X, brain.Path[brain.PathIndex].Y);
            var aimZ = shortEnd ? brain.Path![^1].Z : final ? goalZ : brain.Path![brain.PathIndex].Z;
            var distance = Distance(target.Position, aim);
            var remaining = distance - (final && !shortEnd ? stopAt : 0);
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
            target.Position = new Vector3d(x, y, target.Air ? AirHeight(brain, x, y, slope) : Height(brain, x, y, slope));
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

    /// <summary>
    /// A companion's fight is over (0x8d21 off, as a reset monster's). Left on, the client kept it in combat: Munihelle's Elvida
    /// could not be talked to after the watchstone, so the event's last step (AskNpc) never came (live 2026-10-28).
    /// </summary>
    static List<byte[]> LeaveCombat(Target target, Brain brain)
    {
        if (!brain.Engaged) return [];
        brain.Engaged = false;
        return [GamePackets.CombatState(target.Key, false)];
    }

    List<byte[]> Reset(Target target, Brain brain, long now)
    {
        var replies = new List<byte[]>();
        brain.Pending = null;
        brain.Cooldowns.Clear();
        brain.PhasesDone.Clear();
        brain.PhaseSkills.Clear();
        brain.OpeningAtMs = 0;
        if (brain.Engaged)
        {
            brain.Engaged = false;
            target.InCombat = false;
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
        brain.ChaseAnchor = null;
        brain.ChaseLimit = 0;
        brain.HitVictim = false;
        brain.LostSinceMs = null;
        ResetTactics(brain);
        replies.AddRange(SetBoost(target, brain, 0));
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

    static BossAiTable BossAi => BossAiTable.Embedded;

    /// <summary>The NPC skill data of an id (a damaging skill, or a no-damage mechanic with a cast).</summary>
    /// A skill without damage and without a cast is used too when it puts abnormals on its targets (instant debuffs: 44 monster skills
    /// of the world spawns were never used before 2026-11-12).
    NpcSkill? SkillData(int id) => data?.NpcSkill(id) ?? (data?.NpcAnySkill(id) is { } any && (any.Cast > 0 || NpcTeleports.Of(id) is not null || Debuffs(id)) ? any : null)
        ?? BossAi.Skill(id);

    /// <summary>The skill puts abnormals on its targets (a Negative skill with abnormal rows: poison, stun, sleep, slow...).</summary>
    static bool Debuffs(int skill) => NpcSkillEffects.Of(skill) is { Abnormals.Count: > 0 } && SkillBehaviour.Embedded.Of(skill)?.Disposition == "Negative";

    /// <summary>NpcData speeds 0: it never moves (plants, bombs, totems, turrets).</summary>
    public static bool Immobile(Target target) => target.WalkSpeed <= 0 && target.RunSpeed <= 0;

    /// <summary>A hostile NPC that cannot move but has a skill it can use: it fights from where it stands (not the spawn-poses.xml totems).</summary>
    bool Stationary(Target target) => Immobile(target) && target.Hostile && SpawnPoses.Gauge(target.NpcId) is null && target.Skills.Any(id => SkillData(id) is not null);

    /// <summary>
    /// A stationary monster's tick (the poison grass, the bombs): an aggressive one notices a player in its aggro shape, then it
    /// uses its skills on whoever is in their reach, without moving or turning (OFFICIAL: the grass's cast yaw is its spawn yaw,
    /// rec 20261106-144624 #29062; no 0x371a / 0x371b ever). It gives up (combat off, full HP, in place) when its victim is gone,
    /// farther than StationaryLeashCm beyond its reach, or out of reach for ServerRules.LeashNoHitMs. OURS (CANDIDATE): the give-up
    /// distances; no official sample of a grass losing its target.
    /// </summary>
    List<byte[]> StationaryStep(Target target, Brain brain, Vector3d? player, long now)
    {
        if (!Stationary(target)) return [];
        var replies = new List<byte[]>();
        if (brain.Mode is Mode.Idle or Mode.Wander or Mode.Patrol && target.Aggressive && player is { } seen && Detects(target, brain.Heading, seen))
        {
            brain.Mode = Mode.Idle;
            replies.AddRange(StartChase(target, brain, now, detected: true));
        }
        if (brain.Mode != Mode.Chase) return replies;
        var reach = target.Skills.Select(id => (id, s: SkillData(id))).Where(x => x.s is not null).Select(x => AttackRange(target, x.id, x.s)).DefaultIfEmpty(Reach).Max();
        var limit = Math.Max(reach, target.Aggro?.Radius ?? 0) + StationaryLeashCm;
        var distance = player is { } at ? Distance(target.Position, Flat(at)) : double.MaxValue;
        if (player is not { } p || distance > limit || brain.LastHitMs != long.MinValue && now - brain.LastHitMs > ServerRules.Embedded.LeashNoHitMs)
        {
            brain.Pending = null;
            brain.PendingStart = null;
            replies.AddRange(Reset(target, brain, now));
            brain.WanderAtMs = long.MaxValue;
            return replies;
        }
        if (brain.Pending is null && brain.PendingStart is null && Sight(target, p) && Choose(target, brain, distance, now) is { } skill)
            replies.AddRange(Attack(target, brain, skill, HeadingTo(target.Position, Flat(p)), now, p));
        return replies;
    }

    /// <summary>How far beyond its reach (or aggro radius) a stationary monster keeps its victim (OURS).</summary>
    public const double StationaryLeashCm = 1000.0;

    /// <summary>
    /// The skill to use now (Stage 6, gamedata/provisional/boss-ai.xml): first a boss's HP-phase skills (once each, whatever
    /// their cooldown or range), then the specials in the boss's rotation order (a damaging skill or a cast mechanic whose
    /// cooldown is over and whose range reaches), then a ready leap that lands at an out-of-reach target (gap-close), else the
    /// basic attack when ready and in reach. A special first becomes ready a short opening into the fight (OBSERVED: 1-8 s).
    /// </summary>
    (int Id, NpcSkill Skill)? Choose(Target target, Brain brain, double distance, long now)
    {
        if (data is null || brain.Pending is not null || now < brain.NextAttackMs) return null;
        var boss = BossAi.Of(target.NpcId);
        if (boss is { Phases.Count: > 0 } && target.HpMax > 0)
            foreach (var phase in boss.Phases)
                if (!brain.PhasesDone.Contains(phase.HpPercent) && (long)target.Hp * 100 <= (long)target.HpMax * phase.HpPercent)
                {
                    brain.PhasesDone.Add(phase.HpPercent);
                    foreach (var id in phase.Skills) brain.PhaseSkills.Enqueue(id);
                }
        while (brain.PhaseSkills.TryDequeue(out var phaseSkill))
            if (SkillData(phaseSkill) is { } mechanic) return (phaseSkill, mechanic);
        if (!Ours(target)) return OfficialChoice(target, brain, distance, now);
        // Tactics (NpcTactics, OURS: user 2026-11-07 "really smart, skills used randomly"): every ready skill that can be used now
        // is scored for the moment; the pick is random, weighted by the scores (bosses pick the best more often than normal
        // monsters); a forced phase mechanic always goes first. boss-ai.xml's rotation order adds to the score.
        var basic = Basic(target);
        var melee = basic is { } mb ? AttackRange(target, mb.Id, mb.Skill) : AttackRange(target, null);
        var hpShare = target.HpMax > 0 ? (double)target.Hp / target.HpMax : 1;
        var speed = Math.Sqrt(brain.VictimVelocity.X * brain.VictimVelocity.X + brain.VictimVelocity.Y * brain.VictimVelocity.Y);
        var situation = new NpcTactics.Situation(target.Rank, distance, melee, hpShare, brain.PhaseDue && boss is not { Phases.Count: > 0 }, speed,
            brain.LastSpecial, RangedFighter: basic is { } rb && rb.Skill.Range >= 500);
        var order = BossAi.Ordered(target.NpcId, target.Skills).ToList();
        var options = new List<((int Id, NpcSkill Skill) Skill, double Score)>();
        for (var index = 0; index < order.Count; index++)
        {
            var id = order[index];
            if (SkillData(id) is not { } skill || basic is { } b && b.Id == id) continue;
            if (!brain.Cooldowns.TryGetValue(id, out var readyAt))
            {
                // One opening per fight for all its specials (OBSERVED 1-8 s); bosses and elites a little sooner.
                if (brain.OpeningAtMs == 0)
                    brain.OpeningAtMs = now + (long)(rng.Next(BossAi.OpeningMinMs, Math.Max(BossAi.OpeningMinMs, BossAi.OpeningMaxMs) + 1) * NpcTactics.OpeningShare(target.Rank));
                brain.Cooldowns[id] = Math.Min(now + skill.Cooldown, brain.OpeningAtMs);
                continue;
            }
            var forced = situation.ForcePhase && skill.Effect == 0;
            if (now < readyAt && !forced) continue;
            var reach = AttackRange(target, id, skill);
            // A forward leap may be used from farther (gap-close), a forced mechanic from anywhere.
            var leapsIn = NpcTactics.KindOf(id) == NpcTactics.LeapKind.Forward && distance <= reach + BossAi.GapCloseCm;
            if (distance > reach && !forced && !leapsIn) continue;
            var score = NpcTactics.Score(id, skill, basic: false, situation, rng.NextDouble());
            if (double.IsNaN(score)) continue;
            if (boss is not null) score += (order.Count - index) * 0.15;
            options.Add(((id, skill), score));
        }
        if (basic is { } ready && now >= brain.Cooldowns.GetValueOrDefault(ready.Id) && distance <= AttackRange(target, ready.Id, ready.Skill))
            options.Add((ready, NpcTactics.Score(ready.Id, ready.Skill, basic: true, situation, rng.NextDouble())));
        if (options.Count == 0) return null;
        // A boss with a recorded rotation (boss-ai.xml) opens with it as official: its first special is the first ready one in that
        // order; after that the weighted random pick.
        var pick = boss is { Rotation.Count: > 0 } && brain.LastSpecial == 0 && options.FindIndex(o => basic is not { } ob || o.Skill.Id != ob.Id) is var first and >= 0
            ? first : NpcTactics.Pick(options.Select(o => o.Score).ToList(), target.Rank, rng.NextDouble());
        var chosen = options[pick].Skill;
        if (basic is not { } bb || bb.Id != chosen.Id)
        {
            if (chosen.Skill.Effect == 0) brain.PhaseDue = false;
            brain.LastSpecial = chosen.Id;
        }
        return chosen;
    }

    /// <summary>
    /// The skill choice without our tactics (bosses with monster_ai boss_official, every monster with fun_monsters off): only
    /// client data. The specials in ActiveSkillList order (boss-ai.xml rotation first where recorded): the first one whose
    /// cooldown is over and whose range reaches is used (a leap onto the target also from gap_close_cm farther; a leap home is
    /// left to the scripts), else the basic attack. Specials become ready after the observed opening (boss-ai.xml defaults,
    /// 1-4 s), then follow their client cooldowns. No scores, no random pick, no forced phases.
    /// </summary>
    (int Id, NpcSkill Skill)? OfficialChoice(Target target, Brain brain, double distance, long now)
    {
        var basic = Basic(target);
        foreach (var id in BossAi.Ordered(target.NpcId, target.Skills))
        {
            if (SkillData(id) is not { } skill || basic is { } b && b.Id == id) continue;
            if (!brain.Cooldowns.TryGetValue(id, out var readyAt))
            {
                if (brain.OpeningAtMs == 0)
                    brain.OpeningAtMs = now + rng.Next(BossAi.OpeningMinMs, Math.Max(BossAi.OpeningMinMs, BossAi.OpeningMaxMs) + 1);
                brain.Cooldowns[id] = Math.Min(now + skill.Cooldown, brain.OpeningAtMs);
                continue;
            }
            if (now < readyAt) continue;
            var kind = NpcTactics.KindOf(id);
            if (kind == NpcTactics.LeapKind.Home) continue;
            var reach = AttackRange(target, id, skill);
            if (distance > reach && !(kind == NpcTactics.LeapKind.Forward && distance <= reach + BossAi.GapCloseCm)) continue;
            brain.LastSpecial = id;
            return (id, skill);
        }
        if (basic is { } ready && now >= brain.Cooldowns.GetValueOrDefault(ready.Id) && distance <= AttackRange(target, ready.Id, ready.Skill))
            return ready;
        return null;
    }

    /// <summary>Official order: a stop facing the player, then the skill start; the hit follows the cast and animation.</summary>
    List<byte[]> Attack(Target target, Brain brain, (int Id, NpcSkill Skill) skill, double facing, long now, Vector3d? victimAt = null)
    {
        var replies = new List<byte[]>();
        var still = target.NoRotation || Immobile(target);
        if (!still) replies.AddRange(Stop(target, brain, facing, now));
        else brain.Moving = false;
        // A Self skill (Skill TargetProcessType Self: the grass's poison cloud, the bombs) names its caster as the target in the
        // 0x3809 / 0x3802 (OFFICIAL rec 20261106-144624 #26925 `0238 ae9b01 00 40b71200 01 02 ae9b01 ...`, #29062; bombs #17904).
        var named = NpcSkillEffects.Of(skill.Id)?.SelfTarget == true ? target.Key : Victim(brain);
        brain.ActionSeq = brain.ActionSeq % 255 + 1;
        casts.Add((target, skill.Id, Victim(brain)));
        // A telegraphed skill (client WideCastingAlertTime / GroundRadius / SkillEffectFilterLocation) warns its spots first
        // (0x3848) and lands after the warning; only a victim still on a spot is hit (BossClient, CANDIDATE timings).
        var telegraph = victimAt is { } aimAt ? Telegraph(target, skill.Id, skill.Skill, brain.ActionSeq, facing, aimAt) : null;
        var castMs = telegraph is { } tg ? Math.Max(skill.Skill.Cast, tg.WarnMs) : skill.Skill.Cast;
        var lands = now + castMs + HitDelayMs;
        // A self-destructing monster (the Draco bomb): its blast hits sooner and it is gone right after (SelfDestructStep).
        if (TrapTable.Embedded.SelfDestructOf(target.NpcId) is { } blast && blast.Skill == skill.Id)
        {
            lands = now + castMs + blast.HitMs;
            brain.SelfDestructAt = now + castMs;
            brain.SelfDestructSkill = (skill.Id, brain.ActionSeq);
        }
        brain.Pending = new PendingHit(skill.Id, skill.Skill, brain.ActionSeq, lands, telegraph?.Spots, telegraph?.Radius ?? 0);
        if (ClientCombat.Embedded.NpcSkill(skill.Id) is { Spawns.Count: > 0 } calls && BossMechanicTable.Embedded.OnCast(skill.Id) is null)
            foreach (var (npc, count, distance) in calls.Spawns) adds.Add((target, npc, count, distance, Victim(brain)));
        brain.LastHitMs = now;   // an attack started (in reach and in sight) counts: only a monster that cannot reach or see gives up
        var cooldown = brain.Enraged ? (int)(skill.Skill.Cooldown * NpcTactics.EnragedCooldown(target.Rank)) : skill.Skill.Cooldown;
        brain.Cooldowns[skill.Id] = now + Math.Max(cooldown, skill.Skill.Cast + HitDelayMs);
        brain.NextAttackMs = lands;
        var yaw = still ? brain.Heading : facing;
        var start = GamePackets.SkillStart(skill.Id, brain.ActionSeq, yaw, target.Position, targetKey: named, actorKey: target.Key, cooldown: skill.Skill.Cooldown);
        // A skill with a casting time shows its cast first (0x3809, the cast bar the player can react to), the start follows
        // when the cast is over (OBSERVED: the official NPC's 0x3802 came 1500 ms after its 0x3809 of a 1500 ms cast).
        if (castMs > 0)
        {
            replies.Add(GamePackets.SkillCastingStart(target.Key, skill.Id, named, yaw, target.Position, castMs));
            brain.PendingStart = (start, now + castMs);
        }
        else replies.Add(start);
        if (telegraph is { } warned) replies.AddRange(warned.Notices);
        return replies;
    }


    /// <summary>Centre distance at which the NPC's skill reaches the player.</summary>
    static double AttackRange(Target target, NpcSkill? skill) => (skill?.Range ?? Reach) + target.Radius + PlayerRadius;

    /// <summary>
    /// The reach of a skill by its id: an NPC that cannot move and whose skill is centred on itself with no range (Skill
    /// TargetProcessType Self, NeedSkillUseRange 0: the traps, totems and turrets of the quest layers, 1,268 spawns in the client's
    /// MapData) reaches its SkillEffectFilter area (OFFICIAL client data; the official floor trap 2290014 fired at the player 300 away,
    /// rec 013918 #16634). Before, Range 0 meant a reach of its body: such NPCs never fired (live 2026-11-12, Drakana Heist).
    /// </summary>
    static double AttackRange(Target target, int skillId, NpcSkill? skill) =>
        Immobile(target) && skill is { Range: <= 0 } && NpcSkillEffects.Of(skillId) is { SelfTarget: true, Area: { Radius: > 0 } area }
            ? area.Radius + target.Radius + PlayerRadius
            : AttackRange(target, skill);

    /// <summary>
    /// A self-destructing monster's end after its blast (OFFICIAL Draco bomb 2290013, rec 013918 #16550-#16663: the 0x3802 at its cast's
    /// end, the 0x3806 400 ms later, then 0x3835 no target and 0x3642 act 7 at 500 ms; no kill notice, no corpse).
    /// </summary>
    List<byte[]> SelfDestructStep(Target target, Brain brain, long blastAt, long now)
    {
        var replies = new List<byte[]>();
        if (target.Dead || TrapTable.Embedded.SelfDestructOf(target.NpcId) is not { } blast || brain.SelfDestructSkill is not { } cast)
        {
            brain.SelfDestructAt = null;
            brain.SelfDestructSkill = null;
            brain.SelfDestructEnded = false;
            return replies;
        }
        if (!brain.SelfDestructEnded && now >= blastAt + blast.EndMs)
        {
            brain.SelfDestructEnded = true;
            replies.Add(GamePackets.SkillEnd(target.Key, cast.Skill, cast.Seq));
        }
        if (now < blastAt + blast.GoneMs) return replies;
        replies.Add(GamePackets.TargetUpdate(target.Key, 0));
        replies.Add(GamePackets.Death(target.Key, 7));
        target.Hp = 0;
        target.CorpseRemoved = true;
        target.InCombat = false;
        brain.SelfDestructAt = null;
        brain.SelfDestructSkill = null;
        brain.SelfDestructEnded = false;
        brain.Pending = null;
        vanished.Add(target.Key);
        return replies;
    }

    readonly List<int> vanished = [];

    /// <summary>NPCs that blew themselves up since the last call (the session forgets them as seen).</summary>
    public List<int> TakeVanished()
    {
        var list = vanished.ToList();
        vanished.Clear();
        return list;
    }

    int Damage(Target target, NpcSkill skill)
    {
        var low = Math.Min(skill.DamageMin, skill.DamageMax);
        var high = Math.Max(skill.DamageMin, skill.DamageMax);
        var factor = (low + rng.NextDouble() * (high - low)) / 10000.0;
        // The NPC's level's Grade 3 HP, not its own HP: the recording of 2026-10-19 has the lake cave boss (hero, level 5, 3000 HP)
        // hitting 21-31 with its ordinary attacks (399 x 7.5 % = 30) and level-4 monsters 11-30; the skills' factors spread that,
        // and the rank scales it (Combat.DamageScale).
        // Its Attack debuffs (Wounded -10%, Shrink -10%...: the skill guide) weaken its hits.
        factor *= 1 + target.ModPercent("Attack", tickNow) / 100.0;
        // Past its berserk time (NpcData BossBerserkInvokeTime) a boss hits harder (BossClient.BerserkDamagePercent, CANDIDATE).
        if (target.BerserkAt > 0 && tickNow >= target.BerserkAt) factor *= BossClient.Embedded.BerserkDamagePercent / 100.0;
        return Math.Max(1, (int)Math.Round(Combat.HpForLevel(target.Level) * DamagePerHp * target.DamageScale * factor));
    }

    /// <summary>
    /// The skill in use advances: the start after its cast, each due hit (it lands when the player is still within reach plus
    /// the skill's follow range), then the end (0x3806; OBSERVED after every NPC skill, 15437 in our recordings), after which
    /// the NPC may choose its next skill.
    /// </summary>
    List<byte[]> Resolve(Target target, Brain brain, Vector3d? player, long now, Func<int, List<byte[]>>? hitPlayer)
    {
        if (brain.Pending is not { } hit || now < hit.DueMs) return [];
        brain.Pending = null;
        // A skill that moves its caster (Teleport_Caster: the Lake Guardian's leaps 1603850 back 1000, 1603851 200 past the
        // player; 194 monster skills): the caster lands first, the hit then counts from there. PROVISIONAL: no official sample.
        var leap = player is { } aim ? Leap(target, brain, hit.SkillId, aim) : [];
        // A skill without damage that puts abnormals on its targets (the sleep grass 1239610, instant stuns and slows) lands like a
        // hit for 0: its abnormal hit and its abnormals; one without either is a mechanic, its animation only.
        var fx = NpcSkillEffects.Of(hit.SkillId);
        var debuffs = Debuffs(hit.SkillId) ? fx!.Abnormals : [];
        if (hit.Skill.Effect == 0 && debuffs.Count == 0) return leap;
        if (player is not { } p || hitPlayer is null) return leap;
        if (hit.Skill.Effect == 0 && brain.VictimIsNpc) return leap;   // CANDIDATE: monster debuffs on other NPCs are not modelled
        // The hit lands only if the victim is still within the skill's reach plus a small step (LandSlack): stepping out of a melee
        // swing or out of a ranged skill's reach dodges it. Skill.dat's follow range (NeedSkillFollowRange: 1400-1900 on the
        // 1500-2000 reach skills) added before let hits land from 3000+ away (live 2026-11-07: "melee damage from the distance").
        if (Distance(target.Position, Flat(p)) > AttackRange(target, hit.SkillId, hit.Skill) + Math.Min(hit.Skill.Follow, LandSlack)) return leap;
        if (!Sight(target, p)) return leap;   // the player went behind a wall before the hit landed (N4)
        // A telegraphed skill hits only a victim standing on one of its warned spots (it stepped out: dodged).
        if (hit.Spots is { Count: > 0 } spots && !spots.Any(s => Distance(s, Flat(p)) <= hit.SpotRadius + PlayerRadius)) return leap;
        var victim = Victim(brain);
        LastAttacker = target.Key;
        brain.LastHitMs = now;
        var replies = new List<byte[]>(leap);
        replies.AddRange(HitOne(target, brain, hit, victim, brain.VictimIsNpc, debuffs, hitPlayer));
        // An area skill hits the other players in its area too, up to its target count (SkillEffectFilter TargetCountMax: the
        // grass's cloud 4 within 300 of it, OFFICIAL client data). The area is centred on the caster, or on the victim for
        // NeedTarget filters, facing the caster's heading. CANDIDATE: no official sample with two players in one cloud.
        if (fx?.Area is { Max: > 1 } area && !brain.VictimIsNpc && tickPlayers is { } others && tickHit is { } hitOther)
        {
            var centre = area.NeedTarget ? p : target.Position;
            var room = area.Max - 1;
            foreach (var (key, at) in others.OrderBy(o => Distance(centre, Flat(o.Value))))
            {
                if (room == 0) break;
                if (key == victim || !NpcSkillEffects.Contains(area, centre, brain.Heading, at, area.NeedTarget ? 0 : target.Radius) || !Sight(target, at)) continue;
                room--;
                replies.AddRange(HitOne(target, brain, hit, key, false, debuffs, damage => hitOther(key, damage)));
            }
        }
        return replies;
    }

    /// <summary>
    /// One target's share of a landed hit: its damage hit (0x3804 switch 06) when the skill does damage, then one abnormal hit per
    /// abnormal row (0x3804 hit type 6, OFFICIAL grass #26939; sent whatever the chance roll, the player's session applies the
    /// abnormals as they land), then the target's own answer (HP, abnormals). A dodged hit lands nothing (no abnormal).
    /// </summary>
    List<byte[]> HitOne(Target target, Brain brain, PendingHit hit, int victim, bool npcVictim, IReadOnlyList<NpcSkillEffects.AbnormalRow> debuffs,
        Func<int, List<byte[]>> apply)
    {
        var replies = new List<byte[]>();
        var damage = 0;
        var hitType = 2;
        if (hit.Skill.Effect > 0)
        {
            // The victim's defense reduces the hit (the CANDIDATE 1000/(1000+defense) used for players' hits).
            var defense = npcVictim && current?.GetValueOrDefault(victim) is { } prey ? prey.Defense : DefenseOf?.Invoke(victim) ?? 0;
            damage = Math.Max(1, (int)Math.Round(Damage(target, hit.Skill) * 1000.0 / (1000 + Math.Max(0, defense))));
            // The player's evasion and block (wings, gear, titles...) against monster hits: a dodge lands for 0, a block for less.
            ushort plotterFlags = 0;
            var side = !npcVictim && HitSideOf?.Invoke(victim, target) is { } hs ? hs : (GamePackets.AngleFront, false);
            if (!npcVictim && AvoidanceOf?.Invoke(victim, target) is { } avoid)
            {
                if (avoid.ExtraDefense != 0)
                    damage = Math.Max(1, (int)Math.Round(Damage(target, hit.Skill) * 1000.0 / (1000 + Math.Max(0, defense + avoid.ExtraDefense))));
                damage = Math.Max(1, (int)Math.Round(damage * avoid.Taken));
                if (rng.NextDouble() < avoid.Dodge) { damage = 0; hitType = 1; }   // a dodge shows as a miss
                else if (rng.NextDouble() < avoid.Block)
                {
                    damage = Math.Max(1, (int)Math.Round(damage * (1 - (BlockCutOf?.Invoke(victim) ?? GameSession.BlockReduction))));
                    plotterFlags = side.Item2 ? GamePackets.PlotterShieldBlock : GamePackets.PlotterWeaponBlock;
                }
            }
            if (damage > 0 && victim == Victim(brain)) brain.HitVictim = true;   // a dodged swing is not a hit (the 5 m extra chase needs a real one)
            replies.Add(GamePackets.NpcSkillHit(victim, target.Key, hit.SkillId, hit.ActionSeq, hit.Skill.Effect, damage, hitType: hitType,
                plotterFlags: plotterFlags, angle: side.Item1));
        }
        var landed = hitType != 1;
        if (landed && !npcVictim)
            foreach (var row in debuffs) replies.Add(GamePackets.NpcAbnormalHit(victim, target.Key, hit.SkillId, hit.ActionSeq, row.EffectId, [row.AbnormalId]));
        // The session applies the skill's abnormals (NpcHitsPlayer by LastSkill): none for a dodge.
        LastAttacker = target.Key;
        LastSkill = landed ? hit.SkillId : 0;
        replies.AddRange(apply(damage));
        return replies;
    }

    /// <summary>The caster's move of a Teleport_Caster skill (tools/data/npc-teleports.json), as an NPC stop at the new spot.</summary>
    List<byte[]> Leap(Target target, Brain brain, int skillId, Vector3d aim)
    {
        if (NpcTeleports.Of(skillId) is not { } leap) return [];
        if (leap.Spot is { } spot)
        {
            // MapLocation_MapDirection: to a fixed spot of the map (the Lake Guardian's arena centre), facing the target.
            var at = Nav?.FloorAt(spot.X, spot.Y, spot.Z, 300) is { } ground ? spot with { Z = ground } : spot;
            target.Position = at;
            brain.Heading = Math.Atan2(aim.Y - at.Y, aim.X - at.X) * 180 / Math.PI;
            brain.Moving = false;
            return [GamePackets.NpcStop(target.Key, at, brain.Heading)];
        }
        var dx = aim.X - target.Position.X; var dy = aim.Y - target.Position.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) { dx = Math.Cos(brain.Heading * Math.PI / 180); dy = Math.Sin(brain.Heading * Math.PI / 180); length = 1; }
        var turn = leap.Angle * Math.PI / 180;
        double ux = dx / length, uy = dy / length;
        double rx = ux * Math.Cos(turn) - uy * Math.Sin(turn), ry = ux * Math.Sin(turn) + uy * Math.Cos(turn);
        var from = leap.Anchor.StartsWith("Target", StringComparison.Ordinal) ? aim with { Z = target.Position.Z }
            : leap.Anchor.StartsWith("Spawn", StringComparison.Ordinal) ? brain.Home : target.Position;
        var to = new Vector3d(from.X + rx * leap.Distance, from.Y + ry * leap.Distance, from.Z);
        // The leap stops at a wall or edge on its way (a back leap against a cliff no longer lands inside the rock or off the map).
        if (Nav is { } nav && nav.Locate(target.Position.X, target.Position.Y, target.Position.Z) is not null)
            to = nav.MoveAlong(target.Position, to);
        else if (Nav?.FloorAt(to.X, to.Y, to.Z, 300) is { } floor) to = to with { Z = floor };
        target.Position = to;
        var heading = Math.Atan2(aim.Y - to.Y, aim.X - to.X) * 180 / Math.PI;
        brain.Heading = heading;
        brain.Moving = false;
        return [GamePackets.NpcStop(target.Key, to, heading)];
    }

    static int Victim(Brain brain) => brain.VictimKey != 0 ? brain.VictimKey : PlayerKey;

    List<byte[]> TickOne(Target target, Brain brain, Vector3d? player, long now, Func<int, List<byte[]>>? hitPlayer)
    {
        if (target.Dead)
        {
            if (brain.Mode != Mode.Dead)
            {
                brain.Mode = Mode.Dead; brain.Moving = false; brain.Engaged = false; brain.Pending = null; brain.VictimKey = 0; brain.Hate.Clear();
                ResetTactics(brain);
                ResetBoss(target);
                brain.SpeedBoost = 0;   // the respawn appears without a boost
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
        // Held by a scripted abnormal (stun, hold, stone) or groggy (its guard broken, BossClient): it stops where it is and does
        // nothing until released.
        if (target.Held || now < target.GroggyUntil)
        {
            // Time spent stunned or held does not count towards giving up (user 2026-11-08: a stun reset the boss).
            if (brain.Mode == Mode.Chase) brain.LastHitMs = now;
            // A stun or hold interrupts the cast: the official 0x380c SkillCastingStop_NT {key, 6152 Skill_Abnormal, skill, the
            // skill's remaining cooldown} (OFFICIAL: 43 of 122 interrupted NPC casts right after the 0x382a, e.g. rec 105448 #7468
            // `0c38 d9cb01 0818 40d51200 c03e`); we stopped the cast bar silently.
            List<byte[]> held = [];
            if (brain.PendingStart is not null && brain.Pending is { } casting)
                held.Add(GamePackets.SkillCastingStop(target.Key, EResult.Skill_Abnormal, casting.SkillId,
                    (int)Math.Max(0, brain.Cooldowns.GetValueOrDefault(casting.SkillId) - now)));
            brain.Pending = null;
            brain.PendingStart = null;
            return [.. held, .. brain.Moving ? Stop(target, brain, brain.Heading, now) : []];
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
        var replies = new List<byte[]>();
        if (brain.PendingStart is { } cast && now >= cast.DueMs)
        {
            brain.PendingStart = null;
            replies.Add(cast.Start);
        }
        replies.AddRange(Resolve(target, brain, player, now, hitPlayer));
        if (Immobile(target))
        {
            if (target.Hostile && !brain.VictimIsNpc) replies.AddRange(StationaryStep(target, brain, player, now));
            return replies;
        }
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
            // N4 (user rule 2026-11-07): a monster that could not hit its victim for ServerRules.LeashNoHitMs (10 s, PROVISIONAL)
            // gives up like a leash: it runs home, then resets (full HP, hate cleared; Reset).
            var rules = ServerRules.Embedded;
            var boss = target.Rank == NpcRank.Hero;
            // A boss that lost its target (stealth, invisible, a moment out of the list) stays in the fight for BossLostGraceMs,
            // standing where it is; only then it gives up and resets (user 2026-11-08).
            if (player is null && boss && !companion)
            {
                brain.LostSinceMs ??= now;
                if (now - brain.LostSinceMs.Value < rules.BossLostGraceMs)
                {
                    brain.LastHitMs = now;
                    brain.Pending = null;
                    return brain.Moving ? Stop(target, brain, brain.Heading, now) : [];
                }
            }
            else if (player is not null) brain.LostSinceMs = null;
            var giveUp = !companion && brain.LastHitMs != long.MinValue && now - brain.LastHitMs > (boss ? rules.BossLeashNoHitMs : rules.LeashNoHitMs);
            // Chase distance (user rule 2026-11-08): from where the chase began a monster follows ChaseMinCm..ChaseMaxCm (random per
            // chase) without landing a hit, ChaseAfterHitCm more once it has hit; then it runs home. Bosses (Hero) keep the long leash.
            if (brain.ChaseLimit <= 0) brain.ChaseLimit = rules.ChaseMinCm + rng.NextDouble() * (rules.ChaseMaxCm - rules.ChaseMinCm);
            var tooFar = !companion && target.Rank != NpcRank.Hero && brain.ChaseAnchor is { } anchor
                && Distance(target.Position, Flat(anchor)) > brain.ChaseLimit + (brain.HitVictim ? rules.ChaseAfterHitCm : 0);
            if (player is not { } p || giveUp || tooFar || !companion && (Distance(target.Position, Flat(brain.Home)) > LeashRange || Distance(p, Flat(brain.Home)) > LeashRange))
            {
                // An NPC prey ran out of the leash (Munihelle's Maard walks off at Elvida's 70%): the fight goes on with the next
                // one on the hate table, the player hitting it. Walking home dropped the whole table and healed her to full, so
                // she never reached 40% (live 2026-10-28).
                if (player is not null && brain.VictimIsNpc && !companion && brain.Hate.Remove(brain.VictimKey) && TopHate(brain) is { } next)
                {
                    brain.VictimKey = brain.CandidateKey = next;
                    brain.VictimIsNpc = false;
                    brain.Pending = null;
                    return [];
                }
                // A companion's fight is over: it follows its player again instead of walking home.
                if (brain.ResumeFollowKey != 0 && (player is null || brain.VictimIsNpc))
                {
                    brain.Mode = Mode.Follow;
                    brain.FollowKey = brain.VictimKey = brain.ResumeFollowKey;
                    brain.ResumeFollowKey = 0;
                    brain.VictimIsNpc = false;
                    brain.Pending = null;
                    return [.. LeaveCombat(target, brain), .. brain.Moving ? Stop(target, brain, brain.Heading, now) : []];
                }
                if (companion)
                {
                    brain.Mode = Mode.Idle;
                    brain.VictimKey = 0;
                    brain.VictimIsNpc = false;
                    brain.Directed = false;
                    brain.Pending = null;
                    brain.WanderAtMs = long.MaxValue;
                    return [.. LeaveCombat(target, brain), .. SetBoost(target, brain, 0), .. brain.Moving ? Stop(target, brain, brain.Heading, now) : []];
                }
                brain.Mode = Mode.Return;
                brain.VictimKey = 0;
                brain.Pending = null;
                ResetBoss(target);   // a boss that gives up: berserk timer, groggy guard and its adds start over (CANDIDATE)
                // Home faster than its normal run (ServerRules.ReturnSpeedPercent; Reset clears the boost on arrival).
                replies.AddRange(SetBoost(target, brain, rules.ReturnSpeedPercent * 100));
                replies.AddRange(Go(target, brain, Flat(brain.Home), brain.Home.Z, true, now));
                return replies;
            }
            var basic = Basic(target);
            var reach = AttackRange(target, basic?.Skill);
            var stopAt = Math.Max(target.Radius, reach - 20);   // close in to just inside melee reach
            var distance = Distance(target.Position, Flat(p));
            var facing = HeadingTo(target.Position, Flat(p));
            // Tactics (NpcTactics): the victim's movement, phases, enrage, catch-up speed, hate switch, runs.
            replies.AddRange(Tactics(target, brain, p, distance, reach, basic?.Skill, companion, now));
            if (brain.FleeUntilMs != 0)
            {
                if (now < brain.FleeUntilMs && brain.Moving && !Step(target, brain, brain.Goal, brain.GoalZ, 0, now))
                {
                    replies.AddRange(MoveUpdate(target, brain, now));
                    return replies;
                }
                // Arrived (or out of time): back into the fight; a run for help brings its helpers.
                brain.FleeUntilMs = 0;
                if (!brain.Kiting) brain.CallHelp = true;
                brain.Kiting = false;
                replies.AddRange(Stop(target, brain, facing, now));
                return replies;
            }
            // Chase where the victim is going (NpcTactics.LeadSeconds); with others on the same victim, take a side (surround).
            var lead = Ours(target) ? Lead(brain, p) : (p.X, p.Y);
            // Melee spacing (user rule 2026-11-08): with another monster in front of it or on it, a melee monster first moves to a
            // free spot round the victim, then attacks from there.
            if (basic is { Skill.Range: < 500 } && brain.PackSize > 1 && distance < reach * 3 && brain.Pending is null
                && packMembers.TryGetValue(brain.VictimKey, out var pack))
            {
                if (brain.Spot is { } old && (now >= brain.SpotUntilMs || Distance(p, old) > reach)) { brain.Spot = null; brain.SpotReadyMs = now + SpotCooldownMs; }
                if (brain.Spot is null && now >= brain.SpotReadyMs && Crowded(target, p, pack)
                    && FreeSpot(target, p, pack, Math.Max(PlayerRadius + target.Radius, stopAt - 30)) is { } free)
                {
                    brain.Spot = free;
                    brain.SpotUntilMs = now + SpotMs;
                    replies.AddRange(Go(target, brain, free, p.Z, true, now));
                    return replies;
                }
                if (brain.Spot is { } going)
                {
                    if (Step(target, brain, going, p.Z, 0, now))
                    {
                        brain.Spot = null;
                        brain.SpotReadyMs = now + SpotCooldownMs;
                        replies.AddRange(Stop(target, brain, HeadingTo(target.Position, Flat(p)), now));
                    }
                    else replies.AddRange(MoveUpdate(target, brain, now));
                    return replies;
                }
            }
            // A ready skill that reaches the player is used at once, ranged ones while still closing in; never through a wall
            // (line of sight on the navmesh, N4): without sight it keeps chasing along its path.
            var sight = Sight(target, p);
            if (sight && Choose(target, brain, distance, now) is { } skill)
            {
                replies.AddRange(Attack(target, brain, skill, facing, now, p));
                return replies;
            }
            if (!brain.Moving)
            {
                // Close in whenever the player is out of reach: holding still within the follow range
                // (which only keeps a started hit landing) left mobs staring without attacking.
                // In reach but a wall in the way (a ranged monster behind an obstacle): it moves until it sees its target.
                if (distance > reach || !sight && now >= brain.RepathAtMs)
                {
                    // An obstacle in front within 15 m: jump it when it is jumpable and walking cannot get there (or only the long way).
                    if (brain.Pending is null && TryJump(target, brain, p, now) is { } jump) { replies.AddRange(jump); return replies; }
                    // After a partial path (the player cannot be reached) the next try waits for the re-plan time: a stuck monster
                    // searches once a second, not every tick, until the 10 s leash sends it home.
                    if (brain.Pending is null && (!brain.PathPartial || now >= brain.RepathAtMs)) replies.AddRange(Go(target, brain, lead, p.Z, true, now));
                    return replies;
                }
                if (!target.NoRotation && AngleDelta(facing, brain.Heading) > 30) replies.AddRange(Stop(target, brain, facing, now));
                return replies;
            }
            // The player moves: re-plan every RepathMs, or at once when they left the path's goal.
            if (now >= brain.RepathAtMs || Distance(p, brain.Goal) > RepathDistance + Distance(p, lead))
            {
                brain.Goal = lead;
                brain.GoalZ = p.Z;
                brain.Path = PathTo(target, brain.Goal, p.Z);
                brain.PathPartial = EndsShort(brain.Path, brain.Goal);
                brain.PathIndex = 0;
                brain.RepathAtMs = now + RepathMs;
                if (TryJump(target, brain, p, now) is { } jump) { replies.AddRange(jump); return replies; }
            }
            // Without sight it keeps walking (to melee distance at most) instead of stopping at its attack range.
            if (Step(target, brain, brain.Goal, p.Z, sight ? stopAt : Math.Max(target.Radius, Reach), now) || sight && Distance(target.Position, Flat(p)) <= stopAt)
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
            target.Position = new Vector3d(x, y, target.Air ? AirHeight(brain, x, y, slope) : Height(brain, x, y, slope));
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
                if (nav.FloorAt(goal.X, goal.Y, brain.Home.Z) is not { } floor || (path = PathTo(target, goal, floor, partial: false)) is null)
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
    /// <summary>The time of the running tick (debuffs expire against it).</summary>
    long tickNow;
    /// <summary>The players of the running tick and their hit callback (area skills hit everyone in their area).</summary>
    IReadOnlyDictionary<int, Vector3d>? tickPlayers;
    Func<int, int, List<byte[]>>? tickHit;

    public List<byte[]> Tick(IReadOnlyDictionary<int, Target> targets, IReadOnlyList<(int Key, Vector3d Position)> players, long now,
        Func<int, int, List<byte[]>>? hitPlayer)
    {
        tickNow = now;
        Sync(targets);
        current = targets;
        tickHit = hitPlayer;
        var replies = new List<byte[]>();
        // Interest grid (Stage 3): the players bucketed by position once per tick, so each NPC finds its victim in O(1) and its
        // nearest player by querying only nearby cells, instead of scanning every player (was O(players) per NPC = quadratic in a
        // crowded zone).
        var playerPos = new Dictionary<int, Vector3d>(players.Count);
        var playerGrid = new SpatialGrid();
        foreach (var p in players) { playerPos[p.Key] = p.Position; playerGrid.Add(p.Key, p.Position.X, p.Position.Y); }
        tickPlayers = playerPos;
        // The busy NPCs and those placed near a player, in key order.
        var active = new SortedSet<int>(awake);
        foreach (var p in players) active.UnionWith(homes.Near(p.Position.X, p.Position.Y, SleepRange));
        LastTicked = active.Count;
        // Monsters on each victim (surround it instead of stacking on one spot): each takes a slot.
        var packs = new Dictionary<int, int>();
        packMembers.Clear();
        foreach (var key in active)
            if (brains.TryGetValue(key, out var b) && b.Mode == Mode.Chase && b.VictimKey != 0)
            {
                b.PackSlot = packs.GetValueOrDefault(b.VictimKey);
                packs[b.VictimKey] = b.PackSlot + 1;
                if (targets.TryGetValue(key, out var member) && !member.Dead)
                    (packMembers.TryGetValue(b.VictimKey, out var list) ? list : packMembers[b.VictimKey] = []).Add(member);
            }
        foreach (var key in active)
            if (brains.TryGetValue(key, out var b)) b.PackSize = b.Mode == Mode.Chase ? packs.GetValueOrDefault(b.VictimKey) : 0;
        foreach (var key in active)
        {
            if (!brains.TryGetValue(key, out var brain) || !targets.TryGetValue(key, out var target)) continue;
            // Traps (traps.xml) fire on their own (GameSession.Traps.cs): the AI leaves them be.
            if (TrapTable.Embedded.Of(target.NpcId) is not null) continue;
            // A self-destructing monster after its blast and hit: its skill ends, then it vanishes (traps.xml self_destruct).
            if (brain.SelfDestructAt is { } blastAt && (target.Dead || brain.PendingStart is null && brain.Pending is null))
            {
                replies.AddRange(SelfDestructStep(target, brain, blastAt, now));
                if (brain.SelfDestructAt is not null) { awake.Add(key); continue; }
                if (target.CorpseRemoved) { awake.Remove(key); continue; }
            }
            (int Key, Vector3d Position)? chosen = null;
            if (brain.VictimKey != 0 && brain.Mode is Mode.Chase or Mode.Follow)
            {
                chosen = Locate(playerPos, targets, brain, brain.VictimKey);
                // The victim left (hid, or died): the next one on the hate table, if any is still here.
                while (chosen is null && brain.Hate.Remove(brain.VictimKey) && TopHate(brain) is { } next)
                {
                    brain.VictimKey = next;
                    chosen = Locate(playerPos, targets, brain, next);
                }
            }
            else
            {
                brain.VictimIsNpc = false;
                var best = double.MaxValue;
                foreach (var key2 in playerGrid.Near(target.Position.X, target.Position.Y, SleepRange))
                {
                    if (!playerPos.TryGetValue(key2, out var pp)) continue;
                    var d = Distance(target.Position, Flat(pp));
                    if (d < best) { best = d; chosen = (key2, pp); }
                }
            }
            brain.CandidateKey = chosen?.Key ?? 0;
            var victim = chosen?.Key ?? 0;
            var npcVictim = brain.VictimIsNpc ? brain.VictimKey : 0;
            replies.AddRange(TickOne(target, brain, chosen?.Position, now,
                chosen is null ? null
                : npcVictim != 0 ? damage => HitNpc(targets, npcVictim, target, damage, now)
                : hitPlayer is null ? null : damage => hitPlayer(brain.VictimKey != 0 ? brain.VictimKey : victim, damage)));
            if (brain.CallHelp)
            {
                brain.CallHelp = false;
                if (!brain.VictimIsNpc && brain.Mode == Mode.Chase) replies.AddRange(CallForHelp(targets, target, brain, now));
            }
            if (Busy(brain)) awake.Add(key);
            else awake.Remove(key);
        }
        return replies;
    }

    static void ResetTactics(Brain brain)
    {
        brain.LastSpecial = 0; brain.VictimSeen = null; brain.VictimVelocity = (0, 0); brain.OutOfReachSinceMs = long.MinValue;
        brain.TacticPhases = 0; brain.PhaseDue = false; brain.Enraged = false; brain.FleeUntilMs = 0; brain.Fled = false;
        brain.Kiting = false; brain.KiteAtMs = 0; brain.HateCheckAtMs = 0; brain.CallHelp = false;
    }

    /// <summary>Where the chase aims: the victim's position plus its movement for NpcTactics.LeadSeconds (at most LeadMax).</summary>
    static (double X, double Y) Lead(Brain brain, Vector3d p)
    {
        var (vx, vy) = brain.VictimVelocity;
        double lx = vx * NpcTactics.LeadSeconds, ly = vy * NpcTactics.LeadSeconds;
        var length = Math.Sqrt(lx * lx + ly * ly);
        if (length > NpcTactics.LeadMax) { lx *= NpcTactics.LeadMax / length; ly *= NpcTactics.LeadMax / length; }
        return (p.X + lx, p.Y + ly);
    }

    /// <summary>
    /// One chase tick's tactics (NpcTactics, OURS): tracks the victim's speed; HP phases, enrage, help at phases and target
    /// switches for elites and bosses; the catch-up / enrage speed; a ranged fighter's step back from a victim in its face; a
    /// weak monster's run for help.
    /// </summary>
    List<byte[]> Tactics(Target target, Brain brain, Vector3d p, double distance, double reach, NpcSkill? basic, bool companion, long now)
    {
        var replies = new List<byte[]>();
        if (brain.VictimSeen is { } seen)
        {
            var dt = (now - brain.VictimSeenMs) / 1000.0;
            if (dt >= 0.2)
            {
                // Smoothed: half the new speed, half the old (a jump does not swing the aim); a teleport is not a run.
                var (ox, oy) = brain.VictimVelocity;
                double nx = (p.X - seen.X) / dt, ny = (p.Y - seen.Y) / dt;
                if (Math.Sqrt(nx * nx + ny * ny) > 3000) { nx = 0; ny = 0; }
                brain.VictimVelocity = ((ox + nx) / 2, (oy + ny) / 2);
                brain.VictimSeen = p; brain.VictimSeenMs = now;
            }
        }
        else { brain.VictimSeen = p; brain.VictimSeenMs = now; }
        if (companion) return replies;
        // OURS only for the NPCs that play by our tactics (server-rules.xml monster_ai): bosses with boss_official keep the hate
        // table and nothing else from here.
        var ours = Ours(target);
        var hpShare = target.HpMax > 0 ? (double)target.Hp / target.HpMax : 1;
        if (target.Rank != NpcRank.Normal)
        {
            // OURS: generic 75/50/25 % phases (a forced mechanic, a boss calls its adds again) and enrage below 30 %.
            if (ours)
            {
                for (var i = 0; i < NpcTactics.Phases.Length; i++)
                    if (hpShare <= NpcTactics.Phases[i] && (brain.TacticPhases & 1 << i) == 0)
                    {
                        brain.TacticPhases |= 1 << i;
                        brain.PhaseDue = HasMechanic(target);
                        // A boss calls its adds again at each phase.
                        if (target.Rank == NpcRank.Hero && target.Hostile) brain.CallHelp = true;
                    }
                if (!brain.Enraged && hpShare < NpcTactics.EnrageBelow) brain.Enraged = true;
            }
            // Turn on whoever hurts it most (not in the middle of a hit); a boss sometimes picks another attacker anyway (OURS).
            if (now >= brain.HateCheckAtMs && brain.Pending is null && brain.PendingStart is null)
            {
                brain.HateCheckAtMs = now + NpcTactics.HateCheckMs;
                int? next = TopHate(brain) is { } top && top != brain.VictimKey
                    && brain.Hate[top] >= brain.Hate.GetValueOrDefault(brain.VictimKey) * NpcTactics.HateSwitch ? top : null;
                if (next is null && ours && target.Rank == NpcRank.Hero && brain.Hate.Count > 1 && rng.NextDouble() < NpcTactics.BossRandomTarget)
                    next = brain.Hate.Keys.Where(k => k != brain.VictimKey).ElementAt(rng.Next(brain.Hate.Count - 1));
                if (next is { } chosen)
                {
                    brain.VictimKey = chosen;
                    brain.VictimSeen = null;
                    brain.VictimVelocity = (0, 0);
                    replies.Add(GamePackets.TargetUpdate(target.Key, chosen));
                }
            }
        }
        // Kept out of reach: catch up (by rank); enraged bosses and elites are faster anyway.
        if (distance > reach) { if (brain.OutOfReachSinceMs == long.MinValue) brain.OutOfReachSinceMs = now; }
        else brain.OutOfReachSinceMs = long.MinValue;
        // OURS: catch-up and enrage speed (official NPCs keep their client speed).
        var boost = ours && brain.Enraged ? NpcTactics.EnragedSpeed(target.Rank) : 0;
        if (ours && brain.OutOfReachSinceMs != long.MinValue && now - brain.OutOfReachSinceMs >= NpcTactics.CatchUpAfterMs)
            boost = Math.Max(boost, NpcTactics.CatchUpBoost(target.Rank));
        replies.AddRange(SetBoost(target, brain, boost));
        if (!ours || brain.Pending is not null || brain.PendingStart is not null || target.RunSpeed <= 0 || target.NoRotation
            || brain.PhaseSkills.Count > 0 || brain.PhaseDue) return replies;
        // OURS from here: kite and run for help.
        // A ranged fighter (every damaging skill reaches 500+; never a boss) with the victim in its face steps back to its range
        // (elites always, others half the time).
        if (target.Rank != NpcRank.Hero && basic is { Range: >= 500 } && distance < NpcTactics.KiteWithin && now >= brain.KiteAtMs
            && target.Skills.All(id => SkillData(id) is not { Effect: not 0 } k || k.Range >= 500))
        {
            brain.KiteAtMs = now + NpcTactics.KiteEveryMs;
            if (target.Rank == NpcRank.Elite || rng.NextDouble() < 0.5)
            {
                brain.Kiting = true;
                brain.FleeUntilMs = now + NpcTactics.KiteMs;
                replies.AddRange(Go(target, brain, Reachable(target, Away(brain, target, p, NpcTactics.KiteDistance)), target.Position.Z, true, now));
                return replies;
            }
        }
        // A weak monster near death may run for help once (it calls its kind when it stops).
        if (target.Rank == NpcRank.Normal && target.Hostile && !brain.Fled && hpShare < NpcTactics.FleeBelow)
        {
            brain.Fled = true;
            if (rng.NextDouble() < NpcTactics.FleeChance)
            {
                brain.FleeUntilMs = now + NpcTactics.FleeMs;
                replies.AddRange(Go(target, brain, Reachable(target, Away(brain, target, p, NpcTactics.FleeDistance)), target.Position.Z, true, now));
            }
        }
        return replies;
    }

    /// <summary>A point `length` away from the victim, never past the leash (then toward home).</summary>
    static (double X, double Y) Away(Brain brain, Target target, Vector3d p, double length)
    {
        var away = Math.Atan2(target.Position.Y - p.Y, target.Position.X - p.X);
        (double X, double Y) goal = (target.Position.X + Math.Cos(away) * length, target.Position.Y + Math.Sin(away) * length);
        return Distance(brain.Home, goal) > LeashRange * 0.8 ? Flat(brain.Home) : goal;
    }

    bool HasMechanic(Target target) => target.Skills.Any(id => SkillData(id) is { Effect: 0, Cast: > 0 });

    /// <summary>
    /// A monster that starts a fight (or ran for help, or a boss at a phase) calls the idle ones of its kind near it (a boss: any
    /// lesser monster near it) onto its victim (NpcTactics.HelpRange, HelpMax; OURS). They must see it.
    /// </summary>
    List<byte[]> CallForHelp(IReadOnlyDictionary<int, Target> targets, Target caller, Brain brain, long now)
    {
        // OURS: only NPCs on our tactics call, and only those answer (server-rules.xml monster_ai; bosses as official never do).
        if (!Ours(caller)) return [];
        var range = caller.Rank == NpcRank.Hero ? NpcTactics.HeroHelpRange : NpcTactics.HelpRange;
        var victim = Victim(brain);
        var helpers = homes.Near(caller.Position.X, caller.Position.Y, range + WanderRadius)
            .Where(k => k != caller.Key && brains.TryGetValue(k, out var b) && b.Mode is Mode.Idle or Mode.Wander or Mode.Patrol && !b.Engaged
                && targets.TryGetValue(k, out var t) && !t.Dead && t.Hostile && t.RunSpeed > 0 && Ours(t)
                && (t.NpcId == caller.NpcId || caller.Rank == NpcRank.Hero && t.Rank != NpcRank.Hero)
                && Distance(t.Position, Flat(caller.Position)) <= range && Sight(t, caller.Position))
            .OrderBy(k => Distance(targets[k].Position, Flat(caller.Position)))
            .Take(NpcTactics.HelpMax(caller.Rank)).ToList();
        var replies = new List<byte[]>();
        foreach (var key in helpers)
        {
            var helper = brains[key];
            helper.Hate[victim] = helper.Hate.GetValueOrDefault(victim) + 1;
            helper.CandidateKey = victim;
            helper.VictimIsNpc = false;
            replies.AddRange(StartChase(targets[key], helper, now));
            helper.CallHelp = false;   // no chain of calls
            awake.Add(key);
        }
        return replies;
    }

    /// <summary>Where a victim is now: a player here, or a living NPC (then the brain fights an NPC); null when gone.</summary>
    /// <summary>Adds a boss skill calls (client Spawn effects): the caster, NPC, count, distance, the victim they go for.</summary>
    readonly List<(Target Boss, int Npc, int Count, int Distance, int Victim)> adds = [];
    /// <summary>Bosses that died or gave up since the last call (their adds go, BossClient).</summary>
    readonly List<Target> resets = [];
    public List<(Target Boss, int Npc, int Count, int Distance, int Victim)> TakeAdds() { var list = adds.ToList(); adds.Clear(); return list; }
    public List<Target> TakeResets() { var list = resets.ToList(); resets.Clear(); return list; }

    internal static double AttackRangeForChecks(Target target, int skillId, NpcSkill? skill) => AttackRange(target, skillId, skill);
    internal Warned? TelegraphForChecks(Target target, int skillId, NpcSkill skill, Vector3d victimAt) => Telegraph(target, skillId, skill, 1, 0, victimAt);
    internal void QueueAddForChecks(Target boss, int npc, int count, int distance, int victim) => adds.Add((boss, npc, count, distance, victim));
    internal void ResetBossForChecks(Target target) => ResetBoss(target);

    void ResetBoss(Target target)
    {
        target.BerserkAt = 0;
        target.GroggyUntil = 0;
        target.GroggyEnergy = -1;
        resets.Add(target);
    }

    /// <summary>The warned spots of a telegraphed skill, their radius, the warning time and the 0x3848 notices.</summary>
    public sealed record Warned(IReadOnlyList<Vector3d> Spots, double Radius, int WarnMs, IReadOnlyList<byte[]> Notices);

    /// <summary>
    /// A skill's telegraph from the client tables: SkillEffectFilterLocation Circle [radius, spot radius, ...] count min..max gives
    /// that many spots round the victim (the first on it), else one spot on the victim (GroundRadius or WideCastingAlertTime).
    /// The warning lasts the alert time, else the cast, at least BossClient.WarnMinMs. OFFICIAL layout of 0x3848 (83 samples:
    /// 1221085 five spots, as its filter 600/150/300 count 5); spot radius and timings CANDIDATE.
    /// </summary>
    Warned? Telegraph(Target target, int skillId, NpcSkill skill, int actionSeq, double facing, Vector3d victimAt)
    {
        if (ClientCombat.Embedded.NpcSkill(skillId) is not { Telegraphed: true } extra) return null;
        var rules = BossClient.Embedded;
        var spots = new List<Vector3d> { victimAt };
        var radius = extra.Ground > 0 ? (double)extra.Ground : rules.SpotRadius;
        if (extra.FilterType is not null && extra.FilterValues.Count >= 2)
        {
            var count = rng.Next(Math.Max(1, extra.FilterMin), Math.Max(extra.FilterMin, extra.FilterMax) + 1);
            radius = extra.FilterValues[1] > 0 ? extra.FilterValues[1] : radius;
            for (var i = 1; i < count; i++)
            {
                var angle = rng.NextDouble() * Math.PI * 2;
                var r = Math.Sqrt(rng.NextDouble()) * extra.FilterValues[0];
                var spot = new Vector3d(victimAt.X + Math.Cos(angle) * r, victimAt.Y + Math.Sin(angle) * r, victimAt.Z);
                if (Nav?.FloorAt(spot.X, spot.Y, spot.Z, 300) is { } floor) spot = spot with { Z = floor };
                spots.Add(spot);
            }
        }
        var warn = Math.Max(rules.WarnMinMs, extra.AlertMs > 0 ? extra.AlertMs : skill.Cast);
        var notices = spots.Select((spot, i) => GamePackets.SkillRangeNotice(target.Key, skillId, actionSeq, 1, i + 1, spot, facing, Math.Min(warn, ushort.MaxValue))).ToList();
        return new Warned(spots, radius, warn, notices);
    }

    static (int Key, Vector3d Position)? Locate(IReadOnlyDictionary<int, Vector3d> playerPos, IReadOnlyDictionary<int, Target> targets, Brain brain, int key)
    {
        if (playerPos.TryGetValue(key, out var pos)) { brain.VictimIsNpc = false; return (key, pos); }
        if (targets.TryGetValue(key, out var prey) && !prey.Dead && !prey.CorpseRemoved) { brain.VictimIsNpc = true; return (prey.Key, prey.Position); }
        return null;
    }
}
