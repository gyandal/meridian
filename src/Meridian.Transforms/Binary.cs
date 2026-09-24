using Meridian.Core;

namespace Meridian.Transforms;

public enum CompareMode
{
    /// <summary>current − baseline.</summary>
    AbsoluteDifference,

    /// <summary>(current − baseline) / baseline × 100.</summary>
    PercentChange,
}

/// <summary>
/// Two-input joins. Unary <see cref="ITransform"/>s can't express "compare A to B" or "divide the
/// 7-day rolling by the 28-day rolling", so those live here as explicit combinators — which is how
/// Difference/Correlation stop being bespoke providers and become composition.
/// </summary>
public static class Binary
{
    /// <summary>Join two blocks on key (and optionally instant) and apply <paramref name="combine"/>
    /// to the present value on each side. Only matched, both-present pairs are emitted.</summary>
    public static PointBlock Combine(
        PointBlock left,
        PointBlock right,
        Func<double, double, double> combine,
        bool matchTime,
        Unit? unit = null)
    {
        var builder = new PointBlock.Builder(unit ?? left.Unit, matchTime ? JoinedAxis(left, right) : left.Time);

        if (matchTime)
        {
            var index = new Dictionary<(PointKey Key, long At), double>();
            for (int i = 0; i < right.Count; i++)
            {
                if ((right.Flags[i] & MeasureFlags.Missing) != 0) continue;
                index[(right.Keys[i], right.AtTicks[i])] = right.Values[i];
            }

            for (int i = 0; i < left.Count; i++)
            {
                if ((left.Flags[i] & MeasureFlags.Missing) != 0) continue;
                if (index.TryGetValue((left.Keys[i], left.AtTicks[i]), out double r))
                {
                    long at = left.AtTicks[i];
                    builder.Add(left.Keys[i], Measurement.Of(combine(left.Values[i], r)),
                        at == PointBlock.NoAt ? null : new Instant(at));
                }
            }
        }
        else
        {
            var index = new Dictionary<PointKey, double>();
            for (int i = 0; i < right.Count; i++)
            {
                if ((right.Flags[i] & MeasureFlags.Missing) != 0) continue;
                index[right.Keys[i]] = right.Values[i]; // last present wins
            }

            for (int i = 0; i < left.Count; i++)
            {
                if ((left.Flags[i] & MeasureFlags.Missing) != 0) continue;
                if (index.TryGetValue(left.Keys[i], out double r))
                {
                    builder.Add(left.Keys[i], Measurement.Of(combine(left.Values[i], r)), null);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Matching on time only makes sense within one kind of time (docs/TIME.md): an instant and a calendar
    /// bucket are different things, and two bucketed series must share the zone that drew their boundaries.
    /// </summary>
    private static TimeAxis JoinedAxis(PointBlock left, PointBlock right)
    {
        if (left.Count == 0) return right.Time;
        if (right.Count == 0) return left.Time;
        if (left.Time.Kind != right.Time.Kind)
        {
            throw new InvalidOperationException(
                $"Cannot match time between {left.Time} and {right.Time} data: an instant and a local (calendar) " +
                "value are different kinds of time. Resample the instant series into calendar buckets first (see docs/TIME.md).");
        }
        if (left.Time.Zone is { } lz && right.Time.Zone is { } rz && lz != rz)
        {
            throw new InvalidOperationException(
                $"Cannot match time between series bucketed in different time zones ({lz} vs {rz}); their days are different days.");
        }
        return left.Time with { Zone = left.Time.Zone ?? right.Time.Zone };
    }

    /// <summary>Compare a current block against a baseline, joined on key. Expects one value per key
    /// on each side (i.e. reduced first).</summary>
    public static PointBlock Compare(PointBlock baseline, PointBlock current, CompareMode mode)
    {
        return mode switch
        {
            CompareMode.AbsoluteDifference =>
                Combine(current, baseline, static (c, b) => c - b, matchTime: false, current.Unit),
            CompareMode.PercentChange =>
                Combine(current, baseline, static (c, b) => b == 0 ? double.NaN : (c - b) / b * 100.0,
                    matchTime: false, new Unit("%")),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}
