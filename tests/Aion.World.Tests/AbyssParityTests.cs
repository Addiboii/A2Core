using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AbyssParityTests
{
    [Fact]
    public void Embedded_abyss_json_has_global_season_and_maps()
    {
        var abyss = AbyssTables.Embedded;
        Assert.Equal("G1", abyss.SeasonKey);
        Assert.True(abyss.IsAbyssMap(20));
        Assert.True(abyss.IsAbyssMap(22));
        Assert.Equal(930100003, abyss.ItemIds.AbyssPoint);
        Assert.Equal(930100019, abyss.ItemIds.EntryStone);
    }

    [Fact]
    public void Rank_grade_follows_point_ladder()
    {
        var abyss = AbyssTables.Embedded;
        Assert.Equal(1, abyss.GradeOf(0));
        Assert.Equal(2, abyss.GradeOf(500));
        Assert.Equal(3, abyss.GradeOf(1500));
        Assert.Equal(8, abyss.GradeOf(40000));
    }

    [Fact]
    public void Kill_rewards_scale_with_victim_grade()
    {
        var abyss = AbyssTables.Embedded;
        Assert.True(abyss.KillAp(8) > abyss.KillAp(1));
        Assert.True(abyss.KillRankPoints(5) > abyss.KillRankPoints(1));
    }

    [Fact]
    public void Airspace_battle_schedule_lists_reshanta_fortresses()
    {
        var abyss = AbyssTables.Embedded;
        Assert.True(abyss.Airspaces.Count >= 3);
        Assert.Contains(abyss.Airspaces, a => a.Id == 101 && a.MapId == 20);
        Assert.Single(abyss.AirspaceBattles);
        Assert.Contains(abyss.AirspaceBattles[0].AirspaceIds, id => id == 101);
    }

    [Fact]
    public void Embedded_abyss_json_lists_artifact_portals_and_rifts()
    {
        var abyss = AbyssTables.Embedded;
        Assert.True(abyss.Artifacts.Count >= 6);
        Assert.Contains(abyss.Artifacts, a => a.MapId == 20 && a.OccupyingRace == 1);
        Assert.True(abyss.Rifts.Count >= 1);
        Assert.Contains(abyss.Rifts, r => r.Wave >= 1 && r.EnvObjId > 0);
    }
}
