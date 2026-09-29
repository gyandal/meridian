using System.Globalization;
using Meridian.Core;
using Meridian.Time;

namespace Meridian.Transforms;

/// <summary>
/// How a forecast projects a series of regular buckets. Each model says how much history it needs; a series
/// with less gets no forecast, rather than a confident line through two points.
/// </summary>
public abstract record ForecastModel
{
    /// <summary>
    /// The average bucket so far, for every future bucket: the pace. Goals per month so far, carried to the end
    /// of the season — follow it with a running total for "on pace for 14". Needs one bucket.
    /// </summary>
    public static ForecastModel Mean { get; } = new MeanModel();

    /// <summary>A straight line fitted by least squares through the history, extended. Needs three buckets.</summary>
    public static ForecastModel Trend { get; } = new TrendModel();

    /// <summary>Each future bucket repeats the one a season ago: <paramref name="seasonLength"/> buckets back (12 for
    /// months in a year). Needs one full season.</summary>
    public static ForecastModel SeasonalNaive(int seasonLength) => new SeasonalNaiveModel(Positive(seasonLength));

    /// <summary>
    /// Exponential smoothing of level and trend (Holt), and with <paramref name="seasonLength"/> of a repeating
    /// seasonal pattern too (additive Holt-Winters). Smoothing weights are chosen from a grid to minimise the
    /// one-step-ahead error over the history, so the same data always gives the same forecast. Needs four buckets,
    /// or two full seasons.
    /// </summary>
    public static ForecastModel HoltWinters(int? seasonLength = null) =>
        new HoltWintersModel(seasonLength is { } m ? Positive(m) : null);

    public abstract string Name { get; }

    /// <summary>How many buckets of history the model needs.</summary>
    public abstract int MinimumHistory { get; }

    /// <summary>The next <paramref name="horizon"/> values after <paramref name="history"/> (regular, no gaps).</summary>
    public abstract double[] Project(ReadOnlySpan<double> history, int horizon);

    private static int Positive(int n) =>
        n > 0 ? n : throw new ArgumentOutOfRangeException(nameof(n), n, "A season is at least one bucket long.");

    private sealed record MeanModel : ForecastModel
    {
        public override string Name => "mean";
        public override int MinimumHistory => 1;

        public override double[] Project(ReadOnlySpan<double> history, int horizon)
        {
            double sum = 0;
            foreach (var y in history) sum += y;
            return Enumerable.Repeat(sum / history.Length, horizon).ToArray();
        }
    }

    private sealed record TrendModel : ForecastModel
    {
        public override string Name => "trend";
        public override int MinimumHistory => 3;

        public override double[] Project(ReadOnlySpan<double> history, int horizon)
        {
            int n = history.Length;
            double meanX = (n - 1) / 2.0, meanY = 0;
            foreach (var y in history) meanY += y;
            meanY /= n;
            double sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++)
            {
                sxy += (i - meanX) * (history[i] - meanY);
                sxx += (i - meanX) * (i - meanX);
            }
            double slope = sxy / sxx, intercept = meanY - slope * meanX;
            var result = new double[horizon];
            for (int h = 1; h <= horizon; h++) result[h - 1] = intercept + slope * (n - 1 + h);
            return result;
        }
    }

    private sealed record SeasonalNaiveModel(int SeasonLength) : ForecastModel
    {
        public override string Name => string.Create(CultureInfo.InvariantCulture, $"seasonal-naive({SeasonLength})");
        public override int MinimumHistory => SeasonLength;

        public override double[] Project(ReadOnlySpan<double> history, int horizon)
        {
            var result = new double[horizon];
            for (int h = 1; h <= horizon; h++) result[h - 1] = history[history.Length - SeasonLength + (h - 1) % SeasonLength];
            return result;
        }
    }

    private sealed record HoltWintersModel(int? SeasonLength) : ForecastModel
    {
        private static readonly double[] Grid = [0.05, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];
        private static readonly double[] NoSeason = [0.0];

        public override string Name => SeasonLength is { } m ? string.Create(CultureInfo.InvariantCulture, $"holt-winters({m})") : "holt";
        public override int MinimumHistory => SeasonLength is { } m ? 2 * m : 4;

        public override double[] Project(ReadOnlySpan<double> history, int horizon)
        {
            double bestError = double.PositiveInfinity;
            (double Alpha, double Beta, double Gamma) best = default;
            foreach (var alpha in Grid)
            foreach (var beta in Grid)
            foreach (var gamma in SeasonLength is null ? NoSeason : Grid)
            {
                double error = Smooth(history, alpha, beta, gamma, horizon: 0, out _);
                if (error < bestError - 1e-12)
                {
                    bestError = error;
                    best = (alpha, beta, gamma);
                }
            }
            Smooth(history, best.Alpha, best.Beta, best.Gamma, horizon, out var forecast);
            return forecast;
        }

        /// <summary>Runs the smoother over the history; returns the sum of squared one-step-ahead errors.</summary>
        private double Smooth(ReadOnlySpan<double> y, double alpha, double beta, double gamma, int horizon, out double[] forecast)
        {
            int m = SeasonLength ?? 0;
            double level, trend;
            double[] season = new double[Math.Max(m, 1)];
            int start;
            if (m > 0)
            {
                // Trend: the per-bucket change between the first two seasons' means. Level: where that line is at the
                // end of the first season (its mean sits mid-season). Seasonal terms: the first season less the line.
                double first = 0, second = 0;
                for (int i = 0; i < m; i++) { first += y[i]; second += y[m + i]; }
                first /= m;
                second /= m;
                trend = (second - first) / m;
                level = first + trend * (m - 1) / 2.0;
                for (int i = 0; i < m; i++) season[i] = y[i] - (first + trend * (i - (m - 1) / 2.0));
                start = m;
            }
            else
            {
                level = y[0];
                trend = y[1] - y[0];
                start = 1;
            }

            double error = 0;
            for (int t = start; t < y.Length; t++)
            {
                double s = m > 0 ? season[t % m] : 0;
                double predicted = level + trend + s;
                error += (y[t] - predicted) * (y[t] - predicted);

                double previousLevel = level;
                level = alpha * (y[t] - s) + (1 - alpha) * (level + trend);
                trend = beta * (level - previousLevel) + (1 - beta) * trend;
                if (m > 0) season[t % m] = gamma * (y[t] - level) + (1 - gamma) * s;
            }

            forecast = new double[horizon];
            for (int h = 1; h <= horizon; h++)
            {
                forecast[h - 1] = level + h * trend + (m > 0 ? season[(y.Length - 1 + h) % m] : 0);
            }
            return error;
        }
    }
}

/// <summary>How far a forecast reaches: a number of buckets, or to the end of the season.</summary>
public sealed record ForecastHorizon
{
    private ForecastHorizon(int? buckets) => Count = buckets;

    /// <summary>The number of buckets, or null for the end of the season.</summary>
    public int? Count { get; }

    public static ForecastHorizon Buckets(int count) =>
        count > 0 ? new(count) : throw new ArgumentOutOfRangeException(nameof(count), count, "Forecast at least one bucket.");

    /// <summary>To the end of the season the last observed bucket is in (the report's season calendar).</summary>
    public static ForecastHorizon SeasonEnd { get; } = new((int?)null);

    public override string ToString() => Count is { } n ? n.ToString(CultureInfo.InvariantCulture) : "season-end";
}

/// <summary>
/// Projects each key's series of buckets forward. Future buckets are shared by every key — they follow the last
/// bucket any key has — and each key is projected from its own history, as many steps ahead as each future
/// bucket is from its last. Gaps inside a key's history are interpolated for fitting (and not output). Every
/// projected point is flagged <see cref="MeasureFlags.Estimated"/>, and so is anything later computed from one.
/// </summary>
internal sealed class ForecastTransform(ForecastModel model, ForecastHorizon horizon) : ITransform, ICacheIdentity
{
    public string CacheIdentity => $"forecast({model.Name};{horizon})";

    public PointBlock Apply(PointBlock input, TransformContext ctx)
    {
        var period = Period.Named(input.Time.Grain);
        if (period is null)
        {
            if (input.Count == 0) return input;
            throw new InvalidOperationException("A forecast projects regular buckets: resample the series first (e.g. by month), then forecast.");
        }
        // Bucketed times are wall-clock positions, so step through them with plain calendar arithmetic.
        var calendar = input.Time.Kind == TimeKind.Local ? ctx.Calendar.Floating : ctx.Calendar;
        long Next(long start) => period.BucketFor(new Instant(start), calendar).End.UtcTicks;

        var series = new Dictionary<PointKey, SortedDictionary<long, double?>>();
        var order = new List<PointKey>();
        for (int i = 0; i < input.Count; i++)
        {
            if (input.AtTicks[i] == PointBlock.NoAt)
            {
                throw new InvalidOperationException("A forecast needs points in time; forecast before totalling time away.");
            }
            if (!series.TryGetValue(input.Keys[i], out var points))
            {
                series[input.Keys[i]] = points = [];
                order.Add(input.Keys[i]);
            }
            points[input.AtTicks[i]] = (input.Flags[i] & MeasureFlags.Missing) == 0 ? input.Values[i] : null;
        }
        if (series.Count == 0) return input;

        // The future buckets, after the last bucket of any key.
        long last = series.Values.Max(s => s.Keys.Max());
        var future = new List<long>();
        long end = horizon.Count is null ? calendar.Season.SeasonFor(new Instant(last), calendar).End.UtcTicks : long.MaxValue;
        for (long bucket = Next(last); future.Count < (horizon.Count ?? int.MaxValue) && bucket < end; bucket = Next(bucket)) future.Add(bucket);

        var output = PointBlock.Builder.Like(input);
        for (int i = 0; i < input.Count; i++) output.Add(input.Row(i));
        if (future.Count == 0) return output.Build();

        foreach (var key in order)
        {
            var history = Regular(series[key], Next);
            if (history.Values.All(v => v is null) || history.Values.Count < model.MinimumHistory) continue;
            var filled = Interpolated(history.Values);

            // Steps from this key's last bucket to each future bucket (a key that stopped early is further back).
            var steps = new Dictionary<long, int>();
            long bucket = history.Last;
            for (int step = 1; steps.Count < future.Count; step++)
            {
                bucket = Next(bucket);
                if (bucket >= future[0]) steps[bucket] = step;
            }
            var projected = model.Project(filled, steps[future[^1]]);
            foreach (var at in future)
            {
                output.Add(key, Measurement.Of(projected[steps[at] - 1], estimated: true), new Instant(at));
            }
        }
        return output.Build();
    }

    /// <summary>A key's buckets from its first to its last, in order; null where it has no value.</summary>
    private static (List<double?> Values, long Last) Regular(SortedDictionary<long, double?> points, Func<long, long> next)
    {
        var values = new List<double?>();
        long first = points.Keys.First(), last = points.Keys.Last();
        for (long bucket = first; bucket <= last; bucket = next(bucket))
        {
            values.Add(points.TryGetValue(bucket, out var v) ? v : null);
        }
        return (values, last);
    }

    /// <summary>Gaps filled on a straight line between their neighbours (flat before the first value and after the last).</summary>
    private static double[] Interpolated(List<double?> values)
    {
        var result = new double[values.Count];
        int previous = -1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v) continue;
            result[i] = v;
            if (previous < 0)
            {
                for (int j = 0; j < i; j++) result[j] = v;
            }
            else
            {
                for (int j = previous + 1; j < i; j++) result[j] = result[previous] + (v - result[previous]) * (j - previous) / (i - previous);
            }
            previous = i;
        }
        for (int j = previous + 1; j < values.Count; j++) result[j] = result[previous];
        return result;
    }
}
