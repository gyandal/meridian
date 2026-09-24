using Meridian.Core;

namespace Meridian.Time;

/// <summary>
/// Everything a period needs to turn an instant into a bucket: the timezone the "day" is measured
/// in, which weekday a week starts on, and the domain season calendar. Passed explicitly so bucketing
/// is deterministic and testable — no ambient <c>DateTime.Now</c> / machine-locale surprises.
/// </summary>
public sealed record CalendarContext(TimeZoneInfo Zone, DayOfWeek WeekStart, ISeasonCalendar Season)
{
    /// <summary>UTC, Monday-start, July–June season — a sane default for testing.</summary>
    public static CalendarContext Default { get; } =
        new(TimeZoneInfo.Utc, DayOfWeek.Monday, new FixedSeasonCalendar(startMonth: 7, startDay: 1));
}

/// <summary>Maps an instant to the season interval that contains it. Season boundaries are domain
/// configuration, not ad-hoc <c>AddMonths</c> loops.</summary>
public interface ISeasonCalendar
{
    DateInterval SeasonFor(Instant instant, CalendarContext ctx);
    string Label(DateInterval season, CalendarContext ctx);
}

/// <summary>A season that starts on a fixed month/day each year (e.g. 1 July → 30 June).</summary>
public sealed class FixedSeasonCalendar(int startMonth = 7, int startDay = 1) : ISeasonCalendar
{
    public DateInterval SeasonFor(Instant instant, CalendarContext ctx)
    {
        var local = TimeZoneMath.ToLocal(instant, ctx.Zone);
        var startThisYear = new DateTime(local.Year, startMonth, startDay);
        var startLocal = local >= startThisYear
            ? startThisYear
            : new DateTime(local.Year - 1, startMonth, startDay);
        var endLocal = startLocal.AddYears(1);
        return new DateInterval(
            TimeZoneMath.ToInstant(startLocal, ctx.Zone),
            TimeZoneMath.ToInstant(endLocal, ctx.Zone));
    }

    public string Label(DateInterval season, CalendarContext ctx)
    {
        var startYear = TimeZoneMath.ToLocal(season.Start, ctx.Zone).Year;
        // e.g. 2025/26
        return $"{startYear}/{(startYear + 1) % 100:00}";
    }
}
