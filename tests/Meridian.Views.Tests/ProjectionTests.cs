using Meridian.Core;
using Meridian.Time;
using Meridian.Views;
using Xunit;

namespace Meridian.Views.Tests;

public class ProjectionTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly ProjectionOptions Options = ProjectionOptions.Default;

    private static Instant Day(int d) =>
        Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(d - 1));

    [Fact]
    public void Temporal_line_produces_marks_with_epoch_millis_and_labels()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 1));
        var block = PointBlock.FromRows(
        [
            new Point(key, 10.0, Day(2)),
            new Point(key, 20.0, Day(1)), // deliberately out of order
        ], new Unit("au"));

        var view = ChartProjector.Instance.Project(
            block, new ViewSpec(ChartKind.Line, AxisSource.Time), Options);

        Assert.Single(view.Series);
        var marks = view.Series[0].Marks;
        Assert.Equal(2, marks.Count);
        // Marks are ordered by instant, not insertion, not label string.
        Assert.Equal("2026-08-01", marks[0].Label);
        Assert.Equal(Day(1).ToUnixMilliseconds(), marks[0].At);
        Assert.Equal(20.0, marks[0].Value);

        Assert.Equal(AxisKind.Temporal, view.Axes[0].Kind);
        Assert.Equal(AxisKind.Linear, view.Axes[1].Kind);
        Assert.Equal(10.0, view.Axes[1].Min);
        Assert.Equal(20.0, view.Axes[1].Max);
    }

    [Fact]
    public void Series_split_by_dimension_get_distinct_names_and_colours()
    {
        var block = PointBlock.FromRows(
        [
            new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 10.0, Day(1)),
            new Point(PointKey.Of(KeyPart.Entity(Player, 2)), 20.0, Day(1)),
        ]);

        var view = ChartProjector.Instance.Project(
            block, new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Player), Options);

        Assert.Equal(2, view.Series.Count);
        Assert.Equal("player 1", view.Series[0].Name);
        Assert.Equal("player 2", view.Series[1].Name);
        Assert.NotEqual(view.Series[0].ColorToken, view.Series[1].ColorToken);
        Assert.Equal(["player 1", "player 2"], view.Legend.Series);
    }

    [Fact]
    public void Category_axis_labels_marks_by_category_text()
    {
        var block = PointBlock.FromRows(
        [
            new Point(PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Home")), 3.0, null),
            new Point(PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Away")), 1.0, null),
        ]);

        var view = ChartProjector.Instance.Project(
            block, new ViewSpec(ChartKind.Column, AxisSource.Category(Venue)), Options);

        var labels = view.Series[0].Marks.Select(m => m.Label).ToArray();
        Assert.Equal(["Home", "Away"], labels);
        Assert.Equal(AxisKind.Category, view.Axes[0].Kind);
        Assert.Equal("venue", view.Axes[0].Title);
    }

    [Fact]
    public void Status_rule_drives_the_mark_colour_not_the_datum()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 1));
        var block = PointBlock.FromRows(
        [
            new Point(key, 1.0, Day(1)),   // in band → Good
            new Point(key, 5.0, Day(2)),   // far out → Bad
        ]);

        // ACWR-style sweet spot [0.8, 1.3].
        var spec = new ViewSpec(ChartKind.Line, AxisSource.Time, Status: new TargetBand(0.8, 1.3, watchMargin: 0.2));
        var view = ChartProjector.Instance.Project(block, spec, Options);

        var marks = view.Series[0].Marks;
        Assert.Equal(SemanticStatus.Good, marks[0].Status);
        Assert.Equal(DefaultTheme.Instance.ColorForStatus(SemanticStatus.Good), marks[0].ColorToken);
        Assert.Equal(SemanticStatus.Bad, marks[1].Status);
    }

    [Fact]
    public void Missing_measurement_projects_a_null_valued_mark()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 1));
        var block = PointBlock.FromRows(
        [
            new Point(key, Measurement.Missing, Day(1), Unit.None),
            new Point(key, 20.0, Day(2)),
        ]);

        var view = ChartProjector.Instance.Project(block, new ViewSpec(ChartKind.Line, AxisSource.Time), Options);

        Assert.Null(view.Series[0].Marks[0].Value);
        Assert.Equal(20.0, view.Series[0].Marks[1].Value);
    }
}
