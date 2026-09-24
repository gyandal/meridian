using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Meridian.Views.Json;

namespace Meridian.Hosts.Mcp;

/// <summary>
/// The agent-facing tool surface: exactly two tools over the engine — <c>describe</c> (so an agent
/// learns what it can ask) and <c>query</c> (a bounded, typed request → a finished ChartView). Because
/// a query is a typed spec and not free SQL, every call is safe, auditable, and cacheable. This class is
/// transport-agnostic; a real MCP server (stdio/SSE) binds these two methods as tools — the protocol
/// wrapper is thin and deliberately out of the spike.
/// </summary>
public sealed class MeridianTools(IMetricCatalog catalog, ReportEngine engine, DimensionId entityDimension, string tenant)
{
    /// <summary>Tool: <c>describe</c>. The catalog an agent needs to form a valid query.</summary>
    public CatalogDescription Describe() => new(
        Metrics: [.. catalog.Metrics.Select(m => new MetricDescription(
            m.Id.Value, m.Name, m.Unit.Symbol, m.NativeGrain.ToString(),
            [.. m.ValidDimensions.Select(d => d.Name)]))],
        Transforms: ["raw", "weekly-mean", "weekly-sum", "rolling7-mean", "rolling28-mean"],
        ChartKinds: ["line", "column", "area"]);

    /// <summary>Tool: <c>query</c>. Runs a bounded request and returns the ChartView as JSON.</summary>
    public async Task<string> QueryAsync(McpQuery query, CancellationToken ct = default)
    {
        var spec = ToSpec(query);
        var view = await engine.RunAsync(spec, ProjectionOptions.Default, ct).ConfigureAwait(false);
        return ChartViewJson.Serialize(view);
    }

    private PipelineSpec ToSpec(McpQuery q)
    {
        var today = DateTime.UtcNow.Date;
        var timeframe = new DateInterval(
            Instant.FromUtc(today.AddDays(-Math.Clamp(q.PastDays, 1, 400))),
            Instant.FromUtc(today.AddDays(1)));

        ImmutableArray<ITransform> transforms = q.Transform switch
        {
            "weekly-mean" => [Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing)],
            "weekly-sum" => [Transform.Resample(Period.Week, Aggregators.Sum, GapPolicy.LeaveMissing)],
            "rolling7-mean" => [Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean)],
            "rolling28-mean" => [Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean)],
            _ => [],
        };

        var kind = q.ChartKind switch
        {
            "column" => ChartKind.Column,
            "area" => ChartKind.Area,
            _ => ChartKind.Line,
        };

        return new PipelineSpec(
            tenant,
            new MetricId(q.Metric),
            [.. q.Entities.Select(id => new EntityRef(entityDimension, id))],
            timeframe,
            transforms,
            new ViewSpec(kind, AxisSource.Time, SeriesBy: entityDimension));
    }
}

public sealed record CatalogDescription(
    IReadOnlyList<MetricDescription> Metrics,
    IReadOnlyList<string> Transforms,
    IReadOnlyList<string> ChartKinds);

public sealed record MetricDescription(string Id, string Name, string Unit, string Grain, IReadOnlyList<string> Dimensions);

public sealed record McpQuery(
    string Metric,
    long[] Entities,
    int PastDays = 90,
    string Transform = "weekly-mean",
    string ChartKind = "line");
