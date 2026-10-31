using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestItemAcquireTests
{
    const long Now = 1_760_004_000_000;
    const int ScrollItem = 530199001;

    static QuestBook SampleBook()
    {
        var scroll = new QuestDefinition(
            Id: 9001, Title: "Scroll quest", Type: "DutyScroll", Race: "Light", Level: 1, Map: 1010,
            Acquire: "Item", StartNpc: null, Complete: "UI", FinishNpc: null, GiveUp: true,
            Prerequisites: [], Previous: [], Next: null,
            Steps: [new QuestStep(1, [new QuestObjective("KillNpc", Npcs: [2100050], Count: 1)])], Rewards: null,
            AcquireItems: [[ScrollItem, 1]]);
        return new QuestBook(new Dictionary<string, QuestDefinition> { ["UQ9001"] = scroll });
    }

    [Fact]
    public async Task Item_use_starts_quest_when_acquire_items_match()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 45);
        session.SetQuestLogForTests(new QuestLog(SampleBook(), race: 1));
        store.Items.Add(FakeCharacterStore.BagItem(ScrollItem, 1, wire: 7001));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 20);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(7001).ToArray(), Now + 30);
        Assert.Contains(replies, p => GameSessionTestHelpers.HasOpcode(p, 0x8d11));
        Assert.NotNull(session.Quests!.Get("UQ9001"));
        Assert.Equal(QuestLog.Incomplete, session.Quests!.Get("UQ9001")!.Status);
    }

    [Fact]
    public void QuestBook_indexes_acquire_items()
    {
        var book = SampleBook();
        Assert.Equal(["UQ9001"], book.QuestsByAcquireItem(ScrollItem));
    }
}
