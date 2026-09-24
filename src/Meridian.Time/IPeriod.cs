using Meridian.Core;

namespace Meridian.Time;

/// <summary>
/// A tumbling bucket definition: it can say which bucket an instant falls into, and enumerate the
/// buckets across a range. Resample uses the first; gap-filling uses the second. (Overlapping windows
/// — rolling averages — are a Transform, not a Period, and land in Meridian.Transforms.)
/// </summary>
public interface IPeriod
{
    string Name { get; }

    /// <summary>The half-open interval containing <paramref name="instant"/> under this period.</summary>
    DateInterval BucketFor(Instant instant, CalendarContext ctx);

    /// <summary>Every bucket that overlaps <paramref name="range"/>, in ascending order.</summary>
    IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx);
}

internal static class PeriodHelper
{
    /// <summary>Default bucket enumeration: walk from the first bucket, stepping by each bucket's end.</summary>
    public static IEnumerable<DateInterval> Enumerate(IPeriod period, DateInterval range, CalendarContext ctx)
    {
        if (range.End <= range.Start) yield break;

        var bucket = period.BucketFor(range.Start, ctx);
        while (bucket.Start < range.End)
        {
            yield return bucket;

            var next = period.BucketFor(new Instant(bucket.End.UtcTicks), ctx);

            // Safety: guarantee forward progress even if a boundary lands oddly.
            if (next.Start <= bucket.Start)
            {
                next = period.BucketFor(new Instant(bucket.End.UtcTicks + TimeSpan.TicksPerSecond), ctx);
            }

            bucket = next;
        }
    }
}
