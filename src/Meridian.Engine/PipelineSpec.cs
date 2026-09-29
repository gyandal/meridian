using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// One declarative description of a report: what to fetch (metric + entities + timeframe), how to
/// transform it, and how to view it. A report is a spec, not a bespoke provider — new report = new
/// spec.
/// </summary>
public sealed record PipelineSpec(
    string Tenant,
    MetricId Metric,
    IReadOnlyList<EntityRef> Entities,
    DateInterval Timeframe,
    ImmutableArray<ITransform> Transforms,
    ViewSpec View)
{
    /// <summary>
    /// Dimensions beyond the entity that this report keeps — e.g. venue, so it can group by venue. Each must
    /// be one of the metric's <c>ValidDimensions</c>. Sources return every dimension they know; the engine
    /// folds away the ones a report doesn't declare, so reports grouping the same metric different ways
    /// share one cached fetch.
    /// </summary>
    public ImmutableArray<DimensionId> Dimensions { get; init; } = [];

    /// <summary>This report, keeping <paramref name="dimensions"/> as well as the entity.</summary>
    public PipelineSpec WithDimensions(params DimensionId[] dimensions) => this with { Dimensions = [.. dimensions] };

    /// <summary>Set by <see cref="Earlier"/> or <see cref="ChangeFrom"/>: what this report is compared with.</summary>
    public Comparison? Comparison { get; init; }

    /// <summary>
    /// This report as it was at <paramref name="baseline"/> — last season's goals — drawn on this report's time
    /// axis, so it overlays the current one in a chart: <c>ChartSpec.Of(new(goals), new(goals.Earlier(SeasonsBack(1)), "Last season"))</c>.
    /// </summary>
    public PipelineSpec Earlier(Baseline baseline) => this with { Comparison = new(baseline, ComparisonOutput.Baseline) };

    /// <summary>The change in this report since <paramref name="baseline"/>, per key and bucket: a difference, or a
    /// percentage change. Only buckets with a value on both sides are compared; zero-fill to count empty ones as 0.</summary>
    public PipelineSpec ChangeFrom(Baseline baseline, ComparisonOutput output = ComparisonOutput.PercentChange) =>
        output == ComparisonOutput.Baseline
            ? throw new ArgumentException("For the baseline itself, use Earlier(baseline).", nameof(output))
            : this with { Comparison = new(baseline, output) };

    public static PipelineSpec Create(
        string tenant,
        MetricId metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        ViewSpec view,
        params ITransform[] transforms) =>
        new(tenant, metric, entities, timeframe, [.. transforms], view);
}

public enum ComparisonOutput
{
    /// <summary>The baseline's values, aligned onto the report's time axis.</summary>
    Baseline,

    /// <summary>current − baseline.</summary>
    Difference,

    /// <summary>(current − baseline) ÷ baseline × 100. A zero baseline has no percentage.</summary>
    PercentChange,
}

/// <summary>A report compared with itself at <see cref="Baseline"/>.</summary>
public sealed record Comparison(Baseline Baseline, ComparisonOutput Output);
