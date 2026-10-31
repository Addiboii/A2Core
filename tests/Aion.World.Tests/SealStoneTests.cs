using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Power Shards (SealStone): slotted shards are spent per damaging cast and boost its damage; none, a plain cast.</summary>
public sealed class SealStoneTests
{
    const long Now = 1_760_200_000_000;
    const int Shard = 533460001;
    const long ShardWire = 9100;

    static async Task<(GameSession Session, FakeCharacterStore Store, Target Target, Vector3d At)> FieldAsync(long shards, bool slot)
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 10, rng: new Random(7));
        if (shards > 0) store.Items.Add(FakeCharacterStore.BagItem(Shard, shards, ShardWire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        if (slot) await session.HandleAsync(new PacketWriter().U16(GameSession.SealStoneRegisterRq).I64(ShardWire).ToArray(), Now);
        var spawn = GameSessionTestHelpers.LiveGameData.Spawns(1010).First(s => s.Relationship == 6 && s.Function == "None");
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(1010, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var target = session.Targets!.Values.First(t => t.NpcId == spawn.NpcId);
        return (session, store, target, at);
    }

    static async Task<(int Damage, List<byte[]> Replies)> HitAsync(GameSession session, Target target, Vector3d at)
    {
        var before = target.Hp;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var replies = await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, Now + 1000);
        return (before - target.Hp, replies);
    }

    [Fact]
    public async Task Slotted_shards_are_spent_and_boost_the_hit()
    {
        var (plain, _, plainTarget, plainAt) = await FieldAsync(0, slot: false);
        var (boosted, store, target, at) = await FieldAsync(1000, slot: true);
        Assert.True(boosted.SealStoneOn);
        Assert.Equal("{\"On\":true,\"Item\":9100}", store.Settings["seal_stone"].GetRawText());

        var (plainDamage, _) = await HitAsync(plain, plainTarget, plainAt);
        var (damage, replies) = await HitAsync(boosted, target, at);

        var skill = MapEventScenarioHelpers.BasicAttackSkill(boosted);
        var spent = boosted.SealStoneConsumption(skill);
        Assert.True(spent > 0);
        Assert.Equal(1000 - spent, store.Items.Single(i => i.TemplateId == Shard).Quantity);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.True(plainDamage > 0);
        Assert.True(damage > plainDamage, $"boosted {damage} vs plain {plainDamage}");
    }

    [Fact]
    public async Task Without_shards_the_cast_is_plain()
    {
        var (plain, _, plainTarget, plainAt) = await FieldAsync(0, slot: false);
        var (session, store, target, at) = await FieldAsync(1000, slot: true);
        store.Items.RemoveAll(i => i.TemplateId == Shard);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var (plainDamage, _) = await HitAsync(plain, plainTarget, plainAt);
        var (damage, replies) = await HitAsync(session, target, at);

        Assert.Equal(plainDamage, damage);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
    }

    [Fact]
    public async Task Toggle_off_keeps_the_shards()
    {
        var (session, store, target, at) = await FieldAsync(1000, slot: true);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SealStoneDeactivateRq).ToArray(), Now);
        await HitAsync(session, target, at);
        Assert.Equal(1000, store.Items.Single(i => i.TemplateId == Shard).Quantity);
    }
}
