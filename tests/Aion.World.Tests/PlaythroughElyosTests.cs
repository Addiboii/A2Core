using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// Elyos starter zone (Poeta + Verteron level 1–10) end-to-end on SharedWorld (build pin 25767555, cap 45).
/// </summary>
public sealed class PlaythroughElyosTests(ITestOutputHelper output)
{
    const long Now = 1_760_050_000_000;

    [Fact]
    public async Task Elyos_starter_main_and_side_quests_level_1_through_10()
    {
        var report = await CampaignPlaythroughHelpers.RunElyosStarterPlaythroughAsync(Now);
        output.WriteLine($"level={report.FinalLevel} rewarded={report.QuestsRewarded} last={report.LastRewardedCode}");
        if (report.BlockedQuestCode is not null)
            output.WriteLine($"blocked={report.BlockedQuestCode}: {report.BlockReason}");
        foreach (var side in report.SideQuestAttempts)
            output.WriteLine($"side: {side}");
        foreach (var fb in report.PacketFallbacks)
            output.WriteLine($"fallback: {fb}");
        foreach (var gap in report.DataGaps)
            output.WriteLine($"data: {gap}");

        Assert.Null(report.BlockedQuestCode);
        Assert.Equal(CampaignPlaythroughHelpers.ElyosStarterTerminalQuest, report.LastRewardedCode);
        Assert.True(report.QuestsRewarded >= 12, $"expected full hero segment through {CampaignPlaythroughHelpers.ElyosStarterTerminalQuest}, got {report.QuestsRewarded}");
        Assert.True(report.FinalLevel is >= 8 and <= 12, $"starter rewards should land near level 10 (got {report.FinalLevel})");
        Assert.Contains(report.SideQuestAttempts, s => s.StartsWith("AQ1602000:", StringComparison.Ordinal) && s.Contains("rewarded", StringComparison.Ordinal));
        Assert.Empty(report.DataGaps);
        Assert.Empty(report.PacketFallbacks);
    }
}
