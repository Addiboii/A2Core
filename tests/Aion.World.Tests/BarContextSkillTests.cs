using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// PCContextSkillSlot ContextChangeSkillList on bar slots (cooldown rotation, not ChainSkill*).
/// </summary>
public sealed class BarContextSkillTests
{
    const long Now = 1_760_003_000_000;

    [Fact]
    public void Assassin_ambush_and_flash_slice_are_separate_bar_slots_not_one_context_stack()
    {
        var data = GameData.Embedded;
        Assert.Equal([13_060_000, 0, 0, 0], data.DefaultContextSkills(13_060_000));
        var profile = data.Profile(3, 1)!;
        Assert.Equal(13_060_000, profile.DefaultBar().Single(s => s.Slot == 3).SkillId);
        Assert.Equal(13_050_000, profile.DefaultBar().Single(s => s.Slot == 4).SkillId);
    }

    [Fact]
    public void Mocking_blade_has_no_context_change_stack_crushing_wave_is_slot_four()
    {
        var data = GameData.Embedded;
        var ctx = data.DefaultContextSkills(11_290_000);
        Assert.Equal([11_290_000, 0, 0, 0], ctx);
        var profile = data.Profile(1, 1)!;
        Assert.Equal(11_290_000, profile.DefaultBar().Single(s => s.Slot == 3).SkillId);
        Assert.Equal(11_050_000, profile.DefaultBar().Single(s => s.Slot == 4).SkillId);
    }

    [Theory]
    [InlineData(1, 11_360_000, 11_300_000)]
    [InlineData(5, 14_080_000, 14_050_000)]
    public void Classes_with_pc_context_export_still_rotate_alternate_skills(int creation, int primary, int alternate)
    {
        var data = GameData.Embedded;
        _ = data.Profile(creation, 1)!;
        var ctx = data.DefaultContextSkills(primary);
        Assert.Equal(primary, ctx[0]);
        Assert.Equal(alternate, ctx[1]);
    }

    [Fact]
    public async Task Assassin_hotbar_slot_3_has_no_wire_slot_four_alternate_in_context()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(42)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("BarCtx").U8(1).U32(3).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        var entry = await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now));
        var bar = entry.First(p => (p[0] | p[1] << 8) == 0x5600);
        var ctx = ContextAtSlot(bar, 3);
        Assert.Equal([13_060_000, 0, 0, 0], ctx);
    }

    static int[] ContextAtSlot(byte[] bar, int slot)
    {
        for (var i = 0; i < bar[2]; i++)
        {
            var off = 3 + i * 42;
            if (bar[off + 1] != slot) continue;
            Assert.Equal((byte)4, bar[off + 19]);
            return [.. Enumerable.Range(0, 4).Select(j => BitConverter.ToInt32(bar, off + 20 + j * 4))];
        }
        throw new InvalidOperationException($"slot {slot} missing from hotbar");
    }
}
