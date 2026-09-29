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

public class ShareTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly Instant Week1 = Instant.FromUtc(new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc));
    private static readonly Instant Week2 = Instant.FromUtc(new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc));

    private static Point P(long player, string venue, double value, Instant at) =>
        new(PointKey.Of(KeyPart.Entity(Player, player), KeyPart.Category(Venue, venue)), value, at);

    [Fact]
    public void A_share_is_of_the_points_at_the_same_time_that_differ_only_across_the_dimension()
    {
        var block = PointBlock.FromRows([P(1, "Home", 3, Week1), P(1, "Away", 1, Week1), P(2, "Home", 2, Week1), P(1, "Home", 5, Week2)]);
        var shares = Transform.ShareOf(Venue).Apply(block, TransformContext.Default);

        Assert.Equal([75.0, 25.0, 100.0, 100.0], shares.Values.ToArray()); // per player, per week
        Assert.Equal("%", shares.Unit.Symbol);
        Assert.Equal([60.0, 40.0], Transform.ShareOf(Player, Venue).Apply(
            PointBlock.FromRows([P(1, "Home", 3, Week1), P(2, "Away", 2, Week1)]), TransformContext.Default).Values.ToArray());
    }

    [Fact]
    public void Missing_values_are_skipped_and_a_zero_total_has_no_share()
    {
        var block = PointBlock.FromRows(
        [
            P(1, "Home", 4, Week1),
            new Point(PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Away")), Measurement.Missing, Week1, Unit.None),
            P(2, "Home", 0, Week1), P(2, "Away", 0, Week1),
        ]);
        var shares = Transform.ShareOf(Venue).Apply(block, TransformContext.Default);

        Assert.Equal(100.0, shares.Values[0]);
        Assert.All([1, 2, 3], i => Assert.Equal(MeasureFlags.Missing, shares.Flags[i] & MeasureFlags.Missing));
    }
}

public class CumulativeTests
{
    private static readonly DimensionId Player = new("player");

    private static PointBlock Weeks(long player, params double?[] values) => PointBlock.FromRows([.. values.Select((v, i) =>
        new Point(PointKey.Of(KeyPart.Entity(Player, player)), v is { } x ? Measurement.Of(x) : Measurement.Missing,
            Instant.FromUtc(new DateTime(2026, 7, 6).AddDays(7 * (values.Length - 1 - i))), Unit.None))]); // newest first

    private static double?[] Run(IAggregator aggregator, PointBlock block)
    {
        var output = Transform.Cumulative(aggregator).Apply(block, TransformContext.Default);
        return [.. Enumerable.Range(0, output.Count).Select(i => (output.Flags[i] & MeasureFlags.Missing) != 0 ? (double?)null : output.Values[i])];
    }

    [Fact]
    public void A_running_total_is_in_time_order_and_carries_over_a_missing_value()
    {
        // Rows arrive newest first; the output runs oldest first: 2, 2+1, (missing: still 3), 3+4.
        Assert.Equal([2.0, 3.0, 3.0, 7.0], Run(Aggregators.Sum, Weeks(1, 4, null, 1, 2)));
        Assert.Equal([null, 5.0], Run(Aggregators.Sum, Weeks(1, 5, null)));
    }

    [Fact]
    public void Other_aggregators_run_too()
    {
        var block = Weeks(1, 1, 9, 3, 5); // oldest first: 5, 3, 9, 1
        Assert.Equal([5.0, 5.0, 9.0, 9.0], Run(Aggregators.Max, block));
        Assert.Equal([5.0, 3.0, 3.0, 1.0], Run(Aggregators.Min, block));
        Assert.Equal([5.0, 4.0, 17 / 3.0, 4.5], Run(Aggregators.Mean, block));
        Assert.Equal([1.0, 2.0, 3.0, 4.0], Run(Aggregators.Count, block));
        Assert.True(Aggregators.TryResolve("median", out var median));
        Assert.Equal([5.0, 4.0, 5.0, 4.0], Run(median, block));
    }

    [Fact]
    public void Each_key_runs_separately()
    {
        var block = PointBlock.FromRows([.. Enumerable.Range(0, 2).SelectMany(i => new[]
        {
            new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 1, Instant.FromUtc(new DateTime(2026, 7, 6).AddDays(i))),
            new Point(PointKey.Of(KeyPart.Entity(Player, 2)), 10, Instant.FromUtc(new DateTime(2026, 7, 6).AddDays(i))),
        })]);
        Assert.Equal([1.0, 2.0, 10.0, 20.0], Run(Aggregators.Sum, block));
    }
}

public class RankTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");

    private static Point P(long player, double value, int month = 1, string venue = "Home") =>
        new(PointKey.Of(KeyPart.Entity(Player, player), KeyPart.Category(Venue, venue)), value, Instant.FromUtc(new DateTime(2026, month, 1)));

    private static long[] Players(PointBlock block) =>
        [.. Enumerable.Range(0, block.Count).Select(i => block.Keys[i].TryGet(Player, out var p) ? p.Numeric : -1).Distinct()];

    [Fact]
    public void Top_keeps_the_highest_scoring_keys_with_ties_to_the_lower_key()
    {
        var block = PointBlock.FromRows([P(1, 5), P(2, 9), P(3, 7), P(4, 9), P(5, 1)]);
        Assert.Equal([2L, 4L], Players(Transform.Top(2, Aggregators.Sum).Apply(block, TransformContext.Default)));
        Assert.Equal([1L, 5L], Players(Transform.Bottom(2, Aggregators.Sum).Apply(block, TransformContext.Default)));
        Assert.Equal(5, Transform.Top(10, Aggregators.Sum).Apply(block, TransformContext.Default).Count);
        Assert.Equal([2L], Players(Transform.Top(1, Aggregators.Sum).Apply(block, TransformContext.Default))); // 2 and 4 tie on 9
    }

    [Fact]
    public void Order_sensitive_scores_read_each_keys_values_in_time_order_and_unscorable_keys_are_left_out()
    {
        // Rows arrive newest first. Player 1's latest month is 1 (after a 9); player 2's is 5 (after a 0).
        var block = PointBlock.FromRows([P(1, 1, 2), P(2, 5, 2), P(1, 9, 1), P(2, 0, 1), P(3, 4, 1)]);
        Assert.Equal([2L], Players(Transform.Top(1, Aggregators.Last).Apply(block, TransformContext.Default)));

        // Player 3 has one month, so no spread to rank: it's in neither the top nor the bottom.
        Assert.Equal([1L], Players(Transform.Top(1, Aggregators.StdDev).Apply(block, TransformContext.Default)));
        Assert.Equal([1L, 2L], Players(Transform.Bottom(5, Aggregators.StdDev).Apply(block, TransformContext.Default)).Order());
    }

    [Fact]
    public void Over_time_it_picks_series_by_their_score_and_keeps_all_their_points()
    {
        // Player 1 scores 3 + 3, player 2 has one big month (5) then nothing, player 3 is steady (2, 2).
        var block = PointBlock.FromRows([P(1, 3, 1), P(1, 3, 2), P(2, 5, 1), P(2, 0, 2), P(3, 2, 1), P(3, 2, 2)]);

        var bySum = Transform.Top(1, Aggregators.Sum).Apply(block, TransformContext.Default);
        Assert.Equal([1L], Players(bySum));
        Assert.Equal(2, bySum.Count);
        Assert.Equal([2L], Players(Transform.Top(1, Aggregators.Max).Apply(block, TransformContext.Default)));   // best single month
        Assert.Equal([1L], Players(Transform.Top(1, Aggregators.Last).Apply(block, TransformContext.Default)));  // best latest month
    }

    [Fact]
    public void Per_ranks_within_each_group_and_keys_without_values_are_not_ranked()
    {
        var block = PointBlock.FromRows(
        [
            P(1, 5, venue: "Home"), P(2, 3, venue: "Home"), P(3, 1, venue: "Away"), P(4, 2, venue: "Away"),
            new Point(PointKey.Of(KeyPart.Entity(Player, 5), KeyPart.Category(Venue, "Away")), Measurement.Missing, Instant.FromUtc(new DateTime(2026, 1, 1)), Unit.None),
        ]);
        Assert.Equal([1L, 4L], Players(Transform.Top(1, Aggregators.Sum, Venue).Apply(block, TransformContext.Default)).Order());
        Assert.Equal([2L, 3L], Players(Transform.Bottom(1, Aggregators.Sum, Venue).Apply(block, TransformContext.Default)).Order());
    }

    [Fact]
    public void Rankings_have_stable_identities()
    {
        static string Id(ITransform t) => ((ICacheIdentity)t).CacheIdentity;
        Assert.NotEqual(Id(Transform.Top(5, Aggregators.Sum)), Id(Transform.Bottom(5, Aggregators.Sum)));
        Assert.NotEqual(Id(Transform.Top(5, Aggregators.Sum)), Id(Transform.Top(3, Aggregators.Sum)));
        Assert.NotEqual(Id(Transform.Top(5, Aggregators.Sum)), Id(Transform.Top(5, Aggregators.Max)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Transform.Top(0, Aggregators.Sum));
    }
}

public class ForecastTests
{
    private static readonly DimensionId Player = new("player");

    private static double[] Project(ForecastModel model, int horizon, params double[] history) => model.Project(history, horizon);

    [Fact]
    public void Models_continue_the_patterns_they_model()
    {
        Assert.Equal([3.0, 3.0], Project(ForecastModel.Mean, 2, 2, 4, 3));
        Assert.Equal([9.0, 11.0], Project(ForecastModel.Trend, 2, 1, 3, 5, 7), new ToleranceComparer());
        Assert.Equal([10.0, 20.0, 30.0, 10.0], Project(ForecastModel.SeasonalNaive(3), 4, 99, 10, 20, 30));

        // A line plus a repeating pattern is continued exactly by Holt-Winters, whatever weights it picks.
        double[] pattern = [5, -2, 0, -3];
        double Y(int t) => 10 + 0.5 * t + pattern[t % 4];
        var history = Enumerable.Range(0, 12).Select(Y).ToArray();
        Assert.Equal(Enumerable.Range(12, 6).Select(Y), ForecastModel.HoltWinters(4).Project(history, 6), new ToleranceComparer());
        Assert.Equal([12.0, 14.0], Project(ForecastModel.HoltWinters(), 2, 2, 4, 6, 8, 10), new ToleranceComparer()); // Holt: a line
    }

    [Fact]
    public void Holt_Winters_picks_its_weights_deterministically_from_noisy_data()
    {
        var rng = new Random(7);
        var noisy = Enumerable.Range(0, 24).Select(t => 20 + t + (t % 12 < 6 ? 8 : -8) + rng.NextDouble() * 4).ToArray();
        var once = ForecastModel.HoltWinters(12).Project(noisy, 12);
        Assert.Equal(once, ForecastModel.HoltWinters(12).Project(noisy, 12));
        Assert.True(once.Take(6).Average() > once.Skip(6).Average()); // the high half of the season stays high

        // A level that steps up: the weights that fit best follow the step; slow ones would still be climbing.
        var stepped = Enumerable.Range(0, 30).Select(t => t < 20 ? 10.0 : 20.0).ToArray();
        Assert.InRange(ForecastModel.HoltWinters().Project(stepped, 1)[0], 19, 21);
    }

    private static PointBlock Months(params (long Player, int Month, double? Value)[] rows) => PointBlock.FromRows(
        [.. rows.Select(r => new Point(PointKey.Of(KeyPart.Entity(Player, r.Player)), r.Value is { } v ? Measurement.Of(v) : Measurement.Missing,
            new Instant(new DateTime(2025, 1, 1).AddMonths(r.Month - 1).Ticks), Unit.None))],
        time: TimeAxis.Local("month", "Europe/London"));

    private static List<(long Player, string Month, double Value, bool Estimated)> Rows(PointBlock block) =>
        [.. Enumerable.Range(0, block.Count).Select(i => (block.Keys[i].TryGet(Player, out var p) ? p.Numeric : 0,
            new DateTime(block.AtTicks[i]).ToString("yyyy-MM"), block.Values[i], (block.Flags[i] & MeasureFlags.Estimated) != 0))];

    [Fact]
    public void Every_key_is_projected_over_the_same_future_buckets_from_its_own_history()
    {
        // Player 1 runs to March; player 2 stopped in February, so March is its second step ahead.
        var block = Months((1, 1, 1), (1, 2, 2), (1, 3, 3), (2, 1, 10), (2, 2, 20));
        var trend = Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(2)).Apply(block, TransformContext.Default);
        var future = Rows(trend).Where(r => r.Estimated).ToList();

        Assert.Equal(5, Rows(trend).Count(r => !r.Estimated));  // the history, untouched
        Assert.Equal([(1L, "2025-04", 2.0, true), (1L, "2025-05", 2.0, true), (2L, "2025-04", 15.0, true), (2L, "2025-05", 15.0, true)], future);

        var steps = Rows(Transform.Forecast(ForecastModel.SeasonalNaive(2), ForecastHorizon.Buckets(1)).Apply(block, TransformContext.Default))
            .Where(r => r.Estimated).ToList();
        Assert.Equal([(1L, "2025-04", 2.0, true), (2L, "2025-04", 20.0, true)], steps); // two and three steps ahead
    }

    [Fact]
    public void To_the_end_of_the_season_follows_the_season_calendar()
    {
        var block = Months((1, 1, 4), (1, 2, 4), (1, 3, 4)); // January to March; the season runs July to June
        var toEnd = Rows(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.SeasonEnd).Apply(block, TransformContext.Default));
        Assert.Equal(["2025-04", "2025-05", "2025-06"], toEnd.Where(r => r.Estimated).Select(r => r.Month));
    }

    [Fact]
    public void Gaps_are_bridged_for_fitting_short_series_are_not_projected_and_time_is_required()
    {
        var gappy = Months((1, 1, 1), (1, 2, null), (1, 3, 3), (1, 5, 5), (2, 1, 7));
        var trend = Rows(Transform.Forecast(ForecastModel.Trend, ForecastHorizon.Buckets(1)).Apply(gappy, TransformContext.Default));
        Assert.Equal([(1L, "2025-06", 6.0, true)], trend.Where(r => r.Estimated).Select(r => (r.Player, r.Month, Math.Round(r.Value, 9), r.Estimated)));

        // Player 2 has two months; a model that needs a three-month season leaves it without a forecast.
        var seasonal = Transform.Forecast(ForecastModel.SeasonalNaive(3), ForecastHorizon.Buckets(1))
            .Apply(Months((1, 1, 1), (1, 2, 2), (1, 3, 3), (2, 2, 5), (2, 3, 6)), TransformContext.Default);
        Assert.Equal(6, seasonal.Count);
        Assert.Equal([1L], Rows(seasonal).Where(r => r.Estimated).Select(r => r.Player));

        var raw = PointBlock.FromRows([new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 1, Instant.FromUtc(new DateTime(2025, 1, 1)))]);
        Assert.Contains("resample", Assert.Throws<InvalidOperationException>(() =>
            Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(1)).Apply(raw, TransformContext.Default)).Message);
    }

    [Fact]
    public void Anything_computed_from_a_forecast_is_estimated_and_resampling_one_is_refused()
    {
        var block = Months((1, 1, 2), (1, 2, 2), (2, 1, 1), (2, 2, 1));
        var projected = Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(2)).Apply(block, TransformContext.Default);

        var soFar = Rows(Transform.Cumulative(Aggregators.Sum).Apply(projected, TransformContext.Default)).Where(r => r.Player == 1).ToList();
        Assert.Equal([(1L, "2025-01", 2.0, false), (1L, "2025-02", 4.0, false), (1L, "2025-03", 6.0, true), (1L, "2025-04", 8.0, true)], soFar);

        var squad = Rows(Transform.GroupBy(Aggregators.Sum).Apply(projected, TransformContext.Default));
        Assert.Equal([false, false, true, true], squad.Select(r => r.Estimated));
        Assert.All(Rows(Transform.ShareOf(Player).Apply(projected, TransformContext.Default)).Where(r => r.Month == "2025-03"), r => Assert.True(r.Estimated));
        Assert.True(Rows(Transform.Rolling(TimeSpan.FromDays(62), Aggregators.Mean).Apply(projected, TransformContext.Default)).Last().Estimated);

        Assert.Contains("Forecast at the grain", Assert.Throws<InvalidOperationException>(() =>
            Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing).Apply(projected, TransformContext.Default)).Message);
    }

    [Fact]
    public void Forecasts_have_stable_identities_and_validate_their_inputs()
    {
        static string Id(ITransform t) => ((ICacheIdentity)t).CacheIdentity;
        Assert.NotEqual(Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3))), Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.SeasonEnd)));
        Assert.NotEqual(Id(Transform.Forecast(ForecastModel.HoltWinters(12), ForecastHorizon.Buckets(3))), Id(Transform.Forecast(ForecastModel.HoltWinters(), ForecastHorizon.Buckets(3))));
        Assert.Throws<ArgumentOutOfRangeException>(() => ForecastHorizon.Buckets(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ForecastModel.SeasonalNaive(0));
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-9;
        public int GetHashCode(double obj) => 0;
    }
}
