using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;

namespace Meridian.Engine;

/// <summary>
/// Optional capability: fetch several metrics for the same entities and timeframe in one round trip.
/// A report is single-metric, so without this a dashboard of N metrics (or a query over N metrics) costs N
/// source queries; with it, <see cref="ReportEngine.RunManyAsync"/> batches compatible reports into one.
///
/// When <c>rollup</c> is null the result is raw rows, as <see cref="IPointSource.FetchAsync"/> would return
/// per metric; otherwise it is pushed-down buckets as <see cref="IRollupPointSource.FetchRollupAsync"/>
/// would, and the engine only passes a rollup the source accepted (<c>CanRollup</c>) for every metric.
/// Every requested metric should have an entry (an empty block if it has no data).
/// </summary>
public interface IBatchPointSource : IPointSource
{
    Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchManyAsync(
        IReadOnlyList<MetricDefinition> metrics,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup? rollup,
        CancellationToken ct = default);
}
