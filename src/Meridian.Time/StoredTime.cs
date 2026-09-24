using Meridian.Core;
using DateTimeZone = NodaTime.DateTimeZone;

namespace Meridian.Time;

/// <summary>
/// How a source's time column is stored, so values are normalised once, at fetch (docs/TIME.md):
/// <list type="bullet">
/// <item><see cref="Utc"/> — UTC timestamps (the default) → instants.</item>
/// <item><see cref="InZone"/> — wall-clock times in a known zone, common in older schemas → instants,
/// resolving DST-ambiguous and skipped times with a <see cref="LocalTimeResolution"/>.</item>
/// <item><see cref="Local"/> — dates or wall-clock times with no zone (a birth date, a match day) →
/// local values, never converted.</item>
/// </list>
/// A value that carries its own offset (<see cref="DateTimeOffset"/>, e.g. TIMESTAMPTZ) is an exact
/// instant under <see cref="Utc"/> and <see cref="InZone"/>; under <see cref="Local"/> its wall-clock part is kept.
/// </summary>
public abstract record StoredTime
{
    public static StoredTime Utc { get; } = new UtcStored();
    public static StoredTime Local { get; } = new LocalStored();

    public static StoredTime InZone(string zoneId, LocalTimeResolution? resolution = null) =>
        new ZonedStored(TimeZones.Get(zoneId), resolution ?? LocalTimeResolution.Default);

    /// <summary>What the normalised values are.</summary>
    public abstract TimeKind Kind { get; }

    public TimeAxis Axis => Kind == TimeKind.Instant ? TimeAxis.Instant : TimeAxis.Local();

    /// <summary>Block ticks for a stored value: UTC ticks for instants, wall-clock ticks for local values.</summary>
    public abstract long ToTicks(DateTime stored);

    public long ToTicks(DateTimeOffset stored) => Kind == TimeKind.Instant ? stored.UtcTicks : stored.DateTime.Ticks;

    /// <summary>
    /// Bounds on the stored column that cover <paramref name="timeframe"/> (read in the data's own terms).
    /// When <see cref="NeedsExactFilter"/> is true the range is deliberately wide — the offset is only known
    /// per value — and the caller must keep only converted ticks inside the timeframe.
    /// </summary>
    public abstract (DateTime Start, DateTime End) StoredRange(DateInterval timeframe);

    public virtual bool NeedsExactFilter => false;

    private sealed record UtcStored : StoredTime
    {
        public override TimeKind Kind => TimeKind.Instant;
        public override long ToTicks(DateTime stored) =>
            stored.Kind == DateTimeKind.Local ? stored.ToUniversalTime().Ticks : stored.Ticks;
        public override (DateTime, DateTime) StoredRange(DateInterval t) => (t.Start.UtcDateTime, t.End.UtcDateTime);
    }

    private sealed record LocalStored : StoredTime
    {
        public override TimeKind Kind => TimeKind.Local;
        public override long ToTicks(DateTime stored) => stored.Ticks;
        public override (DateTime, DateTime) StoredRange(DateInterval t) =>
            (new DateTime(t.Start.UtcTicks, DateTimeKind.Unspecified), new DateTime(t.End.UtcTicks, DateTimeKind.Unspecified));
    }

    private sealed record ZonedStored(DateTimeZone Zone, LocalTimeResolution Resolution) : StoredTime
    {
        public override TimeKind Kind => TimeKind.Instant;
        public override bool NeedsExactFilter => true;
        public override long ToTicks(DateTime stored) => TimeZones.ToUtcTicks(stored.Ticks, Zone, Resolution);

        // No zone is more than 14 hours from UTC, so a day either side always covers the timeframe.
        public override (DateTime, DateTime) StoredRange(DateInterval t) =>
            (new DateTime(Math.Max(DateTime.MinValue.Ticks, t.Start.UtcTicks - TimeSpan.TicksPerDay), DateTimeKind.Unspecified),
             new DateTime(Math.Min(DateTime.MaxValue.Ticks, t.End.UtcTicks + TimeSpan.TicksPerDay), DateTimeKind.Unspecified));

        public override string ToString() => $"InZone({Zone.Id}, {Resolution.Ambiguous}, {Resolution.Skipped})";
    }
}
