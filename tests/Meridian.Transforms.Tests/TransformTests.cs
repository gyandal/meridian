using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;
using Xunit;

namespace Meridian.Transforms.Tests;

public class TransformTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly TransformContext Ctx = TransformContext.Default;

    private static Instant Day(int d) =>
        Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(d - 1));

    private static Point Load(long player, double value, int day) =>
        new(PointKey.Of(KeyPart.Entity(Player, player)), value, Day(day));

    [Fact]
    public void Pipeline_composes_filter_then_resample()
    {
        // Days 3,4,5 = Mon/Tue/Wed of the same Monday-start week (2026-08-03 is a Monday).
        var block = PointBlock.FromRows(
        [
            Load(1, 10, 3),
            Load(1, -5, 4),   // will be filtered out
            Load(1, 20, 5),
        ]);

        var pipeline = Pipeline.Of(
            Transform.Filter(p => p.Measure.Value >= 0),
            Transform.Resample(Period.Week, Aggregators.Sum, GapPolicy.LeaveMissing));

        var result = pipeline.Run(block, Ctx);

        Assert.Equal(1, result.Count);
        Assert.Equal(30.0, result.Values[0]); // 10 + 20, the -5 removed
    }

    [Fact]
    public void Reduce_groups_by_projected_key()
    {
        // Two players, several days each → season total per player via a key that drops time.
        var block = PointBlock.FromRows(
        [
            Load(1, 100, 1), Load(1, 150, 2),
            Load(2, 200, 1), Load(2, 50, 2),
        ]);

        var totals = Transform
            .Reduce(p => p.Key, Aggregators.Sum) // key already just {player}; time isn't in the key
            .Apply(block, Ctx);

        var byPlayer = totals.Rows().ToDictionary(
            r => r.Key.TryGet(Player, out var pp) ? pp.Numeric : -1,
            r => r.Measure.Value);

        Assert.Equal(250.0, byPlayer[1]);
        Assert.Equal(250.0, byPlayer[2]);
    }

    [Fact]
    public void PerGroup_runs_inner_transform_independently_per_partition()
    {
        // A 2-day rolling sum, computed per player — player 2's data must not leak into player 1.
        var block = PointBlock.FromRows(
        [
            Load(1, 10, 1), Load(1, 10, 2),
            Load(2, 100, 1), Load(2, 100, 2),
        ]);

        var result = Transform
            .PerGroup(p => p.Key, Transform.Rolling(TimeSpan.FromDays(2), Aggregators.Sum))
            .Apply(block, Ctx);

        var p1Day2 = result.Rows().Single(r =>
            r.Key.TryGet(Player, out var pp) && pp.Numeric == 1 && r.At == Day(2));
        Assert.Equal(20.0, p1Day2.Measure.Value); // 10 + 10, not polluted by player 2
    }

    [Fact]
    public void Rolling_window_is_left_exclusive_right_inclusive()
    {
        // Daily value 1..5. A 2-day window at day 3 covers days 2 and 3 only (sum = 5), not day 1.
        var block = PointBlock.FromRows(
        [
            Load(1, 1, 1), Load(1, 2, 2), Load(1, 3, 3), Load(1, 4, 4), Load(1, 5, 5),
        ]);

        var rolling = Transform.Rolling(TimeSpan.FromDays(2), Aggregators.Sum).Apply(block, Ctx);
        var atDay3 = rolling.Rows().Single(r => r.At == Day(3));

        Assert.Equal(5.0, atDay3.Measure.Value); // days 2 (=2) + 3 (=3)
    }

    [Fact]
    public void Compare_percent_change_between_two_timeframes()
    {
        var baseline = Transform.Reduce(p => p.Key, Aggregators.Sum)
            .Apply(PointBlock.FromRows([Load(1, 100, 1)]), Ctx);
        var current = Transform.Reduce(p => p.Key, Aggregators.Sum)
            .Apply(PointBlock.FromRows([Load(1, 130, 1)]), Ctx);

        var change = Binary.Compare(baseline, current, CompareMode.PercentChange);

        Assert.Equal(30.0, change.Values[0], precision: 10); // +30%
        Assert.Equal(new Unit("%"), change.Unit);
    }

    [Fact]
    public void Filter_can_key_off_a_category_dimension()
    {
        var homeKey = PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Home"));
        var awayKey = PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Away"));
        var block = PointBlock.FromRows(
        [
            new Point(homeKey, 10, Day(1)),
            new Point(awayKey, 20, Day(1)),
        ]);

        var homeOnly = Transform
            .Filter(p => p.Key.TryGet(Venue, out var v) && v.Text == "Home")
            .Apply(block, Ctx);

        Assert.Equal(1, homeOnly.Count);
        Assert.Equal(10.0, homeOnly.Values[0]);
    }
}
