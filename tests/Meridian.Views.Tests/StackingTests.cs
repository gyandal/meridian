using Meridian.Core;
using Meridian.Time;
using Meridian.Views;
using Meridian.Views.Json;
using Xunit;

namespace Meridian.Views.Tests;

public class StackingTests
{
    private static readonly DimensionId Venue = new("venue");
    private static readonly DimensionId Team = new("team");

    /// <summary>Two months of home and away goals: Jan 4 + 5, Feb 6 + 1 (and an away own-goal adjustment of −2 in Feb).</summary>
    private static PointBlock Goals(params (int Month, string Venue, double Goals)[] rows) => PointBlock.FromRows(
        [.. rows.Select(r => new Point(PointKey.Of(KeyPart.Entity(Team, 1), KeyPart.Category(Venue, r.Venue)), r.Goals,
            new Instant(new DateTime(2026, r.Month, 1).Ticks)))], time: TimeAxis.Local("month"));

    private static ChartView Project(PointBlock block, bool stacked) =>
        ChartProjector.Instance.Project(block, new ViewSpec(ChartKind.Column, AxisSource.Time, SeriesBy: Venue, Stacked: stacked), ProjectionOptions.Default);

    [Fact]
    public void A_stacked_view_says_so_and_its_axis_reaches_each_stacks_total()
    {
        var block = Goals((1, "Home", 4), (1, "Away", 5), (2, "Home", 6), (2, "Away", 1));

        var stacked = Project(block, stacked: true);
        Assert.True(stacked.Stacked);
        Assert.Equal(0, stacked.Axes[1].Min);   // stacks rise from 0
        Assert.Equal(9, stacked.Axes[1].Max);   // January: 4 + 5

        var side = Project(block, stacked: false);
        Assert.Null(side.Stacked);
        Assert.Equal(1, side.Axes[1].Min);       // unchanged: the smallest and largest single values
        Assert.Equal(6, side.Axes[1].Max);
    }

    [Fact]
    public void Negative_values_stack_downwards_from_zero()
    {
        var view = Project(Goals((1, "Home", 4), (1, "Away", -2), (1, "Neutral", -3), (2, "Home", 6)), stacked: true);
        Assert.Equal(-5, view.Axes[1].Min);
        Assert.Equal(6, view.Axes[1].Max);
    }

    [Fact]
    public void Json_carries_stacked_only_when_true_and_round_trips()
    {
        var block = Goals((1, "Home", 4), (1, "Away", 5));
        var stacked = ChartViewJson.Serialize(Project(block, stacked: true));
        var plain = ChartViewJson.Serialize(Project(block, stacked: false));

        Assert.Contains("\"stacked\":true", stacked);
        Assert.DoesNotContain("stacked", plain);                          // existing output unchanged
        Assert.True(ChartViewJson.Deserialize(stacked).Stacked);
        Assert.Null(ChartViewJson.Deserialize(plain).Stacked);
    }

    [Fact]
    public void A_composed_chart_takes_stacking_from_its_first_part_and_stacks_across_parts()
    {
        var home = Project(Goals((1, "Home", 4), (2, "Home", 6)), stacked: true);
        var away = Project(Goals((1, "Away", 5), (2, "Away", 1)), stacked: false);

        var composed = ChartComposer.Compose([new ChartPart(home, "Home"), new ChartPart(away, "Away")], ProjectionOptions.Default.Theme);
        Assert.True(composed.Stacked);
        Assert.Equal(9, composed.Axes[1].Max); // January's home and away, stacked across the two parts

        var unstacked = ChartComposer.Compose([new ChartPart(away, "Away"), new ChartPart(home, "Home")], ProjectionOptions.Default.Theme);
        Assert.Null(unstacked.Stacked);
        Assert.Equal(6, unstacked.Axes[1].Max);
    }
}
