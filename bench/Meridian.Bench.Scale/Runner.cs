using System.Diagnostics;
using System.Globalization;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.DuckDb;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Bench.Scale;

public sealed record RunOptions(string DataDir, int ReportEntities, int ReportDays, int Iterations, int WarmIterations, string Label);

/// <summary>
/// Runs one report shape — weekly mean of one metric for N entities over the last year — through every
/// path that matters, and times each. "Cold" means Meridian's cache is empty; the OS file cache is warm
/// after the first (discarded) iteration, as it would be on a live server.
/// </summary>
public static class Runner
{
    private const string Tenant = "bench";
    private static readonly DimensionId EntityDim = new("entity");
    private static readonly MetricId Metric = new("metric-0");

    public static async Task<BenchRun> RunAsync(RunOptions o)
    {
        var manifest = Generator.ReadManifest(o.DataDir);
        var spec = manifest.Spec ?? throw new InvalidOperationException($"{o.DataDir} is not a synthetic dataset.");
        var relation = Generator.Relation(o.DataDir);
        var sourceOptions = new DuckDbSourceOptions("Data Source=:memory:", relation);

        int n = Math.Min(o.ReportEntities, spec.Entities);
        long[] ids = EvenlySpaced(spec.Entities, n);
        long[] extras = [.. Enumerable.Range(1, spec.Entities).Select(i => (long)i).Except(ids).Take(o.WarmIterations + 1)];
        int days = Math.Min(o.ReportDays, spec.Days);
        var timeframe = new DateInterval(Instant.FromUtc(spec.End.AddDays(-days)), Instant.FromUtc(spec.End));

        var catalog = new InMemoryMetricCatalog([new MetricDefinition(Metric, "Metric 0", new Unit("au"), "mean", [EntityDim], TimeGrain.Instant)]);
        var weeklyMean = Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing);
        PipelineSpec Spec(IEnumerable<long> entityIds, params ITransform[] transforms) => PipelineSpec.Create(
            Tenant, Metric, [.. entityIds.Select(id => new EntityRef(EntityDim, id))], timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: EntityDim),
            transforms.Length == 0 ? [weeklyMean] : transforms);

        DuckDbPointSource Source() => new(sourceOptions, EntityDim);
        MeridianRuntime Cold(bool pushdown) =>
            MeridianRuntime.InMemory(catalog, pushdown ? Source() : new RawOnlySource(Source()));

        long rawRows;
        using (var conn = Generator.Open())
        {
            rawRows = Generator.Scalar(conn, $"SELECT count(*) FROM {relation} WHERE metric = 'metric-0' AND entity_id IN ({string.Join(',', ids)}) AND ts >= '{Sql(timeframe.Start)}' AND ts < '{Sql(timeframe.End)}'");
        }

        Console.WriteLine($"Report: weekly mean of {Metric} · {n} entities · {days} days · {rawRows:N0} raw rows in scope");
        Console.WriteLine();

        var results = new List<ScenarioResult>();

        results.Add(await Measure("sql-baseline", "Hand-written SQL",
            "The same weekly mean as one DuckDB GROUP BY, results read into memory. The floor for 'query the store yourself'.",
            o.Iterations, async () => await BaselineSqlAsync(relation, ids, timeframe)));

        results.Add(await Measure("cold-raw", "Cold · raw fetch",
            "Empty cache, no pushdown: every raw row is read into Meridian, then resampled in the engine.",
            o.Iterations, () => Run(Cold(pushdown: false), Spec(ids))));

        results.Add(await Measure("cold-pushdown", "Cold · pushdown",
            "Empty cache, resample pushed into SQL: one row per entity-week leaves DuckDB.",
            o.Iterations, () => Run(Cold(pushdown: true), Spec(ids))));

        var london = ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") };
        results.Add(await Measure("cold-pushdown-zoned", "Cold · pushdown · Europe/London",
            "As cold-pushdown, but weeks drawn in London time: the UTC→local conversion happens inside DuckDB.",
            o.Iterations, () => Run(Cold(pushdown: true), Spec(ids), london)));

        var warm = Cold(pushdown: true);
        await Run(warm, Spec(ids));
        results.Add(await Measure("warm", "Warm · cached",
            "The identical report again: served from Meridian's cache, the source is not touched.",
            o.WarmIterations, () => Run(warm, Spec(ids))));

        if (extras.Length > 0)
        {
            int next = 0;
            results.Add(await Measure("incremental", "Warm · +1 entity",
                "The cached report plus one new entity: only the newcomer is fetched, the rest merge from cache.",
                Math.Min(o.WarmIterations, extras.Length - 1), () => Run(warm, Spec([.. ids, extras[next++ % extras.Length]]))));
        }

        results.Add(await Measure("invalidate-one", "Warm · 1 entity changed",
            "A write invalidates one entity; the next run refetches just that entity's slice.",
            o.WarmIterations, async () =>
            {
                await warm.Invalidator.InvalidateAsync(new ChangeScope(Tenant, Metric.Value, new EntityRef(EntityDim, ids[0])));
                return await Run(warm, Spec(ids));
            }));

        results.Add(await Measure("rolling-28d", "Cold · 28-day rolling mean",
            "A longitudinal calc SQL makes awkward: daily means pushed down, then a per-entity 28-day rolling mean in the engine.",
            o.Iterations, () => Run(Cold(pushdown: true), Spec(ids,
                Transform.Resample(Period.Day, Aggregators.Mean, GapPolicy.LeaveMissing),
                Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean)))));

        using var versionConn = Generator.Open();
        return new BenchRun(
            SchemaVersion: 1,
            RunId: $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{o.Label}",
            Label: o.Label,
            TimestampUtc: DateTime.UtcNow,
            Commit: Machine.Commit(),
            Machine: Machine.Describe(Generator.DuckDbVersion(versionConn)),
            Dataset: manifest,
            Report: new ReportShape(Metric.Value, n, days, "resample(week, mean)", rawRows),
            Scenarios: results);
    }

    internal static async Task<int> Run(MeridianRuntime runtime, PipelineSpec spec, ProjectionOptions? options = null)
    {
        var view = await runtime.Engine.RunAsync(spec, options ?? ProjectionOptions.Default);
        return view.Series.Sum(s => s.Marks.Count);
    }

    private static async Task<int> BaselineSqlAsync(string relation, long[] ids, DateInterval timeframe)
    {
        using var conn = Generator.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT entity_id, date_trunc('week', ts) AS bucket, avg(value) FROM {relation} " +
            $"WHERE metric = 'metric-0' AND entity_id IN ({string.Join(',', ids)}) " +
            $"AND ts >= '{Sql(timeframe.Start)}' AND ts < '{Sql(timeframe.End)}' GROUP BY 1, 2 ORDER BY 1, 2";
        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(long, DateTime, double?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        }
        return rows.Count;
    }

    internal static async Task<ScenarioResult> Measure(string id, string name, string description, int iterations, Func<Task<int>> body, bool warmUp = true)
    {
        iterations = Math.Max(1, iterations);
        int points = warmUp ? await body() : 0; // warm-up: JIT, OS file cache, DuckDB extension load — not timed
        var times = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            points = await body();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        var result = new ScenarioResult(id, name, description, iterations,
            Round(times[0]), Round(Percentile(times, 0.5)), Round(Percentile(times, 0.95)), Round(times.Average()), points);
        Console.WriteLine($"  {name,-28} median {result.MedianMs,10:N2} ms   p95 {result.P95Ms,10:N2} ms   ({iterations}×, {points:N0} points)");
        return result;
    }

    private static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double Round(double ms) => Math.Round(ms, 3);

    private static long[] EvenlySpaced(int total, int count)
    {
        double step = (double)total / count;
        return [.. Enumerable.Range(0, count).Select(k => (long)Math.Floor(k * step) + 1).Distinct()];
    }

    internal static string Sql(Instant instant) => instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Hides the rollup capability so the engine does the resample itself.</summary>
    internal sealed class RawOnlySource(IPointSource inner) : IPointSource
    {
        public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default) =>
            inner.FetchAsync(metric, entities, timeframe, ct);
    }
}
