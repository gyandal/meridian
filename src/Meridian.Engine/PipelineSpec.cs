using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Semantics;
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
    public static PipelineSpec Create(
        string tenant,
        MetricId metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        ViewSpec view,
        params ITransform[] transforms) =>
        new(tenant, metric, entities, timeframe, [.. transforms], view);
}
