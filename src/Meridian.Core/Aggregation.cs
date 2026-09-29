using System.Globalization;

namespace Meridian.Core;

/// <summary>
/// One extensible aggregator abstraction — a single registry (not competing enums)
/// that also takes custom implementations. Only PRESENT values are passed in; gap handling is the
/// resampler's job, not the aggregator's.
/// </summary>
public interface IAggregator
{
    string Name { get; }

    /// <summary>Aggregate the present values of a bucket. For order-sensitive aggregators
    /// (e.g. Last) the span is supplied in ascending time order.</summary>
    double Aggregate(ReadOnlySpan<double> presentValues);
}

public static class Aggregators
{
    public static readonly IAggregator Sum = new SumAggregator();
    public static readonly IAggregator Mean = new MeanAggregator();
    public static readonly IAggregator Min = new MinAggregator();
    public static readonly IAggregator Max = new MaxAggregator();
    public static readonly IAggregator Count = new CountAggregator();
    public static readonly IAggregator Last = new LastAggregator();
    public static readonly IAggregator Median = new MedianAggregator();
    public static readonly IAggregator First = new FirstAggregator();

    /// <summary>Sample standard deviation (n − 1): how spread out a bucket's values are. One value has none.</summary>
    public static readonly IAggregator StdDev = new StdDevAggregator();

    /// <summary>Sample variance (n − 1), the square of <see cref="StdDev"/>.</summary>
    public static readonly IAggregator Variance = new VarianceAggregator();

    /// <summary>
    /// The <paramref name="percent"/>th percentile, interpolating linearly between the two nearest values (the
    /// method of Excel's PERCENTILE.INC and SQL's <c>quantile_cont</c>): <c>Percentile(50)</c> is the median,
    /// <c>Percentile(90)</c> the value 90% of readings are at or below. Named <c>p90</c>, <c>p99.5</c>…
    /// </summary>
    public static IAggregator Percentile(double percent) =>
        percent is > 0 and < 100 ? new PercentileAggregator(percent)
            : throw new ArgumentOutOfRangeException(nameof(percent), percent, "A percentile is between 0 and 100, exclusive.");

    private static readonly IReadOnlyDictionary<string, IAggregator> ByName =
        new Dictionary<string, IAggregator>(StringComparer.OrdinalIgnoreCase)
        {
            [Sum.Name] = Sum,
            [Mean.Name] = Mean,
            [Min.Name] = Min,
            [Max.Name] = Max,
            [Count.Name] = Count,
            [Last.Name] = Last,
            [Median.Name] = Median,
            [First.Name] = First,
            [StdDev.Name] = StdDev,
            [Variance.Name] = Variance,
        };

    /// <summary>The built-in names, for listing in APIs; percentiles are <c>p</c> plus a number, e.g. <c>p90</c>.</summary>
    public static IReadOnlyCollection<string> Names { get; } = [.. ByName.Keys, "p90"];

    /// <summary>Whether <paramref name="aggregator"/> is a built-in <see cref="Percentile"/>, and which.</summary>
    public static bool IsPercentile(IAggregator aggregator, out double percent)
    {
        percent = aggregator is PercentileAggregator p ? p.Percent : 0;
        return aggregator is PercentileAggregator;
    }

    public static bool TryResolve(string name, out IAggregator aggregator)
    {
        if (ByName.TryGetValue(name, out aggregator!)) return true;
        if (name is ['p' or 'P', .. var number]
            && double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
            && percent is > 0 and < 100)
        {
            aggregator = new PercentileAggregator(percent);
            return true;
        }
        return false;
    }

    private sealed class SumAggregator : IAggregator
    {
        public string Name => "sum";
        public double Aggregate(ReadOnlySpan<double> v)
        {
            double sum = 0;
            foreach (var x in v) sum += x;
            return sum;
        }
    }

    private sealed class MeanAggregator : IAggregator
    {
        public string Name => "mean";
        public double Aggregate(ReadOnlySpan<double> v)
        {
            if (v.Length == 0) return double.NaN;
            double sum = 0;
            foreach (var x in v) sum += x;
            return sum / v.Length;
        }
    }

    private sealed class MinAggregator : IAggregator
    {
        public string Name => "min";
        public double Aggregate(ReadOnlySpan<double> v)
        {
            if (v.Length == 0) return double.NaN;
            double min = double.PositiveInfinity;
            foreach (var x in v) if (x < min) min = x;
            return min;
        }
    }

    private sealed class MaxAggregator : IAggregator
    {
        public string Name => "max";
        public double Aggregate(ReadOnlySpan<double> v)
        {
            if (v.Length == 0) return double.NaN;
            double max = double.NegativeInfinity;
            foreach (var x in v) if (x > max) max = x;
            return max;
        }
    }

    private sealed class CountAggregator : IAggregator
    {
        public string Name => "count";
        public double Aggregate(ReadOnlySpan<double> v) => v.Length;
    }

    private sealed class LastAggregator : IAggregator
    {
        public string Name => "last";
        public double Aggregate(ReadOnlySpan<double> v) => v.Length == 0 ? double.NaN : v[^1];
    }

    private sealed class FirstAggregator : IAggregator
    {
        public string Name => "first";
        public double Aggregate(ReadOnlySpan<double> v) => v.Length == 0 ? double.NaN : v[0];
    }

    private sealed class StdDevAggregator : IAggregator
    {
        public string Name => "stddev";
        public double Aggregate(ReadOnlySpan<double> v) => Math.Sqrt(SampleVariance(v));
    }

    private sealed class VarianceAggregator : IAggregator
    {
        public string Name => "variance";
        public double Aggregate(ReadOnlySpan<double> v) => SampleVariance(v);
    }

    /// <summary>Welford's method: one pass, and no catastrophic cancellation for large values.</summary>
    private static double SampleVariance(ReadOnlySpan<double> v)
    {
        if (v.Length < 2) return double.NaN;
        double mean = 0, m2 = 0;
        for (int i = 0; i < v.Length; i++)
        {
            double delta = v[i] - mean;
            mean += delta / (i + 1);
            m2 += delta * (v[i] - mean);
        }
        return m2 / (v.Length - 1);
    }

    /// <summary>Equal by percentile, so <c>p90</c> resolved twice is one aggregator.</summary>
    private sealed record PercentileAggregator(double Percent) : IAggregator
    {
        public string Name => "p" + Percent.ToString(CultureInfo.InvariantCulture);

        public double Aggregate(ReadOnlySpan<double> v)
        {
            if (v.Length == 0) return double.NaN;
            Span<double> sorted = v.Length <= 64 ? stackalloc double[v.Length] : new double[v.Length];
            v.CopyTo(sorted);
            sorted.Sort();
            double rank = Percent / 100 * (sorted.Length - 1);
            int below = (int)Math.Floor(rank);
            return below + 1 < sorted.Length
                ? sorted[below] + (rank - below) * (sorted[below + 1] - sorted[below])
                : sorted[below];
        }
    }

    private sealed class MedianAggregator : IAggregator
    {
        public string Name => "median";
        public double Aggregate(ReadOnlySpan<double> v)
        {
            if (v.Length == 0) return double.NaN;
            Span<double> copy = v.Length <= 64 ? stackalloc double[v.Length] : new double[v.Length];
            v.CopyTo(copy);
            copy.Sort();
            int mid = copy.Length / 2;
            return (copy.Length & 1) == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
        }
    }
}
