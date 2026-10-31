using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

/// <summary>
/// side, region, and ascension at level cap 45 on SharedWorld (build 25767555).
/// </summary>
public sealed class PlaythroughSideQuestTests(ITestOutputHelper output)
{
    const long Now = 1_760_003_000_000;

    static readonly string[] SideRegionAscension = ["side", "region", "ascension"];

    static readonly IReadOnlyDictionary<int, string[]> RepresentativeLight =
        new Dictionary<int, string[]>
        {
            [1] =
            [
                "DQ1202010", "DQ1202011", "DQ1202012", "DQ1202013", "DQ1202014", "DQ1202015", "DQ1202016", "DQ1202017",
                "EQ1803011", "EQ1803012", "EQ1803013",
                "AQ1602000", "AQ1602010", "AQ1602020", "AQ1602030", "AQ1602040",
            ],
            [2] =
            [
                "DQ2202010", "DQ2202011", "DQ2202012", "DQ2202013", "DQ2202014", "DQ2202015", "DQ2202016", "DQ2202017",
                "EQ1803011", "EQ1803012", "EQ1803013",
                "AQ2602000", "AQ2602010", "AQ2602020", "AQ2602030", "AQ2602040",
            ],
        };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Side_region_ascension_representative_packet_playthrough(int race)
    {
        var codes = RepresentativeLight[race];
        var (rewarded, failures) = await CampaignPlaythroughHelpers.RunQuestCodesPlaythroughAsync(race, codes, Now + race * 50_000);
        output.WriteLine($"race={race} representative rewarded={rewarded}/{codes.Length}");
        foreach (var f in failures)
            output.WriteLine($"  fail: {f}");

        Assert.Empty(failures);
        Assert.Equal(codes.Length, rewarded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "LongPlaythrough")]
    public async Task Side_region_ascension_full_objectives_and_reward(int race)
    {
        var summary = await CampaignPlaythroughHelpers.RunCapCategoriesPlaythroughAsync(race, SideRegionAscension, Now + race * 80_000);
        foreach (var row in summary.Categories)
        {
            output.WriteLine($"{row.Category} race={row.Race} rewarded={row.Rewarded}/{row.Attempted}");
            foreach (var f in row.Failures.Take(10))
                output.WriteLine($"  fail: {f}");
        }

        var side = summary.Categories.First(c => c.Category == "side");
        Assert.Equal(CampaignPlaythroughHelpers.SideCompletableAtCapPerFaction, side.Attempted);
        Assert.Equal(CampaignPlaythroughHelpers.SideCompletableAtCapPerFaction, side.Rewarded);

        var region = summary.Categories.First(c => c.Category == "region");
        Assert.Equal(204, region.Attempted);
        Assert.Equal(204, region.Rewarded);

        var ascension = summary.Categories.First(c => c.Category == "ascension");
        Assert.Equal(5, ascension.Attempted);
        Assert.Equal(5, ascension.Rewarded);
    }
}
