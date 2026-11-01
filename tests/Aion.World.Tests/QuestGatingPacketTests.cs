using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Quest-step gating for watchstones (HQ1101018) and supply chests (HQ1101011).</summary>
public sealed class QuestGatingPacketTests
{
    const long Now = 1_760_000_200_000;

    static async Task<GameSession> LightWorldAsync()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        return session;
    }

    static async Task AdvanceToStepAsync(GameSession session, string code, int step)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 12 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete, Step: var s } && s < step; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", code, default);
    }

    [Fact]
    public async Task HQ1101018_watchstone_immune_before_destroy_step()
    {
        var session = await LightWorldAsync();
        await AdvanceToStepAsync(session, "HQ1101018", 3);
        var watch = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2290017);
        Assert.NotNull(watch);
        Assert.Contains(2290017, session.Quests!.PendingKillNpcIds());
        var hpBefore = watch!.Hp;
        var cls = GameData.Embedded.Profile(1, 1)!.Class;
        var skillId = GameData.Embedded.LearnedSkills(cls, session.Level)
            .Select(s => s.SkillId).First(id => GameData.Embedded.Skill(id)?.Damage.Length > 0);
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(watch.Key).F32(0)
            .Vec3(watch.Position).U8(9).ToArray();
        await session.HandleAsync(cast, Now + 1000);
        Assert.Equal(hpBefore, watch.Hp);
        Assert.False(watch.Dead);
    }

    [Fact]
    public async Task HQ1101018_watchstone_damaged_on_step_4_and_respawns_if_killed_early()
    {
        var session = await LightWorldAsync();
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        var watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        watch.Hp = 0;
        watch.CorpseRemoved = true;
        await AdvanceToStepAsync(session, "HQ1101018", 4);
        watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        Assert.False(watch.Dead);
        Assert.True(watch.Hp > 0);
        var cls = GameData.Embedded.Profile(1, 1)!.Class;
        var skillId = GameData.Embedded.LearnedSkills(cls, session.Level)
            .Select(s => s.SkillId).First(id => GameData.Embedded.Skill(id)?.Damage.Length > 0);
        watch.Hp = watch.HpMax;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(watch.Key).F32(0)
            .Vec3(watch.Position).U8(9).ToArray();
        var castAt = Now + 2000;
        await session.HandleAsync(cast, castAt);
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        Assert.True(watch.Hp < watch.HpMax || watch.Dead);
    }

    [Fact]
    public async Task HQ1101011_supply_chest_refused_before_step_3()
    {
        var session = await LightWorldAsync();
        await AdvanceToStepAsync(session, "HQ1101011", 2);
        var mapId = session.MapId ?? 1000;
        var index = WorldObjects.ObjectsOn(mapId).ToList().FindIndex(o => o.Env == 1200530);
        Assert.True(index >= 0);
        session.Teleport(mapId, WorldObjects.ObjectsOn(mapId).ToList()[index].At, Now);
        var wireKey = WorldObjects.FirstKey + index;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), Now);
        Assert.True(use.Any(p => p.Length >= 4 && p[0] == 0x37 && p[1] == 0x36 && p[2] == 1 && p[3] == 0),
            "quest-gated chest should return InteractEnvObjReply result 1");
    }

    [Fact]
    public void Protected_scout_not_in_open_kill_list_on_step_3()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, 1);
        log.Set(QuestLog.New("HQ1101018", book.Find("HQ1101018")!) with { Step = 3 });
        Assert.Contains(2931095, log.ProtectedTalkNpcIds());
        Assert.DoesNotContain(2931095, log.OpenKillNpcIds());
    }

    [Fact]
    public async Task Neutral_citizen_without_quest_kill_objective_cannot_be_damaged()
    {
        var session = await LightWorldAsync();
        var citizen = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2930635 && !t.Hostile);
        Assert.NotNull(citizen);
        Assert.DoesNotContain(citizen!.NpcId, session.Quests!.OpenKillNpcIds());
        var hpBefore = citizen.Hp;
        var skillId = MapEventScenarioHelpers.BasicAttackSkill(session);
        var castAt = Now + 1000;
        await session.HandleAsync(MapEventScenarioHelpers.CombatCast(skillId, citizen.Key, citizen.Position), castAt);
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, MapEventScenarioHelpers.SkillHitsLandMs(skillId));
        Assert.Equal(hpBefore, citizen.Hp);
        Assert.False(citizen.Dead);
    }
}
