using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// After the hero chain to level cap, attempts every quest at UnlockLevel &lt;= 45 via packets and aggregates parity by category.
/// </summary>
public sealed class CampaignPlaythroughParityTests(ITestOutputHelper output)
{
    const long Now = 1_760_002_000_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task All_cap_quests_attempted_by_category(int race)
    {
        var summary = await CampaignPlaythroughHelpers.RunCapQuestParityAsync(race, Now + race * 20_000);
        foreach (var row in summary.Rows)
            output.WriteLine($"{row.Category}: completed={row.Completed} blocked={row.Blocked} top={row.TopBlockReason}");
        output.WriteLine($"hero rewarded={summary.HeroRewarded} unifiedScore={summary.UnifiedObjectiveScore}");

        Assert.True(summary.HeroRewarded >= 80);
        Assert.Equal(45, summary.FinalLevel);
        Assert.True(summary.Rows.First(r => r.Category == "campaign").Completed >= 80);
        Assert.True(summary.UnifiedObjectiveScore >= 1400,
            $"ascension gates need unified score ~1490 (got {summary.UnifiedObjectiveScore})");

        var ascension = summary.Rows.First(r => r.Category == "ascension");
        Assert.True(ascension.Completed >= 1,
            $"expected at least one ascension rewarded (blocked sample: {ascension.TopBlockReason})");

        var side = summary.Rows.First(r => r.Category == "side");
        var (completable, unavailableGate) = CampaignPlaythroughHelpers.SideQuestCapCounts(race);
        Assert.Equal(CampaignPlaythroughHelpers.SideCompletableAtCapPerFaction, completable);
        Assert.Equal(CampaignPlaythroughHelpers.SideUnavailableHeroGatePerFaction, unavailableGate);
        Assert.Equal(completable, side.Completed);
        Assert.Equal(0, side.Blocked);
    }
}
