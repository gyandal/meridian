using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;
using Xunit;

namespace Meridian.Transforms.Tests;

public class TimeKindTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly PointKey Key = PointKey.Of(KeyPart.Entity(Player, 1));
    private static readonly long Day1 = new DateTime(2026, 7, 1).Ticks;

    private static PointBlock Block(TimeAxis time, double value = 1) =>
        PointBlock.FromRows([new Point(Key, value, new Instant(Day1))], time: time);

    [Fact]
    public void Joining_instants_to_calendar_values_is_an_error_that_says_what_to_do()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Binary.Combine(Block(TimeAxis.Instant), Block(TimeAxis.Local("day", "Europe/London")), (a, b) => a + b, matchTime: true));
        Assert.Contains("Resample the instant series", error.Message);
    }

    [Fact]
    public void Series_bucketed_in_different_zones_do_not_join()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Binary.Combine(Block(TimeAxis.Local("day", "Europe/London")), Block(TimeAxis.Local("day", "Asia/Tokyo")), (a, b) => a + b, matchTime: true));
    }

    [Fact]
    public void Calendar_dates_join_buckets_from_any_zone()
    {
        // A daily wellness answer (as-given date) against daily load bucketed in London: same day, same point.
        var joined = Binary.Combine(Block(TimeAxis.Local(), 8), Block(TimeAxis.Local("day", "Europe/London"), 2), (a, b) => a / b, matchTime: true);

        Assert.Equal([4.0], joined.Values.ToArray());
        Assert.Equal(TimeAxis.Local(null, "Europe/London"), joined.Time);
    }

    [Fact]
    public void Transforms_keep_the_time_axis_and_grouped_resamples_adopt_the_new_one()
    {
        var ctx = new TransformContext(CalendarContext.For("Europe/London"));
        var local = Block(TimeAxis.Local("day", "Europe/London"));
        Assert.Equal(local.Time, Transform.Filter(_ => true).Apply(local, ctx).Time);
        Assert.Equal(local.Time, Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(local, ctx).Time);

        var grouped = Transform.PerGroup(p => p.Key, Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing))
            .Apply(Block(TimeAxis.Instant), ctx);
        Assert.Equal(TimeAxis.Local("week", "Europe/London"), grouped.Time);
    }
}
