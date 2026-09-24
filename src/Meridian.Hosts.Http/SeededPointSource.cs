using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;

namespace Meridian.Hosts.Http;

/// <summary>A metric-aware in-memory source over the seed. Counts entity-slices fetched so the dashboard
/// can show the cache actually sparing the source on repeat runs.</summary>
public sealed class SeededPointSource(Dictionary<string, List<Point>> data, DimensionId entityDimension) : IPointSource
{
    private int _fetched;

    public int EntitySlicesFetched => Volatile.Read(ref _fetched);
    public void ResetStats() => Interlocked.Exchange(ref _fetched, 0);

    public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default)
    {
        Interlocked.Add(ref _fetched, entities.Count);
        return Task.FromResult(Raw(metric.Id.Value, [.. entities.Select(e => e.Id)], timeframe, metric.Unit));
    }

    /// <summary>Direct, uncached read — used by the showcase to compose features the single-spec engine
    /// path doesn't cover (binary joins across two timeframes, etc.).</summary>
    public PointBlock Raw(string metric, IReadOnlyList<long> entityIds, DateInterval timeframe, Unit unit)
    {
        var wanted = entityIds.ToHashSet();
        var builder = new PointBlock.Builder(unit);
        if (data.TryGetValue(metric, out var points))
        {
            foreach (var p in points)
            {
                if (!p.Key.TryGet(entityDimension, out var part)) continue;
                if (!wanted.Contains(part.Numeric)) continue;
                if (p.At is { } at && (at.UtcTicks < timeframe.Start.UtcTicks || at.UtcTicks >= timeframe.End.UtcTicks)) continue;
                builder.Add(p);
            }
        }
        return builder.Build();
    }
}
