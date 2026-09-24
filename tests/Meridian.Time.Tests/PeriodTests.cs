using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Time.Tests;

public class PeriodTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static Instant Utc(int y, int m, int d, int h = 12) =>
        Instant.FromUtc(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Day_across_spring_forward_is_23_hours()
    {
        // London clocks jump forward 01:00→02:00 on 2026-03-29.
        var ctx = new CalendarContext(London, DayOfWeek.Monday, new FixedSeasonCalendar());
        var bucket = Period.Day.BucketFor(Utc(2026, 3, 29), ctx);

        Assert.Equal(TimeSpan.FromHours(23).Ticks, bucket.TicksDuration);
    }

    [Fact]
    public void Day_across_fall_back_is_25_hours()
    {
        // London clocks fall back 02:00→01:00 on 2026-10-25.
        var ctx = new CalendarContext(London, DayOfWeek.Monday, new FixedSeasonCalendar());
        var bucket = Period.Day.BucketFor(Utc(2026, 10, 25), ctx);

        Assert.Equal(TimeSpan.FromHours(25).Ticks, bucket.TicksDuration);
    }

    [Fact]
    public void Day_bucket_contains_its_instant()
    {
        var ctx = CalendarContext.Default;
        var i = Utc(2026, 8, 12, 23);
        Assert.True(Period.Day.BucketFor(i, ctx).Contains(i));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Week_bucket_starts_on_the_configured_day_and_lasts_seven_days(DayOfWeek weekStart)
    {
        var ctx = new CalendarContext(TimeZoneInfo.Utc, weekStart, new FixedSeasonCalendar());
        var i = Utc(2026, 8, 12); // some Wednesday-ish midweek instant
        var bucket = Period.Week.BucketFor(i, ctx);

        Assert.Equal(weekStart, bucket.Start.UtcDateTime.DayOfWeek);
        Assert.Equal(TimeSpan.FromDays(7).Ticks, bucket.TicksDuration); // UTC → no DST, exactly 7 days
        Assert.True(bucket.Contains(i));
    }

    [Fact]
    public void Month_bucket_spans_first_to_first()
    {
        var ctx = CalendarContext.Default;
        var bucket = Period.Month.BucketFor(Utc(2026, 8, 15), ctx);

        Assert.Equal(new DateTime(2026, 8, 1), bucket.Start.UtcDateTime);
        Assert.Equal(new DateTime(2026, 9, 1), bucket.End.UtcDateTime);
    }

    [Fact]
    public void Season_uses_domain_boundaries_and_label()
    {
        var ctx = CalendarContext.Default; // July-start season, UTC

        var autumn = Period.Season.BucketFor(Utc(2025, 8, 20), ctx);
        var spring = Period.Season.BucketFor(Utc(2026, 3, 10), ctx);

        // Both instants belong to the SAME 2025/26 season despite being in different calendar years.
        Assert.Equal(autumn, spring);
        Assert.Equal(new DateTime(2025, 7, 1), autumn.Start.UtcDateTime);
        Assert.Equal(new DateTime(2026, 7, 1), autumn.End.UtcDateTime);

        var season = new FixedSeasonCalendar();
        Assert.Equal("2025/26", season.Label(autumn, ctx));

        // June belongs to the previous season.
        var previous = Period.Season.BucketFor(Utc(2025, 6, 30), ctx);
        Assert.Equal(new DateTime(2024, 7, 1), previous.Start.UtcDateTime);
    }

    [Fact]
    public void Buckets_enumerate_contiguously_across_a_range()
    {
        var ctx = CalendarContext.Default;
        var range = new DateInterval(Utc(2026, 8, 1, 0), Utc(2026, 8, 31, 0));
        var days = Period.Day.Buckets(range, ctx).ToList();

        Assert.Equal(30, days.Count);
        for (int i = 1; i < days.Count; i++)
        {
            Assert.Equal(days[i - 1].End, days[i].Start); // no gaps, no overlaps
        }
    }
}
