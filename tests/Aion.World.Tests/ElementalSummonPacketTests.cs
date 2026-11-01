using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>0xe239/0xe23a: mask 0x01, group u8, spirit key varint (official 2026-10-31; no lifetime on wire).</summary>
public sealed class ElementalSummonPacketTests
{
    static int NpcAppearEntityKey(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        return (int)reader.Varint();
    }

    public static (byte Group, int SpiritKey) ReadElementalSummonWire(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(GamePackets.ElementalSummonWireMask, reader.U8());
        return ((byte)reader.U8(), (int)reader.Varint());
    }

    [Fact]
    public void Activate_body_is_mask_group_and_spirit_key_varint()
    {
        var expectedBody = new byte[] { 0x01, 0x01, 0x2a };
        var packet = GamePackets.ElementalSummonActivate(1, 42);
        Assert.Equal(0xe239, GameSessionTestHelpers.Op(packet));
        Assert.Equal(expectedBody, packet.AsSpan(2).ToArray());
    }

    [Fact]
    public void Deactivate_body_is_mask_group_and_spirit_key_varint()
    {
        var expectedBody = new byte[] { 0x01, 0x02, 0x2a };
        var packet = GamePackets.ElementalSummonDeactivate(2, 42);
        Assert.Equal(0xe23a, GameSessionTestHelpers.Op(packet));
        Assert.Equal(expectedBody, packet.AsSpan(2).ToArray());
    }

    [Fact]
    public void Large_spirit_key_uses_multibyte_varint()
    {
        const int spiritKey = 900_000_100;
        var packet = GamePackets.ElementalSummonActivate(4, spiritKey);
        var (group, key) = ReadElementalSummonWire(packet);
        Assert.Equal(4, group);
        Assert.Equal(spiritKey, key);
        var reader = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(GamePackets.ElementalSummonWireMask, reader.U8());
        Assert.Equal(4, reader.U8());
        Assert.Equal(spiritKey, (int)reader.Varint());
        Assert.Equal(packet.Length, reader.Offset);
    }

    [Fact]
    public async Task Summon_activate_carries_spirit_entity_key_not_player_key()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, 1_700_000_100_000, level: 25, rng: new Random(3));
        var replies = session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, 1_700_000_100_100);
        var spirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.NotEqual(session.PlayerKey, spirit.Key);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0xe239);
        var activate = Assert.Single(session.ElementalActivateTick(1_700_000_100_101), p => GameSessionTestHelpers.Op(p) == 0xe239);
        var (_, key) = ReadElementalSummonWire(activate);
        Assert.Equal(spirit.Key, key);
    }

    [Fact]
    public async Task Activate_spirit_key_matches_npc_appear_entity_key()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, 1_700_000_100_000, level: 25, rng: new Random(3));
        var replies = session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, 1_700_000_100_100);
        var appear = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        var appearKey = NpcAppearEntityKey(appear);
        var activate = Assert.Single(session.ElementalActivateTick(1_700_000_100_101), p => GameSessionTestHelpers.Op(p) == 0xe239);
        var (_, activateKey) = ReadElementalSummonWire(activate);
        Assert.Equal(appearKey, activateKey);
    }

    [Fact]
    public async Task Element_switch_deactivates_then_appear_then_deferred_activate()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, 1_700_000_100_000, level: 25, rng: new Random(3));
        var now = 1_700_000_100_000;
        session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, now + 100);
        var fireSpirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        session.ElementalActivateTick(now + 101);
        var water = session.SummonFromSkillForTest(16110000, Combat.TrainingDummyKey, now + 200);
        var waterSpirit = Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.NotEqual(fireSpirit.Key, waterSpirit.Key);

        static int OpIndex(List<byte[]> batch, int op) => batch.FindIndex(p => GameSessionTestHelpers.Op(p) == op);
        var off = OpIndex(water, 0xe23a);
        var appear = OpIndex(water, 0x3641);
        Assert.True(off >= 0 && appear > off, "deactivate old, then NpcAppear in the switch batch");
        Assert.DoesNotContain(water, p => GameSessionTestHelpers.Op(p) == 0xe239);
        Assert.Equal(GamePackets.ElementalSummonDeactivate(1, fireSpirit.Key), water[off]);

        var icon = session.ElementalActivateTick(now + 201);
        Assert.Contains(icon, p => p.SequenceEqual(GamePackets.ElementalSummonActivate(2, waterSpirit.Key)));
        Assert.Equal(-1, OpIndex(water, 0x3642));

        var delayed = session.SummonExpiryTick(now + 200 + GameSession.ElementalReplaceDespawnMs);
        Assert.Contains(delayed, p => GameSessionTestHelpers.Op(p) == 0x3642 && p.SequenceEqual(GamePackets.Death(fireSpirit.Key, 7)));
    }
}
