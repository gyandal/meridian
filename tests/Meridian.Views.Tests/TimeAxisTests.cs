using Meridian.Core;
using Meridian.Time;
using Meridian.Views;
using Meridian.Views.Json;
using Xunit;

namespace Meridian.Views.Tests;

/// <summary>Charts say what their times are, and label buckets server-side — renderers never do zone maths.</summary>
public class TimeAxisTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly PointKey Key = PointKey.Of(KeyPart.Entity(Player, 1));

    private static ChartView Project(PointBlock block, CalendarContext calendar) =>
        ChartProjector.Instance.Project(block, new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Player),
            ProjectionOptions.Default with { Calendar = calendar });

    [Theory]
    [InlineData("day", "2026-07-13")]
    [InlineData("week", "2026-07-13")]
    [InlineData("month", "2026-07")]
    [InlineData("season", "2026/27")]
    public void Buckets_are_labelled_by_grain_from_their_local_start(string grain, string label)
    {
        var block = PointBlock.FromRows([new Point(Key, 1, new Instant(new DateTime(2026, 7, 13).Ticks))],
            time: TimeAxis.Local(grain, "Europe/London"));
        var view = Project(block, CalendarContext.For("Europe/London"));

        Assert.Equal(label, view.Series[0].Marks[0].Label);
        // Local epoch-millis encode the wall clock: 13 July 00:00, not the UTC moment 12 July 23:00.
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), view.Series[0].Marks[0].At);
        Assert.Equal(new AxisView(AxisKind.Temporal, "Time", Time: TimeKind.Local, TimeZone: "Europe/London", Grain: grain), view.Axes[0]);
    }

    [Fact]
    public void Instants_are_labelled_in_the_calendar_zone_and_the_axis_says_which()
    {
        var block = PointBlock.FromRows([new Point(Key, 1, Instant.FromUtc(new DateTime(2026, 7, 12, 23, 30, 0, DateTimeKind.Utc)))]);
        var view = Project(block, CalendarContext.For("Europe/London"));

        Assert.Equal("2026-07-13 00:30", view.Series[0].Marks[0].Label);
        Assert.Equal(TimeKind.Instant, view.Axes[0].Time);
        Assert.Equal("Europe/London", view.Axes[0].TimeZone);
    }

    [Fact]
    public void The_wire_contract_carries_the_time_axis()
    {
        var block = PointBlock.FromRows([new Point(Key, 1, new Instant(new DateTime(2026, 7, 13).Ticks))],
            time: TimeAxis.Local("week", "Europe/London"));
        var json = ChartViewJson.Serialize(Project(block, CalendarContext.For("Europe/London")));

        Assert.Contains("\"time\":\"Local\"", json);
        Assert.Contains("\"timeZone\":\"Europe/London\"", json);
        Assert.Contains("\"grain\":\"week\"", json);
    }
}
