using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillResourceThresholdTests
{
    [Theory]
    [InlineData("HP_Less", 5000, 100, 10_000, 0, 0, true)]
    [InlineData("HP_Less", 50, 100, 10_000, 0, 0, false)]
    [InlineData("HPRatio_More", 5000, 6000, 10_000, 0, 0, true)]
    [InlineData("MP_More", 100, 0, 0, 5000, 0, true)]
    [InlineData("Unknown", 0, 1, 1, 1, 1, false)]
    public void Met_evaluates_skill_dat_thresholds(string type, int value, int hp, int hpMax, int mp, int mpMax, bool expected)
    {
        var req = new SkillResourceCondition(type, value);
        Assert.Equal(expected, SkillResourceThreshold.Met(req, hp, hpMax, mp, mpMax));
    }

    [Theory]
    [InlineData("AP_More", 500, 600, 1000, true)]
    [InlineData("AP_Less", 500, 600, 1000, false)]
    public void Ap_thresholds_use_ap_bar(string type, int value, int ap, int apMax, bool expected)
    {
        var req = new SkillResourceCondition(type, value);
        Assert.Equal(expected, SkillResourceThreshold.Met(req, 1, 1, 1, 1, ap, apMax));
    }
}
