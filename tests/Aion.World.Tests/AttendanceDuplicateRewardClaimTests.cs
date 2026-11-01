using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceDuplicateRewardClaimTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task Second_claim_of_the_same_attendance_step_is_refused_and_grants_no_items()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;
        await session.HandleAsync([0x4d, 0xe3], Now + 100);

        var itemId = AchievementTables.Embedded.Of(board.Achievement)!.StepRewards(1)[0].ItemId;
        var before = store.Items.Where(i => i.TemplateId == itemId).Sum(i => i.Quantity);

        var first = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(),
            Now + 101);
        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(first, p => Op(p) == GameSession.TakeAchievementRewardRs), 2));
        var afterFirst = store.Items.Where(i => i.TemplateId == itemId).Sum(i => i.Quantity);
        Assert.True(afterFirst > before);

        var second = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(),
            Now + 102);
        Assert.Equal(1, BitConverter.ToInt32(Assert.Single(second, p => Op(p) == GameSession.TakeAchievementRewardRs), 2));
        Assert.DoesNotContain(second, p => Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.Equal(afterFirst, store.Items.Where(i => i.TemplateId == itemId).Sum(i => i.Quantity));
        Assert.Equal(1, session.AchievementCount(board.Achievement));
    }

    [Fact]
    public async Task Attendance_step_reward_without_check_in_is_refused()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;
        var reward = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(),
            Now + 100);
        Assert.Equal(1, BitConverter.ToInt32(Assert.Single(reward, p => Op(p) == GameSession.TakeAchievementRewardRs), 2));
        Assert.DoesNotContain(reward, p => Op(p) == GameSession.AchievementCountUpdateNt);
    }
}
