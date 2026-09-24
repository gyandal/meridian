using Meridian.Core;
using DateTimeZone = NodaTime.DateTimeZone;

namespace Meridian.Time;

/// <summary>
/// The report's calendar: the time zone instants are bucketed in, which weekday a week starts on, and
/// the domain season calendar. Part of the query (and the cache key), never applied by a renderer —
/// the zone decides which bucket a reading falls in. See docs/TIME.md.
/// </summary>
public sealed record CalendarContext(DateTimeZone Zone, DayOfWeek WeekStart, ISeasonCalendar Season)
{
    /// <summary>UTC, Monday-start, July–June season — a sane default for testing.</summary>
    public static CalendarContext Default { get; } =
        new(DateTimeZone.Utc, DayOfWeek.Monday, new FixedSeasonCalendar(startMonth: 7, startDay: 1));

    /// <summary>A calendar in an IANA zone, e.g. <c>CalendarContext.For("Europe/London")</c>.</summary>
    public static CalendarContext For(string zoneId, DayOfWeek weekStart = DayOfWeek.Monday, ISeasonCalendar? season = null) =>
        new(TimeZones.Get(zoneId), weekStart, season ?? new FixedSeasonCalendar(startMonth: 7, startDay: 1));

    /// <summary>The same calendar with no zone: for local (wall-clock) values, which are never converted.</summary>
    public CalendarContext Floating => TimeZones.IsUtc(Zone) ? this : this with { Zone = DateTimeZone.Utc };
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
        var local = TimeZones.ToLocal(instant, ctx.Zone);
        var startThisYear = new DateTime(local.Year, startMonth, startDay);
        var startLocal = local >= startThisYear
            ? startThisYear
            : new DateTime(local.Year - 1, startMonth, startDay);
        var endLocal = startLocal.AddYears(1);
        return new DateInterval(
            TimeZones.ToInstant(startLocal, ctx.Zone),
            TimeZones.ToInstant(endLocal, ctx.Zone));
    }

    public string Label(DateInterval season, CalendarContext ctx)
    {
        var startYear = TimeZones.ToLocal(season.Start, ctx.Zone).Year;
        // e.g. 2025/26
        return $"{startYear}/{(startYear + 1) % 100:00}";
    }
}
