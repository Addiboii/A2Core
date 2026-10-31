using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Which SkillProjectile.dat row each ranged cast fires (tools/data/skill-areas.json, class-skills.json effect rows).</summary>
public sealed class RangedProjectileRowTests
{
    static readonly GameData Data = GameData.Embedded;

    [Theory]
    [InlineData(14010000, new[] { 0, 0 })]            // Deadshot 1401000011 / 1401000012: one shot, two effects
    [InlineData(14030000, new[] { 0, 1, 2 })]         // Rapid Fire 1403000011 x 3
    [InlineData(14340000, new[] { 0, 1 })]            // Tempest Shot 1434000011 / 1434000021
    [InlineData(15040000, new[] { 0, 1, 2, 3, 4 })]   // Firestorm, five groups
    [InlineData(17350000, new[] { 0, 0, 0, 0 })]      // Condemnation 1735000011..14
    public void Timed_rows_follow_the_effect_numbering(int skill, int[] rows) =>
        Assert.Equal(rows, SkillTiming.TimedRows(Data.Skill(skill)!));

    [Theory]
    [InlineData(14020010, 140200001)]   // Snipe variant: the family's row (its effect row is 1402000011)
    [InlineData(14130040, 141300001)]   // Snare Shot variant
    [InlineData(14030000, 140300001)]   // Rapid Fire chain step (official)
    [InlineData(14030010, 140300001)]   // Rapid Fire variant
    [InlineData(14040010, 140400001)]   // Spiral Arrow variant
    [InlineData(14340000, 143400001)]   // exported root
    [InlineData(16340000, 163400001)]   // Spiritmaster Rapid Scattershot
    public void Casts_fire_their_family_rows(int skill, int projectile) =>
        Assert.Equal(projectile, GameSession.CastArea(Data, skill)!.ProjectileFor(0));

    [Theory]
    [InlineData(16010000)]   // Cold Shock: the export has no projectile row
    [InlineData(16020000)]   // its chain step Vacuum Explosion
    [InlineData(18010000)]   // Chanter Onslaught
    public void Spells_without_a_projectile_row_fire_none(int skill) =>
        Assert.True(GameSession.CastArea(Data, skill) is not { Projectile: > 0 });

    [Fact]
    public void Exported_row_lists_win_and_a_row_without_one_fires_nothing()
    {
        var area = new SkillAreas.Area(null, null, 1, false, 143400001, 5000, [143400001, 0]);
        Assert.Equal(143400001, area.ProjectileFor(0));
        Assert.Equal(0, area.ProjectileFor(1));
        Assert.Equal(143400002, (area with { Projectiles = null }).ProjectileFor(1));
    }
}
