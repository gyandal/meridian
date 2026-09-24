using System.Runtime.InteropServices;
using Meridian.Core;
using Meridian.Time;

namespace Meridian.Transforms;

/// <summary>Factory for the built-in transforms — the "standard library" of the algebra.</summary>
public static class Transform
{
    public static ITransform Filter(Func<Point, bool> predicate) => new FilterTransform(predicate);

    public static ITransform Map(Func<Point, Measurement> project) => new MapTransform(project);

    public static ITransform Rekey(Func<PointKey, PointKey> reproject) => new RekeyTransform(reproject);

    /// <summary>Group by a derived key and aggregate each group's present values to one point.</summary>
    public static ITransform Reduce(Func<Point, PointKey> keySelector, IAggregator aggregator) =>
        new ReduceTransform(keySelector, aggregator);

    /// <summary>Partition by a derived key and run <paramref name="inner"/> on each partition independently.</summary>
    public static ITransform PerGroup(Func<Point, PointKey> keySelector, ITransform inner) =>
        new PerGroupTransform(keySelector, inner);

    /// <summary>Collapse the time axis into tumbling buckets. Wraps <see cref="Resampler"/>.</summary>
    public static ITransform Resample(IPeriod period, IAggregator aggregator, GapPolicy gap) =>
        new ResampleTransform(period, aggregator, gap);

    /// <summary>Rolling window over time, per key. Window is (t - <paramref name="window"/>, t].</summary>
    public static ITransform Rolling(TimeSpan window, IAggregator aggregator) =>
        new RollingTransform(window, aggregator);
}

internal sealed class FilterTransform(Func<Point, bool> predicate) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = new PointBlock.Builder(input.Unit);
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            if (predicate(row)) builder.Add(row);
        }
        return builder.Build();
    }
}

internal sealed class MapTransform(Func<Point, Measurement> project) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = new PointBlock.Builder(input.Unit);
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            builder.Add(row.Key, project(row), row.At);
        }
        return builder.Build();
    }
}

internal sealed class RekeyTransform(Func<PointKey, PointKey> reproject) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = new PointBlock.Builder(input.Unit);
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            builder.Add(reproject(row.Key), row.Measure, row.At);
        }
        return builder.Build();
    }
}

internal sealed class ReduceTransform(Func<Point, PointKey> keySelector, IAggregator aggregator) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var groups = new Dictionary<PointKey, List<double>>();
        var order = new List<PointKey>();
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            var key = keySelector(row);
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups[key] = values;
                order.Add(key);
            }
            if (row.Measure.IsPresent) values.Add(row.Measure.Value);
        }

        var builder = new PointBlock.Builder(input.Unit);
        foreach (var key in order)
        {
            var values = groups[key];
            var measure = values.Count == 0
                ? Measurement.Missing
                : Measurement.Of(aggregator.Aggregate(CollectionsMarshal.AsSpan(values)));
            builder.Add(key, measure, null);
        }
        return builder.Build();
    }
}

internal sealed class PerGroupTransform(Func<Point, PointKey> keySelector, ITransform inner) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var groups = new Dictionary<PointKey, PointBlock.Builder>();
        var order = new List<PointKey>();
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            var key = keySelector(row);
            if (!groups.TryGetValue(key, out var builder))
            {
                builder = new PointBlock.Builder(input.Unit);
                groups[key] = builder;
                order.Add(key);
            }
            builder.Add(row);
        }

        var output = new PointBlock.Builder(input.Unit);
        foreach (var key in order)
        {
            var partition = inner.Apply(groups[key].Build(), ctx);
            for (int i = 0; i < partition.Count; i++) output.Add(partition.Row(i));
        }
        return output.Build();
    }
}

/// <summary>Public so the engine can recognise a leading resample and push it down to a capable source.</summary>
public sealed class ResampleTransform(IPeriod period, IAggregator aggregator, GapPolicy gap) : ITransform
{
    public IPeriod Period { get; } = period;
    public IAggregator Aggregator { get; } = aggregator;
    public GapPolicy Gap { get; } = gap;

    public PointBlock Apply(PointBlock input, TransformContext ctx) =>
        Resampler.Resample(input, Period, Aggregator, Gap, ctx.Calendar);
}

internal sealed class RollingTransform(TimeSpan window, IAggregator aggregator) : ITransform
{
    private readonly long _windowTicks = window.Ticks;

    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        // Group indices by key; skip rows without a position.
        var byKey = new Dictionary<PointKey, List<int>>();
        var order = new List<PointKey>();
        for (int i = 0; i < input.Count; i++)
        {
            if (input.AtTicks[i] == PointBlock.NoAt) continue;
            if (!byKey.TryGetValue(input.Keys[i], out var list))
            {
                list = [];
                byKey[input.Keys[i]] = list;
                order.Add(input.Keys[i]);
            }
            list.Add(i);
        }

        var output = new PointBlock.Builder(input.Unit);
        var window = new List<double>();

        foreach (var key in order)
        {
            var indices = byKey[key];
            indices.Sort((a, b) => input.AtTicks[a].CompareTo(input.AtTicks[b]));

            int left = 0;
            for (int p = 0; p < indices.Count; p++)
            {
                long t = input.AtTicks[indices[p]];
                long lowerExclusive = t - _windowTicks;

                // Left edge: window is (t - window, t]  → drop anything at or before the lower bound.
                while (input.AtTicks[indices[left]] <= lowerExclusive) left++;

                window.Clear();
                for (int j = left; j <= p; j++)
                {
                    int idx = indices[j];
                    if ((input.Flags[idx] & MeasureFlags.Missing) == 0)
                    {
                        window.Add(input.Values[idx]);
                    }
                }

                var measure = window.Count == 0
                    ? Measurement.Missing
                    : Measurement.Of(aggregator.Aggregate(CollectionsMarshal.AsSpan(window)));
                output.Add(key, measure, new Instant(t));
            }
        }

        return output.Build();
    }
}
