using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;
using Xunit;

namespace Meridian.Transforms.Tests;

public class ForecastRangeTests
{
    private static readonly DimensionId Player = new("player");

    [Theory]
    [InlineData(0.975, null, 1.959964)]
    [InlineData(0.9, null, 1.281552)]
    [InlineData(0.975, 1.0, 12.706205)]
    [InlineData(0.975, 5.0, 2.570582)]
    [InlineData(0.975, 30.0, 2.042272)]
    [InlineData(0.9, 10.0, 1.372184)]
    [InlineData(0.025, 5.0, -2.570582)]
    public void Quantiles_match_published_tables(double p, double? dof, double expected)
    {
        double q = dof is { } v ? Statistics.StudentTQuantile(p, v) : Statistics.NormalQuantile(p);
        Assert.Equal(expected, q, 5);
    }

    private static double Half(ForecastModel model, double[] history, int reach, double percent, params int[] steps)
    {
        var sum = new double[reach];
        foreach (var s in steps) sum[s - 1] = 1;
        return model.Errors(history, reach)!.HalfWidth(percent, sum);
    }

    [Fact]
    public void Mean_and_trend_ranges_are_the_textbook_prediction_intervals()
    {
        double[] y = [4, 7, 5, 9, 6, 8];
        int n = y.Length;
        double mean = y.Average(), s = Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / (n - 1)), t = Statistics.StudentTQuantile(0.9, n - 1);
        Assert.Equal(t * s * Math.Sqrt(1 + 1.0 / n), Half(ForecastModel.Mean, y, 3, 80, 2), 9);
        Assert.Equal(t * s * Math.Sqrt(3 + 9.0 / n), Half(ForecastModel.Mean, y, 3, 80, 1, 2, 3), 9); // a 3-bucket total

        double meanX = (n - 1) / 2.0, sxx = Enumerable.Range(0, n).Sum(i => (i - meanX) * (i - meanX));
        double slope = Enumerable.Range(0, n).Sum(i => (i - meanX) * (y[i] - mean)) / sxx, a = mean - slope * meanX;
        double sr = Math.Sqrt(Enumerable.Range(0, n).Sum(i => Math.Pow(y[i] - (a + slope * i), 2)) / (n - 2));
        double x0 = n - 1 + 2;
        Assert.Equal(Statistics.StudentTQuantile(0.975, n - 2) * sr * Math.Sqrt(1 + 1.0 / n + (x0 - meanX) * (x0 - meanX) / sxx),
            Half(ForecastModel.Trend, y, 2, 95, 2), 9);
    }

    [Fact]
    public void Seasonal_and_smoothing_ranges_widen_as_the_theory_says()
    {
        double[] y = [3, 8, 5, 4, 9, 6, 5, 7, 7];
        var naive = ForecastModel.SeasonalNaive(3);
        Assert.Equal(Half(naive, y, 4, 80, 1) * Math.Sqrt(2), Half(naive, y, 4, 80, 4), 9); // a season further: one more change
        Assert.Equal(Half(naive, y, 4, 80, 1), Half(naive, y, 4, 80, 3), 9);                // within the first season: one

        var holt = ForecastModel.HoltWinters();
        double[] noisy = [10, 12, 11, 14, 13, 15, 17, 16, 18, 19];
        var errors = holt.Errors(noisy, 3)!;
        Assert.Equal(1.0, errors.Weights[1][1]);
        Assert.True(errors.Weights[1][0] > 0 && errors.Weights[2][0] > errors.Weights[1][0]); // c_2 > c_1 while there's trend
        Assert.True(Half(holt, noisy, 3, 80, 3) > Half(holt, noisy, 3, 80, 1));
        Assert.Null(errors.DegreesOfFreedom);
        Assert.Null(ForecastModel.Mean.Errors([5], 2));                                        // one bucket: no spread to go on
    }

    [Theory]
    [InlineData("mean")]
    [InlineData("trend")]
    public void Ranges_cover_what_they_claim_to_for_single_buckets_and_running_totals(string name)
    {
        // Simulate the model's own world many times; an 80% range should hold the real future value about 80% of
        // the time — for next month, and for the total of the next six.
        var model = name == "mean" ? ForecastModel.Mean : ForecastModel.Trend;
        var rng = new Random(20260929);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        int trials = 4000, n = 8, reach = 6, hitNext = 0, hitTotal = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            var all = Enumerable.Range(0, n + reach).Select(t => 10 + (name == "trend" ? 0.7 * t : 0) + 2 * Normal()).ToArray();
            var history = all[..n];
            var projected = model.Project(history, reach);
            var errors = model.Errors(history, reach)!;

            double half1 = errors.HalfWidth(80, [1, 0, 0, 0, 0, 0]);
            if (Math.Abs(all[n] - projected[0]) <= half1) hitNext++;

            double halfTotal = errors.HalfWidth(80, [1, 1, 1, 1, 1, 1]);
            if (Math.Abs(all[n..].Sum() - projected.Sum()) <= halfTotal) hitTotal++;
        }
        Assert.InRange(hitNext / (double)trials, 0.78, 0.82);
        Assert.InRange(hitTotal / (double)trials, 0.78, 0.82);
    }

    private static PointBlock Months(params (long Player, int Month, double Value)[] rows) => PointBlock.FromRows(
        [.. rows.Select(r => new Point(PointKey.Of(KeyPart.Entity(Player, r.Player)), r.Value, new Instant(new DateTime(2025, 1, 1).AddMonths(r.Month - 1).Ticks)))],
        time: TimeAxis.Local("month", "Europe/London"));

    [Fact]
    public void A_running_total_forecast_has_the_totals_range_not_a_sum_of_monthly_ranges()
    {
        var block = Months((1, 1, 2), (1, 2, 5), (1, 3, 3), (1, 4, 6));
        var each = Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 80).Apply(block, TransformContext.Default);
        var total = Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 80, runningTotal: true).Apply(block, TransformContext.Default);

        Assert.Equal([2.0, 7.0, 10.0, 16.0, 20.0, 24.0, 28.0], total.Values.ToArray());        // observed running total, then the pace
        Assert.All(Enumerable.Range(0, 4), i => Assert.Null(total.RangeAt(i)));                // no range on what happened
        var eachHalf = each.RangeAt(6)!.Value.High - each.Values[6];
        var totalHalf = total.RangeAt(6)!.Value.High - total.Values[6];
        Assert.Equal(Half(ForecastModel.Mean, [2, 5, 3, 6], 3, 80, 1, 2, 3), totalHalf, 9);
        Assert.True(totalHalf < 3 * eachHalf);                                                 // errors partly cancel…
        Assert.True(totalHalf > Math.Sqrt(3) * eachHalf);                                      // …but the pace's error doesn't
    }

    [Fact]
    public void Steps_that_combine_values_refuse_ranges_and_steps_that_keep_rows_keep_them()
    {
        var ranged = Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(2), range: 80)
            .Apply(Months((1, 1, 2), (1, 2, 4), (2, 1, 1), (2, 2, 3)), TransformContext.Default);

        foreach (var combine in new[] { Transform.GroupBy(Aggregators.Sum), Transform.Total(Aggregators.Sum), Transform.Cumulative(Aggregators.Sum),
                                         Transform.ShareOf(Player), Transform.Rolling(TimeSpan.FromDays(60), Aggregators.Mean) })
        {
            Assert.Contains("forecast ranges", Assert.Throws<InvalidOperationException>(() => combine.Apply(ranged, TransformContext.Default)).Message);
        }
        var kept = Transform.WhereIn(Player, "1").Apply(ranged, TransformContext.Default);
        Assert.NotNull(kept.RangeAt(kept.Count - 1));
        Assert.NotNull(Transform.Top(1, Aggregators.Sum).Apply(ranged, TransformContext.Default).RangeAt(3));
    }

    [Fact]
    public void Ranges_are_part_of_the_identity_and_validated()
    {
        static string Id(ITransform t) => ((ICacheIdentity)t).CacheIdentity;
        var plain = Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3)));
        Assert.NotEqual(plain, Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 80)));
        Assert.NotEqual(Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 80)), Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 95)));
        Assert.NotEqual(plain, Id(Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), runningTotal: true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Transform.Forecast(ForecastModel.Mean, ForecastHorizon.Buckets(3), range: 100));
    }
}
