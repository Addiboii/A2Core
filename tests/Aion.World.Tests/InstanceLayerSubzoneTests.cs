using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Map-event EnterSubZone on instance layers (HQ2102030 Crash Site Rendezvous, 111025).</summary>
public sealed class InstanceLayerSubzoneTests
{
    [Fact]
    public void InstanceLayerSubzoneWithoutVolumeCountsOnItsLayer()
    {
        var at = new Vector3d(-78003.66, -85761.71, 29044.56);
        Assert.True(Subzones.InMapEventSubzone(111025, "InstanceLayer_World_D_A_Layer_HQ001_Subzone", at));
        Assert.False(Subzones.InMapEventSubzone(111025, "SomeField_Subzone", at));
    }

    [Fact]
    public void ExportedSubzonesStillUseTheirPolygon()
    {
        var area = Subzones.OnMap(1110)[0];
        Assert.Equal(Subzones.InSubzone(1110, area.Name, new Vector3d(0, 0, 0)),
            Subzones.InMapEventSubzone(1110, area.Name, new Vector3d(0, 0, 0)));
    }
}