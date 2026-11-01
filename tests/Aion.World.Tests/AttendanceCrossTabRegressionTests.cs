using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceCrossTabRegressionTests
{
    const long Now = 1_793_454_400_000; // 2026-10-31 14:00 UTC (screenshot day)

    static List<(int GroupId, int Value1, int Value2)> Parse56b6(byte[] packet)
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

    static List<(int AchievementId, int Count, byte RewardedStep)> Parse5684(byte[] packet)
    {
        var list = new List<(int, int, byte)>();
        var count = packet[2];
        var i = 3;
        for (var n = 0; n < count; n++)
        {
            var id = BitConverter.ToInt32(packet, i);
            var cnt = BitConverter.ToInt32(packet, i + 4);
            i += 4 + 4 + 8;
            var step = packet[i];
            i++;
            list.Add((id, cnt, step));
        }
        return list;
    }

    [Fact]
    public async Task Claim_event_B_after_A_keeps_A_rewarded_in_5684_and_56b6()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var seasons = GameSession.LatestAttendanceSeasons(1, Now + 100);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 200);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);

        var first = seasons[0].Board;
        var second = seasons[1].Board;
        await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(first.Achievement).U8(1).ToArray(),
            Now + 101);

        var claimB = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(second.Achievement).U8(1).ToArray(),
            Now + 102);

        var update = Assert.Single(claimB, p => GameSessionTestHelpers.Op(p) == GameSession.AchievementCountUpdateNt);
        var first5684 = Assert.Single(Parse5684(update), s => s.AchievementId == first.Achievement);
        Assert.Equal(1, first5684.Count);
        Assert.Equal((byte)1, first5684.RewardedStep);

        var contents = Assert.Single(claimB, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        var first56b6 = Assert.Single(Parse56b6(contents), e => e.GroupId == first.SeasonGroupId);
        Assert.Equal(todayKey, first56b6.Value1);
        Assert.Equal(todayKey, first56b6.Value2);
    }

    [Fact]
    public async Task Attendance_refresh_between_claims_keeps_prior_reward_day()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var seasons = GameSession.LatestAttendanceSeasons(1, Now + 100);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 200);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);

        var first = seasons[0].Board;
        var last = seasons[^1].Board;
        await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(first.Achievement).U8(1).ToArray(),
            Now + 101);
        await session.HandleAsync([0x4d, 0xe3], Now + 102);

        var claimLast = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(last.Achievement).U8(1).ToArray(),
            Now + 103);
        var contents = Assert.Single(claimLast, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        var first56b6 = Assert.Single(Parse56b6(contents), e => e.GroupId == first.SeasonGroupId);
        Assert.Equal(todayKey, first56b6.Value1);
        Assert.Equal(todayKey, first56b6.Value2);
    }

    [Fact]
    public async Task Reloaded_session_claim_last_tab_keeps_first_tabs_rewarded()
    {
        var store = new FakeCharacterStore();
        var seasons = GameSession.LatestAttendanceSeasons(1, Now + 100);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 200);

        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session1.HandleAsync([0x4d, 0xe3], Now + 100);
        foreach (var (_, board) in seasons.Take(3))
            await session1.HandleAsync(
                new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(),
                Now + 110 + board.SeasonGroupId);

        var session2 = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 150, level: 55);
        var last = seasons[^1].Board;
        var claim = await session2.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(last.Achievement).U8(1).ToArray(),
            Now + 200);
        var contents = Assert.Single(claim, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        foreach (var (_, board) in seasons.Take(3))
        {
            var e = Assert.Single(Parse56b6(contents), x => x.GroupId == board.SeasonGroupId);
            Assert.Equal(todayKey, e.Value1);
            Assert.Equal(todayKey, e.Value2);
        }
    }

    [Fact]
    public async Task Stale_db_count_zero_without_refresh_shows_reward_day_after_other_claim()
    {
        var store = new FakeCharacterStore();
        var seasons = GameSession.LatestAttendanceSeasons(1, Now + 100);
        var first = seasons[0].Board;
        var second = seasons[1].Board;
        store.Achievements.Add(new Aion.Application.Gameplay.AchievementRecord(first.Achievement, 0, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(Now + 100)));
        store.Achievements.Add(new Aion.Application.Gameplay.AchievementRecord(second.Achievement, 1, null, 0, DateTimeOffset.FromUnixTimeMilliseconds(Now + 100)));

        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 150, level: 55);
        var claim = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(second.Achievement).U8(1).ToArray(),
            Now + 170);
        var contents = Assert.Single(claim, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        var first56b6 = Assert.Single(Parse56b6(contents), e => e.GroupId == first.SeasonGroupId);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 170);
        Assert.Equal(todayKey, first56b6.Value1);
        Assert.Equal(todayKey, first56b6.Value2);
    }
}
