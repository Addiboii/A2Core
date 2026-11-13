using System.Text;
using Aion.Protocol.Native;
using Aion.Application.Gameplay;
using Aion.World;

/// <summary>
/// Content audit regressions (2026-11-09): the dynamic walk of every map event (ContentWalk), and the fixes it led to, played in
/// real sessions where they are player-facing (exit doors, the Longest Night's entry, scripted door states).
/// Set CONTENT_WALK_OUT to a file path to write the walk's full result list (tools/content_audit.py writes the static half).
/// CONTENT_WALK_DEBUG=<event> plays one event first with its spawns; CONTENT_WALK_TRACE=<same event> adds its state every 5 s.
/// </summary>
static class ContentChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    public static async Task Run(Func<string, uint, Task<GameSession>> worldSession, long now, Action<bool, string> check)
    {
        var tables = MapEventTables.Embedded;

        // ---------- The walk ----------
        if (Environment.GetEnvironmentVariable("CONTENT_WALK_DEBUG") is { Length: > 0 } debug)
        {
            var r0 = ContentWalk.Walk(tables.Event(debug)!);
            Console.WriteLine($"{r0.Event}: {r0.Completed} {r0.StuckAt} [{string.Join(" | ", r0.Problems)}]");
        }
        var results = ContentWalk.WalkAll();
        if (Environment.GetEnvironmentVariable("CONTENT_WALK_OUT") is { Length: > 0 } outPath)
        {
            var text = new StringBuilder();
            foreach (var r in results)
                text.AppendLine($"{r.Event}\tmap {r.Map}\t{(r.Completed ? "complete" : "STUCK")}\tcleared {r.Cleared}\tsteps {r.StepsPassed}/{r.StepsTotal}\t{r.StuckAt}\t" +
                                $"timeouts [{string.Join(" | ", r.Timeouts)}]\tproblems [{string.Join(" | ", r.Problems)}]");
            File.WriteAllText(outPath, text.ToString());
        }
        Console.WriteLine($"content walk: {results.Count} served map events, {results.Count(r => r.Completed)} complete, " +
                          $"{results.Sum(r => r.StepsTotal)} steps ({results.Sum(r => r.StepsPassed)} passed), {results.Count(r => r.StuckAt is not null)} stuck, " +
                          $"{results.Sum(r => r.Timeouts.Count)} objectives finished only by the 30 s timeout");
        // Every story event of both starting zones plays to its end with the server's data and name rules.
        foreach (var r in results.Where(r => r.Event.Contains("_Starter_", StringComparison.Ordinal)))
            check(r.Completed && r.StuckAt is null, $"content walk: {r.Event} plays through ({r.StepsPassed}/{r.StepsTotal} steps{(r.StuckAt is null ? "" : ", stuck at " + r.StuckAt)})");

        // ---------- Waves (WaveStepData) and a stage whose fail condition is the step's own kill ----------
        // FE_SpiritAttack: three waves of 56/136/240 kills; FierceHornCave_Normal: the wave stage fails on "Lycan totem out of combat"
        // and its last step kills the totem (the kill failed the stage and the wave began again); ReforgedAbyss: a wave per party side.
        foreach (var name in new[] { "FE_SpiritAttack_01_D_MapEvent", "IN_SpiritAttack_01_L_MapEvent", "FierceHornCave_Normal_Mapevent",
                                     "ReforgedAbyss_Normal_1PartyWave_MapEvent", "ReforgedAbyss_Normal_2PartyWave_MapEvent" })
            check(results.FirstOrDefault(r => r.Event == name) is { Completed: true }, $"content walk: {name} plays through its waves");

        // ---------- The Ascension Gauge counts POINTS: 10 per quest of each kind rewarded after it started (OBSERVED, Quests.cs) ----------
        var questBook = QuestBook.Embedded;
        var gaugeLog = new QuestLog(questBook, race: 1);
        var gaugeStart = new DateTimeOffset(2026, 11, 7, 1, 38, 0, TimeSpan.Zero);
        var nextUid = 1000L;
        void Finish(Func<QuestDefinition, bool> which, int count, DateTimeOffset at)
        {
            foreach (var (code, q) in questBook.Quests.Where(q => which(q.Value) && q.Value.Race != "Dark" && gaugeLog.Get(q.Key) is null).Take(count))
                gaugeLog.Set(QuestLog.New(code, q) with { Status = QuestLog.Rewarded, Uid = nextUid++, UpdatedAt = at });
        }
        gaugeLog.Set(QuestLog.New("AQ1602000", questBook.Find("AQ1602000")!) with { Status = QuestLog.Rewarded, Uid = 8, UpdatedAt = gaugeStart });
        Finish(q => q.Type == "Hero", 6, gaugeStart.AddMinutes(-20));   // before the gauge: not counted (the official new Elyos: 6)
        var after = gaugeStart.AddMinutes(5);
        Finish(q => q.Type == "Hero", 82, after);
        Finish(q => q.Type == "District", 58, after);
        Finish(q => q.Type == "Ascension" && q.Id != 1602000, 3, after);
        Finish(q => q.Exploration == "Seal", 102, after);
        Finish(q => q.Exploration == "OccupationTerritory", 27, after);
        Finish(q => q.Type == "DutyMission", 5, after);               // not a gauge kind
        gaugeLog.Set(QuestLog.New("GQ1702010", questBook.Find("GQ1702010")!) with { Uid = 9 });
        var recount = gaugeLog.GaugeRecount(30);
        check(questBook.Find("GQ1702010")!.Steps.Single().UnifiedScore == 2_100_000_000
              && recount.Single() is { Status: QuestLog.Incomplete, Step: 1 } gaugeNow && gaugeNow.Goals.SequenceEqual([820, 580, 30, 1020, 270, 0])
              && gaugeLog.GaugeScore() == 2720 && gaugeLog.GaugeRecount(30).Count == 0,
              "the Ascension Gauge holds 10 points per Hero/District/Ascension/Seal/OccupationTerritory quest rewarded after it started " +
              "(the official Asmodian's [820, 580, 30, 1020, 270]), without a cap, never completing (score 2,100,000,000)");
        check(gaugeLog.ScoreReached("1740") && !gaugeLog.ScoreReached("2721") && !new QuestLog(questBook, race: 1).ScoreReached("320"),
              "UnifiedObjectiveScore conditions (AQ1602040 1740) read the gauge's total; no gauge, no points");

        // ---------- A destroyed object reports its EnvObjData template (GameSession.MapEventObjectName, EnvDestroy/Bombs) ----------
        // Elyos Layer_1_1's tanks (relationship 15, destroyed with skills): its objective names E_L0_Layer_1_TankOn_001, the spawn is
        // L_Starter_InstanceLayer_1_EnvObj_Tank_1; the destroy path reported the spawn name and the step never counted.
        var tankRuns = new List<MapEventRun>();
        ContentWalk.Walk(tables.Event("World_L_Starter_Layer_1_1_MapEvent")!,
            r => r.Open.Any(x => x.Objective.Type == "UseEnvObj" && x.Objective.Names.Contains("E_L0_Layer_1_TankOn_001")), tankRuns);
        var tank = WorldObjects.ObjectsOn(100001).First(o => EnvUsages.Of(o.Env)?.Name == "E_L0_Layer_1_TankOn_001");
        check(tank.Relationship == GameSession.EnvObjDestroyRelationship && tank.Name != "E_L0_Layer_1_TankOn_001"
              && GameSession.MapEventObjectName(tank, tankRuns) == "E_L0_Layer_1_TankOn_001" && tankRuns.Any(r => r.WantsObject("E_L0_Layer_1_TankOn_001")),
              "a destructible object (Layer_1_1's tank) reports its EnvObjData template while the step wants it");

        // ---------- NPCs a KillSpawner directive kills count for kill objectives (World_D_B_Layer_HQ010 step 3's invisible NPCs) ----------
        var scripter = await worldSession("Scripter", 9);
        var invisible = GameData.Embedded.Spawns(870010).First(x => x.Name == "N_D2_HQ010_Invisible_01_1");
        scripter.Teleport(870010, new Vector3d(invisible.Position[0] + 100, invisible.Position[1], invisible.Position[2]), now);
        await scripter.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var waits = new MapEventObjective(1, "KillNpc", ["N_D2_HQ010_Invisible_01", "4"]);
        var synthetic = new MapEventDefinition(870010, [], [], [new MapEventStage(1, "", [new MapEventStep(1, [], [waits], [])])]) { Name = "ScriptKillCheck_MapEvent" };
        var scriptRun = new MapEventRun(synthetic, 77, scripter, now);
        scripter.AddMapEventRunForChecks(scriptRun);
        scriptRun.Start(now);
        for (var i = 1; i <= 4; i++) ((IMapEventHost)scripter).Spawn($"N_D2_HQ010_Invisible_01_{i}", 0);   // the step's StartSpawner
        for (var i = 1; i <= 4; i++) ((IMapEventHost)scripter).Despawn($"N_D2_HQ010_Invisible_01_{i}", 0, true);
        check(scripter.Targets?.Values.Count(t => t.Name.StartsWith("N_D2_HQ010_Invisible_01_") && t.Dead) == 4, "the KillSpawner directive killed the four invisible NPCs");
        await scripter.TickAsync(now + 100);
        check(scriptRun.Finished && scriptRun.Status == MapEventRun.Complete,
              "their deaths count for 'KillNpc N_D2_HQ010_Invisible_01 x4' after the tick (the step the player could never finish)");
        scripter.ExitSharedWorld();

        // ---------- A map's default subzone is the whole map (Map.dat DefaultSubzoneID) ----------
        check(Subzones.DefaultOf(111025)?.Name == "InstanceLayer_World_D_A_Layer_HQ001_Subzone"
              && Subzones.InSubzone(111025, "InstanceLayer_World_D_A_Layer_HQ001_Subzone", new Vector3d(-79000, -85000, 30000))
              && !Subzones.InSubzone(1110, "InstanceLayer_World_D_A_Layer_HQ001_Subzone", new Vector3d(-79000, -85000, 30000)),
              "EnterSubZone of a layer's default subzone (HQ2102030's layer 111025, no volume) counts anywhere in that layer");
        check(results.First(r => r.Event == "World_D_A_Layer_HQ001_MapEvent").Completed && results.First(r => r.Event == "World_D_A_Layer_HQ002_MapEvent").Completed,
              "the first two Asmodian story layers of map 1110 play through (their first step is that EnterSubZone)");

        // ---------- Scripted NPC walks take their route's own speed bonus (MapData AdditionalMoveStat) ----------
        // User 2026-11-09: quest partners ran to the next area before the talk. Every Companion got +8000 on any run route; the
        // official bonus is the route's (Robin's 8000 = his route's 8000; Nemon's first Layer_1 routes have none, Mard's 5000..12000).
        check(GameData.Embedded.Route(100010, "L0_Starter_HQ001_Robin_Waypoints_1")?.Boost == 8000
              && GameData.Embedded.Route(110001, "D_Starter_Layer_1_Waypoints_Nemon_1") is { Run: true, Boost: 0 }
              && GameData.Embedded.Route(110002, "N_D_Starter_HQ001_Mard_01_Waypoints6")?.Boost == 12000,
              "routes carry MapData AdditionalMoveStat (Robin 8000, Nemon_1 none, Mard_6 12000)");
        var watcher = await worldSession("Watcher", 9);
        var nemon = GameData.Embedded.Spawns(110001).First(x => x.Name == "D_Starter_Layer_1_Single_Nemon_1");
        watcher.Teleport(110001, new Vector3d(nemon.Position[0] + 200, nemon.Position[1], nemon.Position[2]), now);
        await watcher.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        ((IMapEventHost)watcher).Spawn("D_Starter_Layer_1_Single_Nemon_1", 0);
        watcher.DrainMapEventsForChecks();
        var nemonKey = watcher.Targets!.Values.First(t => t.Name == "D_Starter_Layer_1_Single_Nemon_1").Key;
        ((IMapEventHost)watcher).MoveNpc("D_Starter_Layer_1_Single_Nemon_1", "D_Starter_Layer_1_Waypoints_Nemon_1");
        var plain = watcher.DrainMapEventsForChecks();
        ((IMapEventHost)watcher).MoveNpc("D_Starter_Layer_1_Single_Nemon_1", "D_Starter_Layer_1_Waypoints_Nemon_3");
        var boosted = watcher.DrainMapEventsForChecks();
        check(!plain.Any(b => b.SequenceEqual(PlayerPackets.MoveSpeedUpdate(nemonKey, [(205, NpcAi.FollowBoost)])))
              && plain.All(b => Op(b) != 0x364b) && boosted.Any(b => b.SequenceEqual(PlayerPackets.MoveSpeedUpdate(nemonKey, [(205, 8000)]))),
              "Companion Nemon walks Waypoints_Nemon_1 at his own speed (no 0x364b) and Waypoints_Nemon_3 with its 8000");
        watcher.ExitSharedWorld();

        // ---------- Exit doors (EnvObjData MoveMap) lead somewhere ----------
        var doors = 0;
        foreach (var map in tables.Events.Values.Select(e => e.Map).Distinct().Where(tables.Serves))
            foreach (var o in WorldObjects.ObjectsOn(map))
                if (EnvUsages.Of(o.Env) is { Kind: "MoveMap", Value: { Length: > 0 } point })
                {
                    doors++;
                    check(tables.Points(map).ContainsKey(point) || tables.Teleport(point) is { At: not null },
                          $"exit door {o.Name} on map {map}: its Teleport.dat point {point} has a location");
                }
        check(doors > 50, $"the served layers' exit doors are checked ({doors})");

        // HQ003 (Asmodian): into the storage through its door, out through the inner door to World_D_Starter_HQ003_Exit on map 1100.
        // Live 2026-11-09 the inner door did nothing ("exit door: no point World_D_Starter_HQ003_Exit for map 110004").
        var anders = await worldSession("Storer", 9);
        anders.DevelopmentQuestLog().Set(new QuestRecord("HQ2101080", 2101080, 90, QuestLog.Incomplete, 6, new int[6]));
        var outer = WorldObjects.ObjectsOn(1100).Select((o, i) => (o, i)).First(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_001");
        anders.Teleport(1100, outer.o.At with { X = outer.o.At.X - 100 }, now);
        await anders.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var inside = await anders.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + outer.i)).ToArray(), now + 100);
        await anders.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 200);
        check(anders.MapId == 110004 && inside.Any(b => Op(b) == 0x3621), "HQ003: the storage door takes the player into the storage (map 110004)");
        var inner = WorldObjects.ObjectsOn(110004).Select((o, i) => (o, i)).First(x => x.o.Name == "E_D0_Starter_HQ003_Warehouse_002");
        var leave = await anders.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + inner.i)).ToArray(), now + 300);
        await anders.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 400);
        var exitPoint = tables.Teleport("World_D_Starter_HQ003_Exit")!.At!;
        check(leave.Any(b => Op(b) == 0x3621 && BitConverter.ToInt32(b, 6) == 1100) && anders.MapId == 1100 && anders.InstanceKey == 0
              && anders.Position is { } outside && Math.Abs(outside.X - exitPoint[0]) < 1 && Math.Abs(outside.Y - exitPoint[1]) < 1,
              "HQ003: the storage's inner door (MoveMap World_D_Starter_HQ003_Exit) moves the player out to map 1100 at the Teleport.dat point");
        anders.ExitSharedWorld();

        // The Longest Night (HQ2101100): the cabin door enters 110006 at the layer point by the door (Respawn_Village), the script's
        // first PCTeleport (D_Starter_Layer_3_StartPoint: in Teleport.dat, placed in no MapData we hold) still plays its cutscene.
        var sleeper = await worldSession("Sleeper", 9);
        sleeper.DevelopmentQuestLog().Set(new QuestRecord("HQ2101100", 2101100, 91, QuestLog.Incomplete, 1, new int[6]));
        var cabin = WorldObjects.ObjectsOn(1100).Select((o, i) => (o, i)).First(x => x.o.Name == "EnvObj_D0_R3_Layer_3_Entrance");
        sleeper.Teleport(1100, cabin.o.At with { X = cabin.o.At.X + 50 }, now);
        await sleeper.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var entered = await sleeper.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)(WorldObjects.FirstKey + cabin.i)).ToArray(), now + 100);
        var night = await sleeper.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 200);
        var village = tables.Points(110006)["D_Starter_Layer_3_Respawn_Village"];
        check(entered.Any(b => Op(b) == 0x3621 && BitConverter.ToInt32(b, 6) == 110006) && sleeper.MapId == 110006
              && sleeper.Position is { } bed && Math.Abs(bed.X - village[0]) < 1 && Math.Abs(bed.Y - village[1]) < 1,
              "the Longest Night: the cabin door puts the player at D_Starter_Layer_3_Respawn_Village (by the door), not the valley 80 m away");
        check(sleeper.MapEventRuns.Any(r => r.Name == "World_D_Starter_Layer_3_MapEvent")
              && night.Concat(sleeper.DrainMapEventsForChecks()).Any(b => Encoding.ASCII.GetString(b).Contains("Cutscene_D_Starter_Seq_2300")),
              "the Longest Night's script runs and its opening cutscene (Cutscene_D_Starter_Seq_2300) is played though its StartPoint is unplaced");

        sleeper.ExitSharedWorld();

        // ChangeEnvObjState names an EnvObjData template: every object of that template takes the state (official 0x8d6d / 0x363b).
        var prison = await worldSession("Prisoner", 9);
        var bigDoor = WorldObjects.ObjectsOn(110005).Select((o, i) => (o, i)).First(x => EnvUsages.Of(x.o.Env)?.Name == "E_D0_Layer_2_PrisonDoorBig_001");
        prison.Teleport(110005, bigDoor.o.At with { X = bigDoor.o.At.X - 200 }, now);
        await prison.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        ((IMapEventHost)prison).EnvObjState("E_D0_Layer_2_PrisonDoorBig_001", "Finish");
        var opened = prison.DrainMapEventsForChecks();
        ((IMapEventHost)prison).EnvObjState("E_D0_Layer_2_PrisonDoorBig_001", "Available");
        var shut = prison.DrainMapEventsForChecks();
        var doorKey = WorldObjects.FirstKey + bigDoor.i;
        check(opened.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjFinishState(doorKey))) && !opened.Any(b => Op(b) == 0x3635)
              && shut.Any(b => b.SequenceEqual(GamePackets.InteractEnvObjAvailableState(doorKey))),
              "ChangeEnvObjState E_D0_Layer_2_PrisonDoorBig_001 Finish/Available reaches the door placed from that template (0x8d6d, then 0x363b; no despawn)");
        prison.ExitSharedWorld();
    }
}
