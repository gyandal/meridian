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

public class GroupByTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly Instant Monday = Instant.FromUtc(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));

    private static Point Goal(long player, string venue, double goals, Instant? at = null) =>
        new(PointKey.Of(KeyPart.Entity(Player, player), KeyPart.Category(Venue, venue)), goals, at ?? Monday);

    private static PointBlock Goals() => PointBlock.FromRows(
    [
        Goal(1, "Home", 2), Goal(1, "Away", 1), Goal(2, "Home", 1),
        Goal(2, "Away", 3, Instant.FromUtc(Monday.UtcDateTime.AddDays(7))),
        new Point(PointKey.Of(KeyPart.Entity(Player, 3), KeyPart.Category(Venue, "Home")), Measurement.Missing, Monday, Unit.None),
    ]);

    [Fact]
    public void Total_collapses_time_and_every_other_dimension()
    {
        var byVenue = Transform.Total(Aggregators.Sum, Venue).Apply(Goals(), TransformContext.Default);
        var totals = Enumerable.Range(0, byVenue.Count).ToDictionary(i => byVenue.Keys[i].ToString(), i => byVenue.Values[i]);

        Assert.Equal(2, byVenue.Count);
        Assert.Equal(3.0, totals[PointKey.Of(KeyPart.Category(Venue, "Home")).ToString()]); // player 3's missing value is skipped
        Assert.Equal(4.0, totals[PointKey.Of(KeyPart.Category(Venue, "Away")).ToString()]);
        Assert.All(byVenue.AtTicks.ToArray(), t => Assert.Equal(PointBlock.NoAt, t));
    }

    [Fact]
    public void GroupBy_keeps_the_time_axis()
    {
        var perWeek = Transform.GroupBy(Aggregators.Sum, Venue).Apply(Goals(), TransformContext.Default);
        Assert.Equal(3, perWeek.Count); // in key order: Away (weeks one and two), then Home (week one)
        Assert.Equal([1.0, 3.0, 3.0], perWeek.Values.ToArray());
    }

    [Fact]
    public void A_group_with_only_missing_values_is_missing()
    {
        var byPlayer = Transform.Total(Aggregators.Sum, Player).Apply(Goals(), TransformContext.Default);
        int player3 = Enumerable.Range(0, byPlayer.Count).Single(i => byPlayer.Keys[i].TryGet(Player, out var p) && p.Numeric == 3);
        Assert.Equal(MeasureFlags.Missing, byPlayer.Flags[player3] & MeasureFlags.Missing);
    }

    [Fact]
    public void Grouping_transforms_have_stable_identities()
    {
        Assert.Equal(((ICacheIdentity)Transform.Total(Aggregators.Sum, Venue, Player)).CacheIdentity,
                     ((ICacheIdentity)Transform.Total(Aggregators.Sum, Player, Venue)).CacheIdentity);
        Assert.NotEqual(((ICacheIdentity)Transform.Total(Aggregators.Sum, Venue)).CacheIdentity,
                        ((ICacheIdentity)Transform.GroupBy(Aggregators.Sum, Venue)).CacheIdentity);
    }
}

public class FilterTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly Instant Monday = Instant.FromUtc(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));

    private static PointBlock Goals() => PointBlock.FromRows(
    [
        new Point(PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Home")), 2, Monday),
        new Point(PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Away")), 1, Monday),
        new Point(PointKey.Of(KeyPart.Entity(Player, 2), KeyPart.Category(Venue, "Home")), 5, Monday),
        new Point(PointKey.Of(KeyPart.Entity(Player, 3), KeyPart.Category(Venue, "Neutral")), Measurement.Missing, Monday, Unit.None),
        new Point(PointKey.Of(KeyPart.Entity(Player, 3)), 4, Monday), // no venue recorded
    ]);

    private static double[] Values(ITransform filter) => filter.Apply(Goals(), TransformContext.Default).Values.ToArray();

    [Fact]
    public void WhereIn_keeps_points_whose_key_matches()
    {
        Assert.Equal([2.0, 5.0], Values(Transform.WhereIn(Venue, "Home")));
        Assert.Equal([2.0, 1.0], Values(Transform.WhereIn(Player, "1")));   // entity parts match by id
        Assert.Equal([2.0, 1.0, 5.0], Values(Transform.WhereIn(Venue, "Home", "Away")));
    }

    [Fact]
    public void WhereNotIn_keeps_points_without_the_dimension()
    {
        var kept = Transform.WhereNotIn(Venue, "Home").Apply(Goals(), TransformContext.Default);
        Assert.Equal(3, kept.Count); // Away, Neutral (missing value — not a value filter) and the unrecorded venue
    }

    [Fact]
    public void WhereValue_bounds_are_inclusive_and_drop_missing_values()
    {
        Assert.Equal([2.0, 5.0, 4.0], Values(Transform.WhereValue(min: 2)));
        Assert.Equal([2.0, 1.0], Values(Transform.WhereValue(max: 2)));
        Assert.Equal([2.0, 4.0], Values(Transform.WhereValue(2, 4)));
    }

    [Fact]
    public void Filters_have_stable_identities()
    {
        static string Id(ITransform t) => ((ICacheIdentity)t).CacheIdentity;
        Assert.Equal(Id(Transform.WhereIn(Venue, "Home", "Away")), Id(Transform.WhereIn(Venue, "Away", "Home")));
        Assert.NotEqual(Id(Transform.WhereIn(Venue, "Home")), Id(Transform.WhereNotIn(Venue, "Home")));
        Assert.NotEqual(Id(Transform.WhereIn(Venue, "Home,Away")), Id(Transform.WhereIn(Venue, "Home", "Away")));
        Assert.NotEqual(Id(Transform.WhereValue(min: 1)), Id(Transform.WhereValue(max: 1)));
    }
}
