using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Dynamic content check: every map event of a served map is played through the real engine (MapEventRun) step by step against
/// a host built from the server's own world data (world-npcs spawns and routes, world-objects objects and trigger volumes,
/// subzones, the deferred spawners, EnvObjData templates). A simulated player does what each open objective asks the way the
/// server would see it: kills the matching living NPCs (KillNpc by NpcData id or spawner name, KillSpawner every NPC of the
/// spawner), talks to them, uses the objects with the name the server reports (the EnvObjData template when an open objective
/// wants it, else the spawn name: GameSession.MapEventObjectName), stands in the trigger volume or subzone, waits for an NPC sent
/// along a route to arrive, and finishes client-side objectives (cutscene, note, talk) once the server has played them.
/// A step that does not advance within 70 s of game time is reported with its open objectives; objectives that only the 30 s
/// fallback finished are reported as timeouts. tools/content_audit.py is the static half (names and data).
/// </summary>
static class ContentWalk
{
    public sealed record Result(string Event, int Map, bool Completed, bool Cleared, int StepsTotal, int StepsPassed, string? StuckAt, List<string> Problems, List<string> Timeouts);

    sealed class Npc(string name, int id, bool hostile, bool alive)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public bool Hostile { get; } = hostile;
        public bool Alive { get; set; } = alive;
        /// <summary>When a killed NPC comes back (RespawnAfterKill); a script's despawn does not.</summary>
        public long RespawnAt { get; set; } = long.MaxValue;
    }

    sealed class Host : IMapEventHost
    {
        public readonly int Map;
        public readonly List<Npc> Npcs = [];
        public readonly List<WorldObject> Objects;
        public readonly HashSet<int> RemovedObjects = [];
        public readonly HashSet<int> UsedObjects = [];
        /// <summary>Names some objective of the event targets (a generic "kill any monster" objective leaves those alone).</summary>
        public readonly HashSet<string> Named = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Triggers = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Subzones = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Started = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Played = new(StringComparer.OrdinalIgnoreCase);   // cutscenes, notes, talks, guides the client was given
        public readonly List<(string Spawner, string Route, long At)> Walking = [];
        public readonly HashSet<(int, int)> QuestSteps = [];
        public readonly List<string> Problems = [];
        public bool Cleared;
        public long Now;
        public long NextKill;
        /// <summary>Kills of wave NPCs: progress though no objective counts them until the wave is cleared.</summary>
        public int WaveKills;
        public string State = "Process";
        string IMapEventHost.DungeonStateName => State;

        public Host(int map)
        {
            Map = map;
            var dormant = MapEventTables.Embedded.Deferred(map);
            foreach (var s in GameData.Embedded.Spawns(map))
                Npcs.Add(new Npc(s.Name, s.NpcId, s.Relationship is 6 or 11 or 16 or 17 or 18 or 19 or 20 or 21, !dormant.Contains(s.Name)));
            Objects = [.. WorldObjects.ObjectsOn(map)];
            foreach (var t in WorldObjects.TriggersOn(map)) Triggers.Add(t.Name);
            // Subzones.InSubzone: the map's areas, its default subzone (the whole map), a layer's base-map areas.
            foreach (var z in Aion.World.Subzones.OnMap(map)) Subzones.Add(z.Name);
            if (Aion.World.Subzones.DefaultOf(map)?.Name is { } whole) Subzones.Add(whole);
            if (MapEventTables.Embedded.Volume(map) is { } layer) foreach (var z in Aion.World.Subzones.OnMap(layer.Base)) Subzones.Add(z.Name);
        }

        IEnumerable<Npc> Of(string spawner) => Npcs.Where(n => string.Equals(n.Name, spawner, StringComparison.OrdinalIgnoreCase));
        void Problem(string text) { if (!Problems.Contains(text)) Problems.Add(text); }

        public void Spawn(string spawner, int delayMs)
        {
            if (Environment.GetEnvironmentVariable("CONTENT_WALK_DEBUG") is { Length: > 0 }) Console.WriteLine($"  {Now} spawn {spawner}");
            Started.Add(spawner);
            foreach (var n in Of(spawner)) { n.Alive = true; n.RespawnAt = long.MaxValue; }
            // An object spawner started again (HQ004's warehouse door, used once to go in and once to come out) is back and usable.
            for (var i = 0; i < Objects.Count; i++)
                if (string.Equals(Objects[i].Name, spawner, StringComparison.OrdinalIgnoreCase)) { RemovedObjects.Remove(i); UsedObjects.Remove(i); }
        }
        public readonly List<Npc> ScriptKills = [];
        public void Despawn(string spawner, int delayMs, bool kill)
        {
            if (kill) ScriptKills.AddRange(Of(spawner).Where(n => n.Alive));
            if (Environment.GetEnvironmentVariable("CONTENT_WALK_DEBUG") is { Length: > 0 }) Console.WriteLine($"  {Now} despawn {spawner} {kill}");
            foreach (var n in string.Equals(spawner, "all", StringComparison.OrdinalIgnoreCase) ? Npcs : Of(spawner)) { n.Alive = false; n.RespawnAt = long.MaxValue; }
            for (var i = 0; i < Objects.Count; i++)
                if (string.Equals(Objects[i].Name, spawner, StringComparison.OrdinalIgnoreCase)) RemovedObjects.Add(i);
        }
        public void SummonAround(string target, string spawner, int count, double radius)
        {
            Started.Add(spawner);
            foreach (var n in Of(spawner).Take(Math.Max(1, count))) n.Alive = true;
        }
        public void MoveNpc(string spawner, string route)
        {
            if (GameData.Embedded.Route(Map, route) is not { } path) { Problem($"route {route} for {spawner} is not on map {Map}"); return; }
            if (!Of(spawner).Any(n => n.Alive)) { Problem($"NpcMoveToWayPoint {spawner}: no living NPC of that spawner"); return; }
            // Arrival after the route's length at a run (~5 m/s), at least 3 s.
            var length = path.Points.Zip(path.Points.Skip(1), (a, b) => Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2))).Sum();
            Walking.Add((spawner, route, Now + Math.Max(3000, (long)(length / 500 * 1000))));
        }
        public void FollowPc(string spawner, bool follow) { }
        public void RotateNpc(string spawner, string target) { }
        public void Talk(string talk) => Played.Add(talk);
        public void ChatBubble(string spawner, string talk) => Played.Add(talk);
        public void SocialAction(string spawner, int action, bool stop) { }
        public void SetAggroPc(string spawner) { }
        public void SetAggroNpc(string spawner, string target) { }
        public void SetRelationship(string spawner, string relationship) { }
        public void TeleportPc(string point, string effect, string before, string after)
        {
            // GameSession.TeleportPc: a point of this map, else Teleport.dat; the cutscenes play either way.
            if (before.Length > 0) Played.Add(before);
            if (after.Length > 0) Played.Add(after);
            if (!MapEventTables.Embedded.Points(Map).ContainsKey(point) && MapEventTables.Embedded.Teleport(point) is null)
                Problem($"PCTeleport {point}: no such point (player not moved)");
        }
        public void MapExit(string point, string mode) { }
        public void DungeonClear(MapEventRun run) => Cleared = true;
        public void DungeonFail(MapEventRun run) => Problem("DungeonFail");
        public void MapEnvState(int state) { }
        public void MapEnvSetting(int group, int state) { }
        public void Cutscene(string name) => Played.Add(name);
        public void ShowGuide(string guide) => Played.Add(guide);
        public void FocusCamera(string camera, string talk) => Played.Add(talk);
        public void ShowNote(string note) => Played.Add(note);
        public void Message(string key, string argument) { }
        public void Abnormal(int id, int durationMs, string target, bool remove) { }
        public void EnvObjState(string spawner, string state)
        {
            if (state is "Dead" or "Despawn")
                for (var i = 0; i < Objects.Count; i++)
                    if (string.Equals(Objects[i].Name, spawner, StringComparison.OrdinalIgnoreCase) || string.Equals(EnvUsages.Of(Objects[i].Env)?.Name, spawner, StringComparison.OrdinalIgnoreCase))
                        RemovedObjects.Add(i);
        }
        public void DynamicTerrain(string spawner, bool block) { }
        public void Progress(MapEventRun run) { }
        public readonly List<string> TimedOut = [];
        public void Unsupported(MapEventRun run, string what)
        {
            if (what.EndsWith("finished by timeout", StringComparison.Ordinal)) TimedOut.Add($"stage {run.StageOrder} step {run.StepId}: {what}");
            else Problem(what);
        }
        public bool QuestStepActive(int questId, int step) => QuestSteps.Contains((questId, step));
        // The player is fighting the hostile NPCs the script put in front of him (the fail conditions "NpcCombatState None" stay quiet).
        public bool NpcInCombat(string spawner) => Of(spawner).Any(n => n.Alive && n.Hostile);
        public bool SpawnerAlive(string spawner) => Of(spawner).Any(n => n.Alive);
        public bool SpawnerExists(string spawner) => Started.Contains(spawner) || Of(spawner).Any(n => n.Alive)
            || Objects.Any(o => string.Equals(o.Name, spawner, StringComparison.OrdinalIgnoreCase));
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
    }

    /// <summary>
    /// When the walk brings a killed NPC back: after GameSession.RespawnMs, so a boss the walk kills before its HpRemaining steps
    /// still serves them (the walk does not deal damage in steps). GameSession.RespawnTargets keeps the dead dead on a map that runs
    /// map events, so a kill objective wanting more NPCs than the map holds can pass here and not in play. A wave's NPCs stay dead
    /// (the wave goes on only when none of its order lives).
    /// </summary>
    static long RespawnAfterKill(long now, string spawner, IEnumerable<MapEventRun> runs) =>
        runs.Any(r => r.WaveNpc(spawner)) ? long.MaxValue : now + GameSession.RespawnMs;

    static bool NameMatches(string name, string spawn) =>
        string.Equals(name, spawn, StringComparison.OrdinalIgnoreCase) || spawn.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One event played on its map with the map's other events beside it, as the server runs them: every event of the map whose start
    /// holds joins at entry (a boss-room event, a sub event), and one that waits for the field event's Process phase joins when it begins.
    /// The quest state is the one the walked event's start names.
    /// </summary>
    public static Result Walk(MapEventDefinition definition, Func<MapEventRun, bool>? stop = null, List<MapEventRun>? runsOut = null)
    {
        var host = new Host(definition.Map);
        foreach (var group in definition.Start)
            foreach (var c in group.Where(c => c.Type == "IncompleteQuestStep"))
                if (MapEventRun.QuestStep(c.Value(0)) is { } q) host.QuestSteps.Add(q);
        var all = MapEventTables.Embedded.EventsOn(definition.Map);
        foreach (var e in all)
            foreach (var o in e.Stages.SelectMany(st => st.Steps).SelectMany(st => st.Objectives)) foreach (var n in o.Names) host.Named.Add(n);
        var stepsTotal = definition.Stages.Sum(s => s.Steps.Length);
        host.State = definition.Start.Any(g => g.Any(c => c.Type == "DungeonState" && c.Value(0) == "Prepare")) ? "Prepare" : "Process";
        var runs = new List<MapEventRun>();
        void Join(long now)
        {
            foreach (var e in all)
            {
                if (runs.Any(r => r.Name == e.Name)) continue;
                var candidate = new MapEventRun(e, runs.Count + 1, host, now);
                if (!candidate.Holds(e.Start, now)) continue;
                runs.Add(candidate);
                candidate.Start(now);
            }
        }
        Join(0);
        runsOut?.AddRange(runs);
        var run = runs.FirstOrDefault(r => r.Name == definition.Name);
        if (run is null)
            return new Result(definition.Name, definition.Map, false, false, stepsTotal, 0, "start conditions never hold", host.Problems, host.TimedOut);
        var passed = new HashSet<(int, int)>();
        var stepSince = 0L;
        var lastProgress = -1;
        var last = (-1, -1);
        string? stuck = null;
        // The time the walk allows: 90 s a step, plus 3 s a kill for kill counts (a field event's waves want 56/136/240 kills).
        var kills = definition.Stages.SelectMany(s => s.Steps).SelectMany(s => s.Objectives)
            .Where(o => o.Type.StartsWith("Kill", StringComparison.Ordinal)).Sum(o => (long)Math.Min(o.Count, 1000));
        var limit = Math.Max(600_000, stepsTotal * 90_000L + kills * 3000);
        long now = 0;
        var trace = Environment.GetEnvironmentVariable("CONTENT_WALK_TRACE") == definition.Name;
        for (; now < limit && !run.Finished; now += 1000)
        {
            host.Now = now;
            if (stop?.Invoke(run) == true) break;
            if (now == 1000 && host.State == "Prepare") { host.State = "Process"; Join(now); }
            var here = (run.StageIndex, run.StepIndex);
            if (here != last) { if (last.Item1 >= 0) passed.Add(last); last = here; stepSince = now; }
            var progress = run.Goals.Sum() + host.WaveKills;
            if (progress != lastProgress) { lastProgress = progress; stepSince = now; }   // stuck = no objective moved for 70 s
            if (now - stepSince > 70_000)
            {
                var open = run.Open.Select(x => $"{x.Objective.Type} {x.Objective.Value(0)}{(x.Objective.Optional ? " (optional)" : "")}: {Why(host, x.Objective)}");
                stuck = $"stage {run.StageOrder} ({run.Stage?.Key}) step {run.StepId}: " + string.Join("; ", open);
                break;
            }
            foreach (var n in host.Npcs.Where(n => !n.Alive && now >= n.RespawnAt)) { n.Alive = true; n.RespawnAt = long.MaxValue; }
            foreach (var arrival in host.Walking.Where(w => now >= w.At).ToList())
            {
                host.Walking.Remove(arrival);
                foreach (var n in host.Npcs.Where(n => n.Alive && string.Equals(n.Name, arrival.Spawner, StringComparison.OrdinalIgnoreCase)).Take(1))
                    foreach (var r in runs) r.NpcArrived(n.Name, arrival.Route, now);
            }
            // The player works on the walked event's open objectives, then the others' (a boss room's kills). An NPC a later step of the
            // walked event wants killed is left for that step (FierceHornCave_Normal: the side event's KillSpawner of the Lycan totem
            // would kill it during the wave, and the wave stage's fail "totem out of combat" restarted the wave forever).
            var ahead = run.Finished ? [] : run.Definition.Stages.Skip(run.StageIndex).SelectMany((st, si) => st.Steps.Skip(si == 0 ? run.StepIndex + 1 : 0))
                .SelectMany(st => st.Objectives).Where(o => o.Type is "KillNpc" or "KillSpawner").SelectMany(o => o.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in runs.OrderBy(r => r == run ? 0 : 1).ToList())
                if (r.Step is { } step)
                    foreach (var (index, o) in r.Open.ToList())
                    {
                        if (r.Finished || r.Step != step) break;
                        if (r != run && o.Type is "KillNpc" or "KillSpawner" && o.Names.Any(ahead.Contains)) continue;
                        Act(r, runs, host, o, index, now);
                    }
            foreach (var r in runs.ToList()) r.Tick(now);
            if (trace && run.Stage?.Fail is { Length: > 0 } fails)
                foreach (var c in fails.SelectMany(g => g))
                    Console.WriteLine($"  {now} fail {c.Type} {c.Value(0)}: [{string.Join(",", host.Npcs.Where(n => n.Name == c.Value(0)).Select(n => $"{(n.Alive ? "alive" : "dead")}/{(n.Hostile ? "hostile" : "friendly")}"))}] step {run.StepId} goals [{string.Join(",", run.Goals)}]");
            if (trace && now % 5000 == 0)
            {
                var spawners = runs.SelectMany(r => r.WaveSpawners()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  t={now / 1000}s stage {run.StageOrder} step {run.StepId} goals [{string.Join(",", run.Goals)}] wave [{string.Join(",", spawners)}] alive " +
                                  $"{host.Npcs.Count(n => n.Alive && spawners.Contains(n.Name))} runs [{string.Join(",", runs.Select(r => $"{r.Name}:{r.StepId}{(r.Finished ? "F" : "")}"))}]");
            }
            // GameSession.ScriptKillsCount: a KillSpawner directive's deaths count for kill objectives after the tick.
            var scripted = host.ScriptKills.ToList();
            host.ScriptKills.Clear();
            foreach (var n in scripted)
                foreach (var r in runs.ToList()) r.Killed(n.Id, n.Name, n.Hostile, host.Npcs.Any(x => x.Alive && x.Name == n.Name), now);
        }
        if (last.Item1 >= 0 && run.Finished) passed.Add(last);
        if (!run.Finished && stuck is null && now >= limit)
            stuck = $"time limit at stage {run.StageOrder} ({run.Stage?.Key}) step {run.StepId}: " +
                    string.Join("; ", run.Open.Select(x => $"{x.Objective.Type} {x.Objective.Value(0)} {run.Goals[x.Index]}/{x.Objective.Count}"));
        return new Result(definition.Name, definition.Map, run.Finished && run.Status == MapEventRun.Complete, host.Cleared, stepsTotal, passed.Count, stuck, host.Problems, host.TimedOut);
    }

    /// <summary>What the player does for one open objective; the world event reaches every run of the map, as GameSession.MapEvent* do.</summary>
    static void Act(MapEventRun run, List<MapEventRun> runs, Host host, MapEventObjective o, int index, long now)
    {
        Npc? Living() => host.Npcs.FirstOrDefault(n => n.Alive && ((o.Npcs ?? []).Contains(n.Id) || o.Names.Any(x => NameMatches(x, n.Name))));
        switch (o.Type)
        {
            case "KillNpc" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType":
            {
                if (now < host.NextKill) return;   // a kill takes the player a few seconds
                var target = o.Type == "KillNpc" ? Living()
                    : host.Npcs.FirstOrDefault(n => n.Alive && n.Hostile && !host.Named.Any(x => NameMatches(x, n.Name)))
                      ?? host.Npcs.FirstOrDefault(n => n.Alive && n.Hostile);
                // An objective with nothing to kill does not take the player's time (FE_Defence_01_D: the optional trap kill, no
                // such NPC on the map, used every slot and the 87 wave kills never began).
                if (target is null) return;
                host.NextKill = now + 3000;
                target.Alive = false;
                target.RespawnAt = RespawnAfterKill(now, target.Name, runs);
                var alive = host.Npcs.Any(n => n.Alive && n.Name == target.Name);
                foreach (var r in runs.ToList()) r.Killed(target.Id, target.Name, target.Hostile, alive, now);
                return;
            }
            case "AchieveUnifiedObjectivesScore" or "ClearWave":
            {
                // Points: a kill the step's UnifiedObjectivesList counts. A wave: an NPC of a running wave's current order.
                if (now < host.NextKill) return;
                Npc? target = null;
                if (o.Type == "ClearWave")
                {
                    // The objective's own wave first (ReforgedAbyss runs a wave per party side and the 01 waves at once).
                    var own = run.WaveSpawners(o.Value(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var spawners = runs.SelectMany(r => r.WaveSpawners()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    target = host.Npcs.FirstOrDefault(n => n.Alive && own.Contains(n.Name)) ?? host.Npcs.FirstOrDefault(n => n.Alive && spawners.Contains(n.Name));
                }
                else
                    foreach (var u in run.Step?.Unified ?? [])
                    {
                        target = u.Type == "KillNpc"
                            ? host.Npcs.FirstOrDefault(n => n.Alive && ((u.Npcs ?? []).Contains(n.Id) || u.Names.Any(x => NameMatches(x, n.Name))))
                            : host.Npcs.FirstOrDefault(n => n.Alive && n.Hostile);
                        if (target is not null) break;
                    }
                if (target is null) return;
                host.NextKill = now + 3000;
                if (o.Type == "ClearWave") host.WaveKills++;
                target.Alive = false;
                target.RespawnAt = RespawnAfterKill(now, target.Name, runs);
                var left = host.Npcs.Any(n => n.Alive && n.Name == target.Name);
                foreach (var r in runs.ToList()) r.Killed(target.Id, target.Name, target.Hostile, left, now);
                return;
            }
            case "UpdateEnvObjState":
                for (var i = 0; i < host.Objects.Count; i++)
                {
                    if (host.UsedObjects.Contains(i) || host.RemovedObjects.Contains(i)) continue;
                    var name = GameSession.MapEventObjectName(host.Objects[i], runs);
                    if (!run.WantsObject(name)) continue;
                    host.UsedObjects.Add(i);
                    // GameSession.EnvObjHit / bombs: destroyed (Dead); an interaction: used (Occupied, Finish).
                    foreach (var r in runs.ToList()) { if (o.Value(1) == "Dead") r.Destroyed(name, now); else r.UsedObject(name, now); }
                    return;
                }
                return;
            case "KillSpawner":
                foreach (var name in o.Names)
                {
                    var spawned = host.Npcs.Where(n => n.Alive && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    for (var i = 0; i < spawned.Count; i++)
                    {
                        spawned[i].Alive = false;
                        spawned[i].RespawnAt = RespawnAfterKill(now, spawned[i].Name, runs);
                        foreach (var r in runs.ToList()) r.Killed(spawned[i].Id, spawned[i].Name, spawned[i].Hostile, i < spawned.Count - 1, now);
                    }
                }
                return;
            case "HpRemaining":
                if (Living() is { } hurt && int.TryParse(o.Value(1), out var limit))
                    foreach (var r in runs.ToList()) { r.CombatStarted(now); r.HpChanged(hurt.Id, hurt.Name, limit, now); }
                return;
            case "AskNpc" or "CloseNpc":
                if (Living() is { } partner) run.Talked(partner.Id, partner.Name, index, o.Type == "CloseNpc", now);
                return;
            case "UseEnvObj":
                for (var i = 0; i < host.Objects.Count; i++)
                {
                    if (host.UsedObjects.Contains(i) || host.RemovedObjects.Contains(i)) continue;
                    var obj = host.Objects[i];
                    // GameSession.MapEventObjectName: the template when an open objective wants it, else the spawn name.
                    var name = GameSession.MapEventObjectName(obj, runs);
                    if (!run.WantsObject(name)) continue;
                    if (EnvUsages.Of(obj.Env) is not { Count: 0 or > 1 }) host.UsedObjects.Add(i);
                    foreach (var r in runs.ToList()) r.UsedObject(name, now);
                    return;
                }
                return;
            case "EnterVolumePC":
                if (host.Triggers.Contains(o.Value(0)))
                    foreach (var r in runs.ToList()) r.PositionAt(n => string.Equals(n, o.Value(0), StringComparison.OrdinalIgnoreCase), _ => false, now);
                return;
            case "EnterSubZone":
                if (host.Subzones.Contains(o.Value(0)))
                    foreach (var r in runs.ToList()) r.PositionAt(_ => false, n => string.Equals(n, o.Value(0), StringComparison.OrdinalIgnoreCase), now);
                return;
            case "PCCombatStart":
                foreach (var r in runs.ToList()) r.CombatStarted(now);
                return;
            case "CompleteCutscene":
                if (host.Played.Contains(o.Value(0))) foreach (var r in runs.ToList()) r.CutsceneDone(o.Value(0), now);
                return;
            case "CloseNote" or "CompleteNpcTalk" or "CloseGuide":
                if (host.Played.Contains(o.Value(0)) || o.Type == "CloseGuide") run.ClientObjective(index, now);
                return;
        }
    }

    static string Why(Host host, MapEventObjective o) => o.Type switch
    {
        "KillNpc" or "AskNpc" or "CloseNpc" or "HpRemaining" =>
            host.Npcs.Any(n => (o.Npcs ?? []).Contains(n.Id) || o.Names.Any(x => NameMatches(x, n.Name)))
                ? "the NPC is placed but not alive (never spawned or already dead)" : "no such NPC on the map",
        "KillSpawner" => host.Npcs.Any(n => o.Names.Any(x => string.Equals(x, n.Name, StringComparison.OrdinalIgnoreCase))) ? "spawner placed but never alive" : "no such spawner on the map",
        "UseEnvObj" => "no object the server would report under that name",
        "EnterVolumePC" => host.Triggers.Contains(o.Value(0)) ? "trigger present (never counted)" : "no such trigger volume on the map",
        "EnterSubZone" => host.Subzones.Contains(o.Value(0)) ? "subzone present" : "no such subzone on the map",
        "NpcArriveWaypoint" => "no NpcMoveToWayPoint sent that NPC along the route (or the route/NPC is missing)",
        "CompleteCutscene" or "CloseNote" or "CompleteNpcTalk" => "the server never played it to the client",
        _ => "not tracked",
    };

    /// <summary>Every event on a served map.</summary>
    public static List<Result> WalkAll(Func<MapEventDefinition, bool>? filter = null)
    {
        var tables = MapEventTables.Embedded;
        return tables.Events.Values.Where(e => tables.Serves(e.Map) && (filter?.Invoke(e) ?? true)).OrderBy(e => e.Name).Select(e => Walk(e)).ToList();
    }
}
