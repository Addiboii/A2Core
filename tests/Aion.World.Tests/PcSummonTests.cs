using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: the Spiritmaster's Summon: Fire/Water Spirit were accepted and did no damage (no spirit appeared).</summary>
public sealed class PcSummonTests
{
    static readonly long Now = 1_700_000_100_000;

    [Theory]
    [InlineData(16100000, 2920101, 16100001)]   // Summon: Fire Spirit -> Elementalist_Skill010_FireEle_Lv1_1
    [InlineData(16110000, 2920121, 16110001)]   // Summon: Water Spirit -> Elementalist_Skill011_WaterEle_Lv1_1
    [InlineData(16120000, 2920141, 16120001)]
    [InlineData(16130000, 2920161, 16130001)]
    public void Summon_skills_name_their_spirit_and_its_attacks_from_client_data(int skill, int npc, int attack)
    {
        var summon = PcSummons.For(skill);
        Assert.NotNull(summon);
        Assert.Equal(npc, summon.Value.Skill.Npc);
        Assert.Contains(attack, summon.Value.Template.Skills);
        Assert.NotNull(GameData.Embedded.NpcSkill(attack));
    }

    [Fact]
    public async Task Fire_spirit_appears_attacks_the_target_and_deals_damage()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(5));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var hpBefore = dummy.Hp;
        var replies = session.SummonFromSkillForTest(16100000, dummy.Key, Now + 100);
        var spirit = Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Equal(2920101, spirit.NpcId);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        for (var t = Now + 200; t <= Now + 15_000 && dummy.Hp == hpBefore; t += 100) await session.TickAsync(t);
        Assert.True(dummy.Hp < hpBefore, "the spirit's attacks land on the player's target");
        // A second elemental replaces the first.
        session.SummonFromSkillForTest(16110000, dummy.Key, Now + 16_000);
        var water = Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Equal(2920121, water.NpcId);
    }

    /// <summary>Official 2026-10-30: the Water Spirit shows "[Annihilator]" above its name and its HP bar, and a water drop sits
    /// at the end of the summoner's MP bar (a fire circle for the Fire Spirit).</summary>
    [Fact]
    public async Task Spirit_appears_with_its_owner_and_drives_the_mode_icon()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(5));
        var fire = session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, Now + 100);
        var spirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        var appear = Assert.Single(fire, p => GameSessionTestHelpers.Op(p) == 0x3641);
        var reader = new PacketReader(appear) { Offset = 2 };
        Assert.Equal(spirit.Key, (int)reader.Varint());
        Assert.Equal(0x30, reader.U16() & 0x30);
        Assert.False(string.IsNullOrEmpty(spirit.OwnerDesc!.Nickname));
        var parent = PacketWriter.Opcode(0).Varint(session.PlayerKey).U8(0x07).String(spirit.OwnerDesc.Nickname).ToArray()[2..];
        Assert.True(appear.AsSpan().IndexOf(parent) > 0, "the appear carries the parent key and desc");
        Assert.DoesNotContain(fire, p => GameSessionTestHelpers.Op(p) == 0xe239);
        var fireIcon = session.ElementalActivateTick(Now + 101);
        Assert.Contains(fireIcon, p => p.SequenceEqual(GamePackets.ElementalSummonActivate(1, spirit.Key)));

        var fireKey = spirit.Key;
        var water = session.SummonFromSkillForTest(16110000, Combat.TrainingDummyKey, Now + 200);
        var waterSpirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        var off = water.FindIndex(p => p.SequenceEqual(GamePackets.ElementalSummonDeactivate(1, fireKey)));
        Assert.True(off >= 0, "switching spirits turns off the fire mode");
        Assert.DoesNotContain(water, p => GameSessionTestHelpers.Op(p) == 0xe239);
        var waterIcon = session.ElementalActivateTick(Now + 201);
        Assert.Contains(waterIcon, p => p.SequenceEqual(GamePackets.ElementalSummonActivate(2, waterSpirit.Key)));

        var leave = session.DismissAllSummons();
        Assert.Contains(leave, p => p.SequenceEqual(GamePackets.ElementalSummonDeactivate(2, waterSpirit.Key)));
    }

    /// <summary>Official 2026-10-31: mask 0x01, group, spirit key varint (no lifetime on wire).</summary>
    [Fact]
    public void Spirit_icon_activate_matches_official_mask_and_varint_wire()
    {
        const int spiritKey = 900_000_042;
        var packet = GamePackets.ElementalSummonActivate(2, spiritKey);
        Assert.Equal(new byte[] { 0x39, 0xe2, GamePackets.ElementalSummonWireMask, 2 }, packet[..4]);
        var (group, key) = ElementalSummonPacketTests.ReadElementalSummonWire(packet);
        Assert.Equal(2, group);
        Assert.Equal(spiritKey, key);
    }

    /// <summary>The icon's button casts Dismiss: &lt;element&gt; Spirit; the circle button Return: All Spirits.</summary>
    [Fact]
    public async Task Dismiss_and_return_skills_send_the_spirit_away()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(5));
        session.SummonFromSkillForTest(16110000, Combat.TrainingDummyKey, Now + 100);
        session.ElementalActivateTick(Now + 101);
        var water = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.Empty(session.DismissFromSkillForTest(16290000));   // Dismiss: Fire Spirit leaves the Water Spirit
        Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        var gone = session.DismissFromSkillForTest(16290001);       // Dismiss: Water Spirit
        Assert.Contains(gone, p => p.SequenceEqual(GamePackets.ElementalSummonDeactivate(2, water.Key)));
        Assert.DoesNotContain(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);

        session.SummonFromSkillForTest(16130000, Combat.TrainingDummyKey, Now + 200);
        session.ElementalActivateTick(Now + 201);
        var earth = Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        var all = session.DismissFromSkillForTest(GameSession.ReturnAllSpiritsSkill);
        Assert.Contains(all, p => p.SequenceEqual(GamePackets.ElementalSummonDeactivate(4, earth.Key)));
        Assert.DoesNotContain(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
    }

    /// <summary>Live 2026-10-30 20:28: every spirit reused one key, the old one's 0x3642 and the new one's 0x3641 in one batch.</summary>
    [Fact]
    public async Task Every_spirit_gets_a_fresh_key_and_expires_with_its_icon()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(5));
        session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, Now + 100);
        var fire = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        session.SummonFromSkillForTest(16110000, Combat.TrainingDummyKey, Now + 200);
        var water = Assert.Single(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
        Assert.NotEqual(fire.Key, water.Key);

        Assert.Empty(session.SummonExpiryTick(Now + 200 + GameSession.ElementalReplaceDespawnMs - 1));
        Assert.Contains(session.SummonExpiryTick(Now + 200 + GameSession.ElementalReplaceDespawnMs),
            p => p.SequenceEqual(GamePackets.Death(fire.Key, 7)));

        Assert.Empty(session.SummonExpiryTick(Now + 200 + GamePackets.SpiritLifeTimeMs - 200));
        var gone = session.SummonExpiryTick(Now + 200 + GamePackets.SpiritLifeTimeMs);
        Assert.Contains(gone, p => p.SequenceEqual(GamePackets.ElementalSummonDeactivate(2, water.Key)));
        Assert.DoesNotContain(session.Targets.Values, t => t.OwnerKey == session.PlayerKey);
    }

    [Fact]
    public void Npc_appear_without_a_parent_is_unchanged()
    {
        var npc = new NpcAppearance(5, 2920121, Vector3d.Zero, 0, 10, 10, 1, 22, false, false);
        var plain = GamePackets.NpcAppear(npc);
        var owned = GamePackets.NpcAppear(npc with { ParentKey = 7, Parent = new CharDescWire("Annihilator", 1, 1, 1) });
        Assert.Equal(0x2004, BitConverter.ToUInt16(plain, 3));
        Assert.Equal(0x2034, BitConverter.ToUInt16(owned, 3));
        Assert.Equal(plain.Length + 1 + 1 + 1 + "Annihilator".Length + 4 + 1 + 1, owned.Length);
    }

    /// <summary>Live 2026-10-30 14:26: Zakl's Fire Spirit stayed in the shared map and followed the session's next character.</summary>
    [Fact]
    public async Task Leaving_the_world_dismisses_the_characters_summon()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(5));
        var shared = session.Targets!;
        session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, Now + 100);
        Assert.Single(shared.Values, t => t.OwnerKey == session.PlayerKey);
        session.ExitSharedWorld();
        Assert.DoesNotContain(shared.Values, t => t.OwnerKey == session.PlayerKey);
    }
}
