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

        var entityDimension = spec.Entities[0].Dimension;
        var scope = new CacheScope(
            Tenant: spec.Tenant,
            Metric: metric.Id.Value,
            EntityDimension: entityDimension,
            Timeframe: spec.Timeframe,
            Signature: "source"); // caches the raw fetch; transforms run after the merge

        var raw = await cache.GetOrLoadAsync(
            scope,
            spec.Entities,
            (missing, token) => source.FetchAsync(metric, missing, spec.Timeframe, token),
            ct).ConfigureAwait(false);

        var transformContext = new TransformContext(options.Calendar);
        var current = raw;
        foreach (var transform in spec.Transforms)
        {
            current = transform.Apply(current, transformContext);
        }

        // Default the value unit from the catalog if the view didn't pin one.
        var view = spec.View.ValueUnit is null ? spec.View with { ValueUnit = metric.Unit } : spec.View;
        return projector.Project(current, view, options);
    }
}
