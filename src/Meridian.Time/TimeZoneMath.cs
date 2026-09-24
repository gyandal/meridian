using Meridian.Core;

namespace Meridian.Time;

/// <summary>
/// The one place UTC ↔ local conversion happens. Bucket boundaries are computed in local wall-clock
/// time then converted back to UTC, so a "day" is correctly 23 or 25 hours across a DST change. It is
/// isolated here and testable.
/// </summary>
internal static class TimeZoneMath
{
    public static DateTime ToLocal(Instant instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, zone);

    public static Instant ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Spring-forward gap: this wall-clock time never existed. Nudge forward past the gap so a
        // midnight boundary still resolves to a real instant.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return Instant.FromUtc(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
