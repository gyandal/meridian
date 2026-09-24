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
        var builder = new PointBlock.Builder(unit ?? left.Unit);

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
