using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;

namespace Meridian.Engine;

/// <summary>
/// A trivial source backed by an in-memory point list — for tests and samples. Records which entities
/// it was asked for so tests can assert the cache actually spared the source (the whole point of §7).
/// </summary>
public sealed class InMemoryPointSource(IEnumerable<Point> points, DimensionId entityDimension, Unit unit) : IPointSource
{
    private readonly List<Point> _points = [.. points];

    public int FetchCount { get; private set; }
    public List<IReadOnlyList<EntityRef>> Fetches { get; } = [];

    public Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default)
    {
        FetchCount++;
        Fetches.Add(entities);

        var wanted = entities.Select(e => e.Id).ToHashSet();
        var builder = new PointBlock.Builder(unit);
        foreach (var p in _points)
        {
            if (!p.Key.TryGet(entityDimension, out var part)) continue;
            if (!wanted.Contains(part.Numeric)) continue;
            if (p.At is { } at && (at.UtcTicks < timeframe.Start.UtcTicks || at.UtcTicks >= timeframe.End.UtcTicks)) continue;
            builder.Add(p);
        }
        return Task.FromResult(builder.Build());
    }
}
