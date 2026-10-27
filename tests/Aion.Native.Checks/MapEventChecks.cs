using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// The map event engine run through the whole HQ001 script (research/map-events.md) against a fake host, and the
/// MapEvent messages against the official recording (capture 20261013-144836).
/// </summary>
static class MapEventChecks
{
    sealed class FakeHost : IMapEventHost
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Spawned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dead { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(int Quest, int Step)> QuestSteps { get; } = [];
        public int Progressed { get; private set; }
        public bool Cleared { get; private set; }
        public string? Exit { get; private set; }

        void Call(string text) => Calls.Add(text);
        public void Spawn(string spawner, int delayMs) { Spawned.Add(spawner); Call($"Spawn {spawner} {delayMs}"); }
        public void Despawn(string spawner, int delayMs, bool kill) { Dead.Add(spawner); Call($"Despawn {spawner} {delayMs} {kill}"); }
        public void SummonAround(string target, string spawner, int count, double radius) => Call($"Summon {spawner} around {target} x{count}");
        public void MoveNpc(string spawner, string route) => Call($"Move {spawner} {route}");
        public void FollowPc(string spawner, bool follow) => Call($"Follow {spawner} {follow}");
        public void RotateNpc(string spawner, string target) => Call($"Rotate {spawner} {target}");
        public void Talk(string talk) => Call($"Talk {talk}");
        public void ChatBubble(string spawner, string talk) => Call($"Bubble {spawner} {talk}");
        public void SocialAction(string spawner, int action, bool stop) => Call($"Social {spawner} {action} {(stop ? "stop" : "play")}");
        public void SetAggroPc(string spawner) => Call($"Aggro {spawner}");
        public void SetAggroNpc(string spawner, string target) => Call($"AggroNpc {spawner} {target}");
        public void SetRelationship(string spawner, string relationship) => Call($"Relationship {spawner} {relationship}");
        public string LastTeleportCutscene = "";
        public void TeleportPc(string point, string effect, string before, string after) { LastTeleportCutscene = after.Length > 0 ? after : before; Call($"Teleport {point}"); }
        public void MapExit(string point, string mode) { Exit = mode; Call($"Exit {point} {mode}"); }
        public void DungeonClear(MapEventRun run) { Cleared = true; Call("Clear"); }
        public void DungeonFail(MapEventRun run) => Call("Fail");
        public void MapEnvState(int state) => Call($"EnvState {state}");
        public void MapEnvSetting(int group, int state) => Call($"EnvSetting {group} {state}");
        public void Cutscene(string name) => Call($"Cutscene {name}");
        public void ShowGuide(string guide) => Call($"Guide {guide}");
        public void FocusCamera(string camera, string talk) => Call($"FocusCamera {camera} {talk}");
        public void ShowNote(string note) => Call($"Note {note}");
        public void Message(string key, string argument) => Call($"Message {key}");
        public void Abnormal(int id, int durationMs, string target, bool remove) => Call($"Abnormal {id} {durationMs} {(target.Length == 0 ? "player" : target)} {(remove ? "remove" : "add")}");
        public void EnvObjState(string spawner, string state) => Call($"ObjState {spawner} {state}");
        public void DynamicTerrain(string spawner, bool block) => Call($"Terrain {spawner} {block}");
        public void Progress(MapEventRun run) => Progressed++;
        public void Unsupported(MapEventRun run, string what) => Call($"Unsupported {what}");
        public bool QuestStepActive(int questId, int step) => QuestSteps.Contains((questId, step));
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => !Dead.Contains(spawner);
        public bool SpawnerExists(string spawner) => Spawned.Contains(spawner);
        public bool PlayerDead { get; set; }
        public double? DistanceTo(string spawner) => null;
    }

    public static int Run(Action<bool, string> check)
    {
        var count = 0;
        void C(bool condition, string name) { check(condition, name); count++; }
        var tables = MapEventTables.Embedded;
        C(tables.Events.Count > 1000 && tables.Serves(100010) && !tables.Serves(1000), "map events embedded; the HQ001 layer is served, Poeta itself is not");
        C(tables.Dungeon(96) is { Map: 100010, Type: "InstanceLayer" } && tables.EntryNpc(2930710) is { Dungeon: 96 } && tables.EntriesOn(1000).Any(e => e.Dungeon == 97),
          "Dungeon.dat row 96 is HQ001; Robin (2930710) opens it; Poeta's HQ002 trigger names dungeon 97");
        C(tables.Deferred(100010).Contains("N_L0_Starter_HQ001_Robin_01") && !tables.Deferred(100010).Contains("N_L0_Starter_HQ001_Elvan_01"),
          "Robin waits for his StartSpawner; Elvan spawns at map load");
        C(tables.Volume(100010) is { Base: 1000 } v && v.Contains(new Vector3d(-21417, -25923, 19328)) && !v.Contains(new Vector3d(0, 0, 0)),
          "the HQ001 layer volume over Poeta contains its first trigger");

        // Messages against the official bytes.
        C(GamePackets.MapEventJoin(0x149942, "World_L_Starter_HQ001_MapEvent", 2, 1, 1, [0, 0, 0, 0], 0x1a135532d5eL, new int[8]).AsSpan(2).SequenceEqual(
              Convert.FromHexString("429914001e576f726c645f4c5f537461727465725f48513030315f4d61704576656e7402010104000000005e2d5335a10100000800000000000000000000000000000000")),
          "0x922e MapEventJoin rebuilds #9690 byte for byte");
        C(GamePackets.MapEventProgress(0x149942, 2, 1, 1, [0, 1, 0, 0], new int[8]).AsSpan(2).SequenceEqual(Convert.FromHexString("4299140002010104000100000800000000000000000000000000000000")),
          "0x922d MapEventUpdateProgress rebuilds #10095");
        C(GamePackets.MapEventProgress(0x149942, 2, 1, 2, [0], new int[8]).AsSpan(2).SequenceEqual(Convert.FromHexString("4299140002010201000800000000000000000000000000000000")),
          "0x922d for step 2 rebuilds #10525");
        C(GamePackets.Caption("HQ001_MapEvent_Caption_03").AsSpan(2).SequenceEqual(Convert.FromHexString("00131948513030315f4d61704576656e745f43617074696f6e5f30330000000000000000000100000000")),
          "0x8d15 caption rebuilds #9702");
        C(GamePackets.Guide("Tutorial_UI_SkillLevelUp").AsSpan(2).SequenceEqual(Convert.FromHexString("002e185475746f7269616c5f55495f536b696c6c4c6576656c55700000000000000000000000000000")),
          "0x8d15 guide (EventFuncAction ShowGuide, member count 0) rebuilds #17352");
        C(GamePackets.Note("Note_D2_DQ2203030_01").AsSpan(2).SequenceEqual(Convert.FromHexString("000f14" + "4e6f74655f44325f4451323230333033305f3031" + "000000000000000000" + "01000000" + "00"))
          && GamePackets.Message("Str_BlasphemyLab_Notice_1")[3] == 16,
          "0x8d15 ShowNote (15) / ShowMessage (16) take the caption's layout with the note or string key");
        C(GamePackets.RelationshipUpdate(0x9b9a, 6).AsSpan(2).SequenceEqual(Convert.FromHexString("9ab70206")), "0x39cd RelationshipEntityUpdate: key varint, relationship byte");
        C(MapEventTables.Relationship("Monster_AntiCompanion") == 11 && MapEventTables.Relationship("NPC_Companion") == 13 && MapEventTables.Relationship("PC_FriendlyToAll") == 23
          && MapEventTables.Relationship("Nothing") is null, "ERelationshipEntity names resolve to the usmap ordinals");
        C(GamePackets.SocialAction(0x9b9a, 1195, false).AsSpan(2).SequenceEqual(Convert.FromHexString("9ab702ab0400000100000000"))
          && GamePackets.SocialAction(0x9b9a, 1195, true).AsSpan(2).SequenceEqual(Convert.FromHexString("9ab702ab0400000200000000")), "0x9507/0x9508 social action start/end rebuild #10524/#10811");
        C(GamePackets.NpcConversationReply(0x10481).AsSpan(2).SequenceEqual(Convert.FromHexString("00000081040100")), "0x4007 NpcConversation_RS rebuilds #9652");
        C(GamePackets.ClientObjectiveReply(1, 3, 0).AsSpan(2).SequenceEqual(Convert.FromHexString("0100000300000000000000")), "0x9231 ClientObjectiveComplete_RS rebuilds #8469");
        C(GamePackets.QuestStepTalkReply(2, 0x149942, 0x9b9a, 0).AsSpan(2).SequenceEqual(Convert.FromHexString("020000429914009a9b000000000000")), "0x8d2c kind 2 rebuilds #10809");
        C(MapEventRun.QuestStep("110101202") == (1101012, 2), "a start condition names quest 1101012 step 2");

        // The whole HQ001 script.
        var host = new FakeHost();
        var definition = tables.Event("World_L_Starter_HQ001_MapEvent")!;
        var gate = new MapEventRun(definition, 1, host, 0);
        C(!gate.Holds(definition.Start, 0), "HQ001 does not start without its quest step");
        host.QuestSteps.Add((1101012, 2));
        C(gate.Holds(definition.Start, 0), "it starts with HQ1101012 at step 2");
        var run = new MapEventRun(definition, 1, host, 0);
        run.Start(0);
        C(host.Calls.Take(3).SequenceEqual(["Spawn N_L0_Starter_HQ001_Robin_01 0", "Move N_L0_Starter_HQ001_Robin_01 L0_Starter_HQ001_Robin_Waypoints_1", "Talk HQ001_MapEvent_Caption_03"])
          && !host.Calls.Contains("Talk HQ001_MapEvent_Caption_01"), "step 1 starts: Robin spawns and walks, the hurry caption plays, the delayed caption waits");
        run.Tick(3000);
        C(host.Calls.Last() == "Talk HQ001_MapEvent_Caption_01" && run.StepOrder == 1, "3 s later the delayed caption");
        host.Calls.Clear();
        run.Tick(15_500);
        C(host.Calls.Contains("Talk HQ001_MapEvent_Caption_03"), "the hurry caption repeats every 15 s while the player has not fought");
        var before = host.Progressed;
        run.PositionAt(name => name == "L_Starter_HQ001_Trigger_01", _ => false, 16_000);
        C(run.Goals.SequenceEqual([0, 1, 0, 0]) && host.Progressed == before + 1, "walking into the first trigger counts (0x922d), as in the recording");
        run.CombatStarted(16_500);
        C(run.Goals[0] == 1 && host.Calls.Last() == "Talk HQ001_MapEvent_Caption_02", "the first fight: its caption");
        host.Calls.Clear();
        run.Tick(31_000);
        C(!host.Calls.Contains("Talk HQ001_MapEvent_Caption_03"), "the hurry caption stops once the player fought");
        for (var i = 0; i < 3; i++) run.Killed(2290000, "M_L0_Starter_HQ001_Cherubim_01_multi_1", true, i < 2, 17_000 + i);
        C(run.Goals[2] == 3 && host.Calls.Contains("Move N_L0_Starter_HQ001_Elvan_01 L0_Starter_HQ001_Elvan_Waypoints_2"), "three Cherubim kills; Elvan walks to the wagon");
        run.NpcArrived("N_L0_Starter_HQ001_Elvan_01", "L0_Starter_HQ001_Elvan_Waypoints_2", 20_000);
        C(run.StepOrder == 2 && host.Calls.Contains("Rotate N_L0_Starter_HQ001_Elvan_01 N_L0_Starter_HQ001_Robin_01")
          && host.Calls.Contains("Social N_L0_Starter_HQ001_Elvan_01 1195 play"), "his arrival ends step 1: the two turn to each other, Elvan's social action (#10524)");
        host.Calls.Clear();
        run.Talked(2931059, "N_L0_Starter_HQ001_Elvan_01", 0, false, 21_000);
        C(run.StepOrder == 3 && host.Calls.Contains("Social N_L0_Starter_HQ001_Elvan_01 1195 stop") && host.Calls.Contains("Follow N_L0_Starter_HQ001_Robin_01 True")
          && host.Calls.Contains("Move N_L0_Starter_HQ001_Elvan_01 L0_Starter_HQ001_Elvan_Waypoints_1"), "talking to Elvan: step 3, Robin follows, Elvan walks back");
        host.Calls.Clear();
        run.UsedObject("E_L0_Starter_HQ001_BrokenWagon_001", 22_000);
        C(run.StepOrder == 4 && host.Calls.Contains("Talk HQ001_MapEvent_Caption_04") && host.Calls.Contains("Spawn M_L0_Starter_HQ001_Cherubim_02_multi_2 0"),
          "the first wagon: step 4 and the second Cherubim group spawns");
        run.PositionAt(name => name == "L_Starter_HQ001_Trigger_02", _ => false, 23_000);
        C(host.Calls.Contains("Aggro M_L0_Starter_HQ001_Cherubim_02_multi_2"), "the second trigger sets them on the player");
        run.Killed(2290167, "M_L0_Starter_HQ001_Cherubim_02_multi_2", true, true, 24_000);
        run.Killed(2290168, "M_L0_Starter_HQ001_Cherubim_02_multi_2", true, true, 24_001);
        run.Killed(2290167, "M_L0_Starter_HQ001_Cherubim_02_multi_2", true, true, 24_002);
        C(run.StepOrder == 4 && run.Goals[1] == 3, "three of four kills counted against both NPC ids");
        run.Killed(2290168, "M_L0_Starter_HQ001_Cherubim_02_multi_2", true, false, 24_003);
        C(run.StepOrder == 5 && host.Calls.Contains("Talk HQ001_MapEvent_Caption_05"), "the fourth ends step 4");
        run.UsedObject("E_L0_Starter_HQ001_BrokenWagon_002", 25_000);
        run.PositionAt(name => name == "L_Starter_HQ001_Trigger_03", _ => false, 26_000);
        for (var i = 0; i < 4; i++) run.Killed(i % 2 == 0 ? 2290169 : 2290170, "M_L0_Starter_HQ001_Cherubim_04_multi_3", true, i < 3, 27_000 + i);
        C(run.StepOrder == 7, "the third wagon and the third group: step 7");
        run.UsedObject("E_L0_Starter_HQ001_BrokenWagon_003", 28_000);
        C(run.StepOrder == 8 && host.Calls.Contains("Follow N_L0_Starter_HQ001_Robin_01 False"), "the last wagon: step 8, Robin stops following");
        host.Calls.Clear();
        run.Talked(2931033, "N_L0_Starter_HQ001_Robin_01", 0, false, 29_000);
        C(host.Cleared && host.Calls.Contains("Despawn N_L0_Starter_HQ001_Robin_01 2000 False") && host.Exit is null,
          "the last talk: Robin walks off and despawns, DungeonClear (the quest's ClearMapEvent), the exit waits 3 s");
        run.Tick(32_000);
        C(host.Exit == "CurrentPosition" && run.Finished && run.Status == MapEventRun.Complete, "3 s later MapExit to the current position; the event is complete");
        C(!host.Calls.Any(c => c.StartsWith("Unsupported")), "nothing in HQ001 was unsupported");

        // Client-side and untracked objectives time out instead of stalling (InstanceLayer_2 has a cutscene).
        var layer2 = tables.Event("World_L_Starter_InstanceLayer_2_MapEvent")!;
        var host2 = new FakeHost();
        var run2 = new MapEventRun(layer2, 2, host2, 0);
        run2.Start(0);
        C(run2.Holds(layer2.Start, 0) && run2.StepOrder == 1, "InstanceLayer_2's event starts without conditions");

        // The directives of the other layers reach the host with their arguments (HQ005 transforms the player; EP3_5 stuns an NPC).
        var hq005 = tables.Event("World_L_A_Layer_HQ005_MapEvent")!;
        var host5 = new FakeHost();
        host5.QuestSteps.UnionWith(hq005.Start.SelectMany(g => g).Select(c => MapEventRun.QuestStep(c.Value(0))).OfType<(int, int)>());
        var run5 = new MapEventRun(hq005, 5, host5, 0);
        run5.Start(0);
        for (var t = 1; t <= 40; t++) run5.Tick(t * 1000);
        C(host5.Calls.Contains("Abnormal 44000501 -1 player add"), "HQ005 adds the Krall transformation (44000501, for the run) to the player");
        C(GamePackets.Cutscene("CoastalCave_Seq_1021")[3] == 9 && GamePackets.Cutscene("CoastalCave_Seq_1021").Length == GamePackets.Caption("CoastalCave_Seq_1021").Length,
          "a cutscene goes out as EventFuncAction Cutscene (9) in the caption's shape");

        // A death fails a stage once (PcDie), not every tick until the revival; the stage restarts.
        var dying = new MapEventDefinition(1, [], [], [new MapEventStage(1, "S1", [new MapEventStep(1, [new MapEventDirective(1, "StartSpawner", ["Mob"])],
            [new MapEventObjective(1, "KillNpc", ["Mob", "1"])], [])], Fail: [[new MapEventCondition("PcDie", ["1"])]])]) { Name = "Dying" };
        var hostD = new FakeHost();
        var runD = new MapEventRun(dying, 9, hostD, 0);
        runD.Start(0);
        hostD.PlayerDead = true;
        var spawnsBefore = hostD.Calls.Count(c => c.StartsWith("Spawn Mob"));
        runD.Tick(1000);
        runD.Tick(2000);
        C(hostD.Calls.Count(c => c.StartsWith("Spawn Mob")) == spawnsBefore + 1 && runD.StepOrder == 1, "the player's death fails the stage once: it restarts (the spawner again), not on the next tick too");
        hostD.PlayerDead = false;
        runD.Tick(3000);
        hostD.PlayerDead = true;
        runD.Tick(4000);
        C(hostD.Calls.Count(c => c.StartsWith("Spawn Mob")) == spawnsBefore + 2, "a second death after a revival fails it again");
        // The lake cave: the dive triggers, then the boss-room teleport with its cutscene; the client's CutsceneComplete (0x3bd2)
        // ends the step at once (live 2026-10-19: without it the boss came on the 30 s timeout) and the boss spawns.
        var hostC = new FakeHost();
        var cave = new MapEventRun(tables.Event("Dungeon_L_0016_MapEvent")!, 16, hostC, 0);
        cave.Start(0);
        for (var t = 1; t <= 14 && cave.StepOrder < 12; t++) cave.PositionAt(_ => true, _ => false, t * 1000);
        C(cave.StepOrder == 12 && hostC.Calls.Contains("Teleport Dungeon_L_0016_Teleport_BossRoom") && !hostC.Calls.Contains("Spawn Dungeon_L_0016_Spawn_Boss_1 0"),
          "the cave's triggers lead to the boss-room teleport; the step waits for its cutscene");
        C(!cave.CutsceneDone("Cutscene_Other", 15_000) && cave.CutsceneDone("Cutscene_L_A_Seq_1290", 15_000) && cave.StepOrder == 13
          && hostC.Calls.Contains("Spawn Dungeon_L_0016_Spawn_Boss_1 0") && hostC.Calls.Contains("Guide Tutorial_ForcedPopup_Groggy_01"),
          "the cutscene's end report finishes the step: the boss spawns and the groggy guide shows");
        hostC.Calls.Clear();
        foreach (var percent in new[] { 70, 40, 10 }) cave.HpChanged(2701034, "Dungeon_L_0016_Spawn_Boss_1", percent, 15_500 + percent);
        cave.Killed(2701034, "Dungeon_L_0016_Spawn_Boss_1", true, false, 16_000);
        C(hostC.Calls.Contains("Clear") && hostC.Calls.Contains("Teleport Dungeon_0005_StartPoint") && hostC.LastTeleportCutscene == "Cutscene_L_A_Seq_1291",
          "the boss kill clears the dungeon and the exit teleport carries its cutscene (the directive's value 3)");
        // HQ002 (the Balaur vessel): the whole script runs to Nania's talk after the reinforcements (step 14) and on, each objective fed.
        {
            var h = new FakeHost();
            var r = new MapEventRun(tables.Event("World_L_Starter_HQ002_MapEvent")!, 2, h, 0);
            r.Start(0);
            long t = 0;
            var reached = new HashSet<int>();
            for (var guard = 0; guard < 200 && !r.Finished && r.StepOrder < 17; guard++)
            {
                t += 1000;
                r.Tick(t);
                reached.Add(r.StepOrder);
                foreach (var (_, o) in r.Open.ToList())
                {
                    var name = o.Names.FirstOrDefault() ?? "";
                    switch (o.Type)
                    {
                        case "EnterVolumePC": r.PositionAt(_ => true, _ => true, t); break;
                        case "AskNpc": r.Talked(o.Npcs?.FirstOrDefault() ?? 0, name, null, false, t); break;
                        case "KillNpc": r.Killed(o.Npcs?.FirstOrDefault() ?? 0, name + "_1_1", true, false, t); break;
                        case "UseEnvObj": r.UsedObject(name, t); break;
                    }
                }
            }
            C(reached.IsSupersetOf([11, 12, 13, 14, 15, 16]) && r.StepOrder == 17
              && h.Calls.Contains("Spawn N_Common_DredgionB_01_001 0") && h.Calls.Contains("FocusCamera C_L0_Starter_HQ002_Camera_01 "),
              "HQ002 runs through the vessel (camera, spawn), both Drakan waves and Nania's talk to step 17");
        }

        // The vessel's and the Drakan's official NpcAppear (capture 20261019-231752 #60769, #60977): flying state 7/2, reason NpcSpawn,
        // the spawn act, the vessel's arrival abnormal.
        {
            var ship = Convert.FromHexString("4136e8e20207300042672c00000254c819c7ba761dc7a686cb4600bc8e4300cb01af24af24640000006400000000000000000000000000000000000000640000006400000001000000000000000000000000000000000000000e011101929698006a0a0000000000007a5f1356a1010000e8e202010054c819c7ba761dc7a686cb46070206001400000098440f0048001b4e00");
            var at = new Vector3d(BitConverter.ToSingle(ship, 14), BitConverter.ToSingle(ship, 18), BitConverter.ToSingle(ship, 22));
            var built = GamePackets.NpcAppear(new NpcAppearance(45416, 2910018, at, 285.46875, 4655, 4655, 20, 14, false, false, 0x4e1b0048,
                MoveState: NpcAppearance.FlyingOnAirStop, MoveSubstate: 2, Reason: NpcAppearance.NpcSpawnReason, SpawnAct: 1000600,
                Abnormals: [new AbnormalWire(1, 10000018, 2666, 0x1a156135f7a, CasterKey: 45416, RangeBase: at)]));
            C(built.AsSpan().SequenceEqual(ship), "NpcAppear rebuilds the official vessel (147 bytes: flying 7/2, abnormal 10000018, reason 6, spawn act 1000600)");
            var drakan = GamePackets.NpcAppear(new NpcAppearance(48732, 2290002, default, 240.46875, 300, 300, 6, 11, true, false, 0x81837468,
                Reason: NpcAppearance.NpcSpawnReason, SpawnAct: 720135));
            C(Convert.ToHexStringLower(drakan).EndsWith("0b000601000600000007fd0a006874838100") && drakan[5] == 0x0c && drakan[6] == 0x30,
              "a Drakan's NpcAppear ends as the official #60977 (mask 0x300c, reason 6, aggressive, level 6, spawn act 720135)");
        }
        return count;
    }
}
