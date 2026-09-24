using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Time;

namespace Meridian.Engine;

/// <summary>A bucket-and-aggregate the engine would otherwise do itself, offered to the source.</summary>
public sealed record SourceRollup(IPeriod Period, IAggregator Aggregator, CalendarContext Calendar)
{
    /// <summary>Distinguishes rolled-up cache slices from raw ones (and from other rollups).</summary>
    public string Signature =>
        $"rollup:{Period.Name}:{Aggregator.Name}:{Calendar.Zone.Id}:{Calendar.WeekStart}";
}

/// <summary>
/// Optional capability: a source that can aggregate into time buckets where the data lives (SQL
/// <c>GROUP BY date_trunc(...)</c>), so the engine moves one point per bucket instead of every raw
/// row. This is what makes very large tables practical — the raw rows never leave the store.
///
/// Contract for <see cref="FetchRollupAsync"/>: one point per (entity, bucket) that has at least one
/// row in the timeframe, positioned at the bucket start (UTC), keyed like <see cref="IPointSource.FetchAsync"/>.
/// A bucket whose rows are all missing is returned as <see cref="Measurement.Missing"/>, not dropped —
/// gap policies depend on it. Results must equal the in-engine <c>Resample</c> for the same inputs.
/// </summary>
public interface IRollupPointSource : IPointSource
{
    /// <summary>True if this source can compute <paramref name="rollup"/> exactly.</summary>
    bool CanRollup(MetricDefinition metric, SourceRollup rollup);

    Task<PointBlock> FetchRollupAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup rollup,
        CancellationToken ct = default);
}
