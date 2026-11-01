using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-10-30: after the 9-byte accept the client waits for 0x3806 before the next cast.</summary>
public sealed class SkillEndTests
{
    static readonly long Now = 1_700_000_100_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<List<byte[]>> CastAndTick()
    {
            var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
            var dummy = session.Targets![Combat.TrainingDummyKey];
            var all = new List<byte[]>(await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), Now + 50));
            for (var t = 100; t <= 3000; t += 100) all.AddRange(await session.TickAsync(Now + 50 + t));
            return all;
    }

    [Fact]
    public async Task Accepted_cast_is_ended_with_skill_end()
    {
        var all = await CastAndTick();
        Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3806);
    }
}
