using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GatherCraftMasteryQuestTests
{
    const long Now = 1_760_400_000_000;

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
}
