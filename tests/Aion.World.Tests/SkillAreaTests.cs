using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>SkillEffectFilter shapes (tools/data/skill-areas.json) and the 0x3803 projectile candidate.</summary>
public sealed class SkillAreaTests
{
    [Fact]
    public void Tempest_shot_hits_four_within_four_metres_of_the_target()
    {
        var area = SkillAreas.For(14340000)!;
        Assert.True(area.IsArea);
        Assert.True(area.OnTarget);
        Assert.Equal(4, area.Max);
        var centre = new Vector3d(1000, 0, 0);
        Assert.True(area.Contains(centre, 0, new Vector3d(1350, 0, 0)));
        Assert.False(area.Contains(centre, 0, new Vector3d(1500, 0, 0)));
        Assert.Equal(140, area.FlightMs(700));   // Speed 5000 cm/s
    }

    [Fact]
    public void Self_cast_rectangle_faces_the_cast_yaw()
    {
        var area = SkillAreas.For(13010000)!;   // Quick Slice: Rectangle 590 x 500 starting 100 behind
        Assert.False(area.OnTarget);
        var at = new Vector3d(0, 0, 0);
        Assert.True(area.Contains(at, 0, new Vector3d(300, 100, 0)));
        Assert.False(area.Contains(at, 180, new Vector3d(300, 100, 0)));
    }

    [Fact]
    public void Single_target_shot_has_projectile_but_no_area()
    {
        var area = SkillAreas.For(14020000)!;   // Snipe
        Assert.False(area.IsArea);
        Assert.Equal(140200001, area.Projectile);
        var wire = PlayerPackets.SkillFireProjectile(100001, 2003, 14020000, 5, 1, new Vector3d(1, 2, 3), 350, 1);
        Assert.Equal(0x3803, wire[0] | wire[1] << 8);
    }
}
