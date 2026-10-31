using Aion.Host;
using Aion.Host.Debug;
using Xunit;

namespace Aion.Host.Tests;

public sealed class WorldOptionsTests
{
    [Fact]
    public void AskNpc_fallback_defaults_on_regardless_of_debug()
    {
        var world = new WorldOptions();
        Assert.True(world.ResolveMapEventAskNpcProximityFallback(new DebugOptions { Enabled = true }));
        Assert.True(world.ResolveMapEventAskNpcProximityFallback(new DebugOptions { Enabled = false }));
    }

    [Fact]
    public void AskNpc_fallback_explicit_config_overrides_debug()
    {
        var off = new WorldOptions { MapEventAskNpcProximityFallback = false };
        Assert.False(off.ResolveMapEventAskNpcProximityFallback(new DebugOptions { Enabled = true }));
        var on = new WorldOptions { MapEventAskNpcProximityFallback = true };
        Assert.True(on.ResolveMapEventAskNpcProximityFallback(new DebugOptions { Enabled = false }));
    }
}
