using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>HQ1102125 / HQ2102105 Empyrean Monolith: env loot grant and CollectItem sync on step 3/4.</summary>
public sealed class EmpyreanMonolithQuestTests
{
    const long Now = 1_760_000_400_000;
    const int LightEnv = 1201290;
    const int DarkEnv = 1301134;
    const int LightToken = 631900001;
    const int DarkToken = 631900007;

    static async Task UseEnvAsync(GameSession session, int mapId, int envId, long now)
    {
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        var index = objects.FindIndex(o => o.Env == envId);
        Assert.True(index >= 0, $"env {envId} on map {mapId}");
        session.Teleport(mapId, objects[index].At, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, now + 10);
        _ = session.QuestWorldGatingForTests(now + 15);
        var wireKey = WorldObjects.FirstKey + index;
        _ = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), now + 20);
        if (objects[index].InteractTime > 0)
            await session.TickAsync(now + objects[index].InteractTime + 100);
    }

    [Fact]
    public async Task Light_HQ1102125_godfragment_grants_token_and_completes_collect_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        Assert.Equal(2, session.Quests!.Get("HQ1102125")!.Step);
        await UseEnvAsync(session, 1010, LightEnv, Now + 500);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(store.Items.Any(i => i.TemplateId == LightToken && i.Quantity > 0), "Empyrean trace token should be in bag");
        Assert.True(quest.Step >= 4 || quest.Goals[0] >= 1,
            $"CollectItem should advance after env loot (step={quest.Step} goals=[{string.Join(',', quest.Goals)}])");
    }

    [Fact]
    public async Task Light_HQ1102125_step3_without_token_can_reuse_godfragment_env()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 30_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        Assert.Equal(3, session.Quests!.Get("HQ1102125")!.Step);
        Assert.False(store.Items.Any(i => i.TemplateId == LightToken && i.Quantity > 0), "soft-lock: at step 3 without token");
        await UseEnvAsync(session, 1010, LightEnv, Now + 31_000);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(store.Items.Any(i => i.TemplateId == LightToken && i.Quantity > 0), "Empyrean trace token should be in bag");
        Assert.True(quest.Step >= 4 || quest.Goals[0] >= 1,
            $"CollectItem should advance after env loot (step={quest.Step} goals=[{string.Join(',', quest.Goals)}])");
    }

    [Fact]
    public async Task Dark_HQ2102105_godfragment_grants_token_and_completes_collect_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 10_000, race: 2, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ2102105", default);
        for (var guard = 0; guard < 3 && session.Quests!.Get("HQ2102105")!.Step < 3; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2102105", default);
        Assert.Equal(3, session.Quests!.Get("HQ2102105")!.Step);
        await UseEnvAsync(session, 1110, DarkEnv, Now + 11_000);
        var quest = session.Quests!.Get("HQ2102105")!;
        Assert.True(store.Items.Any(i => i.TemplateId == DarkToken && i.Quantity > 0), "Empyrean trace token should be in bag");
        Assert.True(quest.Step >= 5 || quest.Goals[0] >= 1,
            $"CollectItem should advance (step={quest.Step} goals=[{string.Join(',', quest.Goals)}])");
    }

    [Fact]
    public async Task Recheck_syncs_collect_item_already_in_bag()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 20_000, race: 1, level: 16);
        store.Items.Add(FakeCharacterStore.BagItem(LightToken, 1, 8002));
        await session.InventoryChangesAsync(default);
        var book = session.Quests!.Book.Find("HQ1102125")!;
        session.Quests!.Set(QuestLog.New("HQ1102125", book) with { Step = 3, Uid = 900 });
        var recheck = await session.QuestRecheckAsync(default);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(recheck.Any(p => p.Length >= 2 && p[0] == 0x10 && p[1] == 0x8d) || quest.Step >= 4 || quest.Goals[0] >= 1,
            "Recheck should credit CollectItem when token already held");
    }

    [Fact]
    public async Task Login_HQ1102125_step4_with_scroll_in_bag_advances_to_step5()
    {
        const int Scroll = 531110002;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 40_000, race: 1, level: 16);
        var characterId = store.States[0].Summary.CharacterId;
        var book = session.Quests!.Book.Find("HQ1102125")!;
        await store.SaveQuestAsync(characterId, QuestLog.New("HQ1102125", book) with { Step = 4, Uid = 910, Status = QuestLog.Incomplete }, default);
        store.Items.Add(FakeCharacterStore.BagItem(Scroll, 1, 8100));
        var wireId = store.States[0].WireId;
        var relog = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameSessionTestHelpers.LiveGameData), CharacterLevel = 16, WorldId = store.WorldId, TrainingDummy = true },
            new Random(33)) { Store = store };
        await relog.HandleAsync(GameSessionTestHelpers.Login(), Now + 41_000);
        await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 41_000);
        await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 41_000);
        var quest = relog.Quests!.Get("HQ1102125")!;
        Assert.True(quest.Step >= 5, $"login should credit CollectItem scroll (step={quest.Step})");
    }

    [Fact]
    public async Task Light_HQ1102125_proximity_fallback_resonates_without_interact_packet()
    {
        const int Scroll = 531110002;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 40_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        for (var guard = 0; guard < 4 && session.Quests!.Get("HQ1102125")!.Step < 4; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        store.Items.Add(FakeCharacterStore.BagItem(LightToken, 1, 8004));
        await session.InventoryChangesAsync(default);
        var objects = WorldObjects.ObjectsOn(1010).ToList();
        var index = objects.FindIndex(o => o.Env == 1200863);
        Assert.True(index >= 0);
        session.Teleport(1010, objects[index].At, Now + 41_000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 41_005);
        await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, Now + 41_010);
        _ = session.QuestWorldGatingForTests(Now + 41_025);
        await session.TickAsync(Now + 43_000);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(store.Items.Any(i => i.TemplateId == Scroll && i.Quantity > 0), "proximity fallback should grant scroll");
        Assert.True(quest.Step >= 5, $"proximity fallback should advance quest (step={quest.Step})");
    }

    [Fact]
    public async Task Light_HQ1102125_monolith_resonate_grants_scroll_and_advances_step()
    {
        const int Scroll = 531110002;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 30_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        for (var guard = 0; guard < 4 && session.Quests!.Get("HQ1102125")!.Step < 4; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        Assert.Equal(4, session.Quests!.Get("HQ1102125")!.Step);
        store.Items.Add(FakeCharacterStore.BagItem(LightToken, 1, 8003));
        await session.InventoryChangesAsync(default);
        await UseEnvAsync(session, 1010, 1200863, Now + 31_000);
        var quest = session.Quests!.Get("HQ1102125")!;
        Assert.True(store.Items.Any(i => i.TemplateId == Scroll && i.Quantity > 0), "scroll should be granted from monolith");
        Assert.True(quest.Step >= 5, $"step should advance after resonate (step={quest.Step})");
    }
}
