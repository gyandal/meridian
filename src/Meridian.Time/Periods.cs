using Meridian.Core;

namespace Meridian.Time;

/// <summary>Factory for the built-in tumbling periods.</summary>
public static class Period
{
    public static IPeriod Day { get; } = new DayPeriod();
    public static IPeriod Week { get; } = new WeekPeriod();
    public static IPeriod Month { get; } = new MonthPeriod();
    public static IPeriod Season { get; } = new SeasonPeriod();
}

internal sealed class DayPeriod : IPeriod
{
    public string Name => "day";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var day = TimeZoneMath.ToLocal(instant, ctx.Zone).Date;
        return new DateInterval(
            TimeZoneMath.ToInstant(day, ctx.Zone),
            TimeZoneMath.ToInstant(day.AddDays(1), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class WeekPeriod : IPeriod
{
    public string Name => "week";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var date = TimeZoneMath.ToLocal(instant, ctx.Zone).Date;
        int offset = ((int)date.DayOfWeek - (int)ctx.WeekStart + 7) % 7;
        var start = date.AddDays(-offset);
        return new DateInterval(
            TimeZoneMath.ToInstant(start, ctx.Zone),
            TimeZoneMath.ToInstant(start.AddDays(7), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class MonthPeriod : IPeriod
{
    public string Name => "month";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var date = TimeZoneMath.ToLocal(instant, ctx.Zone).Date;
        var start = new DateTime(date.Year, date.Month, 1);
        return new DateInterval(
            TimeZoneMath.ToInstant(start, ctx.Zone),
            TimeZoneMath.ToInstant(start.AddMonths(1), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class SeasonPeriod : IPeriod
{
    public string Name => "season";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx) =>
        ctx.Season.SeasonFor(instant, ctx);

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}
