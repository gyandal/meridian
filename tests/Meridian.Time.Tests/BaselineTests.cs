using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Time.Tests;

public class BaselineTests
{
    private static readonly CalendarContext London = CalendarContext.For("Europe/London");

    private static Instant Utc(int y, int m, int d, int h = 0) => Instant.FromUtc(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc));

    private static DateInterval Season2425 => new(Utc(2024, 7, 1), Utc(2025, 7, 1));

    [Fact]
    public void Units_resolve_to_calendar_months_and_days()
    {
        Assert.Equal(new CalendarShift(0, 3), Baseline.DaysBack(3).Resolve(Season2425, London));
        Assert.Equal(new CalendarShift(0, 364), Baseline.WeeksBack(52).Resolve(Season2425, London));
        Assert.Equal(new CalendarShift(2, 0), Baseline.MonthsBack(2).Resolve(Season2425, London));
        Assert.Equal(new CalendarShift(24, 0), Baseline.YearsBack(2).Resolve(Season2425, London));
        Assert.Equal(new CalendarShift(12, 0), Baseline.SeasonsBack(1).Resolve(Season2425, London)); // July–June: a year
        Assert.Throws<ArgumentOutOfRangeException>(() => Baseline.YearsBack(0));
    }

    [Fact]
    public void Seasons_that_start_on_different_dates_resolve_to_the_days_between_their_starts()
    {
        var calendar = CalendarContext.Default with { Season = new ListedSeasons(new(2023, 8, 11), new(2024, 8, 16), new(2025, 8, 15)) };
        var season = new DateInterval(Utc(2024, 8, 16), Utc(2025, 8, 15));

        Assert.Equal(new CalendarShift(0, 371), Baseline.SeasonsBack(1).Resolve(season, calendar)); // 11 Aug 2023 → 16 Aug 2024
    }

    [Fact]
    public void Stepping_back_keeps_the_local_time_across_a_clock_change()
    {
        // April in London runs from 23:00 UTC on 31 March to 23:00 UTC on 30 April (BST); March starts at midnight UTC (GMT).
        var april = new DateInterval(Utc(2025, 3, 31, 23), Utc(2025, 4, 30, 23));
        var march = Baseline.MonthsBack(1).Resolve(april, London).Back(april, London);

        Assert.Equal(Utc(2025, 3, 1), march.Start);
        Assert.Equal(Utc(2025, 3, 31, 23), march.End); // 1 April 00:00 BST, where it started
    }

    [Fact]
    public void Forward_moves_local_buckets_on_the_wall_clock_and_leaves_timeless_points_alone()
    {
        var shift = new CalendarShift(12, 0);
        long march2024 = new DateTime(2024, 3, 1).Ticks;

        Assert.Equal(new DateTime(2025, 3, 1).Ticks, shift.Forward(march2024, TimeAxis.Local("month", "Europe/London"), London));
        Assert.Equal(PointBlock.NoAt, shift.Forward(PointBlock.NoAt, TimeAxis.Local("month"), London));
        Assert.Equal(Utc(2025, 7, 1, 11).UtcTicks, shift.Forward(Utc(2024, 7, 1, 11).UtcTicks, TimeAxis.Instant, London));
    }

    [Theory]
    [InlineData(null, 12, 0, BaselineUnit.Year, true)]    // raw points: anything goes
    [InlineData("month", 12, 0, BaselineUnit.Year, true)]
    [InlineData("month", 0, 7, BaselineUnit.Week, false)]
    [InlineData("week", 0, 364, BaselineUnit.Week, true)]
    [InlineData("week", 12, 0, BaselineUnit.Year, false)]
    [InlineData("week", 0, 3, BaselineUnit.Day, false)]
    [InlineData("day", 0, 3, BaselineUnit.Day, true)]
    [InlineData("day", 12, 0, BaselineUnit.Year, false)]
    [InlineData("15m", 0, 7, BaselineUnit.Week, true)]
    [InlineData("season", 0, 371, BaselineUnit.Season, true)]
    [InlineData("season", 6, 0, BaselineUnit.Month, false)]
    public void Only_shifts_that_land_on_the_reports_own_buckets_are_allowed(string? grain, int months, int days, BaselineUnit unit, bool allowed)
    {
        Assert.Equal(allowed, new CalendarShift(months, days).Mismatch(grain, unit) is null);
    }

    private sealed class ListedSeasons(params DateTime[] starts) : ISeasonCalendar
    {
        public DateInterval SeasonFor(Instant instant, CalendarContext ctx)
        {
            int i = Array.FindLastIndex(starts, s => Instant.FromUtc(s).UtcTicks <= instant.UtcTicks);
            return new DateInterval(Instant.FromUtc(starts[i]), Instant.FromUtc(i + 1 < starts.Length ? starts[i + 1] : starts[i].AddYears(1)));
        }

        public string Label(DateInterval season, CalendarContext ctx) => "";
    }
}
