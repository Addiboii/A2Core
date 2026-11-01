using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GatherCraftMasteryQuestTests
{
    const long Now = 1_760_400_000_000;
    const int ElevateGatherLight = 631010001;
    const int ElevateGatherDark = 631010004;
    const int ElevateCraftBlacksmithLight = 631030001;
    const int ElevateCraftBlacksmithDark = 631030005;
    const int BlacksmithElevateRecipeLight = 119016001;
    const int BlacksmithElevateRecipeDark = 129016005;
    const int LightBlacksmithTableNpc = 2910003;
    const int DarkBlacksmithTableNpc = 2910010;

    static void AddRecipeMaterials(FakeCharacterStore store, CraftTables.Recipe recipe, long wire = 8100)
    {
        var slot = wire;
        foreach (var (item, count) in recipe.Components)
            store.Items.Add(FakeCharacterStore.BagItem(item, count, wire: slot++));
    }

    static Target Worktable(GameSession session, int npcId) =>
        session.Targets!.Values.First(t => t.NpcId == npcId);

    static byte[] CraftPacket(int worktableKey, int recipeId) =>
        new PacketWriter().U16(GameSession.CraftStartRq).Varint((uint)worktableKey).I32(recipeId).Varint(0).I32(0).I32(0).Varint(0).ToArray();

    static async Task CraftOnceAsync(GameSession session, int mapId, int worktableNpcId, int recipeId, long now)
    {
        var table = Worktable(session, worktableNpcId);
        session.Teleport(mapId, table.Position with { X = table.Position.X + 100 }, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        session.CraftRoll = () => 0;
        await session.HandleAsync(CraftPacket(table.Key, recipeId), now);
    }

    static (WorldObject Node, int Index, int Key) FirstGatherNode(int mapId)
    {
        var hit = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).First(x => x.o.Gather is not null);
        return (hit.o, hit.i, WorldObjects.FirstKey + hit.i);
    }

    static async Task GatherOnceAsync(GameSession session, int mapId, int nodeIndex, long now)
    {
        var node = WorldObjects.ObjectsOn(mapId)[nodeIndex];
        var key = WorldObjects.FirstKey + nodeIndex;
        session.Teleport(mapId, node.At with { X = node.At.X + 50 }, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        session.GatherBonusRoll = () => 4;
        await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), now);
        await session.TickAsync(now + node.InteractTime + 10);
    }

    [Fact]
    public void Quest_condition_extract_force_mastery_reads_gather_level()
    {
        var log = new QuestLog(QuestBook.Embedded, race: 1);
        var quest = log.Book.Find("MQ1902010")!;
        Assert.Contains(quest.Conditions!, c => c.Type == "ExtractForceMastery" && c.Value == "50");
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { TrainingDummy = true }, new Random(1));
        session.SetMasteryLevelsForTests(49);
        Assert.False(session.QuestConditionHoldsForTests(quest.Conditions![0]));
        session.SetMasteryLevelsForTests(50);
        Assert.True(session.QuestConditionHoldsForTests(quest.Conditions![0]));
    }

    [Fact]
    public async Task Auto_acquire_gather_craft_mastery_quest_when_mastery_reaches_gate()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        await session.SetMasteryAsync("gather", 0, 49, default);
        Assert.Null(session.Quests!.Get("MQ1902010"));
        var replies = await session.SetMasteryAsync("gather", 0, 50, default);
        Assert.NotNull(session.Quests!.Get("MQ1902010"));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x8d11);
    }

    [Fact]
    public async Task Elevate_gather_quest_light_grants_test_item_on_successful_gather()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var quest = QuestLog.New("MQ1902010", session.Quests!.Book.Find("MQ1902010")!) with { Step = 2, Uid = 9010 };
        session.Quests!.Set(quest);
        store.Quests.Add(quest);
        var (_, index, _) = FirstGatherNode(1010);
        await GatherOnceAsync(session, 1010, index, Now + 1000);
        Assert.Contains(store.Items, i => i.TemplateId == ElevateGatherLight);
        var updated = session.Quests!.Get("MQ1902010")!;
        Assert.True(updated.Status >= QuestLog.Complete || updated.Step > 2,
            "CollectItem for elevate gather product should finish step 2");
    }

    [Fact]
    public async Task Elevate_gather_quest_dark_grants_test_item_on_successful_gather()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        session.Teleport(1110, GameSessionTestHelpers.MapStartOrDefault(1110), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var quest = QuestLog.New("MQ2902010", session.Quests!.Book.Find("MQ2902010")!) with { Step = 2, Uid = 9011 };
        session.Quests!.Set(quest);
        store.Quests.Add(quest);
        var (_, index, _) = FirstGatherNode(1110);
        await GatherOnceAsync(session, 1110, index, Now + 2000);
        Assert.Contains(store.Items, i => i.TemplateId == ElevateGatherDark);
        var updated = session.Quests!.Get("MQ2902010")!;
        Assert.True(updated.Status >= QuestLog.Complete || updated.Step > 2,
            "CollectItem for elevate gather product should finish step 2");
    }

    [Fact]
    public async Task Elevate_craft_quest_light_grants_test_item_on_successful_craft()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        await session.SetMasteryAsync("craft", 1, 50, default);
        var recipe = CraftTables.Embedded.Of(BlacksmithElevateRecipeLight)!;
        AddRecipeMaterials(store, recipe);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var quest = QuestLog.New("MQ1902020", session.Quests!.Book.Find("MQ1902020")!) with { Step = 2, Uid = 9020 };
        session.Quests!.Set(quest);
        store.Quests.Add(quest);
        await CraftOnceAsync(session, 1010, LightBlacksmithTableNpc, BlacksmithElevateRecipeLight, Now + 3000);
        Assert.Contains(store.Items, i => i.TemplateId == ElevateCraftBlacksmithLight);
        var updated = session.Quests!.Get("MQ1902020")!;
        Assert.True(updated.Status >= QuestLog.Complete || updated.Step > 2,
            "CollectItem for elevate craft product should finish step 2");
    }

    [Fact]
    public async Task Elevate_craft_quest_dark_grants_test_item_on_successful_craft()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now);
        session.Teleport(1110, GameSessionTestHelpers.MapStartOrDefault(1110), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.SetMasteryAsync("craft", 1, 50, default);
        var recipe = CraftTables.Embedded.Of(BlacksmithElevateRecipeDark)!;
        AddRecipeMaterials(store, recipe);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var quest = QuestLog.New("MQ2902020", session.Quests!.Book.Find("MQ2902020")!) with { Step = 2, Uid = 9021 };
        session.Quests!.Set(quest);
        store.Quests.Add(quest);
        await CraftOnceAsync(session, 1110, DarkBlacksmithTableNpc, BlacksmithElevateRecipeDark, Now + 4000);
        Assert.Contains(store.Items, i => i.TemplateId == ElevateCraftBlacksmithDark);
        var updated = session.Quests!.Get("MQ2902020")!;
        Assert.True(updated.Status >= QuestLog.Complete || updated.Step > 2,
            "CollectItem for elevate craft product should finish step 2");
    }
}
