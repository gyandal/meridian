using Meridian.Core;
using Meridian.Time;
using Meridian.Views;
using Meridian.Views.Json;
using Xunit;

namespace Meridian.Views.Tests;

public class ComposerTests
{
    private static readonly DimensionId Player = new("player");

    private static ChartView View(ChartKind kind, double scale, int players = 1, IStatusRule? status = null, string unit = "")
    {
        var rows = Enumerable.Range(1, players).SelectMany(p => Enumerable.Range(0, 3).Select(m =>
            new Point(PointKey.Of(KeyPart.Entity(Player, p)), (m + 1) * scale * p, new Instant(new DateTime(2026, 1 + m, 1).Ticks))));
        var block = PointBlock.FromRows(rows, new Unit(unit), TimeAxis.Local("month", "UTC"));
        return ChartProjector.Instance.Project(block,
            new ViewSpec(kind, AxisSource.Time, SeriesBy: players > 1 ? Player : null, Status: status), ProjectionOptions.Default);
    }

    [Fact]
    public void Parts_become_named_coloured_series_with_their_own_kind_and_axis()
    {
        var chart = ChartComposer.Compose(
        [
            new ChartPart(View(ChartKind.Column, 2), "Goals"),
            new ChartPart(View(ChartKind.Line, 300, unit: "min"), "Minutes", ValueAxis.Secondary),
            new ChartPart(View(ChartKind.Line, 0.5, unit: "/90"), "Goals per 90"),
        ], DefaultTheme.Instance);

        Assert.Equal(["Goals", "Minutes", "Goals per 90"], chart.Series.Select(s => s.Name));
        Assert.Equal([ChartKind.Column, ChartKind.Line, ChartKind.Line], chart.Series.Select(s => s.Kind!.Value));
        Assert.Equal([1, 2, 1], chart.Series.Select(s => s.Axis!.Value));
        Assert.Equal(3, chart.Series.Select(s => s.ColorToken).Distinct().Count());
        Assert.All(chart.Series, s => Assert.All(s.Marks, m => Assert.Equal(s.ColorToken, m.ColorToken)));

        Assert.Equal(3, chart.Axes.Count);                        // x, primary, secondary
        Assert.Equal("Goals · Goals per 90", chart.Axes[1].Title);
        Assert.Equal(0.5, chart.Axes[1].Min);                     // spans both primary parts
        Assert.Equal(6, chart.Axes[1].Max);
        Assert.Null(chart.Axes[1].Unit);                          // "" and "/90" differ
        Assert.Equal("min", chart.Axes[2].Unit);
        Assert.Equal(900, chart.Axes[2].Max);
    }

    [Fact]
    public void A_part_with_a_series_per_entity_names_each_one()
    {
        var chart = ChartComposer.Compose([new ChartPart(View(ChartKind.Line, 1, players: 2), "Load")], DefaultTheme.Instance);
        Assert.Equal(["Load · player 1", "Load · player 2"], chart.Series.Select(s => s.Name));
        Assert.Equal(2, chart.Axes.Count); // no secondary axis unless a part asks for one
    }

    [Fact]
    public void Status_colours_survive_recolouring()
    {
        var chart = ChartComposer.Compose(
        [
            new ChartPart(View(ChartKind.Line, 1), "First"),
            new ChartPart(View(ChartKind.Line, 1, status: new TargetBand(0, 2)), "Banded"),
        ], DefaultTheme.Instance);

        var banded = chart.Series[1];
        Assert.Contains(banded.Marks, m => m.Status == SemanticStatus.Good && m.ColorToken == DefaultTheme.Instance.ColorForStatus(SemanticStatus.Good));
        Assert.Contains(banded.Marks, m => m.Status == SemanticStatus.Bad && m.ColorToken == DefaultTheme.Instance.ColorForStatus(SemanticStatus.Bad));
    }

    [Fact]
    public void Parts_must_share_the_x_axis()
    {
        var weekly = ChartProjector.Instance.Project(
            PointBlock.FromRows([new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 1, new Instant(new DateTime(2026, 1, 5).Ticks))],
                time: TimeAxis.Local("week", "UTC")),
            new ViewSpec(ChartKind.Line, AxisSource.Time), ProjectionOptions.Default);

        var error = Assert.Throws<ArgumentException>(() =>
            ChartComposer.Compose([new ChartPart(View(ChartKind.Line, 1), "Monthly"), new ChartPart(weekly, "Weekly")], DefaultTheme.Instance));
        Assert.Contains("one x-axis", error.Message);
    }

    [Fact]
    public void Single_report_views_omit_the_new_fields_on_the_wire()
    {
        var json = ChartViewJson.Serialize(View(ChartKind.Line, 1));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var series = doc.RootElement.GetProperty("series")[0];
        Assert.False(series.TryGetProperty("kind", out _));
        Assert.False(series.TryGetProperty("axis", out _));
    }
}
