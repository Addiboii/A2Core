using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MonolithPacketTests
{
    const long Now = 1_760_000_500_000;
    const int VerteronEnv = 1200863;
    const int VerteronMonolithId = 1200865;
    const int LightToken = 631900001;
    const int DarkToken = 631900007;
    const int Scroll = 531110002;

    static async Task<(List<WorldObject> objects, List<byte[]> replies)> ShowMonolithAsync(GameSession session, int mapId, int envId, long now)
    {
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        var index = objects.FindIndex(o => o.Env == envId);
        Assert.True(index >= 0, $"env {envId} on map {mapId}");
        session.Teleport(mapId, objects[index].At, now);
        var replies = (await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5)).ToList();
        await MapEventScenarioHelpers.SyncPositionAsync(session, objects[index].At, now + 10);
        replies.AddRange(session.QuestWorldGatingForTests(now + 15));
        return (objects, replies);
    }

    static int MonolithWireKey(int mapId, int envId)
    {
        var index = WorldObjects.ObjectsOn(mapId).ToList().FindIndex(o => o.Env == envId);
        return WorldObjects.FirstKey + index;
    }

    [Fact]
    public async Task Monolith_spawn_sends_MonolithInfo_NT_after_entry_list()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 16);
        var (_, spawnReplies) = await ShowMonolithAsync(session, 1010, VerteronEnv, Now);
        var index = WorldObjects.ObjectsOn(1010).ToList().FindIndex(o => o.Env == VerteronEnv);
        var wireKey = WorldObjects.FirstKey + index;
        var info = spawnReplies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithInfoNt);
        Assert.NotNull(info);
        Assert.Equal(VerteronMonolithId, BitConverter.ToInt32(info!, 2));
        Assert.Equal(wireKey, BitConverter.ToInt32(info, 10));
    }

    [Fact]
    public async Task Monolith_spawn_still_flushes_notices_after_LoadPost_reward_tab()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 50, race: 1, level: 16);
        await session.HandleAsync(new PacketWriter().U16(0xe214).U8(2).U8(0).I64(0).I64(0).ToArray(), Now + 60);
        var (_, spawnReplies) = await ShowMonolithAsync(session, 1010, VerteronEnv, Now + 100);
        Assert.NotNull(spawnReplies.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithInfoNt));
    }

    [Fact]
    public async Task Monolith_list_at_world_entry_includes_race_monoliths()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 5_000, race: 1, level: 16);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5_100);
        var list = enter.FirstOrDefault(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithListNt);
        Assert.NotNull(list);
        Assert.True(list!.Length > 4, "MonolithList_NT should list Light monolith ids");
    }

    [Fact]
    public async Task MonolithResonate_RQ_consumes_token_grants_scroll_and_advances_quest()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 10_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        for (var guard = 0; guard < 4 && session.Quests!.Get("HQ1102125")!.Step < 4; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        store.Items.Add(FakeCharacterStore.BagItem(LightToken, 1, 8003));
        await session.InventoryChangesAsync(default);
        await ShowMonolithAsync(session, 1010, VerteronEnv, Now + 11_000);
        var wireKey = MonolithWireKey(1010, VerteronEnv);
        var replies = await session.HandleAsync(new PacketWriter().U16(GamePackets.MonolithResonateRq).I32(VerteronMonolithId).I32(1).I32(wireKey).ToArray(), Now + 11_100);
        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithResonateRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(LightToken, BitConverter.ToInt32(rs, 10));
        Assert.Contains(store.Items, i => i.TemplateId == Scroll && i.Quantity > 0);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == LightToken && i.Quantity > 0);
        Assert.True(session.Quests!.Get("HQ1102125")!.Step >= 5);
    }

    [Fact]
    public async Task MonolithResonate_RQ_refuses_wrong_token()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 20_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        for (var guard = 0; guard < 4 && session.Quests!.Get("HQ1102125")!.Step < 4; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        store.Items.Add(FakeCharacterStore.BagItem(DarkToken, 1, 8010));
        await session.InventoryChangesAsync(default);
        await ShowMonolithAsync(session, 1010, VerteronEnv, Now + 21_000);
        var wireKey = MonolithWireKey(1010, VerteronEnv);
        var replies = await session.HandleAsync(new PacketWriter().U16(GamePackets.MonolithResonateRq).I32(VerteronMonolithId).I32(1).I32(wireKey).ToArray(), Now + 21_100);
        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithResonateRs);
        Assert.NotEqual(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(store.Items, i => i.TemplateId == DarkToken && i.Quantity > 0);
    }

    [Fact]
    public async Task MonolithResonate_RQ_refuses_out_of_range_key()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now + 30_000, race: 1, level: 16);
        await session.QuestCommandAsync("QuestAdd", "HQ1102125", default);
        for (var guard = 0; guard < 4 && session.Quests!.Get("HQ1102125")!.Step < 4; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1102125", default);
        store.Items.Add(FakeCharacterStore.BagItem(LightToken, 1, 8011));
        await session.InventoryChangesAsync(default);
        await ShowMonolithAsync(session, 1010, VerteronEnv, Now + 31_000);
        var replies = await session.HandleAsync(new PacketWriter().U16(GamePackets.MonolithResonateRq).I32(VerteronMonolithId).I32(1).I32(1).ToArray(), Now + 31_100);
        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GamePackets.MonolithResonateRs);
        Assert.NotEqual(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(store.Items, i => i.TemplateId == LightToken && i.Quantity > 0);
    }
}
