using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Combat power on 0x3633 / 0x3656 must meet client EnvObj InteractCondition gates (Insufficient Combat Power otherwise).
/// </summary>
public sealed class CombatPowerAppearTests
{
    static readonly long Now = 1_700_000_000_000;

    [Fact]
    public async Task Level_1_appear_matches_observed_baseline_order_of_magnitude()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 1);
        Assert.InRange(session.CombatPower, 2880, 3200);
    }

    [Fact]
    public async Task Level_16_hero_has_combat_power_above_collapsed_barrier_layer_gates()
    {
        var store = new FakeCharacterStore { Progress = (16, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 16);
        // Client-side Etheria Rift / fissure objects compare against _combat_power from appear; equip-only sums were ~30-50.
        Assert.True(session.CombatPower >= 4000, $"expected CP >= 4000 at level 16, got {session.CombatPower}");
    }
}
