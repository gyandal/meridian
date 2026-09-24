using Meridian.Core;
using Xunit;

namespace Meridian.Core.Tests;

public class CoreValueTests
{
    [Fact]
    public void Missing_is_a_flag_not_a_value()
    {
        Assert.False(Measurement.Missing.IsPresent);
        Assert.True(Measurement.Of(0.0).IsPresent);   // zero is present, not missing
        Assert.True(((Measurement)42.0).IsPresent);
    }

    [Fact]
    public void PointBlock_round_trips_rows_through_columnar_layout()
    {
        var key = PointKey.Of(KeyPart.Entity(new DimensionId("player"), 1));
        var rows = new[]
        {
            new Point(key, 10.0, Instant.FromUtc(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc))),
            new Point(key, 20.0, Instant.FromUtc(new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc))),
            new Point(key, Measurement.Missing, null, Unit.None),
        };

        var block = PointBlock.FromRows(rows, new Unit("au"));
        var back = block.Rows().ToArray();

        Assert.Equal(3, block.Count);
        Assert.Equal(new Unit("au"), block.Unit);
        Assert.Equal(20.0, back[1].Measure.Value);
        Assert.False(back[2].Measure.IsPresent);
        Assert.Null(back[2].At);
    }

    [Theory]
    [InlineData("sum", 10.0)]
    [InlineData("mean", 2.5)]
    [InlineData("min", 1.0)]
    [InlineData("max", 4.0)]
    [InlineData("count", 4.0)]
    [InlineData("last", 4.0)]
    [InlineData("median", 2.5)]
    public void Aggregators_resolve_by_name_and_compute(string name, double expected)
    {
        Assert.True(Aggregators.TryResolve(name, out var agg));
        double[] values = [1, 2, 3, 4];
        Assert.Equal(expected, agg.Aggregate(values), precision: 10);
    }

    [Fact]
    public void Median_of_odd_count_is_the_middle()
    {
        double[] values = [3, 1, 2];
        Assert.Equal(2.0, Aggregators.Median.Aggregate(values));
    }
}
