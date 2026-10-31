using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillApResourceTests
{
    [Theory]
    [InlineData("AP_More", 500, 1000, true)]
    [InlineData("AP_Less", 500, 1000, false)]
    [InlineData("APRatio_More", 6000, 10_000, true)]
    public void Ap_thresholds_use_ap_bar(string type, int value, int ap, bool expected)
    {
        var req = new SkillResourceCondition(type, value);
        Assert.Equal(expected, SkillResourceThreshold.Met(req, 1, 1, 1, 1, ap, 10_000));
    }

    [Fact]
    public void Ap_max_zero_skips_ap_ratio_thresholds()
    {
        var req = new SkillResourceCondition("APRatio_More", 1);
        Assert.False(SkillResourceThreshold.Met(req, 1, 1, 1, 1, 0, 0));
    }
}
