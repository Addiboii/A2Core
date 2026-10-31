using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>Elyos HQ1xxxxxx main story from quest-reference.json + hero-chains.json (SharedWorld, strict packets).</summary>
public sealed class PlaythroughElyosMainStoryTests(ITestOutputHelper output)
{
    const long Now = 1_760_050_100_000;

    [Fact]
    public async Task Elyos_main_story_level_1_through_15()
    {
        var terminal = CampaignPlaythroughHelpers.ElyosMainStoryLevel15Terminal;
        var report = await CampaignPlaythroughHelpers.RunElyosMainStoryThroughAsync(terminal, Now);
        WriteReport(report);
        Assert.Null(report.BlockedQuestCode);
        Assert.Equal(terminal, report.LastRewardedCode);
        Assert.Empty(report.DataGaps);
        Assert.Empty(report.PacketFallbacks);
    }

    [Fact]
    public async Task Elyos_main_story_level_1_through_24()
    {
        var terminal = CampaignPlaythroughHelpers.ElyosMainStoryLevel24Terminal;
        var report = await CampaignPlaythroughHelpers.RunElyosMainStoryThroughAsync(terminal, Now + 600_000);
        WriteReport(report);
        Assert.Null(report.BlockedQuestCode);
        Assert.Equal(terminal, report.LastRewardedCode);
        Assert.Empty(report.DataGaps);
        Assert.Empty(report.PacketFallbacks);
    }

    [Fact(Skip = "Segment 3: HQ1102230 KillNpc (Gravi) — harness/server neutral kill credit TBD")]
    public async Task Elyos_main_story_level_1_through_45()
    {
        var terminal = CampaignPlaythroughHelpers.ElyosMainStoryCap45Terminal;
        var report = await CampaignPlaythroughHelpers.RunElyosMainStoryThroughAsync(terminal, Now + 900_000);
        WriteReport(report);
        Assert.Null(report.BlockedQuestCode);
        Assert.Equal(terminal, report.LastRewardedCode);
    }

    void WriteReport(CampaignPlaythroughHelpers.PlaythroughReport report)
    {
        output.WriteLine($"level={report.FinalLevel} rewarded={report.QuestsRewarded} last={report.LastRewardedCode}");
        if (report.BlockedQuestCode is not null)
            output.WriteLine($"blocked={report.BlockedQuestCode}: {report.BlockReason}");
        foreach (var gap in report.DataGaps)
            output.WriteLine($"data: {gap}");
        foreach (var fb in report.PacketFallbacks)
            output.WriteLine($"fallback: {fb}");
    }
}
