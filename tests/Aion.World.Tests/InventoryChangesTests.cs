using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class InventoryChangesTests
{
    const long Now = 1_793_368_800_000;
    const int GlobalAttendanceGroup = 510_035;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    static int ModificationCount(byte[] packet)
    {
        Assert.Equal(0x561b, Op(packet));
        var r = new PacketReader(packet);
        r.U16();
        return (int)r.Varint();
    }

    [Fact]
    public async Task Reload_without_stored_changes_sends_no_561b()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var empty = await session.InventoryChangesAsync(default);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Reload_with_new_equip_detail_instances_does_not_mark_every_stack_updated()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session.InventoryChangesAsync(default);
        var again = await session.InventoryChangesAsync(default);
        Assert.DoesNotContain(again, p => Op(p) == 0x561b);
    }

    [Fact]
    public async Task Batch_claim_sends_one_inventory_diff_with_increase_reason_on_grants()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);

        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(GlobalAttendanceGroup).U8(1).ToArray(),
            Now + 101);

        var mods = batch.Where(p => Op(p) == 0x561b).ToList();
        Assert.Single(mods);
        Assert.True(ModificationCount(mods[0]) >= 1);
        Assert.True(mods[0].Contains((byte)2), "achievement grants use EIncreaseReason kAchievement (2) on changed stacks");
    }
}
