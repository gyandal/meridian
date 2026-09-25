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
/// <remarks><see cref="TimeKind"/> declares what the metric's timestamps mean (docs/TIME.md): readings at
/// a moment (<see cref="Core.TimeKind.Instant"/>, the default) or calendar dates with no zone, such as a
/// daily wellness score or a match day (<see cref="Core.TimeKind.Local"/>). The engine rejects a source
/// whose data contradicts it.</remarks>
public sealed record MetricDefinition(
    MetricId Id,
    string Name,
    Unit Unit,
    string DefaultAggregation,               // resolves against Meridian.Core.Aggregators
    ImmutableArray<DimensionId> ValidDimensions,
    TimeGrain NativeGrain,
    TimeKind TimeKind = TimeKind.Instant)
{
    /// <summary>For a derived metric, how it's computed from other metrics; null for a stored metric.</summary>
    public MetricFormula? Formula { get; init; }

    public bool IsDerived => Formula is not null;

    /// <summary>
    /// A metric computed as a ratio of totals: within each bucket (or group), <paramref name="numerator"/> and
    /// <paramref name="denominator"/> are each aggregated with <paramref name="aggregation"/> — sum by default —
    /// and then divided, times <paramref name="scale"/>. Goals per 90 is <c>Ratio(goals, minutes, 90)</c>.
    /// Dividing totals, not averaging per-row ratios, is what makes the answer right.
    /// </summary>
    public static MetricDefinition Ratio(
        MetricId id, string name, Unit unit, MetricId numerator, MetricId denominator, double scale,
        ImmutableArray<DimensionId> validDimensions, string aggregation = "sum", TimeKind timeKind = TimeKind.Instant) =>
        new(id, name, unit, aggregation, validDimensions, TimeGrain.Instant, timeKind)
        {
            Formula = new RatioFormula(numerator, denominator, scale, aggregation),
        };

    /// <summary>
    /// A metric that adds other metrics' totals: goal involvements is <c>Sum(goals, assists)</c>. Each input is
    /// aggregated within each bucket (or group) — sum by default — and the totals are added. With sums this
    /// is exact: the sum of totals is the total of the sums.
    /// </summary>
    public static MetricDefinition Sum(
        MetricId id, string name, Unit unit, IReadOnlyList<MetricId> inputs, ImmutableArray<DimensionId> validDimensions,
        string aggregation = "sum", MissingInput missing = MissingInput.Zero, TimeKind timeKind = TimeKind.Instant) =>
        Linear(id, name, unit, [.. inputs.Select(m => new FormulaTerm(m))], validDimensions, aggregation, missing, timeKind);

    /// <summary><paramref name="minuend"/> − <paramref name="subtrahend"/>, per bucket, after each is aggregated.</summary>
    public static MetricDefinition Difference(
        MetricId id, string name, Unit unit, MetricId minuend, MetricId subtrahend, ImmutableArray<DimensionId> validDimensions,
        string aggregation = "sum", MissingInput missing = MissingInput.Zero, TimeKind timeKind = TimeKind.Instant) =>
        Linear(id, name, unit, [new FormulaTerm(minuend), new FormulaTerm(subtrahend, -1)], validDimensions, aggregation, missing, timeKind);

    /// <summary>Σ coefficient × input total, per bucket — the general form of <see cref="Sum"/> and <see cref="Difference"/>.</summary>
    public static MetricDefinition Linear(
        MetricId id, string name, Unit unit, ImmutableArray<FormulaTerm> terms, ImmutableArray<DimensionId> validDimensions,
        string aggregation = "sum", MissingInput missing = MissingInput.Zero, TimeKind timeKind = TimeKind.Instant) =>
        new(id, name, unit, aggregation, validDimensions, TimeGrain.Instant, timeKind)
        {
            Formula = new LinearFormula(terms, aggregation, missing),
        };
}

/// <summary>How a derived metric is computed from other (stored) metrics.</summary>
public abstract record MetricFormula
{
    public abstract IReadOnlyList<MetricId> Inputs { get; }

    /// <summary>How inputs are aggregated within a bucket before the formula combines them.</summary>
    public abstract string Aggregation { get; }
}

/// <summary>
/// numerator ÷ denominator × scale, per bucket, after each input is aggregated. A bucket with a denominator
/// but no numerator rows is a numerator of 0 (no goals scored, not "unknown"); a bucket whose denominator is
/// missing or zero has no value.
/// </summary>
public sealed record RatioFormula(MetricId Numerator, MetricId Denominator, double Scale = 1, string InputAggregation = "sum") : MetricFormula
{
    public override IReadOnlyList<MetricId> Inputs => [Numerator, Denominator];

    public override string Aggregation => InputAggregation;

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ratio({Numerator}/{Denominator}*{Scale};{InputAggregation})");
}

/// <summary>One input of a <see cref="LinearFormula"/>: its total, times <see cref="Coefficient"/>.</summary>
public sealed record FormulaTerm(MetricId Metric, double Coefficient = 1);

/// <summary>What a linear formula does with a bucket where some inputs have no value.</summary>
public enum MissingInput
{
    /// <summary>No rows is zero — right for events (no assists recorded is no assists). The bucket has a
    /// value if any input has one.</summary>
    Zero,

    /// <summary>The bucket needs every input — right for readings, where no row means "not measured".</summary>
    NoValue,
}

/// <summary>
/// Σ coefficient × total, per bucket, after each input is aggregated. There's deliberately no product of
/// metrics: revenue is Σ(price × quantity), not Σprice × Σquantity, so it belongs in the source (a column or
/// a view), where each row's product can be taken.
/// </summary>
public sealed record LinearFormula(ImmutableArray<FormulaTerm> Terms, string InputAggregation = "sum", MissingInput Missing = MissingInput.Zero) : MetricFormula
{
    public override IReadOnlyList<MetricId> Inputs => [.. Terms.Select(t => t.Metric)];

    public override string Aggregation => InputAggregation;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"linear({string.Join('+', Terms.Select(t => $"{t.Coefficient}*{t.Metric}"))};{InputAggregation};{Missing})");

    public bool Equals(LinearFormula? other) => other is not null && ToString() == other.ToString();

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);
}

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
        foreach (var metric in _byId.Values) Validate(metric);
    }

    /// <summary>Bad definitions are rejected when the catalog is built, not when a report hits them.</summary>
    private void Validate(MetricDefinition metric)
    {
        if (metric.Formula is not { } formula) return;
        if (formula.Inputs.Count == 0 || formula.Inputs.Distinct().Count() != formula.Inputs.Count)
        {
            throw new ArgumentException($"Derived metric '{metric.Id}' needs at least one input, each used once (combine coefficients instead).");
        }
        foreach (var input in formula.Inputs)
        {
            if (!_byId.TryGetValue(input, out var source))
            {
                throw new ArgumentException($"Derived metric '{metric.Id}' uses '{input}', which isn't in the catalog.");
            }
            if (source.IsDerived)
            {
                throw new ArgumentException($"Derived metric '{metric.Id}' uses '{input}', which is itself derived. Build derived metrics from stored ones.");
            }
            if (source.TimeKind != metric.TimeKind)
            {
                throw new ArgumentException($"Derived metric '{metric.Id}' is {metric.TimeKind} time but its input '{input}' is {source.TimeKind}.");
            }
            foreach (var dimension in metric.ValidDimensions)
            {
                if (!source.ValidDimensions.Contains(dimension))
                {
                    throw new ArgumentException(
                        $"Derived metric '{metric.Id}' can be sliced by '{dimension}', but its input '{input}' can't. A formula needs every input per group.");
                }
            }
        }
    }

    public IReadOnlyCollection<MetricDefinition> Metrics => _byId.Values;

    public bool TryResolve(MetricId id, out MetricDefinition definition) =>
        _byId.TryGetValue(id, out definition!);
}
