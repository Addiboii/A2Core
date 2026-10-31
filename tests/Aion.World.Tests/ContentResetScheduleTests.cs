using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ContentResetScheduleTests
{
    static long Ms(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeMilliseconds();

    [Fact]
    public void Daily_period_key_rolls_at_07_00_utc()
    {
        Assert.Equal("2026-10-15", ContentResetSchedule.PeriodKey(1, DateTimeOffset.Parse("2026-10-16T06:59:00Z")));
        Assert.Equal("2026-10-16", ContentResetSchedule.PeriodKey(1, DateTimeOffset.Parse("2026-10-16T07:00:00Z")));
    }

    [Fact]
    public void Weekly_period_key_starts_wednesday_07_00_utc()
    {
        Assert.Equal("w2026-10-14", ContentResetSchedule.PeriodKey(2, DateTimeOffset.Parse("2026-10-16T12:00:00Z")));
    }

    [Fact]
    public void Weekly_start_ms_matches_abyss_and_shop_conventions()
    {
        var now = Ms("2026-10-16T12:00:00Z");
        Assert.Equal(Ms("2026-10-14T07:00:00Z"), ContentResetSchedule.WeeklyStartMs(now));
    }

    [Fact]
    public void Achievement_daily_cycle_resets_after_boundary()
    {
        var before = Ms("2026-10-16T06:00:00Z");
        var after = Ms("2026-10-16T08:00:00Z");
        var start = ContentResetSchedule.AchievementCycleStartMs(1, after);
        Assert.True(start > ContentResetSchedule.AchievementCycleStartMs(1, before));
        Assert.Equal(Ms("2026-10-16T07:00:00Z"), start);
    }

    [Fact]
    public void Coordinator_applies_each_boundary_once()
    {
        var coord = new ContentResetCoordinator();
        coord.Load(new(0, 0));
        var t1 = Ms("2026-10-16T08:00:00Z");
        var plan1 = coord.Plan(t1);
        Assert.True(plan1.DailyCrossed);
        coord.MarkApplied(plan1);
        var plan2 = coord.Plan(t1);
        Assert.False(plan2.DailyCrossed);
        var t2 = Ms("2026-10-23T08:00:00Z");
        var plan3 = coord.Plan(t2);
        Assert.True(plan3.WeeklyCrossed);
    }
}
