using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AbnormalCombatTests
{
    const long Now = 3_000_000;
    static GameData Data => GameSessionTestHelpers.LiveGameData;

    [Fact]
    public void Candy_abnormal_parses_damage_reflect_permille()
    {
        var info = RewardBoxes.Embedded.Abnormal(45005001);
        Assert.NotNull(info);
        var combat = new AbnormalCombat.BuffCombat();
        foreach (var (id, type, values) in info!.Effects)
            if (type == "DamageReflect") AbnormalCombat.ApplyRow(id, type, values, combat, 60_000, Now, new Random(1), 10_000);
        Assert.Equal(10_000, combat.ReflectPermille);
        Assert.Equal(500, combat.ReflectCap);
    }

    [Fact]
    public void CharBarrierInfo_packet_matches_schema_sample()
    {
        var body = GamePackets.CharBarrierInfo(1, [(5, 350)]);
        Assert.Equal(0x3646, GameSessionTestHelpers.Op(body));
        Assert.Contains(body, b => b == 5);
    }

    [Fact]
    public async Task Hp_barrier_scroll_absorbs_incoming_damage()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(531060005, 1, 80));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var hpMax = session.Resource!.HpMax;
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(80).ToArray(), Now);
        Assert.Contains(22120011, session.ActiveBuffs);
        var before = session.Resource.Hp;
        var replies = session.NpcHitsPlayer(500, Now + 100, killerKey: Combat.TrainingDummyKey);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3646);
        Assert.InRange(session.Resource.Hp, before - 200, before - 100);
        Assert.True(session.Resource.Hp >= before - 200);
    }

    [Fact]
    public async Task Immunity_scroll_blocks_debuff_abnormal()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(531060025, 1, 81));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 1000, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 1000);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(81).ToArray(), Now + 1000);
        Assert.Contains(22121011, session.ActiveBuffs);
        var blocked = session.TryApplyPlayerDebuff(18017800, 5000, Now + 2000);
        Assert.Contains(blocked, p => GameSessionTestHelpers.Op(p) == 0x382d);
        Assert.DoesNotContain(18017800, session.ActiveBuffs);
    }

    [Fact]
    public async Task Damage_reflect_candy_returns_damage_to_attacker()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(530800003, 1, 82));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now + 5000, level: 55);
        var at = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, at, Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5000);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 5000);
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(82).ToArray(), Now + 5100);
        Assert.Equal(0, use[0][2]);
        Assert.Contains(45005001, session.ActiveBuffs);
        Assert.Equal(10_000, session.ActiveReflectPermille);
        session.SetResource('H', session.Resource!.HpMax);
        var playerHpBefore = session.Resource.Hp;
        session.NpcHitsPlayer(1000, Now + 6000, killerKey: Combat.TrainingDummyKey);
        Assert.Equal(Math.Max(0, playerHpBefore - 1000), session.Resource.Hp);
    }

    [Fact]
    public async Task Stun_debuff_blocks_skill_cast()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now + 7000, level: 55);
        var at = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, at, Now + 7000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 7000);
        session.TryApplyPlayerDebuff(18017800, 5000, Now + 7000);
        Assert.True(session.HeldByAbnormal);
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var replies = await session.HandleAsync(MapEventScenarioHelpers.CombatCast(skill, Combat.TrainingDummyKey, at), Now + 7100);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }
}
