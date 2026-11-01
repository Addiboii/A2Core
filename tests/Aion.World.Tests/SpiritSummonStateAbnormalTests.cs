using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SpiritSummonStateAbnormalTests
{
    const long Now = 1_760_003_000_000;
    const int FireSummon = 16_100_000;
    const int WaterSummon = 16_110_000;
    const long PassiveExpire = 4_102_412_400_000;

    static byte[] Cast(int skill, int target = 0) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static int SpiritStateCount(GameSession session) =>
        session.ActiveBuffs.Count(id => id > GameSession.SpiritSummonStateAbnormalBase && id <= GameSession.SpiritSummonStateAbnormalBase + 5);

    static bool HasAbnormalRemove(IEnumerable<byte[]> packets) =>
        packets.Any(p => GameSessionTestHelpers.Op(p) == 0x382c);

    static async Task<GameSession> EnterAsync(FakeCharacterStore? store = null)
    {
        store ??= new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Spirit").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task ActivatePendingAsync(GameSession session, long now) => _ = await session.TickAsync(now);

    [Fact]
    public async Task Deferred_activate_twice_still_leaves_one_spirit_state_abnormal()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(3));
        session.SummonFromSkillForTest(FireSummon, Combat.TrainingDummyKey, Now + 100);
        var tick = session.ElementalActivateTick(Now + 101);
        tick.AddRange(session.ElementalActivateTick(Now + 101));
        Assert.Contains(tick, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Equal(1, SpiritStateCount(session));
    }

    [Fact]
    public async Task Summon_element_switch_and_resummon_leave_exactly_one_spirit_state_abnormal()
    {
        var session = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 60_000;

        await session.HandleAsync(Cast(FireSummon, dummy.Key), t);
        await ActivatePendingAsync(session, t + 1);
        Assert.Equal(1, SpiritStateCount(session));
        Assert.Equal(GameSession.SpiritSummonStateAbnormal, session.ActiveBuffs.Single(id => id > GameSession.SpiritSummonStateAbnormalBase));

        await session.HandleAsync(Cast(WaterSummon, dummy.Key), t + 16_000);
        await ActivatePendingAsync(session, t + 16_001);
        Assert.Equal(1, SpiritStateCount(session));
        Assert.Equal(GameSession.SpiritSummonStateAbnormalBase + 2, session.ActiveBuffs.Single(id => id > GameSession.SpiritSummonStateAbnormalBase));

        await session.HandleAsync(Cast(FireSummon, dummy.Key), t + 32_000);
        await ActivatePendingAsync(session, t + 32_001);
        Assert.Equal(1, SpiritStateCount(session));
        Assert.Equal(GameSession.SpiritSummonStateAbnormal, session.ActiveBuffs.Single(id => id > GameSession.SpiritSummonStateAbnormalBase));
    }

    [Fact]
    public async Task Dismiss_and_resummon_leave_exactly_one_spirit_state_abnormal()
    {
        var session = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 60_000;

        await session.HandleAsync(Cast(FireSummon, dummy.Key), t);
        await ActivatePendingAsync(session, t + 1);
        Assert.Equal(1, SpiritStateCount(session));

        var back = await session.HandleAsync(Cast(GameSession.ReturnAllSpiritsSkill, dummy.Key), t + 20_000);
        Assert.True(HasAbnormalRemove(back));
        Assert.Equal(0, SpiritStateCount(session));
        Assert.DoesNotContain(session.Targets.Values, v => v.OwnerKey == session.PlayerKey);

        await session.HandleAsync(Cast(FireSummon, dummy.Key), t + 36_000);
        await ActivatePendingAsync(session, t + 36_001);
        Assert.Equal(1, SpiritStateCount(session));
    }

    [Fact]
    public async Task Return_all_with_no_spirit_clears_stale_state_abnormal()
    {
        var session = await EnterAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 60_000;
        await session.HandleAsync(Cast(FireSummon, dummy.Key), t);
        await ActivatePendingAsync(session, t + 1);
        session.OrphanSpiritEntityKeepStateAbnormalForTest();
        Assert.Equal(1, SpiritStateCount(session));

        var clear = await session.HandleAsync(Cast(GameSession.ReturnAllSpiritsSkill, dummy.Key), t + 5_000);
        Assert.True(HasAbnormalRemove(clear));
        Assert.Equal(0, SpiritStateCount(session));

        var again = await session.HandleAsync(Cast(GameSession.ReturnAllSpiritsSkill, dummy.Key), t + 6_000);
        Assert.Equal(0, SpiritStateCount(session));
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x382a);
    }

    [Fact]
    public async Task Lifetime_expiry_clears_spirit_state_abnormal()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(3));
        var castAt = Now + 100;
        session.SummonFromSkillForTest(FireSummon, Combat.TrainingDummyKey, castAt);
        session.ElementalActivateTick(castAt + 1);
        Assert.Equal(1, SpiritStateCount(session));

        var expired = session.SummonExpiryTick(castAt + GamePackets.SpiritLifeTimeMs);
        Assert.True(HasAbnormalRemove(expired));
        Assert.Equal(0, SpiritStateCount(session));
    }

    [Fact]
    public async Task Spirit_death_clears_spirit_state_abnormal()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(3));
        var castAt = Now + 100;
        session.SummonFromSkillForTest(FireSummon, Combat.TrainingDummyKey, castAt);
        session.ElementalActivateTick(castAt + 1);
        var died = session.SummonDiedForTest(castAt + 500);
        Assert.True(HasAbnormalRemove(died));
        Assert.Equal(0, SpiritStateCount(session));
    }

    [Fact]
    public async Task Leave_world_clears_spirit_state_abnormal()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(3));
        session.SummonFromSkillForTest(FireSummon, Combat.TrainingDummyKey, Now + 100);
        session.ElementalActivateTick(Now + 101);
        Assert.Equal(1, SpiritStateCount(session));
        session.ExitSharedWorld();
        Assert.Equal(0, SpiritStateCount(session));
    }

    [Fact]
    public async Task Persisted_stale_spirit_state_is_not_restored_on_entry()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        store.Settings["active_buffs"] = JsonSerializer.SerializeToElement(new[]
        {
            new { Uid = 42, AbnormalId = GameSession.SpiritSummonStateAbnormal, SkillId = 0, ExpireAt = PassiveExpire, Rebirth = false, Polymorph = false, Scripted = true, Relationship = (int?)null, Stats = new Dictionary<string, int>(), Heals = Array.Empty<object>(), Combat = (object?)null, Dots = Array.Empty<object>() },
        });
        var session = await EnterAsync(store);
        Assert.Equal(0, SpiritStateCount(session));
    }
}
