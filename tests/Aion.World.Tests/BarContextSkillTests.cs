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
    public void Assassin_wire_slot_3_context_includes_flash_slice()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(13_060_000);
        Assert.Equal(13_060_000, ctx[0]);
        Assert.Equal(13_050_000, ctx[1]);
    }

    [Fact]
    public void Gladiator_wire_slot_3_context_includes_crushing_wave()
    {
        var ctx = GameData.Embedded.DefaultContextSkills(11_290_000);
        Assert.Equal(11_290_000, ctx[0]);
        Assert.Equal(11_050_000, ctx[1]);
    }

    [Theory]
    [InlineData(1, 11_290_000, 11_050_000)]
    [InlineData(3, 13_060_000, 13_050_000)]
    [InlineData(2, 12_100_000, 12_350_000)]
    [InlineData(5, 14_110_000, 14_170_000)]
    public void Every_class_wire_slot_3_default_has_two_skill_context(int creation, int primary, int alternate)
    {
        var data = GameData.Embedded;
        var profile = data.Profile(creation, 1)!;
        var barSkill = profile.DefaultBar().Single(s => s.Slot == 3).SkillId;
        Assert.Equal(primary, barSkill);
        var ctx = data.DefaultContextSkills(barSkill);
        Assert.Equal(primary, ctx[0]);
        Assert.Equal(alternate, ctx[1]);
    }

    [Fact]
    public async Task Assassin_hotbar_slot_3_sends_both_context_ids_in_0x5600()
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
        Assert.Equal([13_060_000, 13_050_000, 0, 0], ctx);
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
