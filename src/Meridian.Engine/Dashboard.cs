using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// What a dashboard is looking at: the tenant, the entities (a squad, or one player when focused), the
/// timeframe. Charts are defined relative to it, so focusing on one player is the same dashboard run with a
/// different context — and because data is cached per entity, it needs no new queries.
/// </summary>
public sealed record DashboardContext(string Tenant, IReadOnlyList<EntityRef> Entities, DateInterval Timeframe)
{
    /// <summary>The same context narrowed to <paramref name="entities"/>.</summary>
    public DashboardContext Focus(params EntityRef[] entities) => this with { Entities = entities };
}

/// <summary>One series of a dashboard chart: a metric, what to do with it, how to draw it.</summary>
public sealed record DashboardSeries(
    MetricId Metric,
    ImmutableArray<ITransform> Transforms,
    ViewSpec View,
    ImmutableArray<DimensionId> Dimensions = default,
    string? Name = null,
    ValueAxis Axis = ValueAxis.Primary);

public sealed record DashboardChart(string Title, IReadOnlyList<DashboardSeries> Series);

/// <summary>A set of charts over one shared <see cref="DashboardContext"/>.</summary>
public sealed record Dashboard(string Title, IReadOnlyList<DashboardChart> Charts)
{
    /// <summary>The charts as runnable specs for <paramref name="context"/>.</summary>
    public IReadOnlyList<ChartSpec> For(DashboardContext context) => [.. Charts.Select(chart => new ChartSpec(
        [.. chart.Series.Select(s => new SeriesSpec(
            PipelineSpec.Create(context.Tenant, s.Metric, context.Entities, context.Timeframe, s.View, [.. s.Transforms])
                .WithDimensions([.. s.Dimensions.IsDefault ? [] : s.Dimensions]),
            s.Name, s.Axis))]))];
}

public sealed record DashboardView(string Title, IReadOnlyList<DashboardChartView> Charts);

public sealed record DashboardChartView(string Title, ChartView View);
