using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestGatingSharedWorldTests
{
    const long Now = 1_760_000_300_000;

    static async Task<(GameSession early, GameSession ready)> TwoPlayersOnPoetaAsync()
    {
        var shared = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var sessionA = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = shared, CharacterLevel = 55, WorldId = storeA.WorldId, TrainingDummy = true },
            new Random(31)) { Store = storeA };
        var sessionB = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = shared, CharacterLevel = 55, WorldId = storeB.WorldId, TrainingDummy = true },
            new Random(32)) { Store = storeB };
        foreach (var s in new[] { sessionA, sessionB })
        {
            await s.HandleAsync(GameSessionTestHelpers.Login(), Now);
            await s.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
            var wireId = s == sessionA ? storeA.States[0].WireId : storeB.States[0].WireId;
            await s.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        }
        return (sessionA, sessionB);
    }

    static int DamageSkillId()
    {
        var cls = GameData.Embedded.Profile(1, 1)!.Class;
        return GameData.Embedded.LearnedSkills(cls, 55).First(s => GameData.Embedded.Skill(s.SkillId)?.Damage.Length > 0).SkillId;
    }

    static byte[] CastAt(int skillId, Target t) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(t.Key).F32(0).Vec3(t.Position).U8(9).ToArray();

    static async Task AdvanceToStepAsync(GameSession session, string code, int step)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 12 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete, Step: var s } && s < step; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", code, default);
    }

    [Fact]
    public async Task Open_kill_on_current_step_wins_over_pending_future_step_gating()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1102020", default);
        await AdvanceToStepAsync(session, "HQ1102020", 2);
        Assert.Contains(2100002, session.Quests!.OpenKillNpcIds());
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        var prey = session.Targets!.Values.First(t => t.NpcId == 2100002);
        var skillId = DamageSkillId();
        var hp = prey.Hp;
        prey.Hp = 1;
        var castAt = Now + 50;
        await session.HandleAsync(CastAt(skillId, prey), castAt);
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        Assert.True(prey.Dead || session.Quests!.Get("HQ1102020")!.Goals.Any(g => g > 0),
            "current-step KillNpc must be damageable even when other quests gate the same template on later steps");
    }

    [Fact]
    public async Task SharedWorld_pending_gating_is_per_player()
    {
        var (early, ready) = await TwoPlayersOnPoetaAsync();
        await AdvanceToStepAsync(early, "HQ1101018", 2);
        await AdvanceToStepAsync(ready, "HQ1101018", 4);
        var watch = early.Targets!.Values.First(t => t.NpcId == 2290017);
        var skillId = DamageSkillId();
        var hp = watch.Hp;
        var earlyCast = Now + 100;
        await early.HandleAsync(CastAt(skillId, watch), earlyCast);
        await GameSessionTestHelpers.AfterSkillHitsAsync(early, earlyCast, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        Assert.Equal(hp, watch.Hp);
        var readyCast = Now + 200;
        await ready.HandleAsync(CastAt(skillId, watch), readyCast);
        await GameSessionTestHelpers.AfterSkillHitsAsync(ready, readyCast, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        Assert.True(watch.Hp < hp || watch.Dead);
    }

    [Fact]
    public async Task SharedWorld_step4_respawn_does_not_duplicate_watchstone_keys()
    {
        var (a, b) = await TwoPlayersOnPoetaAsync();
        PinQuestStep(a, "HQ1101018", 4);
        PinQuestStep(b, "HQ1101018", 4);
        Assert.Contains(2290017, a.Quests!.OpenKillNpcIds());
        var stones = a.Targets!.Values.Where(t => t.NpcId == 2290017).ToList();
        Assert.True(stones.Count >= 3);
        foreach (var s in stones) { s.Hp = 0; s.CorpseRemoved = true; }
        var keysBefore = stones.Select(s => s.Key).OrderBy(k => k).ToHashSet();
        var need = a.Quests!.OpenKillNpcIds().ToHashSet();
        var deadNeeded = a.Targets!.Values.Count(t => need.Contains(t.NpcId) && (t.Dead || t.CorpseRemoved));
        Assert.True(deadNeeded >= 3, $"expected dead quest targets, need={need.Count} dead={deadNeeded}");
        var appearA = a.QuestWorldGatingForTests(Now + 500);
        var keysAfterA = a.Targets!.Values.Where(t => t.NpcId == 2290017).Select(t => t.Key).ToHashSet();
        Assert.Equal(keysBefore, keysAfterA);
        Assert.Equal(keysBefore.Count, appearA.Count(p => GameSessionTestHelpers.HasOpcode(p, 0x3641)));
        Assert.All(stones, s => Assert.True(s.Hp > 0 && !s.CorpseRemoved));
        // Shared targets: B must not spawn duplicate keys; A already revived the same Target entries.
        var appearB = b.QuestWorldGatingForTests(Now + 600);
        var keysAfterB = b.Targets!.Values.Where(t => t.NpcId == 2290017).Select(t => t.Key).ToHashSet();
        Assert.Equal(keysBefore, keysAfterB);
        Assert.Equal(0, appearB.Count(p => GameSessionTestHelpers.HasOpcode(p, 0x3641)));
    }

    static void PinQuestStep(GameSession session, string code, int step)
    {
        var log = session.DevelopmentQuestLog();
        var def = log.Book.Find(code)!;
        log.Set(QuestLog.New(code, def) with { Step = step, Uid = step, Goals = new int[6] });
    }
}
