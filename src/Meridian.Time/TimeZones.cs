using Meridian.Core;
using NodaTime;
using NodaTime.TimeZones;

namespace Meridian.Time;

/// <summary>What to do with a wall-clock time that happens twice (clocks going back).</summary>
public enum AmbiguousTime
{
    Earlier,
    Later,
    Reject,
}

/// <summary>What to do with a wall-clock time that never happens (clocks going forward).</summary>
public enum SkippedTime
{
    /// <summary>Move forward by the length of the gap: 01:30 in a 01:00→02:00 jump becomes 02:30.</summary>
    ShiftForward,
    Reject,
}

/// <summary>How wall-clock times are turned into instants around DST changes. See docs/TIME.md.</summary>
public sealed record LocalTimeResolution(AmbiguousTime Ambiguous = AmbiguousTime.Earlier, SkippedTime Skipped = SkippedTime.ShiftForward)
{
    public static LocalTimeResolution Default { get; } = new();

    internal ZoneLocalMappingResolver Resolver => Resolvers.CreateMappingResolver(
        Ambiguous switch
        {
            AmbiguousTime.Later => Resolvers.ReturnLater,
            AmbiguousTime.Reject => Resolvers.ThrowWhenAmbiguous,
            _ => Resolvers.ReturnEarlier,
        },
        Skipped == SkippedTime.Reject ? Resolvers.ThrowWhenSkipped : Resolvers.ReturnForwardShifted);
}

/// <summary>
/// The one place UTC ↔ wall-clock conversion happens, over NodaTime's bundled IANA database. Ticks in,
/// ticks out: UTC ticks for instants, wall-clock ticks for local values.
/// </summary>
public static class TimeZones
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>Resolves an IANA zone id (e.g. "Europe/London"); throws with a clear message if unknown.</summary>
    public static DateTimeZone Get(string id) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)
        ?? throw new ArgumentException($"Unknown time zone '{id}'. Use an IANA id such as 'Europe/London'.", nameof(id));

    public static bool IsUtc(DateTimeZone zone) => zone.Id == DateTimeZone.Utc.Id;

    /// <summary>Wall-clock ticks of a UTC instant in <paramref name="zone"/>.</summary>
    public static long ToLocalTicks(long utcTicks, DateTimeZone zone)
    {
        var instant = NodaTime.Instant.FromUnixTimeTicks(utcTicks - UnixEpochTicks);
        return utcTicks + zone.GetUtcOffset(instant).Ticks;
    }

    /// <summary>
    /// Converts many UTC ticks to wall-clock ticks. Reuses the current zone interval (the span between
    /// two DST changes) while values stay inside it, so a sorted column costs one lookup per interval.
    /// </summary>
    public static void ToLocalTicks(ReadOnlySpan<long> utcTicks, Span<long> localTicks, DateTimeZone zone)
    {
        long intervalStart = long.MaxValue, intervalEnd = long.MinValue, offset = 0;
        for (int i = 0; i < utcTicks.Length; i++)
        {
            long t = utcTicks[i];
            if (t == PointBlock.NoAt) { localTicks[i] = t; continue; }
            if (t < intervalStart || t >= intervalEnd)
            {
                var interval = zone.GetZoneInterval(NodaTime.Instant.FromUnixTimeTicks(t - UnixEpochTicks));
                offset = interval.WallOffset.Ticks;
                intervalStart = interval.HasStart ? interval.Start.ToUnixTimeTicks() + UnixEpochTicks : long.MinValue;
                intervalEnd = interval.HasEnd ? interval.End.ToUnixTimeTicks() + UnixEpochTicks : long.MaxValue;
            }
            localTicks[i] = t + offset;
        }
    }

    /// <summary>UTC ticks of a wall-clock time in <paramref name="zone"/>, applying <paramref name="resolution"/>
    /// to times that are ambiguous or skipped by a DST change.</summary>
    public static long ToUtcTicks(long localTicks, DateTimeZone zone, LocalTimeResolution? resolution = null)
    {
        var local = LocalDateTime.FromDateTime(new DateTime(localTicks, DateTimeKind.Unspecified));
        var zoned = zone.ResolveLocal(local, (resolution ?? LocalTimeResolution.Default).Resolver);
        return zoned.ToInstant().ToUnixTimeTicks() + UnixEpochTicks;
    }

    internal static DateTime ToLocal(Core.Instant instant, DateTimeZone zone) =>
        new(ToLocalTicks(instant.UtcTicks, zone), DateTimeKind.Unspecified);

    /// <summary>Bucket boundaries: midnight can be skipped in a few zones, so skipped times shift forward.</summary>
    internal static Core.Instant ToInstant(DateTime local, DateTimeZone zone) =>
        new(ToUtcTicks(local.Ticks, zone, LocalTimeResolution.Default));
}
