using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
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
/// The TSBS (Time Series Benchmark Suite, github.com/timescale/tsbs) cpu-only workload. Data comes from
/// TSBS's own generator; the queries are TSBS's query types with the same semantics and random instances
/// (hosts, time windows). Each runs five ways: DuckDB SQL over TSBS's native wide schema and over a long
/// layout (one row per value), Meridian cold over each, and Meridian warm.
/// </summary>
public static class Tsbs
{
    private static readonly string[] Metrics =
        ["usage_user", "usage_system", "usage_idle", "usage_nice", "usage_iowait",
         "usage_irq", "usage_softirq", "usage_steal", "usage_guest", "usage_guest_nice"];
    private static readonly DimensionId Host = new("host");
    private static readonly DateTime Start = new(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A TSBS query type: aggregate <see cref="MetricCount"/> metrics for <see cref="HostCount"/> hosts
    /// (0 = all) over <see cref="Hours"/> hours in buckets of <see cref="Bucket"/>, either across hosts
    /// (single-groupby, cpu-max-all) or per host (double-groupby).</summary>
    private sealed record QueryType(string Name, int MetricCount, int HostCount, int Hours, TimeSpan Bucket, IAggregator Aggregator, bool PerHost, string Description);

    private static readonly QueryType[] Types =
    [
        new("single-groupby-1-1-1", 1, 1, 1, TimeSpan.FromMinutes(1), Aggregators.Max, false, "max of 1 metric, 1 host, per minute, 1 hour"),
        new("single-groupby-1-1-12", 1, 1, 12, TimeSpan.FromMinutes(1), Aggregators.Max, false, "max of 1 metric, 1 host, per minute, 12 hours"),
        new("single-groupby-1-8-1", 1, 8, 1, TimeSpan.FromMinutes(1), Aggregators.Max, false, "max of 1 metric across 8 hosts, per minute, 1 hour"),
        new("single-groupby-5-1-1", 5, 1, 1, TimeSpan.FromMinutes(1), Aggregators.Max, false, "max of 5 metrics, 1 host, per minute, 1 hour"),
        new("single-groupby-5-8-1", 5, 8, 1, TimeSpan.FromMinutes(1), Aggregators.Max, false, "max of 5 metrics across 8 hosts, per minute, 1 hour"),
        new("cpu-max-all-8", 10, 8, 8, TimeSpan.FromHours(1), Aggregators.Max, false, "max of all 10 metrics across 8 hosts, per hour, 8 hours"),
        new("double-groupby-1", 1, 0, 12, TimeSpan.FromHours(1), Aggregators.Mean, true, "mean of 1 metric per host per hour, all hosts, 12 hours"),
        new("double-groupby-all", 10, 0, 12, TimeSpan.FromHours(1), Aggregators.Mean, true, "mean of all 10 metrics per host per hour, all hosts, 12 hours"),
    ];

    private sealed record Instance(long[] Hosts, DateInterval Window);

    // ---------------------------------------------------------------- data

    public static void Generate(string dataDir, string generator, int scale, int days, int seed)
    {
        Directory.CreateDirectory(dataDir);
        var work = Path.Combine(dataDir, "work.duckdb");
        File.Delete(work);
        File.Delete(work + ".wal");

        var sw = Stopwatch.StartNew();
        long readings = 0;
        using (var conn = new DuckDBConnection($"Data Source={work}"))
        {
            conn.Open();
            Generator.Exec(conn, $"CREATE TABLE cpu (host_id BIGINT, ts TIMESTAMP, {string.Join(", ", Metrics.Select(m => m + " DOUBLE"))})");

            var end = Start.AddDays(days);
            var psi = new ProcessStartInfo(generator,
                $"--use-case=cpu-only --seed={seed} --scale={scale} --log-interval=10s --format=timescaledb " +
                $"--timestamp-start={Start:yyyy-MM-ddTHH:mm:ssZ} --timestamp-end={end:yyyy-MM-ddTHH:mm:ssZ}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {generator}");
            using (var appender = conn.CreateAppender("cpu"))
            {
                long host = -1;
                string? line;
                while ((line = process.StandardOutput.ReadLine()) is not null)
                {
                    // Pairs of lines: "tags,hostname=host_17,..." then "cpu,<unix ns>,<10 integer metrics>".
                    if (line.StartsWith("tags,hostname=host_", StringComparison.Ordinal))
                    {
                        int comma = line.IndexOf(',', 19);
                        host = long.Parse(line.AsSpan(19, comma - 19), CultureInfo.InvariantCulture);
                        continue;
                    }
                    if (!line.StartsWith("cpu,", StringComparison.Ordinal) || host < 0 || line.StartsWith("cpu,usage_", StringComparison.Ordinal)) continue;

                    var fields = line.Split(',');
                    long ns = long.Parse(fields[1], CultureInfo.InvariantCulture);
                    var row = appender.CreateRow()
                        .AppendValue(host)
                        .AppendValue(new DateTime(DateTime.UnixEpoch.Ticks + ns / 100, DateTimeKind.Unspecified));
                    for (int m = 0; m < Metrics.Length; m++) row.AppendValue(double.Parse(fields[m + 2], CultureInfo.InvariantCulture));
                    row.EndRow();
                    if (++readings % 5_000_000 == 0) Console.WriteLine($"  {readings:N0} readings…");
                }
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"tsbs_generate_data exited with {process.ExitCode}");

            Console.WriteLine($"  {readings:N0} readings generated; writing Parquet…");
            var wide = Generator.ToSqlPath(Path.Combine(dataDir, "cpu_wide.parquet"));
            Generator.Exec(conn, $"COPY (SELECT * FROM cpu ORDER BY host_id, ts) TO '{wide}' (FORMAT parquet, COMPRESSION zstd)");
            foreach (var metric in Metrics)
            {
                var dir = Path.Combine(dataDir, "long", $"metric={metric}");
                Directory.CreateDirectory(dir);
                Generator.Exec(conn,
                    $"COPY (SELECT host_id AS entity_id, {metric} AS value, ts FROM cpu ORDER BY host_id, ts) " +
                    $"TO '{Generator.ToSqlPath(Path.Combine(dir, "data.parquet"))}' (FORMAT parquet, COMPRESSION zstd)");
            }
        }
        File.Delete(work);
        File.Delete(work + ".wal");

        long bytes = Directory.EnumerateFiles(Path.Combine(dataDir, "long"), "*.parquet", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        using var versionConn = Generator.Open();
        var manifest = new DatasetManifest(null, readings * Metrics.Length, bytes, sw.Elapsed.TotalSeconds, Generator.DuckDbVersion(versionConn),
            Name: "tsbs-cpu-only",
            Description: $"TSBS cpu-only · {scale:N0} hosts · {days} days · 10 s interval · seed {seed} · {readings:N0} readings × 10 metrics");
        File.WriteAllText(Path.Combine(dataDir, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Options));
        Console.WriteLine($"  {manifest.Rows:N0} metric values · {bytes / 1048576d:N0} MB long Parquet · {sw.Elapsed.TotalSeconds:N0} s");
    }

    // ---------------------------------------------------------------- queries

    public static async Task<BenchRun> RunAsync(string dataDir, int instances, int seed, string label)
    {
        var manifest = Generator.ReadManifest(dataDir);
        string wide = $"read_parquet('{Generator.ToSqlPath(Path.Combine(dataDir, "cpu_wide.parquet"))}')";
        string longRel = $"read_parquet('{Generator.ToSqlPath(Path.Combine(dataDir, "long"))}/**/*.parquet', hive_partitioning = true)";

        long hostCount;
        DateInterval span;
        using (var conn = Generator.Open())
        {
            hostCount = Generator.Scalar(conn, $"SELECT count(DISTINCT host_id) FROM {wide}");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT min(ts), max(ts) FROM {wide}";
            using var r = cmd.ExecuteReader();
            r.Read();
            span = new DateInterval(Instant.FromUtc(r.GetDateTime(0)), Instant.FromUtc(r.GetDateTime(1)));
        }

        var catalog = new InMemoryMetricCatalog(Metrics.Select(m => new MetricDefinition(new MetricId(m), m, new Unit("%"), "mean", [Host], TimeGrain.Instant)));
        var sourceOptions = new DuckDbSourceOptions("Data Source=:memory:", longRel);
        // One source (one database) for the whole run, as a live application holds; "cold" = empty Meridian cache.
        var source = new DuckDbPointSource(sourceOptions, Host);
        MeridianRuntime Fresh() => MeridianRuntime.InMemory(catalog, source);
        // The same data read in TSBS's native wide schema: a column per metric, one scan for all of them.
        var wideSource = new DuckDbPointSource(new DuckDbSourceOptions("Data Source=:memory:", wide, EntityColumn: "host_id",
            MetricColumns: Metrics.ToDictionary(m => m, m => m)), Host);
        MeridianRuntime FreshWide() => MeridianRuntime.InMemory(catalog, wideSource);

        Console.WriteLine($"{manifest.Description}");
        Console.WriteLine($"{instances} random instances per query type (seed {seed}); medians below.");
        Console.WriteLine();

        var rng = new Random(seed);
        var results = new List<ScenarioResult>();
        foreach (var type in Types)
        {
            // +1: the harness's untimed warm-up call consumes one instance.
            var pool = Enumerable.Range(0, instances + 1).Select(_ => RandomInstance(type, rng, hostCount, span)).ToList();

            await CheckAgreement(type, pool[0], Fresh(), longRel);
            await CheckAgreement(type, pool[0], FreshWide(), longRel);

            int i = 0;
            Instance Next() => pool[i++ % pool.Count];
            results.Add(await Runner.Measure($"{type.Name}/sql-wide", $"{type.Name} · SQL (TSBS wide schema)",
                $"{type.Description}. DuckDB over TSBS's native schema: one row per reading, a column per metric.",
                instances, () => SqlAsync(type, Next(), wide, wideSchema: true)));
            i = 0;
            results.Add(await Runner.Measure($"{type.Name}/sql-long", $"{type.Name} · SQL (long)",
                $"{type.Description}. DuckDB over the long layout Meridian reads: one row per metric value.",
                instances, () => SqlAsync(type, Next(), longRel, wideSchema: false)));
            i = 0;
            results.Add(await Runner.Measure($"{type.Name}/meridian-cold", $"{type.Name} · Meridian cold",
                $"{type.Description}. Empty cache; bucketing pushed down, cross-host merge in the engine.",
                instances, () => MeridianAsync(type, Next(), Fresh())));
            i = 0;
            results.Add(await Runner.Measure($"{type.Name}/meridian-cold-wide", $"{type.Name} · Meridian cold (wide schema)",
                $"{type.Description}. Empty cache, reading TSBS's wide schema directly: every metric from one scan.",
                instances, () => MeridianAsync(type, Next(), FreshWide())));

            // Warm: each instance on its own runtime, primed once, then timed.
            var primed = new Dictionary<Instance, MeridianRuntime>();
            foreach (var inst in pool)
            {
                primed[inst] = Fresh();
                await MeridianAsync(type, inst, primed[inst]);
            }
            i = 0;
            results.Add(await Runner.Measure($"{type.Name}/meridian-warm", $"{type.Name} · Meridian warm",
                $"{type.Description}. The same query again: served from Meridian's cache.",
                instances, () => { var inst = Next(); return MeridianAsync(type, inst, primed[inst]); }));
            Console.WriteLine();
        }

        using var dv = Generator.Open();
        return new BenchRun(1, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{label}", label, DateTime.UtcNow, Machine.Commit(),
            Machine.Describe(Generator.DuckDbVersion(dv)), manifest,
            new ReportShape("cpu", (int)hostCount, (int)Math.Round((span.End.UtcDateTime - span.Start.UtcDateTime).TotalDays),
                $"TSBS cpu-only query types × {instances} random instances", manifest.Rows),
            results);
    }

    private static Instance RandomInstance(QueryType type, Random rng, long hostCount, DateInterval span)
    {
        long[] hosts = type.HostCount == 0
            ? [.. Enumerable.Range(0, (int)hostCount).Select(h => (long)h)]
            : [.. Enumerable.Range(0, (int)hostCount).OrderBy(_ => rng.Next()).Take(type.HostCount).Select(h => (long)h).Order()];
        long window = TimeSpan.FromHours(type.Hours).Ticks;
        long latest = span.End.UtcTicks - window;
        long start = span.Start.UtcTicks + (long)(rng.NextDouble() * (latest - span.Start.UtcTicks));
        start -= start % TimeSpan.TicksPerSecond;
        return new Instance(hosts, new DateInterval(new Instant(start), new Instant(start + window)));
    }

    private static IEnumerable<string> MetricsOf(QueryType type) => Metrics.Take(type.MetricCount);

    private static async Task<int> SqlAsync(QueryType type, Instance q, string relation, bool wideSchema)
    {
        string bucket = $"time_bucket(to_microseconds({type.Bucket.Ticks / 10}), ts)";
        string agg = type.Aggregator == Aggregators.Max ? "max" : "avg";
        string hostFilter = type.HostCount == 0 ? "" : $" AND {(wideSchema ? "host_id" : "entity_id")} IN ({string.Join(',', q.Hosts)})";
        string hostCol = wideSchema ? "host_id" : "entity_id";
        string sql = wideSchema
            ? $"SELECT {(type.PerHost ? "host_id, " : "")}{bucket} AS b, {string.Join(", ", MetricsOf(type).Select(m => $"{agg}({m})"))} " +
              $"FROM {relation} WHERE ts >= $s AND ts < $e{hostFilter} GROUP BY ALL ORDER BY ALL"
            : $"SELECT metric, {(type.PerHost ? hostCol + ", " : "")}{bucket} AS b, {agg}(value) FROM {relation} " +
              $"WHERE metric IN ({string.Join(',', MetricsOf(type).Select(m => $"'{m}'"))}) AND ts >= $s AND ts < $e{hostFilter} GROUP BY ALL ORDER BY ALL";

        using var conn = Generator.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter("s", q.Window.Start.UtcDateTime));
        cmd.Parameters.Add(new DuckDBParameter("e", q.Window.End.UtcDateTime));
        using var reader = await cmd.ExecuteReaderAsync();
        int rows = 0;
        while (await reader.ReadAsync()) rows++;
        return rows;
    }

    /// <summary>One Meridian report per metric (reports are single-metric), run as one batch: the engine
    /// fetches every metric in a single source query. Across-host queries bucket per host (pushed down),
    /// then merge hosts per bucket with the same aggregator.</summary>
    private static async Task<int> MeridianAsync(QueryType type, Instance q, MeridianRuntime runtime)
    {
        var views = await runtime.Engine.RunManyAsync([.. MetricsOf(type).Select(m => Spec(type, q, m))], ProjectionOptions.Default);
        return views.Sum(v => v.Series.Sum(s => s.Marks.Count));
    }

    private static PipelineSpec Spec(QueryType type, Instance q, string metric)
    {
        var period = Period.Every(type.Bucket);
        var perHost = Transform.Resample(period, type.Aggregator, GapPolicy.LeaveMissing);
        ITransform[] transforms = type.PerHost
            ? [perHost]
            : [perHost, Transform.Named("merge-hosts", Transform.Rekey(_ => PointKey.Empty)), Transform.Resample(period, type.Aggregator, GapPolicy.LeaveMissing)];
        return PipelineSpec.Create("tsbs", new MetricId(metric), [.. q.Hosts.Select(h => new EntityRef(Host, h))], q.Window,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: type.PerHost ? Host : null), transforms);
    }

    /// <summary>Before timing: Meridian's answer must equal DuckDB's for the same instance.</summary>
    private static async Task CheckAgreement(QueryType type, Instance q, MeridianRuntime runtime, string longRel)
    {
        var metric = MetricsOf(type).First();
        var view = await runtime.Engine.RunAsync(Spec(type, q, metric), ProjectionOptions.Default);
        var meridian = view.Series.SelectMany(s => s.Marks.Select(m => (Series: type.PerHost ? s.Name : "", m.At, Value: Math.Round(m.Value ?? double.NaN, 9))))
            .OrderBy(x => x.Series, StringComparer.Ordinal).ThenBy(x => x.At).ToList();

        string bucket = $"time_bucket(to_microseconds({type.Bucket.Ticks / 10}), ts)";
        string agg = type.Aggregator == Aggregators.Max ? "max" : "avg";
        string hostFilter = type.HostCount == 0 ? "" : $" AND entity_id IN ({string.Join(',', q.Hosts)})";
        using var conn = Generator.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {(type.PerHost ? "'host ' || entity_id" : "''")} AS series, epoch_ms({bucket}) AS at, round({agg}(value), 9) " +
            $"FROM {longRel} WHERE metric = '{metric}' AND ts >= $s AND ts < $e{hostFilter} GROUP BY 1, 2";
        cmd.Parameters.Add(new DuckDBParameter("s", q.Window.Start.UtcDateTime));
        cmd.Parameters.Add(new DuckDBParameter("e", q.Window.End.UtcDateTime));
        var sql = new List<(string Series, long? At, double Value)>();
        using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) sql.Add((r.GetString(0), r.GetInt64(1), r.GetDouble(2)));
        }
        sql = [.. sql.OrderBy(x => x.Series, StringComparer.Ordinal).ThenBy(x => x.At)];

        bool same = meridian.Count == sql.Count && meridian.Zip(sql).All(p => p.First.Series == p.Second.Series && p.First.At == p.Second.At
            && Math.Abs(p.First.Value - p.Second.Value) <= 1e-6 * Math.Max(1, Math.Abs(p.Second.Value)));
        Console.WriteLine($"{type.Name}: Meridian matches DuckDB on a sample instance — {(same ? "yes" : "NO")} ({sql.Count} values)");
        if (!same) throw new InvalidOperationException($"{type.Name}: Meridian and DuckDB disagree.");
    }

    public static string DefaultGenerator()
    {
        var exe = OperatingSystem.IsWindows() ? "tsbs_generate_data.exe" : "tsbs_generate_data";
        var gopath = Environment.GetEnvironmentVariable("GOPATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go");
        return Path.Combine(gopath, "bin", exe);
    }
}
