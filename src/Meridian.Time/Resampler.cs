using System.Runtime.InteropServices;
using Meridian.Core;

namespace Meridian.Time;

/// <summary>
/// Resample longitudinal points into tumbling buckets — the single primitive for bucketing,
/// gap handling and calendar grouping. Points keep their key; only the time axis collapses.
///
/// Buckets are local (docs/TIME.md): instants are first placed on the calendar's wall clock, then
/// grouped by calendar, so a London day holds exactly the readings from that London date. The output is
/// a <see cref="TimeKind.Local"/> block positioned at each bucket's local start, tagged with the grain
/// and the zone that decided the boundaries. Local input is bucketed as given, with no conversion.
/// Labels are never produced here (that is presentation's job).
///
/// This spike favours clarity over the eventual columnar/streaming hot path — the dictionary grouping
/// below is where the SIMD/allocation pass will land once the benchmarks exist.
/// </summary>
public static class Resampler
{
    public static PointBlock Resample(
        IEnumerable<Point> rows,
        IPeriod period,
        IAggregator aggregator,
        GapPolicy gap,
        CalendarContext ctx,
        Unit? unit = null) =>
        Resample(PointBlock.FromRows(rows, unit), period, aggregator, gap, ctx);

    public static PointBlock Resample(
        PointBlock input,
        IPeriod period,
        IAggregator aggregator,
        GapPolicy gap,
        CalendarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(ctx);

        long[] at;
        string? zone;
        if (input.Time.Kind == TimeKind.Instant)
        {
            at = new long[input.Count];
            TimeZones.ToLocalTicks(input.AtTicks, at, ctx.Zone);
            zone = ctx.Zone.Id;
        }
        else
        {
            at = input.AtTicks.ToArray();
            zone = input.Time.Zone;
        }
        ctx = ctx.Floating; // everything below is plain calendar arithmetic on wall-clock ticks

        // Group row indices by key. Rows with no position are not longitudinal and are skipped.
        var byKey = new Dictionary<PointKey, List<int>>();
        for (int i = 0; i < input.Count; i++)
        {
            if (at[i] == PointBlock.NoAt) continue;
            if (!byKey.TryGetValue(input.Keys[i], out var list))
            {
                list = [];
                byKey[input.Keys[i]] = list;
            }
            list.Add(i);
        }

        var output = new PointBlock.Builder(input.Unit, TimeAxis.Local(period.Name, zone));

        foreach (var (key, indices) in byKey)
        {
            indices.Sort((a, b) => at[a] != at[b] ? at[a].CompareTo(at[b]) : a.CompareTo(b)); // stable: ties keep source order

            // bucket-start-ticks -> present values, in ascending time order
            var buckets = new List<(long Start, List<double> Values)>();
            var lookup = new Dictionary<long, int>();
            foreach (var i in indices)
            {
                long start = period.BucketFor(new Instant(at[i]), ctx).Start.UtcTicks;
                if (!lookup.TryGetValue(start, out int pos))
                {
                    pos = buckets.Count;
                    buckets.Add((start, []));
                    lookup[start] = pos;
                }
                if ((input.Flags[i] & MeasureFlags.Missing) == 0)
                {
                    buckets[pos].Values.Add(input.Values[i]);
                }
            }

            buckets.Sort((x, y) => x.Start.CompareTo(y.Start));

            if (gap == GapPolicy.LeaveMissing)
            {
                foreach (var (start, values) in buckets)
                {
                    if (values.Count == 0) continue;
                    double v = aggregator.Aggregate(CollectionsMarshal.AsSpan(values));
                    output.Add(key, Measurement.Of(v), new Instant(start));
                }
                continue;
            }

            EmitWithGapFill(output, key, period, aggregator, gap, ctx, buckets);
        }

        return output.Build();
    }

    private static void EmitWithGapFill(
        PointBlock.Builder output,
        PointKey key,
        IPeriod period,
        IAggregator aggregator,
        GapPolicy gap,
        CalendarContext ctx,
        List<(long Start, List<double> Values)> buckets)
    {
        if (buckets.Count == 0) return;

        long firstStart = buckets[0].Start;
        long lastStart = buckets[^1].Start;

        var aggregated = new Dictionary<long, double>();
        foreach (var (start, values) in buckets)
        {
            if (values.Count > 0)
            {
                aggregated[start] = aggregator.Aggregate(CollectionsMarshal.AsSpan(values));
            }
        }

        // Every bucket across the observed span, in order (fills the holes between first and last).
        var span = period
            .Buckets(new DateInterval(new Instant(firstStart), new Instant(lastStart + 1)), ctx)
            .Select(b => (Start: b.Start.UtcTicks, Value: aggregated.TryGetValue(b.Start.UtcTicks, out var v) ? (double?)v : null))
            .ToList();

        switch (gap)
        {
            case GapPolicy.ZeroFill:
                foreach (var (start, value) in span)
                {
                    output.Add(key, Measurement.Of(value ?? 0.0), new Instant(start));
                }
                break;

            case GapPolicy.CarryForward:
                double? carried = null;
                foreach (var (start, value) in span)
                {
                    if (value.HasValue) carried = value;
                    if (carried.HasValue)
                    {
                        output.Add(key, Measurement.Of(carried.Value), new Instant(start));
                    }
                    // leading gap before any value: nothing to carry, skip.
                }
                break;

            case GapPolicy.Interpolate:
                InterpolateRun(output, key, span);
                break;
        }
    }

    private static void InterpolateRun(
        PointBlock.Builder output,
        PointKey key,
        List<(long Start, double? Value)> span)
    {
        int n = span.Count;
        int i = 0;
        while (i < n)
        {
            if (span[i].Value.HasValue)
            {
                output.Add(key, Measurement.Of(span[i].Value!.Value), new Instant(span[i].Start));
                i++;
                continue;
            }

            int prev = i - 1;
            int next = i;
            while (next < n && !span[next].Value.HasValue) next++;

            if (prev >= 0 && next < n)
            {
                double v0 = span[prev].Value!.Value;
                double v1 = span[next].Value!.Value;
                int steps = next - prev;
                for (int k = i; k < next; k++)
                {
                    double t = (double)(k - prev) / steps;
                    output.Add(key, Measurement.Of(v0 + (v1 - v0) * t), new Instant(span[k].Start));
                }
            }
            // Unbounded leading/trailing gaps: nothing to interpolate against, skip.
            i = next;
        }
    }
}
