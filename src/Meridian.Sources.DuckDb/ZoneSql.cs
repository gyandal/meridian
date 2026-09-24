using System.Globalization;
using System.Text;
using Meridian.Core;
using Meridian.Time;
using DateTimeZone = NodaTime.DateTimeZone;
using NodaInstant = NodaTime.Instant;

namespace Meridian.Sources.DuckDb;

/// <summary>
/// Time-zone conversion as plain SQL arithmetic, generated from NodaTime's zone rules. Over a report's
/// timeframe a zone has only a handful of offset changes, so each conversion is a short <c>CASE</c> over
/// literal cut-offs. This keeps pushdown exactly in step with the engine — same tz database, same DST
/// resolution policy — instead of relying on the database's own time-zone data and its own choice of
/// which occurrence an ambiguous wall-clock time means.
/// </summary>
internal static class ZoneSql
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;
    private static readonly long Margin = 2 * TimeSpan.TicksPerDay; // wall clocks are within ±14 h of UTC

    /// <summary>SQL turning a UTC <c>TIMESTAMP</c> expression into wall-clock time in <paramref name="zone"/>.</summary>
    public static string UtcToWall(string utc, DateTimeZone zone, DateInterval range)
    {
        var intervals = Intervals(zone, range);
        if (intervals.Count == 1) return Shift(utc, intervals[0].Offset);

        var sb = new StringBuilder("(CASE");
        for (int i = 0; i < intervals.Count - 1; i++)
        {
            sb.Append($" WHEN {utc} < {Literal(intervals[i].EndUtc)} THEN {Shift(utc, intervals[i].Offset)}");
        }
        return sb.Append($" ELSE {Shift(utc, intervals[^1].Offset)} END)").ToString();
    }

    /// <summary>
    /// SQL turning a wall-clock <c>TIMESTAMP</c> expression in <paramref name="zone"/> into UTC, resolving
    /// DST exactly as <see cref="TimeZones.ToUtcTicks"/> does: at each change the wall clock either jumps
    /// forward (a gap; skipped times shift forward, i.e. keep the old offset) or repeats (an overlap;
    /// <see cref="AmbiguousTime.Earlier"/> keeps the old offset, <see cref="AmbiguousTime.Later"/> takes the new).
    /// Returns null for policies that reject times, which SQL cannot express.
    /// </summary>
    public static string? WallToUtc(string wall, DateTimeZone zone, LocalTimeResolution resolution, DateInterval range)
    {
        if (resolution.Ambiguous == AmbiguousTime.Reject || resolution.Skipped == SkippedTime.Reject) return null;

        var intervals = Intervals(zone, range);
        if (intervals.Count == 1) return Shift(wall, -intervals[0].Offset);

        var sb = new StringBuilder("(CASE");
        for (int i = 0; i < intervals.Count - 1; i++)
        {
            long change = intervals[i].EndUtc, before = intervals[i].Offset, after = intervals[i + 1].Offset;
            // First wall-clock time that maps with the NEW offset.
            long cutoff = after > before || resolution.Ambiguous == AmbiguousTime.Later
                ? change + after      // gap: skipped times keep the old offset; overlap+Later: repeats take the new one
                : change + before;    // overlap+Earlier: the repeated hour keeps the old offset
            sb.Append($" WHEN {wall} < {Literal(cutoff)} THEN {Shift(wall, -before)}");
        }
        return sb.Append($" ELSE {Shift(wall, -intervals[^1].Offset)} END)").ToString();
    }

    /// <summary>
    /// True when converting a wall-clock value to UTC and back to the same zone never moves it to another
    /// calendar day, so day / week / month buckets can be taken from the stored wall clock directly. The
    /// round trip is exact except for times in a skipped (spring-forward) gap, which shift forward by the
    /// gap's length; that is harmless unless the shifted range crosses midnight.
    /// </summary>
    public static bool RoundTripKeepsDays(DateTimeZone zone, DateInterval range)
    {
        var intervals = Intervals(zone, range);
        for (int i = 0; i < intervals.Count - 1; i++)
        {
            long change = intervals[i].EndUtc, before = intervals[i].Offset, after = intervals[i + 1].Offset;
            if (after <= before) continue; // an overlap round-trips exactly
            long firstSkipped = change + before, lastShifted = change + after + (after - before) - 1;
            if (new DateTime(firstSkipped).Date != new DateTime(lastShifted).Date) return false;
        }
        return true;
    }

    /// <summary>
    /// The wall-clock range whose rows are exactly the rows whose converted instants fall in
    /// <paramref name="timeframe"/> — so the filter can run on the stored column (and prune Parquet row
    /// groups) instead of converting every row. Null when a boundary is within a few hours of a DST change,
    /// where the mapping isn't monotonic and only the converted comparison is exact.
    /// </summary>
    public static (DateTime Start, DateTime End)? WallRange(DateTimeZone zone, DateInterval timeframe)
    {
        long guard = 3 * TimeSpan.TicksPerHour; // wider than any DST shift
        var changes = Intervals(zone, timeframe).Select(i => i.EndUtc).ToList();
        bool NearChange(long utc) => changes.Any(c => Math.Abs(c - utc) < guard);
        if (NearChange(timeframe.Start.UtcTicks) || NearChange(timeframe.End.UtcTicks)) return null;
        return (new DateTime(TimeZones.ToLocalTicks(timeframe.Start.UtcTicks, zone)),
                new DateTime(TimeZones.ToLocalTicks(timeframe.End.UtcTicks, zone)));
    }

    private static List<(long EndUtc, long Offset)> Intervals(DateTimeZone zone, DateInterval range)
    {
        long start = Math.Max(range.Start.UtcTicks, DateTime.MinValue.Ticks + Margin) - Margin;
        long end = Math.Min(range.End.UtcTicks, DateTime.MaxValue.Ticks - Margin) + Margin;
        return zone.GetZoneIntervals(ToNoda(start), ToNoda(end))
            .Select(z => (z.HasEnd ? z.End.ToUnixTimeTicks() + UnixEpochTicks : long.MaxValue, z.WallOffset.Ticks))
            .ToList();
    }

    private static NodaInstant ToNoda(long utcTicks) => NodaInstant.FromUnixTimeTicks(utcTicks - UnixEpochTicks);

    private static string Shift(string expr, long offsetTicks) => offsetTicks == 0
        ? expr
        : $"({expr} + to_microseconds({(offsetTicks / 10).ToString(CultureInfo.InvariantCulture)}))";

    private static string Literal(long ticks) =>
        $"TIMESTAMP '{new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'"; // DuckDB: µs precision
}
