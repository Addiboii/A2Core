using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Client resends 0x3800 every ~17 ms while a key is held. Official ignores duplicates during swing lockout (no 0x3801 flood).
/// </summary>
public sealed class CastResendTests
{
    const long Now = 1_760_005_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool CastAccepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public async Task Held_key_resend_inside_swing_is_silent_without_second_skill_start()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(31));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int skillId = 11_020_000;
        var castAt = Now + 500;
        var first = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt);
        Assert.True(CastAccepted(first));
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3802);

        var resend = await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt + 17);
        Assert.Contains(resend, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        Assert.DoesNotContain(resend, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Chain_step_resend_inside_gap_is_silent_and_keeps_combo_window()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (40, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 40, WorldId = store.WorldId, TrainingDummy = true },
            new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Ranger").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int root = 14_020_000;
        var chain = data.ChainFollowUpIds(root, masteryLevel: 1).First();
        var t0 = Now + 1000;
        await session.HandleAsync(Cast(root, dummy.Key, dummy.Position), t0);
        Assert.True(session.ActiveChainSkills.ContainsKey(chain));

        var earlyChain = await session.HandleAsync(Cast(chain, dummy.Key, dummy.Position), t0 + 50);
        Assert.DoesNotContain(earlyChain, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.True(session.ActiveChainSkills.ContainsKey(chain));

        var drainAt = t0 + session.HeldCastLockoutRemainingMs(t0 + 1) + 1;
        var replies = new List<byte[]>();
        for (var at = t0 + 50; at <= drainAt; at += 17)
            replies.AddRange(await session.HandleAsync(Cast(chain, dummy.Key, dummy.Position), at));
        replies.AddRange(await session.TickAsync(drainAt));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3802);
    }

    [Fact]
    public async Task Root_spam_resends_do_not_close_ranger_chain_window()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (40, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 40, WorldId = store.WorldId, TrainingDummy = true },
            new Random(1)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Ranger").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int root = 14_020_000;
        var chain = data.ChainFollowUpIds(root, masteryLevel: 1).First();
        var t0 = Now + 2000;
        await session.HandleAsync(Cast(root, dummy.Key, dummy.Position), t0);
        Assert.True(session.ActiveChainSkills.ContainsKey(chain));
        for (var i = 1; i <= 5; i++)
            Assert.Contains(await session.HandleAsync(Cast(root, dummy.Key, dummy.Position), t0 + i * 17),
                p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        Assert.True(session.ActiveChainSkills.ContainsKey(chain));
    }
}
