namespace Meridian.Core;

/// <summary>
/// A point on the timeline, stored as UTC ticks. Timezones and calendars are applied only at
/// bucketing/presentation — internally everything is UTC. Dates are never smuggled through a
/// formatted string or integer id.
/// </summary>
public readonly record struct Instant(long UtcTicks) : IComparable<Instant>
{
    public static readonly Instant MinValue = new(DateTime.MinValue.Ticks);
    public static readonly Instant MaxValue = new(DateTime.MaxValue.Ticks);

    public DateTime UtcDateTime => new(UtcTicks, DateTimeKind.Utc);

    public static Instant FromUtc(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks);

    public static Instant FromUnixMilliseconds(long ms) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcTicks);

    public long ToUnixMilliseconds() =>
        new DateTimeOffset(UtcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public int CompareTo(Instant other) => UtcTicks.CompareTo(other.UtcTicks);

    public static bool operator <(Instant a, Instant b) => a.UtcTicks < b.UtcTicks;
    public static bool operator >(Instant a, Instant b) => a.UtcTicks > b.UtcTicks;
    public static bool operator <=(Instant a, Instant b) => a.UtcTicks <= b.UtcTicks;
    public static bool operator >=(Instant a, Instant b) => a.UtcTicks >= b.UtcTicks;

    public override string ToString() => UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>A half-open interval [Start, End) on the timeline. Buckets are intervals, not labels.</summary>
public readonly record struct DateInterval(Instant Start, Instant End)
{
    public long TicksDuration => End.UtcTicks - Start.UtcTicks;

    /// <summary>Half-open containment: Start is inside, End is not.</summary>
    public bool Contains(Instant i) => i.UtcTicks >= Start.UtcTicks && i.UtcTicks < End.UtcTicks;

    public override string ToString() => $"[{Start} .. {End})";
}
