using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AccountContentsInfoWireTests
{
    const long Now = 1_760_184_000_000; // 2025-10-11

    static List<(int GroupId, int Value1, int Value2)> ParseEntries(byte[] packet)
    {
        Assert.Equal(GameSession.AccountContentsInfoListNt, BitConverter.ToUInt16(packet, 0));
        var count = packet[2];
        var list = new List<(int, int, int)>();
        var i = 3;
        for (var n = 0; n < count; n++)
        {
            i++; // season type
            var groupId = BitConverter.ToInt32(packet, i);
            i += 4 + 4 + 4 + 8;
            list.Add((groupId, BitConverter.ToInt32(packet, i), BitConverter.ToInt32(packet, i + 4)));
            i += 8;
        }
        return list;
    }

    [Fact]
    public async Task Check_in_sends_today_as_value1_and_zero_reward_day()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 100);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var contents = Assert.Single(reply, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        Assert.All(ParseEntries(contents), e =>
        {
            Assert.Equal(todayKey, e.Value1);
            Assert.Equal(0, e.Value2);
        });
    }

    [Fact]
    public async Task After_reward_matching_board_sets_value2_to_today()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100)[0].Board;
        var todayKey = ContentResetSchedule.GameDayKey(Now + 101);
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var reward = await session.HandleAsync(new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(), Now + 101);
        var contents = Assert.Single(reward, p => GameSessionTestHelpers.Op(p) == GameSession.AccountContentsInfoListNt);
        var entry = Assert.Single(ParseEntries(contents), e => e.GroupId == board.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value1);
        Assert.Equal(todayKey, entry.Value2);
    }
}
