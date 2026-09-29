namespace Meridian.Time;

/// <summary>
/// What to do with buckets that have no data — made a first-class, explicit choice instead of
/// scattered spread / skip-empty-days / is-missing flags.
/// </summary>
public enum GapPolicy
{
    /// <summary>Emit only buckets that contain at least one present value.</summary>
    LeaveMissing,

    /// <summary>Emit every bucket in range; empty buckets become 0.</summary>
    ZeroFill,

    /// <summary>Emit every bucket in range; empty buckets carry the previous value forward.</summary>
    CarryForward,

    /// <summary>Emit every bucket in range; empty runs are linearly interpolated between neighbours.</summary>
    Interpolate,
}

/// <summary>
/// Which buckets a filling <see cref="GapPolicy"/> fills: those between a series' own first and last points
/// (<see cref="Observed"/>, the default), or every bucket of the report's timeframe (<see cref="Timeframe"/>) — so a
/// quiet first or last month is a 0, not a missing column. A series with no points at all still produces nothing:
/// nothing says it exists.
/// </summary>
public enum FillAcross
{
    Observed,
    Timeframe,
}
