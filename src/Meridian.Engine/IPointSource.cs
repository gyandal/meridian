using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;

namespace Meridian.Engine;

/// <summary>
/// The data-access seam — the ~20% per-client deliverable. Implementations pull raw points for a
/// metric from wherever the data lives (SQL, warehouse, a public API), for exactly the entities and
/// timeframe asked. The engine calls this only for cache misses, so it never sees a cache key.
/// </summary>
public interface IPointSource
{
    Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default);
}
