using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Enchant tables vs client export (tools/data/enchant.json) and published Global curves.</summary>
public sealed class EnchantParityTests
{
    [Fact]
    public void Unique_weapon_plus10_to_11_matches_global_table()
    {
        var step = EnchantTables.Embedded.Next(110120003, 10);
        Assert.NotNull(step);
        Assert.Equal(6500, step!.Success);
        Assert.Equal(500, step.FailCorrection);
        Assert.Equal(0, step.FailPenalty);
    }

    [Fact]
    public void Unique_weapon_safe_band_is_hundred_percent_to_plus10()
    {
        for (var level = 0; level < 10; level++)
        {
            var step = EnchantTables.Embedded.Next(110120003, level);
            Assert.NotNull(step);
            Assert.Equal(10_000, step!.Success);
            Assert.Equal(0, step.FailPenalty);
        }
    }

    [Fact]
    public void Rune_fail_step_uses_negative_penalty_in_dat()
    {
        var step = EnchantTables.Embedded.Next(310900001, 1);
        Assert.NotNull(step);
        Assert.Equal(8000, step!.Success);
        Assert.Equal(0, step.FailCorrection);
        Assert.Equal(-1, step.FailPenalty);
    }
}
