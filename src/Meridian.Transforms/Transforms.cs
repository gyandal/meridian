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

    /// <summary>
    /// Group by dimensions and aggregate, keeping the time axis: each output point is one combination of
    /// <paramref name="by"/> at one time. <c>GroupBy(Sum, venue)</c> after a monthly resample gives goals per
    /// venue per month; with no dimensions it merges everything per time (e.g. the max across hosts).
    /// Every other key part (typically the entity) is folded in. Declarative, so reports that use it cache.
    /// </summary>
    public static ITransform GroupBy(IAggregator aggregator, params DimensionId[] by) =>
        new GroupByTransform(aggregator, by, keepTime: true);

    /// <summary>
    /// Group by dimensions and aggregate over the whole timeframe: one point per combination of
    /// <paramref name="by"/>, with no time. <c>Total(Sum, venue)</c> is goals by venue; <c>Total(Sum, player)</c>
    /// is goals per player.
    /// </summary>
    public static ITransform Total(IAggregator aggregator, params DimensionId[] by) =>
        new GroupByTransform(aggregator, by, keepTime: false);

    /// <summary>Keep points whose <paramref name="dimension"/> is one of <paramref name="values"/> — e.g. home
    /// matches only. Entity ids match their number ("7"). Declarative, so it caches and can be stored.</summary>
    public static ITransform WhereIn(DimensionId dimension, params string[] values) =>
        new KeyFilterTransform(dimension, values, exclude: false);

    /// <summary>Drop points whose <paramref name="dimension"/> is one of <paramref name="values"/>.</summary>
    public static ITransform WhereNotIn(DimensionId dimension, params string[] values) =>
        new KeyFilterTransform(dimension, values, exclude: true);

    /// <summary>Keep points whose value is within [<paramref name="min"/>, <paramref name="max"/>] (either bound
    /// optional). Missing values have no value to compare, so they're dropped. It filters whatever it's given
    /// — raw readings before a resample, bucket totals after one.</summary>
    public static ITransform WhereValue(double? min = null, double? max = null) => new ValueFilterTransform(min, max);

    /// <summary>
    /// Gives a transform built from a lambda (Filter, Map, Rekey…) a stable name, so results that use it can
    /// be cached. The name is a promise: the same name must always mean the same behaviour.
    /// </summary>
    public static ITransform Named(string name, ITransform inner) => new NamedTransform(name, inner);
}

/// <summary>
/// Aggregates the present values of each group — the <c>by</c> projection of the key, plus the time when
/// <c>keepTime</c>. Groups come out in key order, then time, so the result (and a chart's series order and
/// colours) doesn't depend on the order rows arrived in — cache, pushdown or raw fetch. A group with no
/// present values is missing.
/// </summary>
internal sealed class GroupByTransform(IAggregator aggregator, DimensionId[] by, bool keepTime) : IAggregatingTransform, IDimensionalTransform, ICacheIdentity
{
    public IReadOnlyCollection<DimensionId> Dimensions => by;

    public IAggregator Aggregator => aggregator;

    public ITransform WithAggregator(IAggregator other) => new GroupByTransform(other, by, keepTime);

    private readonly HashSet<DimensionId> _by = [.. by];

    public string CacheIdentity =>
        $"{(keepTime ? "groupby" : "total")}({aggregator.Name};{string.Join(",", by.Select(d => d.Name).Order(StringComparer.Ordinal))})";

    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var groups = new Dictionary<(PointKey Key, long At), List<double>>();
        var order = new List<(PointKey Key, long At)>();
        for (int i = 0; i < input.Count; i++)
        {
            var group = (input.Keys[i].Only(_by), keepTime ? input.AtTicks[i] : PointBlock.NoAt);
            if (!groups.TryGetValue(group, out var values))
            {
                values = [];
                groups[group] = values;
                order.Add(group);
            }
            if ((input.Flags[i] & MeasureFlags.Missing) == 0) values.Add(input.Values[i]);
        }

        order.Sort(static (a, b) => a.Key.CompareTo(b.Key) is var byKey and not 0 ? byKey : a.At.CompareTo(b.At));
        var output = PointBlock.Builder.Like(input);
        foreach (var group in order)
        {
            var values = groups[group];
            var measure = values.Count == 0
                ? Measurement.Missing
                : Measurement.Of(aggregator.Aggregate(CollectionsMarshal.AsSpan(values)));
            output.Add(group.Key, measure, group.At == PointBlock.NoAt ? null : new Instant(group.At));
        }
        return output.Build();
    }
}

internal sealed class KeyFilterTransform(DimensionId dimension, string[] values, bool exclude) : IKeyFilter, ICacheIdentity
{
    private readonly HashSet<string> _values = new(values, StringComparer.Ordinal);

    public IReadOnlyCollection<DimensionId> Dimensions => [dimension];

    public string CacheIdentity =>
        $"{(exclude ? "wherenotin" : "wherein")}({dimension.Name};{string.Join(",", _values.Order(StringComparer.Ordinal).Select(Uri.EscapeDataString))})"; // escaped: "a,b" ≠ "a", "b"

    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = PointBlock.Builder.Like(input);
        for (int i = 0; i < input.Count; i++)
        {
            bool match = input.Keys[i].TryGet(dimension, out var part) && _values.Contains(Text(part));
            if (match != exclude) builder.Add(input.Row(i));
        }
        return builder.Build();
    }

    private static string Text(KeyPart part) => part.Kind == KeyPartKind.Category
        ? part.Text
        : part.Numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ValueFilterTransform(double? min, double? max) : IValueFilter, ICacheIdentity
{
    public string CacheIdentity => FormattableString.Invariant($"wherevalue({min},{max})");

    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = PointBlock.Builder.Like(input);
        for (int i = 0; i < input.Count; i++)
        {
            if ((input.Flags[i] & MeasureFlags.Missing) != 0) continue;
            double v = input.Values[i];
            if ((min is null || v >= min) && (max is null || v <= max)) builder.Add(input.Row(i));
        }
        return builder.Build();
    }
}

internal sealed class NamedTransform(string name, ITransform inner) : ITransform, ICacheIdentity
{
    public string CacheIdentity => $"named({name})";

    public PointBlock Apply(PointBlock input, TransformContext ctx) => inner.Apply(input, ctx);
}

internal sealed class FilterTransform(Func<Point, bool> predicate) : ITransform
{
    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var builder = PointBlock.Builder.Like(input);
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
        var builder = PointBlock.Builder.Like(input);
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
        var builder = PointBlock.Builder.Like(input);
        for (int i = 0; i < input.Count; i++)
        {
            var row = input.Row(i);
            builder.Add(reproject(row.Key), row.Measure, row.At);
        }
        return builder.Build();
    }
}

internal sealed class ReduceTransform(Func<Point, PointKey> keySelector, IAggregator aggregator) : IAggregatingTransform
{
    public IAggregator Aggregator => aggregator;

    public ITransform WithAggregator(IAggregator other) => new ReduceTransform(keySelector, other);

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

        var builder = PointBlock.Builder.Like(input);
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
                builder = PointBlock.Builder.Like(input);
                groups[key] = builder;
                order.Add(key);
            }
            builder.Add(row);
        }

        // The inner transform may change the time axis (a resample makes local buckets), so the output
        // takes its axis from the partitions, not from the input.
        PointBlock.Builder? output = null;
        foreach (var key in order)
        {
            var partition = inner.Apply(groups[key].Build(), ctx);
            output ??= PointBlock.Builder.Like(partition);
            for (int i = 0; i < partition.Count; i++) output.Add(partition.Row(i));
        }
        return output?.Build() ?? inner.Apply(PointBlock.Empty(input.Unit, input.Time), ctx);
    }
}

/// <summary>Public so the engine can recognise a leading resample and push it down to a capable source.</summary>
public sealed class ResampleTransform(IPeriod period, IAggregator aggregator, GapPolicy gap) : IAggregatingTransform, ICacheIdentity
{
    public IPeriod Period { get; } = period;
    public IAggregator Aggregator { get; } = aggregator;
    public GapPolicy Gap { get; } = gap;

    public string CacheIdentity => $"resample({Period.Name},{Aggregator.Name},{Gap})";

    public ITransform WithAggregator(IAggregator other) => new ResampleTransform(Period, other, Gap);

    public PointBlock Apply(PointBlock input, TransformContext ctx) =>
        Resampler.Resample(input, Period, Aggregator, Gap, ctx.Calendar);
}

internal sealed class RollingTransform(TimeSpan window, IAggregator aggregator) : IAggregatingTransform, ICacheIdentity
{
    public IAggregator Aggregator => aggregator;

    public ITransform WithAggregator(IAggregator other) => new RollingTransform(window, other);

    private readonly long _windowTicks = window.Ticks;

    public string CacheIdentity => $"rolling({_windowTicks},{aggregator.Name})";

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

        var output = PointBlock.Builder.Like(input);
        var window = new List<double>();

        foreach (var key in order)
        {
            var indices = byKey[key];
            indices.Sort((a, b) => input.AtTicks[a] != input.AtTicks[b] ? input.AtTicks[a].CompareTo(input.AtTicks[b]) : a.CompareTo(b));

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
