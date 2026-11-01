using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The follow-up a turn-in adds by itself (0x8d11) is gated by Quest.dat: AcquireType Auto, or Npc given by the turn-in NPC.
/// Regression: Zakl (Dark) got quests he had not taken from their NPC when the reward added any Next unconditionally.
/// </summary>
public sealed class QuestFollowUpGatingTests
{
    static QuestLog Rewarded(int race, params string[] codes)
    {
        var log = new QuestLog(QuestBook.Embedded, race);
        foreach (var code in codes)
            log.Set(QuestLog.New(code, log.Book.Find(code)!) with { Status = QuestLog.Rewarded });
        return log;
    }

    [Fact]
    public void Npc_follow_up_from_turn_in_npc_waits_for_accept_HQ2101020_to_HQ2101030()
    {
        // Stellan turns in HQ2101020; «Shadow Forest» HQ2101030 is AcquireType Npc and is not added on reward.
        var log = Rewarded(2, "HQ2101010", "HQ2101020");
        Assert.Null(log.FollowUpOnReward(log.Book.Find("HQ2101020")!, level: 3));
    }

    [Fact]
    public void Auto_follow_up_is_added_on_reward()
    {
        var log = Rewarded(1, "HQ1101070");
        Assert.Equal("HQ1102010", log.FollowUpOnReward(log.Book.Find("HQ1101070")!, level: 45));
    }

    [Fact]
    public void Follow_up_from_another_npc_is_not_added_HQ1102010_to_HQ1102011()
    {
        // HQ1102011 is AcquireType Npc from 2931023; HQ1102010 has no turn-in NPC: the player takes it at that NPC.
        var finished = QuestBook.Embedded.Find("HQ1102010")!;
        var log = Rewarded(1, "HQ1101070", "HQ1102010");
        Assert.True(log.CanAccept("HQ1102011", level: 45));
        Assert.Null(log.FollowUpOnReward(finished, level: 45));
    }
}
