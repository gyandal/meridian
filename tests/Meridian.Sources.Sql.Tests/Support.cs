using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Views;

namespace Meridian.Sources.Sql.Tests;

/// <summary>Counts how a source was asked for data, so a test can tell pushdown from a raw fetch.</summary>
internal sealed class CountingSource(IRollupPointSource inner) : IRollupPointSource, IBatchPointSource
{
    private int _raws, _rollups, _batches;

    public int Raws => _raws;
    public int Rollups => _rollups;
    public int Batches => _batches;

    public Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchManyAsync(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<EntityRef> entities, DateInterval timeframe, SourceRollup? rollup, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _batches);
        return ((IBatchPointSource)inner).FetchManyAsync(metrics, entities, timeframe, rollup, ct);
    }

    public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _raws);
        return inner.FetchAsync(metric, entities, timeframe, ct);
    }

    public bool CanRollup(MetricDefinition metric, SourceRollup rollup) => inner.CanRollup(metric, rollup);

    public Task<PointBlock> FetchRollupAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, SourceRollup rollup, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _rollups);
        return inner.FetchRollupAsync(metric, entities, timeframe, rollup, ct);
    }
}

/// <summary>The same source with pushdown hidden, so every report is computed in the engine from raw rows.</summary>
internal sealed class RawOnly(IPointSource inner) : IPointSource
{
    public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default) =>
        inner.FetchAsync(metric, entities, timeframe, ct);
}

internal static class Same
{
    /// <summary>Two views are the same chart: axis, series, marks, and values to 1e-9 relative.</summary>
    public static void View(ChartView expected, ChartView actual, string context)
    {
        Assert.Equal(expected.Axes[0], actual.Axes[0]);
        Assert.Equal(expected.Series.Count, actual.Series.Count);
        for (int s = 0; s < expected.Series.Count; s++)
        {
            var e = expected.Series[s];
            var a = actual.Series[s];
            Assert.Equal(e.Name, a.Name);
            Assert.True(e.Marks.Count == a.Marks.Count, $"{context}, {e.Name}: {e.Marks.Count} marks vs {a.Marks.Count}");
            for (int i = 0; i < e.Marks.Count; i++)
            {
                Assert.Equal(e.Marks[i].At, a.Marks[i].At);
                Assert.Equal(e.Marks[i].Label, a.Marks[i].Label);
                if (e.Marks[i].Value is not { } ev)
                {
                    Assert.True(a.Marks[i].Value is null, $"{context}, {e.Name} @ {e.Marks[i].Label}: missing vs {a.Marks[i].Value}");
                    continue;
                }
                var av = a.Marks[i].Value;
                Assert.True(av is { } v && Math.Abs(ev - v) <= 1e-9 * Math.Max(1, Math.Abs(ev)), $"{context}, {e.Name} @ {e.Marks[i].Label}: {ev} vs {av}");
            }
        }
    }

    /// <summary>Two raw blocks hold the same points (in any order): keys, times, values and missing flags.</summary>
    public static void Points(PointBlock expected, PointBlock actual, string context)
    {
        static List<(string Key, long At, double? Value)> Rows(PointBlock b) =>
            [.. Enumerable.Range(0, b.Count)
                .Select(i => (b.Keys[i].ToString(), b.AtTicks[i], (b.Flags[i] & MeasureFlags.Missing) != 0 ? (double?)null : b.Values[i]))
                .OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2).ThenBy(r => r.Item3)];

        Assert.Equal(expected.Time, actual.Time);
        var e = Rows(expected);
        var a = Rows(actual);
        Assert.True(e.Count == a.Count, $"{context}: {e.Count} points vs {a.Count}");
        for (int i = 0; i < e.Count; i++)
        {
            Assert.True(e[i].Key == a[i].Key && e[i].At == a[i].At, $"{context}: point {i} is {e[i].Key} @ {e[i].At} vs {a[i].Key} @ {a[i].At}");
            Assert.True(e[i].Value is null ? a[i].Value is null : a[i].Value is { } v && Math.Abs(e[i].Value!.Value - v) <= 1e-9 * Math.Max(1, Math.Abs(v)),
                $"{context}: {e[i].Key} @ {new DateTime(e[i].At):u}: {e[i].Value} vs {a[i].Value}");
        }
    }
}
