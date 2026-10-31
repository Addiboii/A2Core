using Aion.Application.Gameplay;

namespace Aion.World;

/// <summary>
/// Central scheduler for world-scoped periodic work: legion attendance rollover, weekly contribution totals,
/// and notifying online sessions to apply lazy character resets.
/// </summary>
public sealed class ContentResetCoordinator
{
    const long TickIntervalMs = 30_000;
    long tickAt;
    long dailyAppliedMs;
    long weeklyAppliedMs;
    bool loaded;
    public bool Dirty { get; private set; }
    public bool PendingDailyWorldWork { get; private set; }
    public bool PendingWeeklyWorldWork { get; private set; }

    public bool NeedsTick(long now) => now >= tickAt;

    public void Load(WorldResetCursorRecord? record)
    {
        dailyAppliedMs = record?.DailyBoundaryMs ?? 0;
        weeklyAppliedMs = record?.WeeklyBoundaryMs ?? 0;
        loaded = true;
        Dirty = false;
        tickAt = 0;
    }

    public WorldResetCursorRecord Snapshot() => new(dailyAppliedMs, weeklyAppliedMs);

    public sealed record TickPlan(bool DailyCrossed, bool WeeklyCrossed, long DailyBoundary, long WeeklyBoundary);

    /// <summary>Detect period boundaries not yet applied (pure; safe under world lock).</summary>
    public TickPlan Plan(long now)
    {
        if (!loaded) Load(null);
        var daily = ContentResetSchedule.PeriodStartMs(1, now);
        var weekly = ContentResetSchedule.PeriodStartMs(2, now);
        var dailyCross = daily > dailyAppliedMs;
        var weeklyCross = weekly > weeklyAppliedMs;
        return new TickPlan(dailyCross, weeklyCross, daily, weekly);
    }

    public void MarkApplied(TickPlan plan)
    {
        if (plan.DailyCrossed) { dailyAppliedMs = plan.DailyBoundary; PendingDailyWorldWork = true; Dirty = true; }
        if (plan.WeeklyCrossed) { weeklyAppliedMs = plan.WeeklyBoundary; PendingWeeklyWorldWork = true; Dirty = true; }
    }

    public void ScheduleNext(long now) => tickAt = now + TickIntervalMs;

    public void ClearPendingWorldWork(bool daily, bool weekly)
    {
        if (daily) PendingDailyWorldWork = false;
        if (weekly) PendingWeeklyWorldWork = false;
    }

    public void ClearDirty() => Dirty = false;
}
