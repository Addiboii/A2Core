using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Consumable item use, cooldown groups, dispel, buff persistence (boxes.json parity).</summary>
public sealed class ItemEffectsParityTests
{
    [Fact]
    public void Dispel_scroll_cooldown_group_is_dat_dispel()
    {
        var cool = RewardBoxes.Embedded.Cooldown(530146001);
        Assert.Equal("dispel", cool.Group);
        Assert.Equal(10_000, cool.CooldownMs);
    }

    [Fact]
    public void Debuff_heuristic_marks_cc_abnormal()
    {
        Assert.True(RewardBoxes.Embedded.Debuff(1726));
    }

    [Fact]
    public async Task Dispel_scroll_consumes_and_accepts_when_no_debuffs()
    {
        const long now = 900_000;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(530146001, 1, 71));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(71).ToArray(), now);
        Assert.True(GameSessionTestHelpers.HasOpcode(use, RidePackets.ItemUseRs));
        Assert.Equal(0, use[0][2]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 530146001 && i.Quantity > 0);
    }

    [Fact]
    public async Task Food_buff_persists_in_character_settings_for_relog()
    {
        const long now = 1_000_000;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(510715007, 1, 72));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        var hpBefore = session.Resource!.HpMax;
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(72).ToArray(), now + 1000);
        Assert.True(session.Resource.HpMax > hpBefore);
        await session.TickAsync(now + 2000);
        Assert.True(store.Settings.ContainsKey("active_buffs"));
        var wireId = store.States[0].WireId;
        var reloaded = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(9))
        {
            Store = store
        };
        await reloaded.HandleAsync(GameSessionTestHelpers.Login(), now + 5000);
        await reloaded.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now + 5000);
        await reloaded.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5000);
        Assert.True(reloaded.Resource!.HpMax > hpBefore);
        Assert.NotEmpty(reloaded.ActiveBuffs);
    }

    [Fact]
    public async Task Exp_booster_no_effect_skill_still_consumes()
    {
        const long now = 2_000_000;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(534950001, 1, 73));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(73).ToArray(), now);
        Assert.Equal(0, use[0][2]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == 534950001);
    }
}
