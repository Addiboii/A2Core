using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// main-story hero chain on SharedWorld (build 25767555, level cap 45) for unlock levels 10–45.
/// Step log covers map / instance / cutscene objectives, rewards, and exp per quest.
/// </summary>
public sealed class PlaythroughCampaignTests(ITestOutputHelper output)
{
    const long Now = 1_760_003_000_000;

    [Theory]
    [InlineData(1, 78, "HQ1103010")]
    [InlineData(2, 77, "HQ2103010")]
    public async Task Main_story_campaign_levels_10_to_45_both_factions(int race, int expectedSegmentQuests, string capGateCode)
    {
        var report = await CampaignPlaythroughHelpers.RunMainStoryCampaignAsync(race, Now + race * 15_000);
        output.WriteLine($"race={report.Race} segment={report.SegmentQuestsRewarded} level={report.FinalLevel} last={report.LastSegmentCode}");
        if (report.CapGateQuestCode is not null)
            output.WriteLine($"cap gate (expected unavailable): {report.CapGateQuestCode}");
        foreach (var step in report.Steps.Take(5))
            output.WriteLine($"  {step.Code} map={step.MapId} lvl={step.LevelAfter} exp+={step.ExpReward} kinds={string.Join(',', step.ObjectiveKinds.Take(4))}");
        output.WriteLine($"  … {report.Steps.Count} quests total in segment");
        foreach (var gap in report.SegmentDataGaps.Take(6))
            output.WriteLine($"gap: {gap}");
        var devFallbacks = report.Steps.Where(s => s.DevQuestCompleteFallback).ToList();
        if (devFallbacks.Count > 0)
        {
            output.WriteLine($"dev QuestComplete fallbacks ({devFallbacks.Count}): {string.Join(", ", devFallbacks.Select(s => s.Code).Take(8))}");
            foreach (var fb in devFallbacks.Take(12))
                output.WriteLine($"  {fb.Code}: {fb.DevCompleteReason}");
        }

        Assert.Equal(45, report.FinalLevel);
        Assert.Equal(expectedSegmentQuests, report.SegmentQuestsRewarded);
        Assert.Empty(report.SegmentDataGaps);
        Assert.Null(report.CapGateQuestCode);
        Assert.Equal(race == 1 ? "HQ1102995" : "HQ2102810", report.LastSegmentCode);
        _ = capGateCode; // documented expected post-segment gate (unavailable above cap)
        Assert.True(report.Steps.All(s => s.UnlockLevel is >= 10 and <= 45));
        Assert.Empty(devFallbacks);
    }
}
