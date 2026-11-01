using System.Buffers.Binary;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>0x3633 tail: conqueror, rift/succession, then two i64 combat-power fields (usmap).</summary>
public sealed class MyCharAppearCombatPowerWireTests
{
    static readonly long Now = 1_700_000_000_000;

    [Theory]
    [InlineData(2882)]
    [InlineData(7301)]
    public void Appear_tail_carries_current_and_highest_combat_power_i64_pair(long cp)
    {
        var packet = GamePackets.MyCharAppear(new MyCharacterAppear { Name = "Hero", CombatPower = cp, CharLevel = 18 });
        var current = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(^20));
        var highest = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(^12));
        Assert.Equal(cp, current);
        Assert.Equal(cp, highest);
    }

    [Fact]
    public async Task Level_18_world_entry_appear_embeds_matching_combat_power_at_tail()
    {
        var store = new FakeCharacterStore { Progress = (18, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 18);
        var enter = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        var appear = enter.First(b => b.Length > 2 && b[0] == 0x33 && b[1] == 0x36);
        var cp = BinaryPrimitives.ReadInt64LittleEndian(appear.AsSpan(^20));
        Assert.InRange(cp, 7000, 8500);
        Assert.Equal(session.CombatPower, cp);
    }
}
