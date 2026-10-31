using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ElementalSummonActivateOrderTests
{
    static readonly long Now = 1_700_000_100_000;

    [Theory]
    [InlineData(16100000, 1, 2920101)]
    [InlineData(16110000, 2, 2920121)]
    [InlineData(16120000, 3, 2920141)]
    [InlineData(16130000, 4, 2920161)]
    public async Task Activate_is_deferred_until_after_appear_and_aggro_packets(int skill, byte group, int npc)
    {
        Assert.Equal(group, ElementalSummons.GroupTypeForNpc(npc));
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(9));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var batch = session.SummonFromSkillForTest(skill, dummy.Key, Now + 50);
        var appear = batch.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3641);
        var aggro = batch.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x8d52);
        var target = batch.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3835);
        Assert.True(appear >= 0, "spirit NpcAppear is sent in the summon batch");
        Assert.True(aggro > appear, "0x8d52 follows appear when the spirit engages the dummy");
        Assert.True(target > appear, "0x3835 follows appear");
        Assert.DoesNotContain(batch, p => GameSessionTestHelpers.Op(p) == 0xe239);
        var activate = session.ElementalActivateTick(Now + 51);
        var packet = Assert.Single(activate, p => GameSessionTestHelpers.Op(p) == 0xe239);
        Assert.Equal(group, packet[3]);
        var spirit = Assert.Single(session.Targets!.Values, t => t.OwnerKey == session.PlayerKey);
        var (readGroup, key) = ElementalSummonPacketTests.ReadElementalSummonWire(packet);
        Assert.Equal(group, readGroup);
        Assert.Equal(spirit.Key, key);
    }

    [Fact]
    public void Elemental_summon_table_matches_usmap_enum_and_jointstrike_groups()
    {
        Assert.Equal(1, ElementalSummons.GroupType("Fire"));
        Assert.Equal(2, ElementalSummons.GroupType("Water"));
        Assert.Equal(3, ElementalSummons.GroupType("Wind"));
        Assert.Equal(4, ElementalSummons.GroupType("Earth"));
        Assert.Equal(5, ElementalSummons.GroupType("Ancient"));
        var joint = GameSessionTestHelpers.LiveGameData.Skill(16_140_000)!;
        Assert.Equal(["Fire", "Water", "Wind", "Earth", "Ancient"], joint.NeedsSpirit!);
        Assert.All(joint.NeedsSpirit!, name => Assert.True(ElementalSummons.GroupType(name) > 0));
    }
}
