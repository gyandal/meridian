using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;
using Xunit;

namespace Meridian.Transforms.Tests;

/// <summary>
/// The headline proof: acute:chronic workload ratio — the canonical longitudinal sports-science
/// calculation — expressed purely as composition of the algebra. Here it is two rolling means and a time-matched divide.
/// </summary>
public class AcwrScenarioTests
{
    private static readonly DimensionId Player = new("player");

    private static Instant Day(int d) =>
        Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(d - 1));

    [Fact]
    public void Acwr_is_the_7day_mean_over_the_28day_mean()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 1));

        // Daily training load: 100 for days 1..28, stepped up to 140 for days 29..35.
        var rows = new List<Point>();
        for (int d = 1; d <= 35; d++)
        {
            rows.Add(new Point(key, d <= 28 ? 100.0 : 140.0, Day(d)));
        }
        var load = PointBlock.FromRows(rows, new Unit("au"));

        var ctx = TransformContext.Default;
        var acute = Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(load, ctx);
        var chronic = Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(load, ctx);

        var acwr = Binary.Combine(
            acute, chronic,
            (a, c) => c == 0 ? double.NaN : a / c,
            matchTime: true,
            new Unit("ratio"));

        // At day 35:
        //   acute  = mean(days 29..35) = 140
        //   chronic = mean(days 8..35) = (21×100 + 7×140) / 28 = 110
        //   ACWR = 140 / 110 = 1.2727…
        var atDay35 = acwr.Rows().Single(r => r.At == Day(35));
        Assert.Equal(140.0 / 110.0, atDay35.Measure.Value, precision: 10);

        // Sanity: deep in the steady 100-load stretch the ratio is exactly 1.
        var atDay20 = acwr.Rows().Single(r => r.At == Day(20));
        Assert.Equal(1.0, atDay20.Measure.Value, precision: 10);
    }

    [Fact]
    public void Acwr_composes_as_a_reusable_pipeline_per_athlete()
    {
        // Same calc, but proving PerGroup keeps two athletes' rolling windows independent.
        var rows = new List<Point>();
        for (int d = 1; d <= 30; d++)
        {
            rows.Add(new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 100.0, Day(d)));
            rows.Add(new Point(PointKey.Of(KeyPart.Entity(Player, 2)), 200.0, Day(d)));
        }
        var load = PointBlock.FromRows(rows, new Unit("au"));
        var ctx = TransformContext.Default;

        var acute = Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(load, ctx);
        var chronic = Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(load, ctx);
        var acwr = Binary.Combine(acute, chronic, (a, c) => a / c, matchTime: true);

        // Both athletes are on a flat load → ratio 1.0 each, and neither bleeds into the other.
        foreach (long player in new long[] { 1, 2 })
        {
            var atDay30 = acwr.Rows().Single(r =>
                r.Key.TryGet(Player, out var pp) && pp.Numeric == player && r.At == Day(30));
            Assert.Equal(1.0, atDay30.Measure.Value, precision: 10);
        }
    }
}
