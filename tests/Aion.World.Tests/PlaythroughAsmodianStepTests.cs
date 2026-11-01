using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Strict packet steps on Ishalgen / early Altgard (Dark hero HQ21xxxxxx).</summary>
public sealed class PlaythroughAsmodianStepTests
{
    const long Now = 1_760_002_500_000;

    [Fact]
    public async Task HQ2101080_prison_memo_after_full_starter_through_1070()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 1);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 2);
        var gaps = new List<string>();
        var fbs = new List<string>();
        var t = Now;
        foreach (var code in new[] { "HQ2101010", "HQ2101020", "HQ2101030", "HQ2101040", "HQ2101050", "HQ2101060", "HQ2101070" })
        {
            var played = await CampaignPlaythroughHelpers.PlayQuestToRewardForStarterAsync(session, store, code, 2, t, gaps, fbs);
            Assert.True(played.Ok, $"{code}: {played.Reason}");
            t = played.Time;
        }

        var strict = await CampaignPlaythroughHelpers.PlayQuestStrictAsync(session, store, "HQ2101080", 2, t, gaps, fbs);
        Assert.True(strict.Ok, strict.Reason);
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("HQ2101080")!.Status);
    }
}
