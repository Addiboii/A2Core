using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AccountContentsInfoTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC → game day 20261030

    [Fact]
    public void Game_day_key_rolls_at_07_00_utc()
    {
        var before = DateTimeOffset.Parse("2026-10-16T06:59:00Z").ToUnixTimeMilliseconds();
        var after = DateTimeOffset.Parse("2026-10-16T07:00:00Z").ToUnixTimeMilliseconds();
        Assert.Equal(20_261_015, ContentResetSchedule.GameDayKey(before));
        Assert.Equal(20_261_016, ContentResetSchedule.GameDayKey(after));
    }

    [Fact]
    public void Unchecked_board_sends_zero_day_keys()
    {
        Assert.Equal((0, 0), GameSession.AttendanceAccountValues(0, 0, 0, Now));
    }

    [Fact]
    public void Checked_in_today_unrewarded_sends_today_and_zero_reward_day()
    {
        var (check, reward) = GameSession.AttendanceAccountValues(1, 0, Now, Now);
        Assert.Equal(20_261_030, check);
        Assert.Equal(0, reward);
    }

    [Fact]
    public void Checked_in_and_rewarded_today_sends_matching_day_keys()
    {
        var (check, reward) = GameSession.AttendanceAccountValues(1, 1, Now, Now);
        Assert.Equal(20_261_030, check);
        Assert.Equal(20_261_030, reward);
    }

    [Fact]
    public void Account_contents_list_writes_day_keys_not_counts()
    {
        var packet = GameSession.AccountContentsInfoList(1, 1204, 70, Now, _ => (20_261_030, 20_261_030));
        Assert.True(packet.Length > 3);
        Assert.Equal(4, packet[2]);
        var tail = packet[^8..];
        Assert.Equal(20_261_030, BitConverter.ToInt32(tail, 0));
        Assert.Equal(20_261_030, BitConverter.ToInt32(tail, 4));
    }
}
