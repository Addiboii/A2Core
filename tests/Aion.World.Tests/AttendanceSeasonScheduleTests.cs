using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceSeasonScheduleTests
{
    // 2026-10-30 14:00 UTC: the Global boards 1022..1025 (Light) are open (Attendance.dat of the 2026-10-28 client).
    const long Now = 1_793_368_800_000;

    [Fact]
    public void Open_boards_carry_their_season_schedule_group()
    {
        var boards = AchievementTables.Embedded.OpenBoards(1, Now);
        Assert.Contains(boards, b => b.Achievement == 510350100 && b.SeasonGroup == "Global_Attendance_Group_4" && b.SeasonNo == 1);
        Assert.All(boards, b => Assert.False(string.IsNullOrEmpty(b.SeasonGroup)));
    }

    [Fact]
    public void Season_schedule_lists_the_latest_attendance_season_per_type()
    {
        var packet = GameSession.SeasonSchedule(1, Now);
        Assert.Equal(GameSession.SeasonScheduleNt, BitConverter.ToUInt16(packet, 0));
        Assert.Equal(4, packet[2]);   // Attendance1..Attendance4
        Assert.Equal(22, packet[3]);  // ESeasonContentsType::Attendance1
        var name = "Global_Attendance_Group_1"u8.ToArray();
        Assert.Equal(name.Length, packet[4]);
        Assert.Equal(name, packet[5..(5 + name.Length)]);
        Assert.Equal(1, BitConverter.ToInt32(packet, 5 + name.Length));
    }

    [Fact]
    public void Season_schedule_names_the_previous_season_of_the_type()
    {
        var expected = new PacketWriter().U8(22).String("Global_Attendance_Group_1").I32(1).String("Attendance_Group_1").I32(3).String("").I32(0).ToArray();
        var packet = GameSession.SeasonSchedule(1, Now);
        Assert.Equal(expected, packet[3..(3 + expected.Length)]);
    }

    [Fact]
    public void Account_contents_list_has_one_row_per_latest_attendance_season_type()
    {
        var packet = GameSession.AccountContentsInfoList(1, 1204, 70, Now, _ => (20_261_030, 20_261_030));
        Assert.Equal(GameSession.AccountContentsInfoListNt, BitConverter.ToUInt16(packet, 0));
        Assert.Equal(4, packet[2]);
    }
}
