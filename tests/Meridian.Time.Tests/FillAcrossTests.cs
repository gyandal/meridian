using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Time.Tests;

public class FillAcrossTests
{
    private static readonly DimensionId Player = new("player");

    private static Instant Utc(int y, int m, int d, int h = 0) => Instant.FromUtc(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc));

    private static PointBlock Instants(params (int Month, double Value)[] rows) => PointBlock.FromRows(
        [.. rows.Select(r => new Point(PointKey.Of(KeyPart.Entity(Player, 1)), r.Value, Utc(2025, r.Month, 10)))]);

    private static DateInterval FirstHalf => new(Utc(2025, 1, 1), Utc(2025, 7, 1));

    private static string[] Months(PointBlock block) => [.. block.AtTicks.ToArray().Select(t => new DateTime(t).ToString("yyyy-MM"))];

    [Fact]
    public void ZeroFill_across_the_timeframe_makes_quiet_first_and_last_months_zero()
    {
        var block = Instants((3, 5), (4, 2));

        var observed = Resampler.Resample(block, Period.Month, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.Default);
        var across = Resampler.Resample(block, Period.Month, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.Default, FirstHalf);

        Assert.Equal(["2025-03", "2025-04"], Months(observed));                                  // today's behaviour, unchanged
        Assert.Equal(["2025-01", "2025-02", "2025-03", "2025-04", "2025-05", "2025-06"], Months(across));
        Assert.Equal([0.0, 0.0, 5.0, 2.0, 0.0, 0.0], across.Values.ToArray());
    }

    [Fact]
    public void The_window_is_the_buckets_holding_its_start_and_its_last_moment()
    {
        // A timeframe from mid-January to 1 June: January is in (it holds the start), June is not (the end is exclusive).
        var window = new DateInterval(Utc(2025, 1, 15), Utc(2025, 6, 1));
        var across = Resampler.Resample(Instants((3, 5)), Period.Month, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.Default, window);
        Assert.Equal(["2025-01", "2025-02", "2025-03", "2025-04", "2025-05"], Months(across));
    }

    [Fact]
    public void Local_values_are_filled_across_the_timeframe_as_wall_clock_dates()
    {
        // Dates as given (a daily questionnaire): the timeframe's ticks are the wall clock too, never converted.
        var dates = PointBlock.FromRows(
            [new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 7, new Instant(new DateTime(2025, 3, 3).Ticks))], time: TimeAxis.Local());
        var window = new DateInterval(new Instant(new DateTime(2025, 3, 1).Ticks), new Instant(new DateTime(2025, 3, 6).Ticks));

        var days = Resampler.Resample(dates, Period.Day, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.For("Pacific/Auckland"), window);
        Assert.Equal(["2025-03-01", "2025-03-02", "2025-03-03", "2025-03-04", "2025-03-05"],
            days.AtTicks.ToArray().Select(t => new DateTime(t).ToString("yyyy-MM-dd")));
        Assert.Equal([0.0, 0.0, 7.0, 0.0, 0.0], days.Values.ToArray());
    }

    [Fact]
    public void Instant_edges_are_placed_on_the_calendars_wall_clock_across_a_clock_change()
    {
        // London's clocks go forward on 30 March 2025. The timeframe is 28 March 00:00 GMT to 2 April 00:00 BST
        // (1 April 23:00 UTC): five London days, the third one 23 hours long. Its last moment is still 1 April.
        var london = CalendarContext.For("Europe/London");
        var block = PointBlock.FromRows([new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 4, Utc(2025, 3, 30, 12))]);
        var window = new DateInterval(Utc(2025, 3, 28), Utc(2025, 4, 1, 23));

        var days = Resampler.Resample(block, Period.Day, Aggregators.Sum, GapPolicy.ZeroFill, london, window);

        Assert.Equal(["2025-03-28", "2025-03-29", "2025-03-30", "2025-03-31", "2025-04-01"],
            days.AtTicks.ToArray().Select(t => new DateTime(t).ToString("yyyy-MM-dd")));
        Assert.Equal([0.0, 0.0, 4.0, 0.0, 0.0], days.Values.ToArray());
    }

    [Fact]
    public void Buckets_already_drawn_in_a_zone_place_a_utc_timeframe_on_that_zones_clock()
    {
        // A pushed-down rollup arrives as London wall-clock buckets. Its timeframe is still UTC, so it must land on the
        // same buckets as it would for the raw instants: 24 April 23:00 UTC is 25 April in London, not 24 April.
        var london = CalendarContext.For("Europe/London");
        var buckets = PointBlock.FromRows(
            [new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 3, new Instant(new DateTime(2025, 4, 27).Ticks))], time: TimeAxis.Local("day", "Europe/London"));
        var window = new DateInterval(Utc(2025, 4, 24, 23), Utc(2025, 4, 28, 23));

        var days = Resampler.Resample(buckets, Period.Day, Aggregators.Last, GapPolicy.ZeroFill, london, window);
        Assert.Equal(["2025-04-25", "2025-04-26", "2025-04-27", "2025-04-28"], days.AtTicks.ToArray().Select(t => new DateTime(t).ToString("yyyy-MM-dd")));
    }

    [Fact]
    public void Carry_forward_and_interpolate_keep_their_rules_for_leading_and_open_ended_gaps()
    {
        var block = Instants((3, 5), (5, 9));

        // Nothing to carry before March; after May the last value carries to the end of the window.
        var carried = Resampler.Resample(block, Period.Month, Aggregators.Sum, GapPolicy.CarryForward, CalendarContext.Default, FirstHalf);
        Assert.Equal(["2025-03", "2025-04", "2025-05", "2025-06"], Months(carried));
        Assert.Equal([5.0, 5.0, 9.0, 9.0], carried.Values.ToArray());

        // Interpolation needs a value on both sides, so the window adds nothing at either end.
        var interpolated = Resampler.Resample(block, Period.Month, Aggregators.Sum, GapPolicy.Interpolate, CalendarContext.Default, FirstHalf);
        Assert.Equal([5.0, 7.0, 9.0], interpolated.Values.ToArray());

        // Leaving gaps missing ignores the window altogether.
        Assert.Equal(2, Resampler.Resample(block, Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing, CalendarContext.Default, FirstHalf).Count);
    }

    [Fact]
    public void Points_outside_the_window_are_kept_and_a_series_with_none_produces_nothing()
    {
        var early = Resampler.Resample(Instants((1, 3)), Period.Month, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.Default,
            new DateInterval(Utc(2025, 3, 1), Utc(2025, 5, 1)));
        Assert.Equal(["2025-01", "2025-02", "2025-03", "2025-04"], Months(early)); // widened, never narrowed

        Assert.Equal(0, Resampler.Resample(PointBlock.FromRows([]), Period.Month, Aggregators.Sum, GapPolicy.ZeroFill, CalendarContext.Default, FirstHalf).Count);
    }
}
