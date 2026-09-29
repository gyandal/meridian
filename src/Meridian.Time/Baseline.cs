using System.Globalization;
using Meridian.Core;

namespace Meridian.Time;

/// <summary>The calendar unit a <see cref="Baseline"/> steps back in.</summary>
public enum BaselineUnit
{
    Day,
    Week,
    Month,
    Year,

    /// <summary>A season of the report's <see cref="ISeasonCalendar"/>.</summary>
    Season,
}

/// <summary>
/// What a report is compared with: the same report, <see cref="Count"/> <see cref="Unit"/>s earlier. Steps are
/// calendar steps in the report's zone — a month back from 15 March is 15 February, a year back from a Monday
/// is usually not a Monday — which is why weekly comparisons step back in weeks (<c>WeeksBack(52)</c>, the
/// retail "same week last year"), and monthly or seasonal ones in months, years or seasons.
/// </summary>
public sealed record Baseline(int Count, BaselineUnit Unit)
{
    public static Baseline DaysBack(int count) => new(Positive(count), BaselineUnit.Day);

    public static Baseline WeeksBack(int count) => new(Positive(count), BaselineUnit.Week);

    public static Baseline MonthsBack(int count) => new(Positive(count), BaselineUnit.Month);

    public static Baseline YearsBack(int count) => new(Positive(count), BaselineUnit.Year);

    public static Baseline SeasonsBack(int count) => new(Positive(count), BaselineUnit.Season);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Count} {Unit.ToString().ToLowerInvariant()}s back");

    /// <summary>
    /// The shift, in local calendar months and days, for a report over <paramref name="timeframe"/>. A season
    /// is resolved through the calendar: to whole years when seasons start on the same date each year, otherwise
    /// to the days between the season containing the timeframe's start and the one <see cref="Count"/> earlier.
    /// </summary>
    public CalendarShift Resolve(DateInterval timeframe, CalendarContext calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        switch (Unit)
        {
            case BaselineUnit.Day: return new(0, Count);
            case BaselineUnit.Week: return new(0, 7 * Count);
            case BaselineUnit.Month: return new(Count, 0);
            case BaselineUnit.Year: return new(12 * Count, 0);
        }

        var season = calendar.Season.SeasonFor(timeframe.Start, calendar);
        var earlier = season;
        for (int i = 0; i < Count; i++) earlier = calendar.Season.SeasonFor(new Instant(earlier.Start.UtcTicks - 1), calendar);
        var start = TimeZones.ToLocal(season.Start, calendar.Zone);
        var earlierStart = TimeZones.ToLocal(earlier.Start, calendar.Zone);
        return earlierStart.AddYears(Count) == start
            ? new CalendarShift(12 * Count, 0)
            : new CalendarShift(0, (int)(start.Date - earlierStart.Date).TotalDays);
    }

    private static int Positive(int count) =>
        count > 0 ? count : throw new ArgumentOutOfRangeException(nameof(count), count, "A baseline is at least one step back.");
}

/// <summary>A step back in local calendar time: <see cref="Months"/> months and <see cref="Days"/> days.</summary>
public readonly record struct CalendarShift(int Months, int Days)
{
    /// <summary>The timeframe this far back, stepping each end in local time.</summary>
    public DateInterval Back(DateInterval timeframe, CalendarContext calendar) =>
        new(Step(timeframe.Start, calendar, -1), Step(timeframe.End, calendar, -1));

    /// <summary>
    /// Moves a point's time forward by the shift, so a baseline lines up with the report it's compared with:
    /// local values (calendar buckets, dates) move on the wall clock; instants move in the calendar's zone.
    /// </summary>
    public long Forward(long ticks, TimeAxis time, CalendarContext calendar)
    {
        if (ticks == PointBlock.NoAt) return ticks;
        return time.Kind == TimeKind.Local
            ? new DateTime(ticks).AddMonths(Months).AddDays(Days).Ticks
            : Step(new Instant(ticks), calendar, +1).UtcTicks;
    }

    /// <summary>
    /// Why a report bucketed by <paramref name="grain"/> can't be compared across this shift, or null if it can:
    /// the shifted buckets must be the report's own buckets. <paramref name="grain"/> is the period name of the
    /// report's time buckets, or null when it has none (raw points, or time totalled away).
    /// </summary>
    public string? Mismatch(string? grain, BaselineUnit unit) => grain switch
    {
        null => null,
        "week" when Months != 0 || Days % 7 != 0 =>
            "Weekly buckets a month or year apart start on different weekdays. Compare with Baseline.WeeksBack(52) — the same week last year.",
        "month" when Days != 0 =>
            "Monthly buckets can only be compared whole months apart. Use MonthsBack, YearsBack or SeasonsBack.",
        "season" when unit != BaselineUnit.Season && (Days != 0 || Months % 12 != 0) =>
            "Seasonal buckets can only be compared whole seasons apart. Use SeasonsBack.",
        "week" or "month" or "season" => null,
        _ when Months != 0 =>
            $"'{grain}' buckets a month or year apart fall on different weekdays, and 29 February has no partner. " +
            "Compare with Baseline.WeeksBack(52) for the same weekday last year, or resample to months.",
        _ => null,
    };

    private Instant Step(Instant instant, CalendarContext calendar, int direction) =>
        TimeZones.ToInstant(TimeZones.ToLocal(instant, calendar.Zone).AddMonths(direction * Months).AddDays(direction * Days), calendar.Zone);
}
