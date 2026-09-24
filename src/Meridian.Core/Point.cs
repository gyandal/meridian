namespace Meridian.Core;

/// <summary>
/// The datum. Key + measure + optional longitudinal position + unit. That is all — no colour, no
/// tooltip, no CSS class, no culture-formatted string. Everything presentational is projected on at
/// the edge, which is the whole point of the rebuild.
/// </summary>
public interface IPoint
{
    PointKey Key { get; }
    Measurement Measure { get; }
    Instant? At { get; }
    Unit Unit { get; }
}

public readonly record struct Point(PointKey Key, Measurement Measure, Instant? At, Unit Unit) : IPoint
{
    public Point(PointKey key, double value, Instant? at = null)
        : this(key, Measurement.Of(value), at, Unit.None)
    {
    }

    public bool IsLongitudinal => At.HasValue;
}
