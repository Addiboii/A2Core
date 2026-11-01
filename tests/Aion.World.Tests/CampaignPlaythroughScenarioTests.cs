using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// End-to-end hero campaign from level 1: both factions, packet-level objectives where modeled, full chain as far as data allows.
/// </summary>
public sealed class CampaignPlaythroughScenarioTests(ITestOutputHelper output)
{
    const long Now = 1_760_001_000_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Hero_campaign_playthrough_from_level_1(int race)
    {
        var report = await CampaignPlaythroughHelpers.RunHeroCampaignAsync(race, Now + race * 10_000);
        output.WriteLine($"race={report.Race} level={report.FinalLevel} rewarded={report.QuestsRewarded} last={report.LastRewardedCode}");
        if (report.BlockedQuestCode is not null)
            output.WriteLine($"blocked={report.BlockedQuestCode}: {report.BlockReason}");
        foreach (var gap in report.DataGaps.Take(8))
            output.WriteLine($"data: {gap}");
        foreach (var fb in report.PacketFallbacks.Take(8))
            output.WriteLine($"fallback: {fb}");
        foreach (var side in report.SideQuestAttempts)
            output.WriteLine($"side: {side}");

        Assert.True(report.QuestsRewarded >= 80,
            $"hero chain should pass Verteron/Morheim core (got {report.QuestsRewarded}, blocked {report.BlockedQuestCode})");
        Assert.True(report.FinalLevel >= 10, "quest rewards should raise level well above 1");
        Assert.True(report.PacketFallbacks.Count < 200,
            "unexpected volume of step fallbacks: " + string.Join(", ", report.PacketFallbacks.Take(8)));

        Assert.Equal(45, report.FinalLevel);
        if (race == 1)
        {
            Assert.Equal("HQ1103010", report.BlockedQuestCode);
            Assert.True(report.LevelBeforeSideQuests == report.LevelAfterSideQuests,
                "Side AQ1602020 is ascension (0 ExpReward in export); gate is quest UnlockLevel 46 / unavailable above cap");
            Assert.Contains(report.SideQuestAttempts, s => s.StartsWith("AQ1602020:", StringComparison.Ordinal));
        }
        else
        {
            Assert.True(report.QuestsRewarded >= 80, "Dark hero chain through Morheim field content up to cap-gated quests");
            Assert.Equal("HQ2103010", report.BlockedQuestCode);
            Assert.True(report.LevelBeforeSideQuests == report.LevelAfterSideQuests,
                "Side AQ2602040 is ascension (0 ExpReward in export); cap stops at 45 before lvl-51 hero gate");
            Assert.Contains(report.SideQuestAttempts, s => s.StartsWith("AQ2602040:", StringComparison.Ordinal));
        }
    }
}
