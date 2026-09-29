using Meridian.Core;

namespace Meridian.Time;

/// <summary>Factory for the built-in tumbling periods.</summary>
public static class Period
{
    public static IPeriod Day { get; } = new DayPeriod();
    public static IPeriod Week { get; } = new WeekPeriod();
    public static IPeriod Month { get; } = new MonthPeriod();
    public static IPeriod Season { get; } = new SeasonPeriod();

    public static IPeriod Hour { get; } = new FixedPeriod(TimeSpan.FromHours(1));

    /// <summary>Fixed clock-aligned buckets — every 1 minute, 5 minutes, 1 hour… The span must divide a day,
    /// so buckets line up with midnight. Like every period they are local clock positions (docs/TIME.md).</summary>
    public static IPeriod Every(TimeSpan span) => span == TimeSpan.FromHours(1) ? Hour : new FixedPeriod(span);

    /// <summary>
    /// The period with this name, or null: <c>day</c>, <c>week</c>, <c>month</c>, <c>season</c>, <c>hour</c>, or a
    /// span dividing a day — <c>15m</c>, <c>6h</c>, <c>30s</c>. Every period's <see cref="IPeriod.Name"/> resolves
    /// back to it, so a time axis's grain says which buckets it has.
    /// </summary>
    public static IPeriod? Named(string? name)
    {
        switch (name?.ToLowerInvariant())
        {
            case "day": return Day;
            case "week": return Week;
            case "month": return Month;
            case "season": return Season;
            case "hour": return Hour;
        }
        if (name is { Length: > 1 } && long.TryParse(name[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            var span = char.ToLowerInvariant(name[^1]) switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => TimeSpan.Zero,
            };
            if (span > TimeSpan.Zero && TimeSpan.TicksPerDay % span.Ticks == 0) return Every(span);
        }
        return null;
    }
}

/// <summary>
/// Sub-daily buckets of a fixed span, aligned to local midnight. Because buckets are wall-clock positions,
/// on the night clocks go back the repeated hour folds into one bucket (holding two hours of readings),
/// and on the night they go forward the skipped hour has no bucket. Bucket in UTC when elapsed-time
/// windows matter more than the local clock.
/// </summary>
public sealed class FixedPeriod : IPeriod
{
    public FixedPeriod(TimeSpan span)
    {
        if (span <= TimeSpan.Zero || TimeSpan.TicksPerDay % span.Ticks != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(span), span, "A fixed period must divide a day evenly (e.g. 1, 5, 15 minutes or 1, 6 hours).");
        }
        Span = span;
    }

    public TimeSpan Span { get; }

    public string Name => Span.TotalHours >= 1 && Span.Ticks % TimeSpan.TicksPerHour == 0
        ? $"{(long)Span.TotalHours}h"
        : Span.Ticks % TimeSpan.TicksPerMinute == 0 ? $"{(long)Span.TotalMinutes}m" : $"{(long)Span.TotalSeconds}s";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        long local = TimeZones.ToLocalTicks(instant.UtcTicks, ctx.Zone);
        long start = local - local % Span.Ticks;
        return new DateInterval(
            TimeZones.ToInstant(new DateTime(start), ctx.Zone),
            TimeZones.ToInstant(new DateTime(start + Span.Ticks), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class DayPeriod : IPeriod
{
    public string Name => "day";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var day = TimeZones.ToLocal(instant, ctx.Zone).Date;
        return new DateInterval(
            TimeZones.ToInstant(day, ctx.Zone),
            TimeZones.ToInstant(day.AddDays(1), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class WeekPeriod : IPeriod
{
    public string Name => "week";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var date = TimeZones.ToLocal(instant, ctx.Zone).Date;
        int offset = ((int)date.DayOfWeek - (int)ctx.WeekStart + 7) % 7;
        var start = date.AddDays(-offset);
        return new DateInterval(
            TimeZones.ToInstant(start, ctx.Zone),
            TimeZones.ToInstant(start.AddDays(7), ctx.Zone));
    }

    public IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx) =>
        PeriodHelper.Enumerate(this, range, ctx);
}

internal sealed class MonthPeriod : IPeriod
{
    public string Name => "month";

    public DateInterval BucketFor(Instant instant, CalendarContext ctx)
    {
        var date = TimeZones.ToLocal(instant, ctx.Zone).Date;
        var start = new DateTime(date.Year, date.Month, 1);
        return new DateInterval(
            TimeZones.ToInstant(start, ctx.Zone),
            TimeZones.ToInstant(start.AddMonths(1), ctx.Zone));
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
