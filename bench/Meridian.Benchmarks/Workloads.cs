using BenchmarkDotNet.Attributes;
using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;

namespace Meridian.Benchmarks;

/// <summary>
/// The in-engine hot paths (see docs/BENCHMARKS.md). Run with:
///   dotnet run -c Release --project bench/Meridian.Benchmarks
/// [MemoryDiagnoser] makes allocations a first-class number — the goal is near-zero on aggregation.
/// These are the baselines a columnar/SIMD pass will be measured against.
/// </summary>
[MemoryDiagnoser]
public class Workloads
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Metric = new("metric");
    private static readonly TransformContext Ctx = TransformContext.Default;

    // Scales the realistic macro: EntityCount entities × MetricCount metrics × one season of daily points.
    [Params(25, 100)]
    public int EntityCount;

    private const int MetricCount = 10;
    private const int Days = 365;

    private PointBlock _seasonDaily = null!;   // entities × metrics × 365 days
    private PointBlock _oneSeriesDaily = null!; // one entity, one metric, 365 days (for rolling/ACWR)
    private double[] _rawValues = null!;         // flat values for the aggregate micro-benchmark

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        var start = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var builder = new PointBlock.Builder(new Unit("au"));
        for (int e = 0; e < EntityCount; e++)
        {
            for (int m = 0; m < MetricCount; m++)
            {
                var key = PointKey.Of(KeyPart.Entity(Player, e), KeyPart.Entity(Metric, m));
                for (int d = 0; d < Days; d++)
                {
                    var at = Instant.FromUtc(start.AddDays(d));
                    builder.Add(key, Measurement.Of(rng.NextDouble() * 100.0), at);
                }
            }
        }
        _seasonDaily = builder.Build();

        var single = new PointBlock.Builder(new Unit("au"));
        var soloKey = PointKey.Of(KeyPart.Entity(Player, 0));
        for (int d = 0; d < Days; d++)
        {
            single.Add(soloKey, Measurement.Of(rng.NextDouble() * 100.0), Instant.FromUtc(start.AddDays(d)));
        }
        _oneSeriesDaily = single.Build();

        _rawValues = new double[100_000];
        for (int i = 0; i < _rawValues.Length; i++) _rawValues[i] = rng.NextDouble() * 100.0;
    }

    [Benchmark]
    public double AggregateMean_100k() => Aggregators.Mean.Aggregate(_rawValues);

    [Benchmark]
    public int ResampleWeekly_Season() =>
        Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing)
            .Apply(_seasonDaily, Ctx).Count;

    [Benchmark]
    public int Acwr_OneSeason()
    {
        var acute = Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(_oneSeriesDaily, Ctx);
        var chronic = Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(_oneSeriesDaily, Ctx);
        return Binary.Combine(acute, chronic, static (a, c) => c == 0 ? double.NaN : a / c, matchTime: true).Count;
    }

    [Benchmark]
    public ulong KeyBuildAndStableHash()
    {
        var key = PointKey.Of(KeyPart.Entity(Player, 42), KeyPart.Entity(Metric, 7));
        return key.StableHash64();
    }
}
