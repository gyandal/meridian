using Meridian.Core;

namespace Meridian.Views;

/// <summary>Where the primary (x) axis gets its values: the time axis, or a category dimension.</summary>
public abstract record AxisSource
{
    public static AxisSource Time { get; } = new TemporalAxisSource();
    public static AxisSource Category(DimensionId dimension) => new CategoryAxisSource(dimension);
}

public sealed record TemporalAxisSource : AxisSource;
public sealed record CategoryAxisSource(DimensionId Dimension) : AxisSource;

/// <summary>
/// The per-chart difference. The SAME pipeline result becomes a line, a column, or a pie by swapping
/// the spec — not by writing a new provider. This is how "unify toward multi-series" actually lands.
/// </summary>
public sealed record ViewSpec(
    ChartKind Kind,
    AxisSource XAxis,
    DimensionId? SeriesBy = null,      // dimension that splits series; null = a single series
    string? ValueAxisTitle = null,
    Unit? ValueUnit = null,
    IStatusRule? Status = null);

/// <summary>Turns a value into a semantic status. Alerting logic lives here as data, decoupled from
/// both the transform layer and the colour.</summary>
public interface IStatusRule
{
    SemanticStatus Evaluate(double value);
}

/// <summary>Good inside [low, high]; Watch just outside; Bad far outside (by a margin).</summary>
public sealed class TargetBand(double low, double high, double watchMargin = 0) : IStatusRule, ICacheIdentity
{
    public string CacheIdentity => FormattableString.Invariant($"band({low},{high},{watchMargin})");

    public SemanticStatus Evaluate(double value)
    {
        if (value >= low && value <= high) return SemanticStatus.Good;
        if (value >= low - watchMargin && value <= high + watchMargin) return SemanticStatus.Watch;
        return SemanticStatus.Bad;
    }
}
