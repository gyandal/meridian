using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Engine;

public interface IReportEngine
{
    Task<ChartView> RunAsync(PipelineSpec spec, ProjectionOptions options, CancellationToken ct = default);
}

/// <summary>
/// The keystone: resolve the metric from the catalog → cache-aware per-entity fetch → apply the
/// transform pipeline → project to a ChartView. Six libraries become "give a spec, get a chart".
/// The cache holds the RAW per-entity source data (compact PointBlock); transforms and projection are
/// cheap and run per request — cache metric values, not chart models.
/// </summary>
public sealed class ReportEngine(
    IMetricCatalog catalog,
    IPointSource source,
    PointCache cache,
    IChartProjector projector) : IReportEngine
{
    public async Task<ChartView> RunAsync(PipelineSpec spec, ProjectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Entities.Count == 0)
        {
            throw new ArgumentException("A report needs at least one entity.", nameof(spec));
        }
        if (!catalog.TryResolve(spec.Metric, out var metric))
        {
            throw new InvalidOperationException($"Unknown metric '{spec.Metric}'. Is it in the catalog?");
        }

        var transforms = spec.Transforms;
        var signature = "source"; // caches the raw fetch; transforms run after the merge
        PointLoader loader = async (missing, token) =>
            CheckTimeKind(metric, await source.FetchAsync(metric, missing, spec.Timeframe, token).ConfigureAwait(false));

        // Pushdown: a leading resample the source can compute is done where the data lives.
        if (transforms.Length > 0 && transforms[0] is ResampleTransform resample && source is IRollupPointSource rollupSource)
        {
            var rollup = new SourceRollup(resample.Period, resample.Aggregator, options.Calendar);
            if (rollupSource.CanRollup(metric, rollup))
            {
                signature = rollup.Signature;
                loader = (missing, token) => rollupSource.FetchRollupAsync(metric, missing, spec.Timeframe, rollup, token);
                // One point per bucket, so re-resampling with Last leaves values untouched while applying
                // the gap policy exactly as the in-engine resample would.
                transforms = transforms.SetItem(0, Transform.Resample(resample.Period, Aggregators.Last, resample.Gap));
            }
        }

        var entityDimension = spec.Entities[0].Dimension;
        var scope = new CacheScope(
            Tenant: spec.Tenant,
            Metric: metric.Id.Value,
            EntityDimension: entityDimension,
            Timeframe: spec.Timeframe,
            Signature: signature);

        var raw = await cache.GetOrLoadAsync(scope, spec.Entities, loader, ct).ConfigureAwait(false);

        var transformContext = new TransformContext(options.Calendar);
        var current = raw;
        foreach (var transform in transforms)
        {
            current = transform.Apply(current, transformContext);
        }

        // Default the value unit from the catalog if the view didn't pin one.
        var view = spec.View.ValueUnit is null ? spec.View with { ValueUnit = metric.Unit } : spec.View;
        return projector.Project(current, view, options);
    }

    private static PointBlock CheckTimeKind(MetricDefinition metric, PointBlock block)
    {
        if (block.Count > 0 && block.Time.Kind != metric.TimeKind)
        {
            throw new InvalidOperationException(
                $"Metric '{metric.Id}' is declared as {metric.TimeKind} time but its source returned {block.Time.Kind} " +
                "timestamps. Declare the metric's TimeKind to match, or configure the source's StoredTime (docs/TIME.md).");
        }
        return block;
    }
}
