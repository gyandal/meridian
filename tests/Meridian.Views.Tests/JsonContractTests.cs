using Meridian.Core;
using Meridian.Time;
using Meridian.Views;
using Meridian.Views.Json;
using Xunit;

namespace Meridian.Views.Tests;

public class JsonContractTests
{
    private static readonly DimensionId Player = new("player");

    private static ChartView SampleView()
    {
        var block = PointBlock.FromRows(
        [
            new Point(PointKey.Of(KeyPart.Entity(Player, 1)), 10.0,
                Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))),
        ], new Unit("au"));
        return ChartProjector.Instance.Project(
            block, new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Player), ProjectionOptions.Default);
    }

    [Fact]
    public void Serialises_camelCase_with_enums_as_strings()
    {
        var json = ChartViewJson.Serialize(SampleView());

        Assert.Contains("\"kind\":\"Line\"", json);       // enum as string, camelCase property
        Assert.Contains("\"series\":", json);
        Assert.Contains("\"colorToken\":\"#", json);       // theme colour present in the view
        Assert.DoesNotContain("Values", json);             // no columnar internals leak
    }

    [Fact]
    public void Round_trips_through_the_source_generated_contract()
    {
        var original = SampleView();
        var restored = ChartViewJson.Deserialize(ChartViewJson.Serialize(original));

        Assert.Equal(original.Kind, restored.Kind);
        Assert.Equal(original.Series.Count, restored.Series.Count);
        Assert.Equal(original.Series[0].Name, restored.Series[0].Name);
        Assert.Equal(original.Series[0].Marks[0].At, restored.Series[0].Marks[0].At);
        Assert.Equal(original.Axes[0].Kind, restored.Axes[0].Kind);
    }

    [Fact]
    public void Null_fields_are_omitted()
    {
        var json = ChartViewJson.Serialize(SampleView());
        // The single mark has a value and an At but no X/Y/Z → those must not appear.
        Assert.DoesNotContain("\"x\":", json);
        Assert.DoesNotContain("\"y\":", json);
    }
}
