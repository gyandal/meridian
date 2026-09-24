using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Time.Tests;

public class ResampleTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly CalendarContext Ctx = CalendarContext.Default; // UTC, Monday-start

    private static Instant Day(int y, int m, int d) =>
        Instant.FromUtc(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc));

    private static Point P(long player, double value, Instant at) =>
        new(PointKey.Of(KeyPart.Entity(Player, player)), value, at);

    // Three weeks with a hole in the middle: Aug 3 (value 10), Aug 17 (value 30). Aug 10 week empty.
    private static Point[] SparseWeeks() =>
    [
        P(1, 10, Day(2026, 8, 3)),
        P(1, 30, Day(2026, 8, 17)),
    ];

    [Fact]
    public void LeaveMissing_emits_only_weeks_with_data()
    {
        var result = Resampler.Resample(SparseWeeks(), Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing, Ctx);
        Assert.Equal([10.0, 30.0], result.Values.ToArray());
    }

    [Fact]
    public void ZeroFill_fills_the_empty_week_with_zero()
    {
        var result = Resampler.Resample(SparseWeeks(), Period.Week, Aggregators.Mean, GapPolicy.ZeroFill, Ctx);
        Assert.Equal([10.0, 0.0, 30.0], result.Values.ToArray());
    }

    [Fact]
    public void CarryForward_repeats_the_previous_week()
    {
        var result = Resampler.Resample(SparseWeeks(), Period.Week, Aggregators.Mean, GapPolicy.CarryForward, Ctx);
        Assert.Equal([10.0, 10.0, 30.0], result.Values.ToArray());
    }

    [Fact]
    public void Interpolate_fills_the_gap_linearly()
    {
        var result = Resampler.Resample(SparseWeeks(), Period.Week, Aggregators.Mean, GapPolicy.Interpolate, Ctx);
        Assert.Equal([10.0, 20.0, 30.0], result.Values.ToArray());
    }

    [Fact]
    public void Weekly_mean_aggregates_within_a_bucket()
    {
        // Two points in the same week → mean; positioned at the week's start.
        var points = new[]
        {
            P(1, 10, Day(2026, 8, 3)),
            P(1, 20, Day(2026, 8, 5)),
        };
        var result = Resampler.Resample(points, Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing, Ctx);

        Assert.Equal(1, result.Count);
        Assert.Equal(15.0, result.Values[0]);
        Assert.Equal(DayOfWeek.Monday, new Instant(result.AtTicks[0]).UtcDateTime.DayOfWeek);
    }

    [Fact]
    public void Resample_keeps_entities_independent()
    {
        var points = new[]
        {
            P(1, 10, Day(2026, 8, 3)),
            P(2, 99, Day(2026, 8, 3)),
        };
        var result = Resampler.Resample(points, Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing, Ctx);

        Assert.Equal(2, result.Count);
        // Each player's value is aggregated only against its own data — no cross-entity bleed.
        var byPlayer = result.Rows().ToDictionary(
            r => r.Key.TryGet(Player, out var p) ? p.Numeric : -1,
            r => r.Measure.Value);
        Assert.Equal(10.0, byPlayer[1]);
        Assert.Equal(99.0, byPlayer[2]);
    }

    [Fact]
    public void Missing_measurements_do_not_count_toward_the_bucket_mean()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 1));
        var points = new[]
        {
            new Point(key, 10.0, Day(2026, 8, 3)),
            new Point(key, Measurement.Missing, Day(2026, 8, 4), Unit.None),
            new Point(key, 20.0, Day(2026, 8, 5)),
        };
        var result = Resampler.Resample(points, Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing, Ctx);

        Assert.Equal(15.0, result.Values[0]); // mean(10, 20), the missing point ignored
    }
}
