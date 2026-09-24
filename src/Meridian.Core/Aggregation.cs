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
        };

    public static bool TryResolve(string name, out IAggregator aggregator) =>
        ByName.TryGetValue(name, out aggregator!);

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
