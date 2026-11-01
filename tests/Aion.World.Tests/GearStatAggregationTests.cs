using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class GearStatAggregationTests
{
    const long Now = 1_760_100_000_000;
    const int CombatSpeedWire = 282;

    static JsonElement Rolled(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Rolled_substat_combat_speed_appears_on_stat_sheet()
    {
        var empty = Rolled("{}");
        var rolled = Rolled("""{"sub_stats":[["CombatSpeed",660]]}""");
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9000, 1));
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 110120003, 1, "equipment", 5, 1, rolled, empty, 9001));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 25);
        var refresh = session.RefreshStats();
        var stats = refresh.First(p => GameSessionTestHelpers.Op(p) == 0x364a);
        Assert.Equal(660, StatValue(stats, CombatSpeedWire));
        Assert.Equal(10_660, session.AttackSpeedForTests);
    }

    [Fact]
    public async Task Equipped_combat_speed_shortens_basic_swing_lockout()
    {
        var empty = Rolled("{}");
        var rolled = Rolled("""{"sub_stats":[["CombatSpeed",5000]]}""");
        var slowStore = new FakeCharacterStore();
        var fastStore = new FakeCharacterStore();
        slowStore.Items.Add(FakeCharacterStore.EquippedItem(110160001, 9100, 1));
        fastStore.Items.Add(new ItemState(Guid.NewGuid(), "b", 110160001, 1, "equipment", 1, 1, rolled, empty, 9101));
        var slow = await GameSessionTestHelpers.EnterSharedWorldAsync(slowStore, Now, level: 25, rng: new Random(40));
        var fast = await GameSessionTestHelpers.EnterSharedWorldAsync(fastStore, Now, level: 25, rng: new Random(40));
        var dummy = slow.Targets![Combat.TrainingDummyKey];
        const int basic = 11_020_000;
        var t = Now + 10_000;
        var cast = new PacketWriter().U16(0x3800).U16(0).I32(basic).U8(2).Varint(dummy.Key).F32(0).F32(100).F32(200).F32(300).ToArray();
        Assert.True(await slow.HandleAsync(cast, t) is { } slowReplies && slowReplies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0));
        Assert.True(await fast.HandleAsync(cast, t) is { } fastReplies && fastReplies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0));
        Assert.True(fast.HeldCastLockoutRemainingMs(t + 1) < slow.HeldCastLockoutRemainingMs(t + 1));
    }

    [Fact]
    public async Task Amplify_all_damage_from_gear_increases_skill_hit()
    {
        var empty = Rolled("{}");
        var rolled = Rolled("""{"sub_stats":[["AmplifyAllDamage",1000]]}""");
        var store = new FakeCharacterStore();
        store.Items.Add(new ItemState(Guid.NewGuid(), "b", 110160001, 1, "equipment", 1, 1, rolled, empty, 9200));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45, rng: new Random(1));
        var row = new DamageRow([0], [10_000], null, 0);
        var amp = session.AmplifyAllDamageBpForTests;
        Assert.True(amp >= 1000);
        var shared = new WeaponStats(
            session.WeaponStatsForTests().Min,
            session.WeaponStatsForTests().Max,
            session.WeaponStatsForTests().Critical,
            session.WeaponStatsForTests().Accuracy);
        var baseline = Combat.RowHits(GameData.Embedded, [row], session.ClassName, 45, 110160001, 0, new Random(99),
            extraFixing: session.ExtraFixingForTests, weaponOverride: shared, amplifyAllBp: 0).Max(h => h.Amount);
        var boosted = Combat.RowHits(GameData.Embedded, [row], session.ClassName, 45, 110160001, 0, new Random(99),
            extraFixing: session.ExtraFixingForTests, weaponOverride: shared, amplifyAllBp: amp).Max(h => h.Amount);
        Assert.True(boosted > baseline);
    }

    static int StatValue(byte[] packet, int wire)
    {
        if (GameSessionTestHelpers.Op(packet) != 0x364a || packet.Length < 6) return -1;
        var i = 2;
        while (i < packet.Length && (packet[i] & 0x80) != 0) i++;
        i++;
        if (i >= packet.Length) return -1;
        var count = packet[i++];
        for (var n = 0; n < count && i + 6 <= packet.Length; n++)
        {
            var type = BitConverter.ToUInt16(packet, i);
            var value = BitConverter.ToInt32(packet, i + 2);
            i += 6;
            if (type == wire) return value;
        }
        return -1;
    }
}
