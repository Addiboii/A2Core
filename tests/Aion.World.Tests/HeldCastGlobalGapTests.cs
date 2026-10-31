using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class HeldCastGlobalGapTests
{
    const long Now = 1_760_008_000_000;
    const int FireSpirit = 16_100_000;
    const int Jointstrike = 16_140_000;

    static byte[] Cast(int skillId, int targetKey) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0).F32(100).F32(200).F32(300).ToArray();

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static async Task<GameSession> ElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("GapEl").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task After_summon_fire_spirit_a_different_skill_is_accepted_at_300ms()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 5000;
        Assert.True(Accepted(await session.HandleAsync(Cast(FireSpirit, dummy.Key), t)));
        var follow = await session.HandleAsync(Cast(Jointstrike, dummy.Key), t + 300);
        Assert.True(Accepted(follow));
    }

    [Fact]
    public async Task Held_swing_lockout_remaining_never_exceeds_one_second()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 8000;
        Assert.True(Accepted(await session.HandleAsync(Cast(FireSpirit, dummy.Key), t)));
        Assert.Equal(0, session.HeldCastLockoutRemainingMs(t + 1));

        var store = new FakeCharacterStore { Progress = (45, 0) };
        var ranger = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(0)) { Store = store };
        await ranger.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await ranger.HandleAsync(new PacketWriter().U16(0x361b).String("GapRg").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await ranger.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await ranger.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var tr = Now + 9000;
        Assert.True(Accepted(await ranger.HandleAsync(Cast(14_020_000, dummy.Key), tr)));
        Assert.InRange(ranger.HeldCastLockoutRemainingMs(tr + 1), 1, 1000);
    }

    [Fact]
    public async Task Higher_combat_speed_shortens_basic_swing_lockout()
    {
        var store = new FakeCharacterStore();
        var slow = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(40));
        var fastStore = new FakeCharacterStore();
        var fast = await GameSessionTestHelpers.EnterSharedWorldAsync(fastStore, Now, level: 25, rng: new Random(40));
        fast.SetCombatSpeedBonusForTests(10_000);
        var dummy = slow.Targets![Combat.TrainingDummyKey];
        const int basic = 11_020_000;
        var t = Now + 10_000;
        Assert.True(Accepted(await slow.HandleAsync(Cast(basic, dummy.Key), t)));
        Assert.True(Accepted(await fast.HandleAsync(Cast(basic, dummy.Key), t)));
        Assert.True(fast.HeldCastLockoutRemainingMs(t + 1) < slow.HeldCastLockoutRemainingMs(t + 1));
    }

    [Fact]
    public async Task Trap_placement_does_not_set_held_swing_lockout()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(2)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("TrapRg").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        const int explosionTrap = 14_170_000;
        var t = Now + 11_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(explosionTrap, session.PlayerKey), t)));
        Assert.Equal(0, session.HeldCastLockoutRemainingMs(t + 1));
    }
}
