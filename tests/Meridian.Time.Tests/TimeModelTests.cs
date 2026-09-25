using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Time.Tests;

/// <summary>The rules in docs/TIME.md: buckets are local, the zone decides them, local values never move.</summary>
public class TimeModelTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly PointKey Key = PointKey.Of(KeyPart.Entity(Player, 1));

    private static Instant Utc(int y, int m, int d, int h = 0, int min = 0) =>
        Instant.FromUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc));

    private static long Wall(int y, int m, int d) => new DateTime(y, m, d).Ticks;

    [Fact]
    public void A_reading_belongs_to_the_local_day_it_happened_on()
    {
        // 00:30 on 1 July in London (BST) is 23:30 on 30 June in UTC.
        var block = PointBlock.FromRows([new Point(Key, 5, Utc(2026, 6, 30, 23, 30))]);

        var london = Resampler.Resample(block, Period.Day, Aggregators.Sum, GapPolicy.LeaveMissing, CalendarContext.For("Europe/London"));
        var utc = Resampler.Resample(block, Period.Day, Aggregators.Sum, GapPolicy.LeaveMissing, CalendarContext.Default);

        Assert.Equal(Wall(2026, 7, 1), london.AtTicks[0]);
        Assert.Equal(Wall(2026, 6, 30), utc.AtTicks[0]);
    }

    [Fact]
    public void Resampled_buckets_are_local_and_record_their_grain_and_zone()
    {
        var block = PointBlock.FromRows([new Point(Key, 5, Utc(2026, 7, 15, 12))]);
        var weekly = Resampler.Resample(block, Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing, CalendarContext.For("America/New_York"));

        Assert.Equal(TimeAxis.Local("week", "America/New_York"), weekly.Time);
        Assert.Equal(Wall(2026, 7, 13), weekly.AtTicks[0]); // Monday 13 July, New York wall clock
    }

    [Fact]
    public void A_local_day_across_the_spring_change_holds_its_23_hours_of_readings()
    {
        // Hourly readings through 29 March 2026 in London: clocks jump 01:00 → 02:00, so the day has 23 hours.
        var rows = Enumerable.Range(0, 48)
            .Select(h => new Point(Key, 1, Instant.FromUtc(Utc(2026, 3, 28, 23).UtcDateTime.AddHours(h))))
            .ToList();
        var counts = Resampler.Resample(PointBlock.FromRows(rows), Period.Day, Aggregators.Count, GapPolicy.LeaveMissing, CalendarContext.For("Europe/London"));

        int i = counts.AtTicks.IndexOf(Wall(2026, 3, 29));
        Assert.Equal(23, counts.Values[i]);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Pacific/Kiritimati")]
    public void Local_values_are_bucketed_as_given_whatever_the_zone(string zone)
    {
        // A date-valued metric (think date of birth, or a wellness answer for 15 March): never converted.
        var block = PointBlock.FromRows([new Point(Key, 7, new Instant(Wall(1990, 3, 15)))], time: TimeAxis.Local());
        var daily = Resampler.Resample(block, Period.Day, Aggregators.Mean, GapPolicy.LeaveMissing, CalendarContext.For(zone));

        Assert.Equal(Wall(1990, 3, 15), daily.AtTicks[0]);
        Assert.Equal(TimeAxis.Local("day"), daily.Time); // no zone drew this bucket
    }

    [Fact]
    public void Wall_clock_times_resolve_around_dst_by_policy()
    {
        var london = TimeZones.Get("Europe/London");
        long repeated = new DateTime(2026, 10, 25, 1, 30, 0).Ticks; // happens twice
        long missing = new DateTime(2026, 3, 29, 1, 30, 0).Ticks;   // never happens

        Assert.Equal(Utc(2026, 10, 25, 0, 30).UtcTicks, TimeZones.ToUtcTicks(repeated, london, new(AmbiguousTime.Earlier)));
        Assert.Equal(Utc(2026, 10, 25, 1, 30).UtcTicks, TimeZones.ToUtcTicks(repeated, london, new(AmbiguousTime.Later)));
        Assert.Equal(Utc(2026, 3, 29, 1, 30).UtcTicks, TimeZones.ToUtcTicks(missing, london)); // shifted to 02:30 BST
        Assert.ThrowsAny<Exception>(() => TimeZones.ToUtcTicks(repeated, london, new(AmbiguousTime.Reject)));
        Assert.ThrowsAny<Exception>(() => TimeZones.ToUtcTicks(missing, london, new(Skipped: SkippedTime.Reject)));
    }

    [Fact]
    public void Bulk_conversion_matches_one_at_a_time_across_zone_changes()
    {
        var sydney = TimeZones.Get("Australia/Sydney");
        long[] utc = [.. Enumerable.Range(0, 24 * 400).Select(h => Utc(2025, 1, 1).UtcTicks + h * TimeSpan.TicksPerHour)];
        var bulk = new long[utc.Length];
        TimeZones.ToLocalTicks(utc, bulk, sydney);

        Assert.Equal(utc.Select(t => TimeZones.ToLocalTicks(t, sydney)), bulk);
    }

    [Fact]
    public void Unknown_zones_fail_with_a_helpful_message()
    {
        var error = Assert.Throws<ArgumentException>(() => CalendarContext.For("Europe/Atlantis"));
        Assert.Contains("IANA", error.Message);
    }
}

public class FixedPeriodTests
{
    private static readonly PointKey Key = PointKey.Of(KeyPart.Entity(new DimensionId("host"), 1));

    [Fact]
    public void Fixed_buckets_align_to_the_local_clock()
    {
        var at = Instant.FromUtc(new DateTime(2026, 7, 1, 10, 7, 30, DateTimeKind.Utc));
        var block = PointBlock.FromRows([new Point(Key, 1, at)]);

        var fiveMin = Resampler.Resample(block, Period.Every(TimeSpan.FromMinutes(5)), Aggregators.Max, GapPolicy.LeaveMissing, CalendarContext.Default);
        var kolkata = Resampler.Resample(block, Period.Hour, Aggregators.Max, GapPolicy.LeaveMissing, CalendarContext.For("Asia/Kolkata")); // UTC+05:30

        Assert.Equal(new DateTime(2026, 7, 1, 10, 5, 0).Ticks, fiveMin.AtTicks[0]);
        Assert.Equal(TimeAxis.Local("5m", "UTC"), fiveMin.Time);
        Assert.Equal(new DateTime(2026, 7, 1, 15, 0, 0).Ticks, kolkata.AtTicks[0]); // 15:37 local → the 15:00 bucket
    }

    [Fact]
    public void The_repeated_autumn_hour_folds_into_one_local_bucket()
    {
        // 05:30 and 06:30 UTC on 2 Nov 2025 are both 01:30 in New York (EDT, then EST).
        var rows = new[] { 5, 6 }.Select(h => new Point(Key, 1, Instant.FromUtc(new DateTime(2025, 11, 2, h, 30, 0, DateTimeKind.Utc))));
        var hourly = Resampler.Resample(PointBlock.FromRows(rows), Period.Hour, Aggregators.Count, GapPolicy.LeaveMissing, CalendarContext.For("America/New_York"));

        Assert.Equal(1, hourly.Count);
        Assert.Equal(2, hourly.Values[0]);
    }

    [Theory]
    [InlineData(7)]   // minutes: doesn't divide a day
    [InlineData(0)]
    public void Spans_must_divide_a_day(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Period.Every(TimeSpan.FromMinutes(minutes)));
    }
}
