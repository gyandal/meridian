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

public class AggregatorTests
{
    private static double Run(IAggregator aggregator, params double[] values) => aggregator.Aggregate(values);

    [Fact]
    public void Spread_is_the_sample_standard_deviation_and_one_value_has_none()
    {
        Assert.Equal(2.5, Run(Aggregators.Variance, 1, 2, 3, 4, 5), 12);
        Assert.Equal(Math.Sqrt(2.5), Run(Aggregators.StdDev, 1, 2, 3, 4, 5), 12);
        Assert.Equal(0.0, Run(Aggregators.StdDev, 7, 7, 7));
        Assert.True(double.IsNaN(Run(Aggregators.StdDev, 7)));
        Assert.Equal(2.5, Run(Aggregators.Variance, 1e9 + 1, 1e9 + 2, 1e9 + 3, 1e9 + 4, 1e9 + 5), 6); // no cancellation
    }

    [Fact]
    public void Percentiles_interpolate_between_the_nearest_values()
    {
        Assert.Equal(1.75, Run(Aggregators.Percentile(25), 4, 1, 3, 2), 12);
        Assert.Equal(3.7, Run(Aggregators.Percentile(90), 1, 2, 3, 4), 12);
        Assert.Equal(Run(Aggregators.Median, 5, 1, 9, 3), Run(Aggregators.Percentile(50), 5, 1, 9, 3));
        Assert.Equal(8.0, Run(Aggregators.Percentile(99), 8));
        Assert.True(double.IsNaN(Run(Aggregators.Percentile(90))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Aggregators.Percentile(100));
    }

    [Fact]
    public void First_and_last_are_by_time_order()
    {
        Assert.Equal(4.0, Run(Aggregators.First, 4, 1, 9));
        Assert.Equal(9.0, Run(Aggregators.Last, 4, 1, 9));
    }

    [Theory]
    [InlineData("p90", "p90")]
    [InlineData("P99.5", "p99.5")]
    [InlineData("stddev", "stddev")]
    [InlineData("first", "first")]
    public void Names_resolve(string name, string resolved)
    {
        Assert.True(Aggregators.TryResolve(name, out var aggregator));
        Assert.Equal(resolved, aggregator.Name);
    }

    [Theory]
    [InlineData("p0")]
    [InlineData("p100")]
    [InlineData("p")]
    [InlineData("pnine")]
    [InlineData("p-5")]
    public void Nonsense_percentiles_do_not(string name) => Assert.False(Aggregators.TryResolve(name, out _));

    [Fact]
    public void Percentiles_are_recognised_by_type_not_name()
    {
        Assert.True(Aggregators.IsPercentile(Aggregators.Percentile(90), out var percent));
        Assert.Equal(90, percent);
        Assert.Equal(Aggregators.Percentile(90), Aggregators.TryResolve("p90", out var resolved) ? resolved : null);
        Assert.False(Aggregators.IsPercentile(new Named("p90"), out _)); // a custom aggregator that happens to share the name
    }

    [Fact]
    public void NaN_is_never_a_present_value() => Assert.False(Measurement.Of(double.NaN).IsPresent);

    private sealed class Named(string name) : IAggregator
    {
        public string Name => name;
        public double Aggregate(ReadOnlySpan<double> presentValues) => 0;
    }
}
