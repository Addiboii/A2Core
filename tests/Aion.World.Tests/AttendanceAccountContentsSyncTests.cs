using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceAccountContentsSyncTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC — four Global attendance boards open (Light)

    static List<(int GroupId, int Value1, int Value2)> ParseEntries(byte[] packet)
    {
        var count = packet[2];
        var list = new List<(int, int, int)>();
        var i = 3;
        for (var n = 0; n < count; n++)
        {
            i++;
            var groupId = BitConverter.ToInt32(packet, i);
            i += 4 + 4 + 4 + 8;
            list.Add((groupId, BitConverter.ToInt32(packet, i), BitConverter.ToInt32(packet, i + 4)));
            i += 8;
        }
        return list;
    }

    [Fact]
    public async Task Batch_attendance_claim_refreshes_account_contents_value2()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var atreia = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;
        var todayKey = ContentResetSchedule.GameDayKey(Now + 100);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        Assert.Equal(1, session.AchievementCount(atreia.Achievement));

        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(0).U8(1).ToArray(),
            Now + 101);
        var contents = Assert.Single(batch, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        var entry = Assert.Single(ParseEntries(contents), e => e.GroupId == atreia.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value1);
        Assert.Equal(todayKey, entry.Value2);
    }

    [Fact]
    public async Task Two_day_progress_with_rewards_claimed_clears_reward_day_key()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        await session.HandleAsync([0x4d, 0xe3], Now + 86_400_100);
        await session.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(), Now + 86_400_101);
        await session.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(2).ToArray(), Now + 86_400_102);
        var refresh = await session.HandleAsync([0x4d, 0xe3], Now + 86_400_103);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 86_400_103);
        var entry = Assert.Single(ParseEntries(Assert.Single(refresh, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt)),
            e => e.GroupId == board.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value1);
        Assert.Equal(todayKey, entry.Value2);
    }
}
