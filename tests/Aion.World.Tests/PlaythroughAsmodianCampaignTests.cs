using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>Dark main story (HQ21xxxxxx) strict SharedWorld playthrough by recommended-level segment (cap 45).</summary>
public sealed class PlaythroughAsmodianCampaignTests(ITestOutputHelper output)
{
    const int RaceDark = 2;

    [Theory]
    [InlineData(1, 10, "HQ2102020")]
    public Task Main_story_strict_segment_starter(int minLevel, int maxLevel, string expectedLastCode) =>
        Main_story_strict_segment_core(minLevel, maxLevel, expectedLastCode);

    [Theory(Skip = "HQ2102065 dream crossroad / HQ2102070 auto-acquire — segment harness follow-up")]
    [InlineData(11, 20, "HQ2102150")]
    [InlineData(21, 30, "HQ2102280")]
    [InlineData(31, 40, "HQ2102615")]
    [InlineData(41, 45, "HQ2102810")]
    public Task Main_story_strict_segment_late(int minLevel, int maxLevel, string expectedLastCode) =>
        Main_story_strict_segment_core(minLevel, maxLevel, expectedLastCode);

    async Task Main_story_strict_segment_core(int minLevel, int maxLevel, string expectedLastCode)
    {
        var report = await CampaignPlaythroughHelpers.RunMainStoryCampaignStrictAsync(
            RaceDark, NowForSegment(minLevel), minLevel, maxLevel);
        output.WriteLine($"segment {minLevel}-{maxLevel}: quests={report.SegmentQuestsRewarded} lvl={report.FinalLevel} last={report.LastSegmentCode}");
        if (report.CapGateQuestCode is not null)
            output.WriteLine($"blocked: {report.CapGateQuestCode}");
        foreach (var gap in report.SegmentDataGaps.Take(8))
            output.WriteLine($"gap: {gap}");
        var dev = report.Steps.Where(s => s.DevQuestCompleteFallback).Select(s => s.Code).ToList();
        if (dev.Count > 0)
            output.WriteLine($"dev fallbacks: {string.Join(", ", dev.Take(10))}");

        Assert.Null(report.CapGateQuestCode);
        Assert.Equal(expectedLastCode, report.LastSegmentCode);
        Assert.Empty(report.SegmentDataGaps);
        Assert.Empty(report.SegmentFallbacks);
        Assert.All(report.Steps, s => Assert.InRange(s.UnlockLevel, minLevel, maxLevel));
        Assert.Empty(dev);
    }

    static long NowForSegment(int minLevel) => 1_760_010_000_000L + minLevel * 90_000L;
}
