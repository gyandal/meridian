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
