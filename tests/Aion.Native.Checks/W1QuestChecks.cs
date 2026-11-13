using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// fix/w1-quests (audit 2026-11-13, ACTION-PLAN Wave 1 items 1.2, 1.3, 1.5, 1.6): level-ups re-check quests (G1), map-event kills made
/// before their step count when it begins (G2), class reward items (G5), mastery quests and their taken items (G6), player-kill counts,
/// grades and maps (G8), map-event CollectItem / GetAbnormal by state (G10, G15), RemoveFromAggroList (G11), ChangeBT (G14) and
/// NpcChangePhase.
/// </summary>
static class W1QuestChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);
    static ItemState Item(int template, long quantity, long wire) =>
        new(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), wire);

    /// <summary>A map-event host whose world the check sets: living spawners, held items, gained abnormals, aggro drops.</summary>
    sealed class Host : IMapEventHost
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> Alive { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long>? Items { get; set; }
        public HashSet<int>? Abnormals { get; set; }
        public HashSet<string> AggroDropped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(int Quest, int Step)> QuestSteps { get; } = [];
        void Call(string text) => Calls.Add(text);
        public void Spawn(string spawner, int delayMs) { Alive.Add(spawner); Existing.Add(spawner); Call($"Spawn {spawner}"); }
        public void Despawn(string spawner, int delayMs, bool kill) { Alive.Remove(spawner); Call($"Despawn {spawner} {kill}"); }
        public void SummonAround(string target, string spawner, int count, double radius) { }
        public void MoveNpc(string spawner, string route) { }
        public void FollowPc(string spawner, bool follow) { }
        public void RotateNpc(string spawner, string target) { }
        public void Talk(string talk) => Call($"Talk {talk}");
        public void ChatBubble(string spawner, string talk) { }
        public void SocialAction(string spawner, int action, bool stop) { }
        public void SetAggroPc(string spawner) { }
        public void SetAggroNpc(string spawner, string target) { }
        public void SetRelationship(string spawner, string relationship) { }
        public void TeleportPc(string point, string effect, string before, string after) { }
        public void MapExit(string point, string mode) { }
        public void DungeonClear(MapEventRun run) => Call("Clear");
        public void DungeonFail(MapEventRun run) { }
        public void MapEnvState(int state) { }
        public void MapEnvSetting(int group, int state) { }
        public void Cutscene(string name) { }
        public void ShowGuide(string guide) { }
        public void FocusCamera(string camera, string talk) { }
        public void ShowNote(string note) { }
        public void Message(string key, string argument) { }
        public void Abnormal(int id, int durationMs, string target, bool remove) { }
        public void EnvObjState(string spawner, string state) { }
        public void DynamicTerrain(string spawner, bool block) { }
        public void Progress(MapEventRun run) { }
        public void Unsupported(MapEventRun run, string what) => Call($"Unsupported {what}");
        public bool QuestStepActive(int questId, int step) => QuestSteps.Contains((questId, step));
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => Alive.Contains(spawner);
        public bool SpawnerExists(string spawner) => Existing.Contains(spawner);
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
        public long HeldItems(string itemName) => Items is null ? -1 : Items.GetValueOrDefault(itemName);
        public bool? GainedAbnormal(int abnormalId, long sinceMs) => Abnormals?.Contains(abnormalId);
        public bool RemovedFromAggro(string spawner) => AggroDropped.Remove(spawner);
        public void ChangeBehaviour(string spawner, string behaviour) => Call($"BT {spawner} {behaviour}");
        public void NpcPhase(string spawner, int phase) => Call($"Phase {spawner} {phase}");
    }

    static MapEventObjective O(int id, string type, params string[] values) => new(id, type, values);
    static MapEventDirective D(int id, string type, params string[] values) => new(id, type, values);
    static MapEventStep S(int id, MapEventObjective[] objectives, MapEventDirective[]? pre = null, MapEventDirective[]? post = null) =>
        new(id, pre ?? [], objectives, post ?? []);
    static MapEventDefinition Event(params MapEventStage[] stages) => new(1, [], [], stages) { Name = "W1_Check_MapEvent" };

    public static async Task Run(Func<string, FakeStore, Task<GameSession>> storedWorldSession, long now, Action<bool, string> check)
    {
        // ---------- G2: kills made before their step ----------
        {
            var host = new Host();
            host.Spawn("S1_Boss_1", 0);
            var run = new MapEventRun(Event(new MapEventStage(1, "", [
                S(1, [O(1, "AskNpc", "N_Guide_01")]),
                S(2, [O(2, "KillSpawner", "S1_Boss_1")], post: [D(1, "PlayNpcTalk", "after_boss")]),
                S(3, [O(3, "KillNpc", "M_Wolf", "2")]),
            ])), 1, host, now);
            run.Start(now);
            // The boss dies during step 1 (a cutscene, a companion, a directive): nothing counts it then.
            host.Alive.Remove("S1_Boss_1");
            run.Killed(2701034, "S1_Boss_1", true, false, now + 100);
            // A wolf killed during step 1 too, but its spawn entry comes back (respawned): not a dead target.
            host.Spawn("M_Wolf_1", 0);
            run.Killed(100, "M_Wolf_1", true, false, now + 200);
            check(run.StepIndex == 0 && run.UncountedKills == 2, "G2: kills no objective of the current step wants are kept for the run");
            run.Talked(1, "N_Guide_01", null, false, now + 300);
            run.Tick(now + 400);
            check(run.StepIndex == 2 && run.Goals is [0],
                  "G2: the boss killed before its KillSpawner step counts when the step begins (Dungeon_L_0016 Boss_1); the wolf whose spawn entry lives again does not");
            host.Alive.Remove("M_Wolf_1");
            run.Killed(100, "M_Wolf_1", true, false, now + 500);
            check(run.Goals is [1], "G2: the wolf killed in its own step counts as before");
        }
        {
            // A kill counted by one step is not counted again by a later step naming the same NPC.
            var host = new Host();
            var run = new MapEventRun(Event(new MapEventStage(1, "", [
                S(1, [O(1, "KillNpc", "M_Guard", "1")]),
                S(2, [O(2, "KillNpc", "M_Guard", "1"), O(3, "AskNpc", "N_Guide_01")]),
            ])), 2, host, now);
            run.Start(now);
            run.Killed(200, "M_Guard_1", true, false, now + 100);
            run.Tick(now + 200);
            check(run.StepIndex == 1 && run.Goals is [0, 0] && run.UncountedKills == 0,
                  "G2: a kill that counted for its step is not replayed for the next step naming the same NPC");
        }
        {
            // The first NPCs of a KillSpawner spawner do not count alone; once the last dies they leave the kept kills.
            var host = new Host();
            host.Spawn("S2_Group", 0);
            var run = new MapEventRun(Event(new MapEventStage(1, "", [
                S(1, [O(1, "KillSpawner", "S2_Group")]),
                S(2, [O(2, "KillNpc", "S2_Group", "3"), O(3, "AskNpc", "N_Guide_01")]),
            ])), 3, host, now);
            run.Start(now);
            run.Killed(300, "S2_Group", true, true, now + 100);
            run.Killed(300, "S2_Group", true, true, now + 110);
            host.Alive.Remove("S2_Group");
            run.Killed(300, "S2_Group", true, false, now + 120);
            run.Tick(now + 200);
            check(run.StepIndex == 1 && run.Goals is [0, 0] && run.UncountedKills == 0,
                  "G2: the spawner's earlier kills go with the KillSpawner they belonged to (not replayed for the next step)");
        }

        // ---------- G10 / G15: CollectItem and GetAbnormal by state ----------
        {
            var host = new Host { Items = [] };
            var run = new MapEventRun(Event(new MapEventStage(1, "", [
                S(1, [O(1, "CollectItem", "Quest_UseItem_NoEffect_Key_A_c_01a_b", "1")], post: [D(1, "PlayNpcTalk", "key")]),
                S(2, [O(2, "AskNpc", "N_Guide_01")]),
            ])), 4, host, now);
            run.Start(now);
            run.Tick(now + MapEventRun.FallbackMs + 1000);
            check(run.StepIndex == 0 && !host.Calls.Any(c => c.StartsWith("Unsupported", StringComparison.Ordinal)),
                  "G10: a map-event CollectItem no longer finishes by the 30 s timeout while the host can count the bag");
            host.Items["Quest_UseItem_NoEffect_Key_A_c_01a_b"] = 1;
            run.Tick(now + MapEventRun.FallbackMs + 2000);
            check(run.StepIndex == 1 && host.Calls.Contains("Talk key"), "G10: the Worn Key in the bag counts the Seal's CollectItem");
        }
        {
            var host = new Host();   // a host that cannot tell (the defaults): the fallback stays
            var run = new MapEventRun(Event(new MapEventStage(1, "", [S(1, [O(1, "CollectItem", "Unknown_Item", "1")]), S(2, [O(2, "AskNpc", "N_Guide_01")])])), 5, host, now);
            run.Start(now);
            run.Tick(now + MapEventRun.FallbackMs + 1);
            check(run.StepIndex == 1, "G10: a host that cannot count the item keeps the 30 s fallback (the event never stalls)");
        }
        {
            var host = new Host { Abnormals = [] };
            var run = new MapEventRun(Event(new MapEventStage(1, "", [S(1, [O(1, "GetAbnormal", "12253001")]), S(2, [O(2, "AskNpc", "N_Guide_01")])])), 6, host, now);
            run.Start(now);
            run.Tick(now + MapEventRun.FallbackMs + 1000);
            var waited = run.StepIndex == 0;
            host.Abnormals.Add(12253001);
            run.Tick(now + MapEventRun.FallbackMs + 1100);
            check(waited && run.StepIndex == 1, "G15: GetAbnormal waits for the abnormal (Dungeon_D_0008_Sub 12253001), no 30 s timeout");
        }

        // ---------- G11: RemoveFromAggroList fails the boss stage ----------
        {
            var host = new Host();
            var stage = new MapEventStage(1, "BossRoom", [S(1, [O(1, "KillSpawner", "S7_Spawn_Boss_CoastalCave")], pre: [D(1, "StartSpawner", "S7_Spawn_Boss_CoastalCave")])],
                Fail: [[new MapEventCondition("RemoveFromAggroList", ["S7_Spawn_Boss_CoastalCave"])]], FailDirectives: [D(2, "PlayNpcTalk", "boss_reset")]);
            var run = new MapEventRun(Event(stage), 7, host, now);
            run.Start(now);
            run.Tick(now + 100);
            var quiet = !host.Calls.Contains("Talk boss_reset");
            host.AggroDropped.Add("S7_Spawn_Boss_CoastalCave");
            run.Tick(now + 200);
            run.Tick(now + 300);
            check(quiet && host.Calls.Count(c => c == "Talk boss_reset") == 1 && run.StageIndex == 0 && run.StepIndex == 0,
                  "G11: the boss dropping the players from its aggro list fails the stage once (CoastalCave's boss room resets)");
        }

        // ---------- G14 ChangeBT, NpcChangePhase ----------
        {
            var host = new Host();
            var run = new MapEventRun(Event(new MapEventStage(1, "", [S(1, [O(1, "AskNpc", "N_Guide_01")], pre: [
                D(1, "ChangeBT", "S1_Dungeon_D_0007_Single_Griscilla_1", "Def_Companion_NPC_01"), D(2, "NpcChangePhase", "M_Dungeon_D_0016_Caras_01", "1")])])), 8, host, now);
            run.Start(now);
            check(host.Calls.Contains("BT S1_Dungeon_D_0007_Single_Griscilla_1 Def_Companion_NPC_01") && host.Calls.Contains("Phase M_Dungeon_D_0016_Caras_01 1")
                  && !host.Calls.Any(c => c.StartsWith("Unsupported", StringComparison.Ordinal)),
                  "G14: ChangeBT and NpcChangePhase reach the host (no longer dropped)");
            check(NpcAi.FightingBehaviour("Def_Companion_NPC_01") && !NpcAi.FightingBehaviour("Def_Companion_NPC_NoCombat")
                  && !NpcAi.FightingBehaviour("Def_Citizen_01") && !NpcAi.FightingBehaviour("def_idle"),
                  "G14: Def_Companion_NPC_01 fights beside the player; NoCombat, Citizen and idle trees do not (CANDIDATE reading)");
        }

        // ---------- G8: KillPC / KillPCAbyssGrade ----------
        {
            var book = QuestBook.Embedded;
            var pc = book.Find("UQ1309185")!.Steps.SelectMany(s => s.Objectives).First(o => o.Type == "KillPC");
            var grade = book.Find("UQ1309182")!.Steps.SelectMany(s => s.Objectives).First(o => o.Type == "KillPCAbyssGrade");
            check(pc.Count == 50 && grade.Count == 5, "G8: KillPC counts 50 and KillPCAbyssGrade 5 as the table says (were 1)");
            var log = new QuestLog(book, 1);
            log.Set(QuestLog.New("UQ1309182", book.Find("UQ1309182")!) with { Uid = 1 });
            log.Set(QuestLog.New("UQ1309185", book.Find("UQ1309185")!) with { Uid = 2 });
            var trainee = log.KilledPlayer(30, 22, 19);
            var otherMap = log.KilledPlayer(30, 20, 3);
            var general = log.KilledPlayer(30, 22, 3);
            check(trainee.Select(r => r.Code).SequenceEqual(["UQ1309185"]) && otherMap.Count == 0 && general.Count == 2
                  && log.Get("UQ1309182")!.Goals[0] == 1 && log.Get("UQ1309185")!.Goals[0] == 2 && log.Get("UQ1309182")!.Status == QuestLog.Incomplete,
                  "G8: an unranked victim counts KillPC only; another map counts nothing; a General (grade 3) counts both, 1 of 5");
        }

        // ---------- G5, G6: rewards and mastery quests ----------
        var empty = JsonSerializer.SerializeToElement(new { });
        {
            var rewards = QuestBook.Embedded.Find("HQ1102010")!.Rewards!;
            check(rewards.ClassItemsFor("Cleric").SequenceEqual([(110750028, 1L)]) && !rewards.ClassItemsFor("Novice").Any() && !rewards.ClassItemsFor(null).Any()
                  && QuestBook.Embedded.Quests.Values.Count(q => q.Rewards?.ClassItems is { Count: > 0 }) == 14,
                  "G5: Table_QuestClassReward reaches the server: 14 Hero quests, HQ1102010 gives the Cleric 110750028");
            check(QuestBook.TakenAtTurnIn(QuestBook.Embedded.Find("MQ1902010")!).SequenceEqual([(631010001, 1)])
                  && QuestBook.Embedded.Quests.Values.Count(q => QuestBook.TakenAtTurnIn(q).Any()) == 12,
                  "G6: the 12 mastery quests take their \"TRUE\" collected piece back at the turn-in");
        }
        {
            var store = new FakeStore();
            var player = await storedWorldSession("ClassReward", store);
            var quest = QuestBook.Embedded.Find("HQ1102010")!;
            var record = await store.SaveQuestAsync(player.CharacterId!.Value, QuestLog.New("HQ1102010", quest) with { Status = QuestLog.Complete, Step = quest.Steps[^1].Order }, default);
            player.Quests!.Set(record);
            await player.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), now);
            var classItems = quest.Rewards!.ClassItemsFor(player.ClassName).Count();
            check(classItems == 1 && store.Rewards.LastOrDefault() is { Code: "HQ1102010" } paid && paid.Items == quest.Rewards.Items!.Length + 1 + classItems,
                  $"G5: the turn-in pays the {player.ClassName}'s class item with the quest's items and Kina");
            player.ExitSharedWorld();
        }
        {
            var store = new FakeStore();
            var player = await storedWorldSession("Mastery", store);
            var quest = QuestBook.Embedded.Find("MQ1902010")!;
            var record = await store.SaveQuestAsync(player.CharacterId!.Value, QuestLog.New("MQ1902010", quest) with { Status = QuestLog.Complete, Step = 2 }, default);
            player.Quests!.Set(record);
            var refused = await player.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), now);
            store.Items.Add(Item(631010001, 1, 88_001));
            await player.InventoryChangesAsync(default);
            var paid = await player.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), now + 1000);
            check(refused.Any(b => b.SequenceEqual(GamePackets.QuestTakeRewardReply((int)record.Uid, 1))) && paid.Any(b => b.SequenceEqual(GamePackets.QuestTakeRewardReply((int)record.Uid)))
                  && !store.Items.Any(i => i.TemplateId == 631010001 && i.Quantity > 0) && player.Quests.Get("MQ1902010")!.Status == QuestLog.Rewarded,
                  "G6: MQ1902010's turn-in needs the Splendent Ruby piece and takes it (refused without it)");
            player.ExitSharedWorld();

            // The mastery condition: MQ1902010 (Auto, map 1010, level 10, ExtractForceMastery 50) starts when the gathering mastery reaches 50.
            var other = await storedWorldSession("Gatherer", new FakeStore());
            var light = other.Quests?.Get("HQ2100010") is null;   // the opening quest of the character's race (LoadQuestsAsync)
            var (code, map) = light ? ("MQ1902010", 1010) : ("MQ2902010", 1110);
            other.Teleport(map, GameData.Embedded.MapStart(map)!.Value.Position, now);
            await other.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
            other.SetLevelForChecks(12);
            var before = other.Quests?.Get(code) is null;
            await other.SetMasteryAsync("gather", 0, 49, default);
            var at49 = other.Quests?.Get(code) is null;
            var at50 = await other.SetMasteryAsync("gather", 0, 50, default);
            check(before && at49 && other.Quests!.Get(code) is { Status: QuestLog.Incomplete } && at50.Any(b => Op(b) == 0x8d11),
                  $"G6: {code} starts when the gathering mastery reaches 50 (not at 49) on map {map} (QuestAdd 0x8d11)");
            other.ExitSharedWorld();
        }

        // ---------- G1: a level-up re-checks the quests ----------
        {
            var store = new FakeStore();
            var player = await storedWorldSession("Leveler", store);
            var quest = QuestBook.Embedded.Find("HQ1103010")!;   // step 1: PCLevel 45
            player.Quests!.Set(await store.SaveQuestAsync(player.CharacterId!.Value, QuestLog.New("HQ1103010", quest), default));
            player.SetLevelForChecks(44);
            var leveled = await player.GainExperienceForChecks(GameData.Embedded.NeedExp(44) + 1, default);
            check(player.Level == 45 && player.Quests.Get("HQ1103010") is { Status: >= QuestLog.Complete } && leveled.Any(b => Op(b) == 0x8d10),
                  "G1: reaching level 45 completes HQ1103010's \"reach level 45\" step at once (it waited for a relog)");
            player.ExitSharedWorld();
        }
    }
}
