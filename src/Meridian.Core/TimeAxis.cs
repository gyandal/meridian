namespace Meridian.Core;

/// <summary>What a block's timestamps mean. See docs/TIME.md.</summary>
public enum TimeKind : byte
{
    /// <summary>A moment on the global timeline; ticks are UTC.</summary>
    Instant = 0,

    /// <summary>A calendar position with no zone (a date of birth, a match day, a bucket such as
    /// "the week of 13 July"); ticks are wall-clock and are never converted.</summary>
    Local = 1,
}

/// <summary>
/// Describes the time column of a <see cref="PointBlock"/>. For <see cref="TimeKind.Local"/> data produced
/// by bucketing, <see cref="Grain"/> is the bucket size (day, week, month, season) and <see cref="Zone"/>
/// is the IANA zone that decided the boundaries; both are null for local values read as-is.
/// </summary>
public readonly record struct TimeAxis(TimeKind Kind, string? Grain = null, string? Zone = null)
{
    public static TimeAxis Instant => default;

    public static TimeAxis Local(string? grain = null, string? zone = null) => new(TimeKind.Local, grain, zone);

    public override string ToString() => Kind == TimeKind.Instant
        ? "instant"
        : $"local{(Grain is null ? "" : ":" + Grain)}{(Zone is null ? "" : "@" + Zone)}";
}
