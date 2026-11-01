using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HpRemaining objectives must advance from non-player damage and from a non-player kill (DQ1202013 / Belgor on 840001).
/// </summary>
public sealed class MapEventHpRemainingTests
{
    const long Now = 1_760_004_000_000;
    const int BelgorNpc = 2931177;
    const string BelgorSpawner = "N_L1_Q_Belgor_01";

    static MapEventDefinition BelgorCombatStepDefinition()
    {
        var combat = new MapEventStep(4, [],
        [
            new MapEventObjective(7, "HpRemaining", [BelgorSpawner, "50"], Optional: true, Npcs: [BelgorNpc]),
            new MapEventObjective(8, "KillNpc", ["M_L1_FQ_DaevaAss_02", "4"], Npcs: [2200035, 2200036]),
            new MapEventObjective(9, "HpRemaining", [BelgorSpawner, "1"], Npcs: [BelgorNpc]),
        ], []);
        var kalidTalk = new MapEventStep(5, [],
        [
            new MapEventObjective(10, "AskNpc", ["N_L1_Q_Kalid_56", "DQ1202013_08_INS_16"], Npcs: [2931178]),
        ],
        [new MapEventDirective(26, "DungeonClear", [])]);
        return new MapEventDefinition(840001, [], [],
            [new MapEventStage(2, "", [combat, kalidTalk], null, null, null, null)]) { Name = "BelgorCombatSlice" };
    }

    [Fact]
    public void Non_player_kill_of_Belgor_credits_HpRemaining_and_reaches_Kalid_talk_step()
    {
        var run = new MapEventRun(BelgorCombatStepDefinition(), 1, new NullHost(), Now);
        run.Start(Now);
        Assert.Equal(4, run.StepOrder);
        for (var i = 0; i < 4; i++)
            run.Killed(2200035, "M_L1_FQ_DaevaAss_02", hostile: true, spawnerAlive: i < 3, Now + i);
        run.Killed(BelgorNpc, BelgorSpawner, hostile: true, spawnerAlive: false, Now + 10);
        Assert.Equal(5, run.StepOrder);
        Assert.Equal(2, run.WireStep);
    }

    [Fact]
    public void HpChanged_from_non_player_hit_credits_HpRemaining_without_kill()
    {
        var run = new MapEventRun(BelgorCombatStepDefinition(), 1, new NullHost(), Now);
        run.Start(Now);
        Assert.True(run.HpChanged(BelgorNpc, BelgorSpawner, 50, Now + 1));
        Assert.Equal(1, run.Goals[0]);
    }

    sealed class NullHost : IMapEventHost
    {
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
        public void ShowNote(string note) { }
        public void Message(string key, string argument) { }
        public void Abnormal(int id, int durationMs, string target, bool remove) { }
        public void EnvObjState(string spawner, string state) { }
        public void EnvObjChangePhase(string spawner, int phase) { }
        public void SetEnvObjCanInteract(string spawner, bool canInteract) { }
        public void DynamicTerrain(string spawner, bool block) { }
        public void Progress(MapEventRun run) { }
        public void ScriptBeat(MapEventRun run, string kind, string detail, long now) { }
        public void Unsupported(MapEventRun run, string what) { }
        public bool QuestStepActive(int questId, int step) => false;
        public bool MapEventStartAllowed(MapEventDefinition definition) => true;
        public bool NpcInCombat(string spawner) => false;
        public bool SpawnerAlive(string spawner) => true;
        public bool SpawnerExists(string spawner) => true;
        public bool PlayerDead => false;
        public double? DistanceTo(string spawner) => null;
    }
}
