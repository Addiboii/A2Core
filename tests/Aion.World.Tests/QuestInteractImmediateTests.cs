using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Map-event and quest UseEnvObj steps should advance and enable the next interact prompt without multi-second stalls.</summary>
public sealed class QuestInteractImmediateTests
{
    sealed class NoteHost : IMapEventHost
    {
        public List<string> Calls { get; } = [];
        public void Spawn(string spawner, int delayMs) { }
        public void Despawn(string spawner, int delayMs, bool kill) { }
        public void SummonAround(string target, string spawner, int count, double radius) { }
        public void MoveNpc(string spawner, string route) { }
        public void FollowPc(string spawner, bool follow) { }
        public void RotateNpc(string spawner, string target) { }
        public void Talk(string talk) { }
        public void ChatBubble(string spawner, string talk) { }
        public void SocialAction(string spawner, int action, bool stop) { }
        public void SetAggroPc(string spawner) { }
        public void SetAggroNpc(string spawner, string target) { }
        public void SetRelationship(string spawner, string relationship) { }
        public void TeleportPc(string point, string effect, string before, string after) { }
        public void MapExit(string point, string mode) { }
        public void DungeonClear(MapEventRun run) { }
        public void DungeonFail(MapEventRun run) { }
        public void MapEnvState(int state) { }
        public void MapEnvSetting(int group, int state) { }
        public void Cutscene(string name) { }
        public void ShowGuide(string guide) { }
        public void FocusCamera(string camera, string talk) { }
        public void ShowNote(string note) => Calls.Add($"Note {note}");
        public void Message(string key, string argument) { }
        public void Abnormal(int id, int durationMs, string target, bool remove) { }
        public void EnvObjState(string spawner, string state) { }
        public void EnvObjChangePhase(string spawner, int phase) { }
        public void SetEnvObjCanInteract(string spawner, bool canInteract) { }
        public void DynamicTerrain(string spawner, bool block) { }
        public void Progress(MapEventRun run) { }
        public void ScriptBeat(MapEventRun run, string kind, string detail, long now) { }
        public void Unsupported(MapEventRun run, string what) { }
        public bool QuestStepActive(int questId, int step) => true;
        public bool MapEventStartAllowed(MapEventDefinition definition) => true;
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => true;
        public bool SpawnerExists(string spawner) => true;
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
        public int CountDead(string[] spawners) => 0;
        public void EnsureStep(MapEventStep step, int[] goals) { }
        public bool EnvObjectUsed(string spawnName) => false;
        public string DungeonStateName => "None";
    }

    [Fact]
    public void UseEnvObj_on_same_step_credits_paired_CloseNote_and_ShowNote()
    {
        var step = new MapEventStep(3, [],
        [
            new MapEventObjective(4, "UseEnvObj", ["E_CoastalCave_Corpse_001", "1"]),
            new MapEventObjective(5, "CloseNote", ["Note_CoastalCave_001", "False"]),
        ], []);
        var step2 = new MapEventStep(4, [], [new MapEventObjective(6, "EnterVolumePC", ["Trigger_Next", "210001"])], []);
        var def = new MapEventDefinition(210001, [[]], [], [new MapEventStage(1, "", [step, step2], null, null, null, null)])
        {
            Name = "CoastalCave_Test",
        };
        var host = new NoteHost();
        var run = new MapEventRun(def, 1, host, 0);
        run.Start(0);
        Assert.True(run.UsedObject("E_CoastalCave_Corpse_001", 2500));
        Assert.Contains("Note Note_CoastalCave_001", host.Calls);
        Assert.Equal(4, run.StepOrder);
    }

    const long Now = 1_760_006_000_000;

    static async Task<GameSession> Hq1102040LayerAsync(long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), now, race: 1, level: 12);
        await session.QuestCommandAsync("QuestAdd", "HQ1102040", default);
        var start = GameSessionTestHelpers.MapStartOrDefault(101028);
        session.Teleport(101028, start, now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 60);
        await session.TickAsync(now + 4000);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_A_Layer_HQ002_MapEvent", now + 5000)
                  ?? throw new InvalidOperationException("HQ002 layer map event did not start");
        Assert.Equal(1, run.StepOrder);
        return session;
    }

    static async Task<(GameSession Session, WorldObject Blood)> Hq002BloodClothesStepAsync(long now)
    {
        var session = await Hq1102040LayerAsync(now);
        var trigger = WorldObjects.TriggersOn(101028).First(t => t.Name.Contains("TriggerActor_01", StringComparison.Ordinal));
        await MapEventScenarioHelpers.SyncPositionAsync(session, trigger.At, now + 6000);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(trigger.At), now + 6010);
        for (var i = 0; i < 8; i++)
            await session.TickAsync(now + 6500 + i * 500);
        var run = session.MapEventRuns.First(r => r.Name == "World_L_A_Layer_HQ002_MapEvent");
        Assert.Equal(2, run.StepOrder);
        var blood = WorldObjects.ObjectsOn(101028).First(o => o.Name.Contains("bloodClothes", StringComparison.OrdinalIgnoreCase));
        await MapEventScenarioHelpers.SyncPositionAsync(session, blood.At, now + 9000);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(blood.At), now + 9010);
        await session.TickAsync(now + 9100);
        return (session, blood);
    }

    [Fact]
    public async Task Hq002_layer_UseEnvObj_step_sends_can_interact_for_blood_clothes()
    {
        var (session, _) = await Hq002BloodClothesStepAsync(Now);
        var gating = session.QuestWorldGatingForTests(Now + 9200);
        var tick = await session.TickAsync(Now + 9300);
        Assert.True(gating.Concat(tick).Any(p => GameSessionTestHelpers.Op(p) == 0x363c && p[^1] == 1),
            "blood clothes should get InteractEnvObjCanInteract when the UseEnvObj step opens");
    }
}
