using System.Collections.Immutable;
using Meridian.Core;

namespace Meridian.Semantics;

/// <summary>Stable identity of a metric in the catalog (e.g. "acwr", "training-load").</summary>
public readonly record struct MetricId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>The native cadence a metric is stored at — informs the default resample grain.</summary>
public enum TimeGrain
{
    Instant,
    Daily,
    Weekly,
    Monthly,
    Seasonal,
}

/// <summary>
/// A declarative metric definition — the ~20% per-client deliverable that makes everything above it
/// (charts, API, agent query, cache invalidation) fall out for free. This is only the shape sketch;
/// source binding, calculated metrics, and the change→tag mapping are fleshed out in later phases.
/// </summary>
public sealed record MetricDefinition(
    MetricId Id,
    string Name,
    Unit Unit,
    string DefaultAggregation,               // resolves against Meridian.Core.Aggregators
    ImmutableArray<DimensionId> ValidDimensions,
    TimeGrain NativeGrain);

public interface IMetricCatalog
{
    IReadOnlyCollection<MetricDefinition> Metrics { get; }
    bool TryResolve(MetricId id, out MetricDefinition definition);
}

/// <summary>A trivial in-memory catalog, enough to prove the shape and feed a future MCP `describe`.</summary>
public sealed class InMemoryMetricCatalog : IMetricCatalog
{
    private readonly Dictionary<MetricId, MetricDefinition> _byId;

    public InMemoryMetricCatalog(IEnumerable<MetricDefinition> definitions)
    {
        _byId = definitions.ToDictionary(d => d.Id);
    }

    public IReadOnlyCollection<MetricDefinition> Metrics => _byId.Values;

    public bool TryResolve(MetricId id, out MetricDefinition definition) =>
        _byId.TryGetValue(id, out definition!);
}
