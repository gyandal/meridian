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
///
/// Every request is planned as one or more <em>inputs</em> — a stored metric's fetch plus the transforms to
/// run on it. A plain report has one input; a derived metric (e.g. goals per 90) has one per formula input,
/// combined after aggregation. All inputs of a batch are loaded together: identical fetches once, compatible
/// ones in a single <see cref="IBatchPointSource"/> round trip.
/// </summary>
public sealed class ReportEngine(
    IMetricCatalog catalog,
    IPointSource source,
    PointCache cache,
    IChartProjector projector,
    ViewCache? views = null) : IReportEngine
{
    /// <summary>One stored metric's fetch: its cache scope, pushed-down rollup (if any), and the transforms
    /// still to run in the engine.</summary>
    private sealed record Plan(PipelineSpec Spec, MetricDefinition Metric, CacheScope Scope, SourceRollup? Rollup,
        ImmutableArray<ITransform> Transforms, HashSet<DimensionId> Keep)
    {
        /// <summary>Identical fetches (same scope and entity list) are loaded once per batch.</summary>
        public string LoadKey => $"{BatchKey}|{Metric.Id}";

        /// <summary>Fetches batch together when one source call can serve them all: same tenant, entity list,
        /// timeframe and pushdown shape.</summary>
        public string BatchKey =>
            $"{Spec.Tenant}|{Scope.EntityDimension.Name}|{Spec.Timeframe.Start.UtcTicks}|{Spec.Timeframe.End.UtcTicks}|" +
            $"{Scope.Signature}|{string.Join(',', Spec.Entities.Select(e => e.Id))}";
    }

    /// <summary>A report: its inputs, how they combine (null for a stored metric), and transforms that run
    /// on the combined series.</summary>
    private sealed record Request(PipelineSpec Spec, MetricDefinition Metric, IReadOnlyList<Plan> Inputs,
        RatioFormula? Ratio, ImmutableArray<ITransform> After);

    public async Task<ChartView> RunAsync(PipelineSpec spec, ProjectionOptions options, CancellationToken ct = default) =>
        (await RunManyAsync([spec], options, ct).ConfigureAwait(false))[0];

    /// <summary>
    /// Runs several reports, loading their inputs together: identical fetches once, and fetches that share
    /// tenant, entities, timeframe and pushdown shape in one source round trip when the source is an
    /// <see cref="IBatchPointSource"/>. Each report is cached, transformed and projected exactly as it would
    /// be alone, so the views are identical to running them one at a time.
    /// </summary>
    public async Task<IReadOnlyList<ChartView>> RunManyAsync(IReadOnlyList<PipelineSpec> specs, ProjectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var requests = specs.Select(s => Prepare(s, options)).ToList();
        var viewKeys = requests.Select(r => ViewKey(r, options)).ToList();
        var result = new ChartView[requests.Count];

        // Finished views already cached need no data at all.
        var pending = Enumerable.Range(0, requests.Count)
            .Where(i => viewKeys[i] is not { } key || !views!.TryGet(key, options, out result[i]))
            .ToList();

        var raws = await LoadAsync([.. pending.SelectMany(i => requests[i].Inputs)], ct).ConfigureAwait(false);
        foreach (var i in pending)
        {
            var request = requests[i];
            var view = projector.Project(Compute(request, raws, options), ResolvedView(request), options);
            if (viewKeys[i] is { } key)
            {
                views!.Set(key, options, view, request.Inputs.SelectMany(p =>
                    p.Spec.Entities.Select(e => ViewCache.Tag(p.Spec.Tenant, p.Metric.Id.Value, e))));
            }
            result[i] = view;
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------ planning

    private Request Prepare(PipelineSpec spec, ProjectionOptions options)
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
        ValidateDimensions(spec, metric);

        if (metric.Formula is null)
        {
            return new Request(spec, metric, [PlanFor(spec, metric, options)], Ratio: null, After: []);
        }
        if (metric.Formula is not RatioFormula ratio)
        {
            throw new NotSupportedException($"Metric '{metric.Id}' uses an unsupported formula: {metric.Formula}.");
        }
        if (!Aggregators.TryResolve(ratio.Aggregation, out var inputAggregator))
        {
            throw new InvalidOperationException($"Derived metric '{metric.Id}' aggregates its inputs with unknown aggregator '{ratio.Aggregation}'.");
        }

        // Each input is aggregated — every aggregating step, with the formula's aggregator — up to and
        // including the report's last aggregating transform; the formula combines the totals there, and any
        // later transforms run on the result. That's what makes "goals per 90 by venue per month" a ratio of
        // monthly totals per venue, rather than an average of per-match ratios.
        int last = -1;
        for (int t = 0; t < spec.Transforms.Length; t++)
        {
            if (spec.Transforms[t] is IAggregatingTransform) last = t;
        }
        var before = spec.Transforms.Take(last + 1)
            .Select(t => t is IAggregatingTransform a ? a.WithAggregator(inputAggregator) : t)
            .ToImmutableArray();
        var after = spec.Transforms.Skip(last + 1).ToImmutableArray();

        var inputs = ratio.Inputs.Select(id =>
        {
            var input = catalog.TryResolve(id, out var m) ? m
                : throw new InvalidOperationException($"Derived metric '{metric.Id}' uses unknown metric '{id}'.");
            var inputSpec = spec with { Metric = id, Transforms = before };
            ValidateDimensions(inputSpec, input);
            return PlanFor(inputSpec, input, options);
        }).ToList();
        return new Request(spec, metric, inputs, ratio, after);
    }

    private static void ValidateDimensions(PipelineSpec spec, MetricDefinition metric)
    {
        var entityDimension = spec.Entities[0].Dimension;
        foreach (var dimension in Dimensions(spec))
        {
            if (dimension == entityDimension || !metric.ValidDimensions.Contains(dimension))
            {
                throw new ArgumentException(
                    $"Metric '{metric.Id}' has no dimension '{dimension}' to keep. Its dimensions: {string.Join(", ", metric.ValidDimensions)}.", nameof(spec));
            }
        }
    }

    private static ImmutableArray<DimensionId> Dimensions(PipelineSpec spec) =>
        spec.Dimensions.IsDefault ? [] : [.. spec.Dimensions.Distinct().Order()];

    private Plan PlanFor(PipelineSpec spec, MetricDefinition metric, ProjectionOptions options)
    {
        var entityDimension = spec.Entities[0].Dimension;
        var dimensions = Dimensions(spec);
        var keep = new HashSet<DimensionId>(dimensions) { entityDimension };

        var transforms = spec.Transforms;
        var signature = "source"; // raw slices carry every dimension the source knows, so all reports share them
        SourceRollup? pushed = null;

        // Pushdown: a leading resample the source can compute is done where the data lives.
        if (transforms.Length > 0 && transforms[0] is ResampleTransform resample && source is IRollupPointSource rollupSource)
        {
            var rollup = new SourceRollup(resample.Period, resample.Aggregator, options.Calendar, dimensions);
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
            EntityDimension: entityDimension,
            Timeframe: spec.Timeframe,
            Signature: signature);
        return new Plan(spec, metric, scope, pushed, transforms, keep);
    }

    // ------------------------------------------------------------------------------------------ loading

    /// <summary>Loads every input's raw slices: identical fetches once; compatible ones in one batch call.</summary>
    private async Task<Dictionary<string, PointBlock>> LoadAsync(IReadOnlyList<Plan> inputs, CancellationToken ct)
    {
        var unique = inputs.DistinctBy(p => p.LoadKey).ToList();
        var groups = unique.GroupBy(p => p.BatchKey).ToList();
        var loaded = await Task.WhenAll(groups.Select(async group =>
        {
            var plans = group.ToList();
            if (source is IBatchPointSource batch && plans.Count > 1)
            {
                var first = plans[0];
                var metricOf = plans.ToDictionary(p => p.Scope, p => p.Metric);
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
                var raws = await cache.GetOrLoadManyAsync([.. plans.Select(p => p.Scope)], first.Spec.Entities, loader, ct).ConfigureAwait(false);
                return plans.Select(p => (p.LoadKey, Block: raws[p.Scope])).ToList();
            }

            var single = new List<(string LoadKey, PointBlock Block)>();
            foreach (var plan in plans)
            {
                single.Add((plan.LoadKey, await cache.GetOrLoadAsync(plan.Scope, plan.Spec.Entities, SingleLoader(plan), ct).ConfigureAwait(false)));
            }
            return single;
        })).ConfigureAwait(false);

        return loaded.SelectMany(x => x).ToDictionary(x => x.LoadKey, x => x.Block);
    }

    private PointLoader SingleLoader(Plan plan)
    {
        var metric = plan.Metric;
        var timeframe = plan.Spec.Timeframe;
        return plan.Rollup is { } rollup
            ? (missing, token) => ((IRollupPointSource)source).FetchRollupAsync(metric, missing, timeframe, rollup, token)
            : async (missing, token) =>
                CheckTimeKind(metric, await source.FetchAsync(metric, missing, timeframe, token).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------------------------------ computing

    private static PointBlock Compute(Request request, Dictionary<string, PointBlock> raws, ProjectionOptions options)
    {
        var context = new TransformContext(options.Calendar);
        var blocks = request.Inputs.Select(p => Apply(p.Transforms, KeepOnly(raws[p.LoadKey], p.Keep), context)).ToList();
        var combined = request.Ratio is { } ratio ? Ratio(blocks[0], blocks[1], ratio.Scale, request.Metric.Unit) : blocks[0];
        return Apply(request.After, combined, context);
    }

    private static PointBlock Apply(ImmutableArray<ITransform> transforms, PointBlock block, TransformContext context)
    {
        foreach (var transform in transforms) block = transform.Apply(block, context);
        return block;
    }

    /// <summary>
    /// numerator ÷ denominator × scale for each (key, time) the denominator has. A missing numerator is 0 —
    /// no goal rows means no goals — while a missing or zero denominator gives no value at all.
    /// </summary>
    private static PointBlock Ratio(PointBlock numerator, PointBlock denominator, double scale, Unit unit)
    {
        if (numerator.Count > 0 && denominator.Count > 0 && numerator.Time.Kind != denominator.Time.Kind)
        {
            throw new InvalidOperationException("A ratio's inputs must have the same kind of time.");
        }
        var totals = new Dictionary<(PointKey, long), double>();
        for (int i = 0; i < numerator.Count; i++)
        {
            if ((numerator.Flags[i] & MeasureFlags.Missing) == 0) totals[(numerator.Keys[i], numerator.AtTicks[i])] = numerator.Values[i];
        }

        var output = new PointBlock.Builder(unit, denominator.Count > 0 ? denominator.Time : numerator.Time);
        for (int i = 0; i < denominator.Count; i++)
        {
            double d = denominator.Values[i];
            if ((denominator.Flags[i] & MeasureFlags.Missing) != 0 || d == 0) continue;
            double n = totals.TryGetValue((denominator.Keys[i], denominator.AtTicks[i]), out var v) ? v : 0;
            long at = denominator.AtTicks[i];
            output.Add(denominator.Keys[i], Measurement.Of(n / d * scale), at == PointBlock.NoAt ? null : new Instant(at));
        }
        return output.Build();
    }

    /// <summary>Fold away dimensions the report didn't declare (sources return all they know).</summary>
    private static PointBlock KeepOnly(PointBlock block, HashSet<DimensionId> keep)
    {
        var keys = block.Keys;
        int i = 0;
        while (i < keys.Length && keys[i].Only(keep).Count == keys[i].Count) i++;
        if (i == keys.Length) return block; // nothing to fold: the common case costs one scan, no copy

        var builder = PointBlock.Builder.Like(block);
        for (int j = 0; j < block.Count; j++)
        {
            var row = block.Row(j);
            builder.Add(row.Key.Only(keep), row.Measure, row.At);
        }
        return builder.Build();
    }

    // ------------------------------------------------------------------------------------------ views

    /// <summary>
    /// The finished-view cache key, or null when the request can't be cached: no view cache, or an input
    /// without a stable identity (e.g. an unnamed lambda transform). It includes the current data version of
    /// every entity of every input, computed BEFORE fetching, so a view built across a concurrent write is
    /// keyed to the old version and never served after it — and a derived view goes stale with its inputs.
    /// </summary>
    private string? ViewKey(Request request, ProjectionOptions options)
    {
        if (views is null) return null;

        var parts = new List<object?> { request.Metric.Id, request.Metric.Formula?.ToString() };
        foreach (var plan in request.Inputs)
        {
            var s = plan.Scope;
            parts.Add(string.Join('|',
                s.Tenant, s.Metric, s.EntityDimension.Name, s.Timeframe.Start.UtcTicks, s.Timeframe.End.UtcTicks, s.Signature,
                cache.VersionStamp(s, plan.Spec.Entities),
                string.Join(',', plan.Keep.Select(d => d.Name).Order(StringComparer.Ordinal))));
            if (Identities(plan.Transforms) is not { } transforms) return null;
            parts.Add(transforms);
        }
        if (Identities(request.After) is not { } after) return null;
        parts.Add(after);

        var view = ResolvedView(request);
        if (view.Status is { } status && status is not ICacheIdentity) return null;
        if (options.Calendar.Season is not ICacheIdentity season) return null;
        var axis = view.XAxis is CategoryAxisSource c ? "cat:" + c.Dimension.Name : "time";
        parts.AddRange([view.Kind, axis, view.SeriesBy?.Name, view.ValueAxisTitle, view.ValueUnit?.Symbol, (view.Status as ICacheIdentity)?.CacheIdentity,
            options.Calendar.Zone.Id, options.Calendar.WeekStart, season.CacheIdentity]);
        return string.Join('‖', parts);
    }

    private static string? Identities(ImmutableArray<ITransform> transforms)
    {
        var ids = new List<string>(transforms.Length);
        foreach (var t in transforms)
        {
            if (t is not ICacheIdentity id) return null;
            ids.Add(id.CacheIdentity);
        }
        return string.Join(';', ids);
    }

    private static ViewSpec ResolvedView(Request request) =>
        // Default the value unit from the catalog if the view didn't pin one.
        request.Spec.View.ValueUnit is null ? request.Spec.View with { ValueUnit = request.Metric.Unit } : request.Spec.View;

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
