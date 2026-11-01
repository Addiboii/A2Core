using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceWireOpcodeTests
{
    const long Now = 1_760_184_000_000; // 2025-10-11 — open Global attendance boards for Light

    [Fact]
    public async Task Live_e34d_attendance_rq_returns_e34e_with_u16_result_zero()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var rs = Assert.Single(reply, p => GameSessionTestHelpers.Op(p) == GameSession.AttendanceRs);
        Assert.Equal(0xe34e, GameSessionTestHelpers.Op(rs));
        Assert.Equal(4, rs.Length);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(reply, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt);
    }

    [Fact]
    public async Task Second_e34d_same_day_still_returns_u16_result_zero_four_bytes()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var again = await session.HandleAsync([0x4d, 0xe3], Now + 101);
        var rs = Assert.Single(again, p => GameSessionTestHelpers.Op(p) == GameSession.AttendanceRs);
        Assert.Equal(4, rs.Length);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.Contains(again, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
    }
}
