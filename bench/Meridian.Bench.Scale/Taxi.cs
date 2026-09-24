using System.Globalization;
using System.Text.Json;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.DuckDb;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Bench.Scale;

/// <summary>
/// A real-data benchmark on the NYC TLC yellow-taxi trip records (public Parquet, published monthly).
/// Pickup times are New York wall-clock times with no zone — exactly the kind of column
/// <see cref="StoredTime.InZone"/> exists for — so reports convert them with New York's DST rules and
/// bucket them into New York weeks. Entities are the 263 taxi pickup zones; the metric is trips.
/// </summary>
public static class Taxi
{
    private const string BaseUrl = "https://d37ci6vzurychx.cloudfront.net";
    private const string Tenant = "nyc";
    private static readonly DimensionId Zone = new("zone");
    private static readonly MetricId Trips = new("trips");

    public static async Task DownloadAsync(string dataDir, string from, string to)
    {
        Directory.CreateDirectory(dataDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var files = Months(from, to).Select(m => $"trip-data/yellow_tripdata_{m}.parquet").Append("misc/taxi_zone_lookup.csv");
        foreach (var file in files)
        {
            var path = Path.Combine(dataDir, Path.GetFileName(file));
            if (File.Exists(path)) { Console.WriteLine($"  have {Path.GetFileName(path)}"); continue; }
            Console.Write($"  get  {Path.GetFileName(path)} … ");
            using var response = await http.GetAsync($"{BaseUrl}/{file}", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var partial = path + ".part";
            await using (var output = File.Create(partial))
            {
                await response.Content.CopyToAsync(output);
            }
            File.Move(partial, path);
            Console.WriteLine($"{new FileInfo(path).Length / 1048576d:N0} MB");
        }
    }

    public static async Task<BenchRun> RunAsync(string dataDir, int zones, int iterations, int warmIterations, string label)
    {
        var parquet = Generator.ToSqlPath(Path.Combine(dataDir, "yellow_tripdata_*.parquet"));
        // One row per trip: the pickup zone, a count of 1, and the pickup time as the cab's meter logged it.
        var relation =
            $"(SELECT CAST(PULocationID AS BIGINT) AS entity_id, 'trips' AS metric, 1.0 AS value, tpep_pickup_datetime AS ts " +
            $"FROM read_parquet('{parquet}', union_by_name = true)) AS trips";
        var time = StoredTime.InZone("America/New_York");
        var sourceOptions = new DuckDbSourceOptions("Data Source=:memory:", relation, StoredTime: time);

        var months = Directory.GetFiles(dataDir, "yellow_tripdata_*.parquet").Select(f => Path.GetFileNameWithoutExtension(f)[^7..]).Order().ToList();
        if (months.Count == 0) throw new InvalidOperationException($"No trip files in {dataDir}. Run 'taxi-download' first.");
        var first = DateTime.ParseExact(months[0], "yyyy-MM", CultureInfo.InvariantCulture);
        var last = DateTime.ParseExact(months[^1], "yyyy-MM", CultureInfo.InvariantCulture).AddMonths(1);
        // The files hold New York months; as instants that's roughly first-of-month 05:00 UTC. Stay inside them.
        var timeframe = new DateInterval(Instant.FromUtc(first.AddDays(1)), Instant.FromUtc(last.AddDays(-1)));

        long totalRows, bytes = Directory.GetFiles(dataDir, "yellow_tripdata_*.parquet").Sum(f => new FileInfo(f).Length);
        long[] ids;
        long rowsInScope;
        string duckDb;
        using (var conn = Generator.Open())
        {
            duckDb = Generator.DuckDbVersion(conn);
            totalRows = Generator.Scalar(conn, $"SELECT count(*) FROM {relation}");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT entity_id FROM {relation} GROUP BY 1 ORDER BY count(*) DESC LIMIT {zones}";
            using var reader = cmd.ExecuteReader();
            var busiest = new List<long>();
            while (reader.Read()) busiest.Add(reader.GetInt64(0));
            ids = [.. busiest];
            rowsInScope = Generator.Scalar(conn, $"SELECT count(*) FROM {relation} WHERE entity_id IN ({string.Join(',', ids)}) " +
                $"AND ts >= '{Runner.Sql(timeframe.Start)}' AND ts < '{Runner.Sql(timeframe.End)}'");
        }
        long[] extras = [.. Enumerable.Range(1, 265).Select(i => (long)i).Except(ids).Take(warmIterations + 1)];

        var catalog = new InMemoryMetricCatalog([new MetricDefinition(Trips, "Trips", new Unit("trips"), "sum", [Zone], TimeGrain.Instant)]);
        var newYork = ProjectionOptions.Default with { Calendar = CalendarContext.For("America/New_York") };
        var weekly = Transform.Resample(Period.Week, Aggregators.Sum, GapPolicy.LeaveMissing);
        PipelineSpec Spec(IEnumerable<long> zoneIds, params ITransform[] transforms) => PipelineSpec.Create(
            Tenant, Trips, [.. zoneIds.Select(id => new EntityRef(Zone, id))], timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Zone), transforms.Length == 0 ? [weekly] : transforms);
        DuckDbPointSource Source() => new(sourceOptions, Zone);
        MeridianRuntime Cold(bool pushdown) =>
            MeridianRuntime.InMemory(catalog, pushdown ? Source() : new Runner.RawOnlySource(Source()));

        Console.WriteLine($"NYC yellow taxi · {months[0]} → {months[^1]} · {totalRows:N0} trips · {bytes / 1048576d:N0} MB Parquet");
        Console.WriteLine($"Report: weekly trips in New York weeks for the {ids.Length} busiest pickup zones · {rowsInScope:N0} trips in scope");

        // Real data, both paths: the pushed-down result must equal the engine's, DST weeks included.
        var viaSql = await Cold(pushdown: true).Engine.RunAsync(Spec(ids), newYork);
        var inEngine = await Cold(pushdown: false).Engine.RunAsync(Spec(ids), newYork);
        bool same = viaSql.Series.Zip(inEngine.Series).All(p => p.First.Marks.Select(m => (m.At, m.Value)).SequenceEqual(p.Second.Marks.Select(m => (m.At, m.Value))));
        Console.WriteLine($"Pushdown equals the in-engine result on real data: {(same ? "yes" : "NO — investigate")}");
        Console.WriteLine();
        if (!same) throw new InvalidOperationException("Pushdown and in-engine results differ on the taxi data.");

        var results = new List<ScenarioResult>
        {
            await Runner.Measure("sql-baseline", "Hand-written SQL",
                "The obvious analyst query: GROUP BY pickup zone and date_trunc('week') on the logged wall-clock time.",
                iterations, () => BaselineAsync(relation, ids, timeframe)),
            await Runner.Measure("cold-raw", "Cold · raw fetch",
                "Empty cache, no pushdown: every trip is read into Meridian, converted from New York time, then bucketed.",
                Math.Max(1, iterations / 2), () => Runner.Run(Cold(pushdown: false), Spec(ids), newYork), warmUp: false),
            await Runner.Measure("cold-pushdown", "Cold · pushdown",
                "Empty cache, New York DST conversion and weekly bucketing both done inside DuckDB.",
                iterations, () => Runner.Run(Cold(pushdown: true), Spec(ids), newYork)),
        };

        var warm = Cold(pushdown: true);
        await Runner.Run(warm, Spec(ids), newYork);
        results.Add(await Runner.Measure("warm", "Warm · cached",
            "The identical report again: served from Meridian's cache.", warmIterations, () => Runner.Run(warm, Spec(ids), newYork)));
        int next = 0;
        results.Add(await Runner.Measure("incremental", "Warm · +1 zone",
            "The cached report plus one more pickup zone: only the newcomer is fetched.",
            Math.Min(warmIterations, extras.Length - 1), () => Runner.Run(warm, Spec([.. ids, extras[next++ % extras.Length]]), newYork)));
        results.Add(await Runner.Measure("invalidate-one", "Warm · 1 zone changed",
            "A write invalidates one zone; the next run refetches just that zone.", warmIterations, async () =>
            {
                await warm.Invalidator.InvalidateAsync(new ChangeScope(Tenant, Trips.Value, new EntityRef(Zone, ids[0])));
                return await Runner.Run(warm, Spec(ids), newYork);
            }));
        results.Add(await Runner.Measure("rolling-28d", "Cold · 28-day rolling mean",
            "Daily trips per zone pushed down (New York days), then a 28-day rolling mean in the engine.",
            iterations, () => Runner.Run(Cold(pushdown: true), Spec(ids,
                Transform.Resample(Period.Day, Aggregators.Sum, GapPolicy.LeaveMissing),
                Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean)), newYork)));

        var manifest = new DatasetManifest(null, totalRows, bytes, 0, duckDb,
            Name: "nyc-yellow-taxi",
            Description: $"NYC yellow taxi trips {months[0]} → {months[^1]} · 263 pickup zones · New York wall-clock times");
        return new BenchRun(1, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}", label, DateTime.UtcNow, Machine.Commit(),
            Machine.Describe(duckDb), manifest,
            new ReportShape(Trips.Value, ids.Length, (int)(timeframe.End.UtcDateTime - timeframe.Start.UtcDateTime).TotalDays,
                "resample(week, sum) in America/New_York", rowsInScope),
            results);
    }

    private static async Task<int> BaselineAsync(string relation, long[] ids, DateInterval timeframe)
    {
        using var conn = Generator.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT entity_id, date_trunc('week', ts) AS week, count(*) FROM {relation} " +
            $"WHERE entity_id IN ({string.Join(',', ids)}) AND ts >= '{Runner.Sql(timeframe.Start)}' AND ts < '{Runner.Sql(timeframe.End)}' " +
            "GROUP BY 1, 2 ORDER BY 1, 2";
        using var reader = await cmd.ExecuteReaderAsync();
        int rows = 0;
        while (await reader.ReadAsync()) rows++;
        return rows;
    }

    private static IEnumerable<string> Months(string from, string to)
    {
        var m = DateTime.ParseExact(from, "yyyy-MM", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(to, "yyyy-MM", CultureInfo.InvariantCulture);
        for (; m <= end; m = m.AddMonths(1)) yield return m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    public static void Save(BenchRun run, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"{run.RunId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run, Json.Options));
        Console.WriteLine();
        Console.WriteLine($"Results → {Path.GetFullPath(path)}");
    }
}
