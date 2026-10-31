using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceSequentialClaimTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC — four Global attendance boards open (Light)
    const int AtreiaAchievement = 510_350_100;
    const int LaunchAchievement = 510_340_100;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

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

    [Fact]
    public async Task Claim_second_tab_keeps_first_tab_rewarded_in_db_and_packets()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 100);
        var atreiaBoard = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.Achievement == AtreiaAchievement).Board;
        var launchBoard = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.Achievement == LaunchAchievement).Board;

        await session.HandleAsync([0x4d, 0xe3], Now + 100);

        await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(AtreiaAchievement).U8(1).ToArray(),
            Now + 101);

        var claimB = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(LaunchAchievement).U8(1).ToArray(),
            Now + 102);

        var update = Assert.Single(claimB, p => Op(p) == GameSession.AchievementCountUpdateNt);
        var states = Parse5684(update);
        var atreia5684 = Assert.Single(states, s => s.AchievementId == AtreiaAchievement);
        Assert.Equal(1, atreia5684.Count);
        Assert.Equal((byte)1, atreia5684.RewardedStep);

        var contents = Assert.Single(claimB, p => Op(p) == GameSession.AccountContentsInfoListNt);
        var atreia56b6 = Assert.Single(Parse56b6(contents), e => e.GroupId == atreiaBoard.SeasonGroupId);
        Assert.Equal(todayKey, atreia56b6.Value1);
        Assert.Equal(todayKey, atreia56b6.Value2);

        var launch56b6 = Assert.Single(Parse56b6(contents), e => e.GroupId == launchBoard.SeasonGroupId);
        Assert.Equal(todayKey, launch56b6.Value1);
        Assert.Equal(todayKey, launch56b6.Value2);

        var storedAtreia = Assert.Single(store.Achievements, a => a.Id == AtreiaAchievement);
        Assert.Equal(1, storedAtreia.Count);
        Assert.Equal(1, storedAtreia.RewardedStep);
        var storedLaunch = Assert.Single(store.Achievements, a => a.Id == LaunchAchievement);
        Assert.Equal(1, storedLaunch.Count);
        Assert.Equal(1, storedLaunch.RewardedStep);
    }
}
