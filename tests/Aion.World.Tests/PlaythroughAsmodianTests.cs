using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// Ishalgen starter (levels 1–10, build 25767555) — hero chain + side quests via SharedWorld packets.
/// </summary>
public sealed class PlaythroughAsmodianTests(ITestOutputHelper output)
{
    const long Now = 1_760_002_000_000;
    const int RaceDark = 2;

    [Fact]
    public async Task Asmodian_starter_zone_levels_1_through_10_packet_playthrough()
    {
        var report = await StarterPlaythroughHelpers.RunStarterZoneAsync(RaceDark, maxLevel: 10, Now);
        output.WriteLine($"hero rewarded={report.HeroQuestsRewarded}/{report.HeroQuestsPlanned} level={report.FinalLevel}");
        output.WriteLine($"side rewarded={report.SideQuestsRewarded}/{report.SideQuestsPlanned}");
        foreach (var row in report.QuestResults)
            output.WriteLine(row);
        if (report.BlockedQuestCode is not null)
            output.WriteLine($"BLOCKED {report.BlockedQuestCode}: {report.BlockReason}");
        foreach (var gap in report.DataGaps.Take(12))
            output.WriteLine($"data: {gap}");
        foreach (var fb in report.PacketFallbacks.Take(12))
            output.WriteLine($"fallback: {fb}");

        Assert.Null(report.BlockedQuestCode);
        Assert.Equal(report.HeroQuestsPlanned, report.HeroQuestsRewarded);
        Assert.Equal(report.SideQuestsPlanned, report.SideQuestsRewarded);
        Assert.True(report.FinalLevel >= 10,
            $"hero line should reach at least level 10 before Altgard side content (got {report.FinalLevel})");
        Assert.DoesNotContain(report.DataGaps, g => g.Contains("CollectFakeItem", StringComparison.Ordinal));
        Assert.Empty(report.PacketFallbacks);
    }
}
