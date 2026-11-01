using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Global daily/weekly/monthly boundaries (tools/data/resets.json, tools/reset_data.py).</summary>
public sealed class ContentResetSchedule
{
    public sealed record DailyRule([property: JsonPropertyName("hour_utc")] int HourUtc);
    public sealed record WeeklyRule([property: JsonPropertyName("hour_utc")] int HourUtc, [property: JsonPropertyName("weekday")] int Weekday);
    public sealed record MonthlyRule([property: JsonPropertyName("hour_utc")] int HourUtc, [property: JsonPropertyName("day_of_month")] int DayOfMonth);
    sealed record ScheduleFile(
        [property: JsonPropertyName("schedule")] Schedule Schedule);
    sealed record Schedule(
        [property: JsonPropertyName("daily")] DailyRule Daily,
        [property: JsonPropertyName("weekly")] WeeklyRule Weekly,
        [property: JsonPropertyName("monthly")] MonthlyRule Monthly);

    readonly Schedule schedule;

    ContentResetSchedule(ScheduleFile file) => schedule = file.Schedule;

    static readonly Lazy<ContentResetSchedule> embedded = new(() =>
    {
        using var stream = typeof(ContentResetSchedule).Assembly.GetManifestResourceStream("resets.json")
            ?? throw new InvalidOperationException("Missing embedded game data resets.json");
        return new ContentResetSchedule(JsonSerializer.Deserialize<ScheduleFile>(stream) ?? throw new InvalidDataException("resets.json"));
    });

    public static ContentResetSchedule Embedded => embedded.Value;

    public int DailyHourUtc => schedule.Daily.HourUtc;
    public int WeeklyWeekday => schedule.Weekly.Weekday;

    /// <summary>Game calendar date: UTC shifted back by the daily reset hour.</summary>
    public static DateTime GameDayUtc(DateTimeOffset now) => now.UtcDateTime.AddHours(-Embedded.schedule.Daily.HourUtc).Date;

    /// <summary>Game day as yyyyMMdd (07:00 UTC anchor); used for attendance AccountContentsInfo value1/value2 like guild attendance_date.</summary>
    public static int GameDayKey(long nowMs)
    {
        var day = GameDayUtc(DateTimeOffset.FromUnixTimeMilliseconds(nowMs));
        return day.Year * 10_000 + day.Month * 100 + day.Day;
    }

    /// <summary>Unix ms at the start of the current daily period (07:00 UTC anchor).</summary>
    public static long DailyStartMs(long nowMs) => PeriodStartMs(1, nowMs);

    /// <summary>Unix ms at the start of the current weekly period (Wednesday 07:00 UTC).</summary>
    public static long WeeklyStartMs(long nowMs) => PeriodStartMs(2, nowMs);

    /// <summary>Unix ms at the start of the current monthly period (day 1, 07:00 UTC).</summary>
    public static long MonthlyStartMs(long nowMs) => PeriodStartMs(3, nowMs);

    /// <summary>Period key for shop limits, expedition tickets, morph counts (kind 0=all, 1=day, 2=week, 3=month).</summary>
    public static string PeriodKey(int kind, DateTimeOffset now)
    {
        var day = GameDayUtc(now);
        return kind switch
        {
            1 => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            2 => "w" + day.AddDays(-(((int)day.DayOfWeek - Embedded.schedule.Weekly.Weekday + 7) % 7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            3 => day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            _ => "all",
        };
    }

    /// <summary>Achievement reset cycle (1 daily, 2 weekly, 3 monthly, 4 yearly → monthly anchor for now).</summary>
    public static long AchievementCycleStartMs(int resetCycle, long nowMs) =>
        resetCycle switch
        {
            1 => DailyStartMs(nowMs),
            2 => WeeklyStartMs(nowMs),
            3 or 4 => MonthlyStartMs(nowMs),
            _ => 0,
        };

    public static long PeriodStartMs(int kind, long nowMs)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
        var day = GameDayUtc(now);
        return kind switch
        {
            1 => new DateTimeOffset(day.AddHours(Embedded.schedule.Daily.HourUtc), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            2 => new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek - Embedded.schedule.Weekly.Weekday + 7) % 7)).AddHours(Embedded.schedule.Weekly.HourUtc), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            3 => new DateTimeOffset(new DateTime(day.Year, day.Month, Math.Min(Embedded.schedule.Monthly.DayOfMonth, DateTime.DaysInMonth(day.Year, day.Month))).AddHours(Embedded.schedule.Monthly.HourUtc), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            _ => 0,
        };
    }

    /// <summary>Next boundary after <paramref name="nowMs"/> for daily (1) or weekly (2) periods.</summary>
    public static long NextBoundaryMs(int kind, long nowMs)
    {
        var start = PeriodStartMs(kind, nowMs);
        if (nowMs <= start) return start;
        return kind switch
        {
            1 => start + 86_400_000L,
            2 => start + 7 * 86_400_000L,
            _ => start,
        };
    }
}
