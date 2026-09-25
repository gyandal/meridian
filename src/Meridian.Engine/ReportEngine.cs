using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Engine;

public interface IReportEngine
{
    Task<ChartView> RunAsync(PipelineSpec spec, ProjectionOptions options, CancellationToken ct = default);

    /// <summary>Run several reports; compatible ones share source round trips. Views come back in order.</summary>
    Task<IReadOnlyList<ChartView>> RunManyAsync(IReadOnlyList<PipelineSpec> specs, ProjectionOptions options, CancellationToken ct = default);
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
    IChartProjector projector,
    ViewCache? views = null) : IReportEngine
{
    /// <summary>How one report will be served: its metric, cache scope, pushed-down rollup (if any), and
    /// the transforms still to run in the engine.</summary>
    private sealed record Plan(PipelineSpec Spec, MetricDefinition Metric, CacheScope Scope, SourceRollup? Rollup, ImmutableArray<ITransform> Transforms);

    public async Task<ChartView> RunAsync(PipelineSpec spec, ProjectionOptions options, CancellationToken ct = default)
    {
        var plan = Prepare(spec, options);
        var viewKey = ViewKey(plan, options);
        if (viewKey is not null && views!.TryGet(viewKey, options, out var cached)) return cached;

        var metric = plan.Metric;
        PointLoader loader = plan.Rollup is { } rollup
            ? (missing, token) => ((IRollupPointSource)source).FetchRollupAsync(metric, missing, spec.Timeframe, rollup, token)
            : async (missing, token) =>
                CheckTimeKind(metric, await source.FetchAsync(metric, missing, spec.Timeframe, token).ConfigureAwait(false));

        var raw = await cache.GetOrLoadAsync(plan.Scope, spec.Entities, loader, ct).ConfigureAwait(false);
        return Finish(plan, raw, options, viewKey);
    }

    /// <summary>
    /// Runs several reports, batching those that share tenant, entities, timeframe and pushdown shape (and
    /// differ, typically, only in metric) into one source round trip when the source is an
    /// <see cref="IBatchPointSource"/>. Each report is cached, transformed and projected exactly as
    /// <see cref="RunAsync"/> would, so the views are identical to running them one at a time.
    /// </summary>
    public async Task<IReadOnlyList<ChartView>> RunManyAsync(IReadOnlyList<PipelineSpec> specs, ProjectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        if (source is not IBatchPointSource batch)
        {
            return await Task.WhenAll(specs.Select(s => RunAsync(s, options, ct))).ConfigureAwait(false);
        }

        var plans = specs.Select(s => Prepare(s, options)).ToList();
        var result = new ChartView[plans.Count];
        var viewKeys = plans.Select(p => ViewKey(p, options)).ToList();

        // Finished views already cached need no data at all; batch only the rest.
        var pending = Enumerable.Range(0, plans.Count)
            .Where(i => viewKeys[i] is not { } key || !views!.TryGet(key, options, out result[i]))
            .ToList();
        var groups = pending.GroupBy(i => BatchKey(plans[i])).ToList();
        await Task.WhenAll(groups.Select(async group =>
        {
            var members = group.ToList();
            if (members.Select(i => plans[i].Metric.Id).Distinct().Count() == 1)
            {
                foreach (var i in members) result[i] = await RunAsync(specs[i], options, ct).ConfigureAwait(false);
                return;
            }

            var first = plans[members[0]];
            var metricOf = members.Select(i => plans[i]).GroupBy(p => p.Scope).ToDictionary(g => g.Key, g => g.First().Metric);
            BatchPointLoader loader = async (scopes, missing, token) =>
            {
                var blocks = await batch.FetchManyAsync([.. scopes.Select(s => metricOf[s])], missing,
                    first.Spec.Timeframe, first.Rollup, token).ConfigureAwait(false);
                var byScope = new Dictionary<CacheScope, PointBlock>();
                foreach (var scope in scopes)
                {
                    var metric = metricOf[scope];
                    if (!blocks.TryGetValue(metric.Id, out var block)) continue;
                    byScope[scope] = first.Rollup is null ? CheckTimeKind(metric, block) : block;
                }
                return byScope;
            };

            var raws = await cache.GetOrLoadManyAsync([.. members.Select(i => plans[i].Scope)], first.Spec.Entities, loader, ct).ConfigureAwait(false);
            foreach (var i in members) result[i] = Finish(plans[i], raws[plans[i].Scope], options, viewKeys[i]);
        })).ConfigureAwait(false);

        return result;
    }

    private Plan Prepare(PipelineSpec spec, ProjectionOptions options)
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
        SourceRollup? pushed = null;

        // Pushdown: a leading resample the source can compute is done where the data lives.
        if (transforms.Length > 0 && transforms[0] is ResampleTransform resample && source is IRollupPointSource rollupSource)
        {
            var rollup = new SourceRollup(resample.Period, resample.Aggregator, options.Calendar);
            if (rollupSource.CanRollup(metric, rollup))
            {
                pushed = rollup;
                signature = rollup.Signature;
                // One point per bucket, so re-resampling with Last leaves values untouched while applying
                // the gap policy exactly as the in-engine resample would.
                transforms = transforms.SetItem(0, Transform.Resample(resample.Period, Aggregators.Last, resample.Gap));
            }
        }

        var scope = new CacheScope(
            Tenant: spec.Tenant,
            Metric: metric.Id.Value,
            EntityDimension: spec.Entities[0].Dimension,
            Timeframe: spec.Timeframe,
            Signature: signature);
        return new Plan(spec, metric, scope, pushed, transforms);
    }

    /// <summary>Reports batch together when one source call can serve them all: same tenant, entity list,
    /// timeframe and pushdown shape.</summary>
    private static string BatchKey(Plan p) =>
        $"{p.Spec.Tenant}|{p.Scope.EntityDimension.Name}|{p.Spec.Timeframe.Start.UtcTicks}|{p.Spec.Timeframe.End.UtcTicks}|" +
        $"{p.Scope.Signature}|{string.Join(',', p.Spec.Entities.Select(e => e.Id))}";

    /// <summary>
    /// The finished-view cache key, or null when the request can't be cached: no view cache, or an input
    /// without a stable identity (e.g. an unnamed lambda transform). It includes the current data version of
    /// every entity, computed BEFORE fetching, so a view built across a concurrent write is keyed to the
    /// old version and never served after it.
    /// </summary>
    private string? ViewKey(Plan plan, ProjectionOptions options)
    {
        if (views is null) return null;

        var transforms = new List<string>(plan.Transforms.Length);
        foreach (var t in plan.Transforms)
        {
            if (t is not ICacheIdentity id) return null;
            transforms.Add(id.CacheIdentity);
        }
        var view = ResolvedView(plan);
        if (view.Status is { } status && status is not ICacheIdentity) return null;
        if (options.Calendar.Season is not ICacheIdentity season) return null;

        var s = plan.Scope;
        var axis = view.XAxis is CategoryAxisSource c ? "cat:" + c.Dimension.Name : "time";
        return string.Join('|',
            s.Tenant, s.Metric, s.EntityDimension.Name, s.Timeframe.Start.UtcTicks, s.Timeframe.End.UtcTicks, s.Signature,
            cache.VersionStamp(s, plan.Spec.Entities),
            string.Join(';', transforms),
            view.Kind, axis, view.SeriesBy?.Name, view.ValueAxisTitle, view.ValueUnit?.Symbol, (view.Status as ICacheIdentity)?.CacheIdentity,
            options.Calendar.Zone.Id, options.Calendar.WeekStart, season.CacheIdentity);
    }

    private static ViewSpec ResolvedView(Plan plan) =>
        // Default the value unit from the catalog if the view didn't pin one.
        plan.Spec.View.ValueUnit is null ? plan.Spec.View with { ValueUnit = plan.Metric.Unit } : plan.Spec.View;

    private ChartView Finish(Plan plan, PointBlock raw, ProjectionOptions options, string? viewKey)
    {
        var transformContext = new TransformContext(options.Calendar);
        var current = raw;
        foreach (var transform in plan.Transforms)
        {
            current = transform.Apply(current, transformContext);
        }

        var view = projector.Project(current, ResolvedView(plan), options);
        if (viewKey is not null)
        {
            views!.Set(viewKey, options, view, plan.Spec.Entities.Select(e => ViewCache.Tag(plan.Spec.Tenant, plan.Metric.Id.Value, e)));
        }
        return view;
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
