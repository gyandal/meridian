using DuckDB.NET.Data;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.DuckDb;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Sources.DuckDb.Tests;

/// <summary>
/// A small DuckDB file with the awkward cases. <c>datapoints</c> holds UTC readings every 5 hours from
/// January to May 2025 — across the US (9 Mar), UK (30 Mar) and Sydney (6 Apr) DST changes — with dropped
/// rows, NULL values, a multi-week hole, an all-NULL week, leading all-NULL days, and a second metric.
/// <c>wellness</c> holds calendar dates (DATE). <c>legacy</c> holds London wall-clock times, including the
/// hour that happens twice and the hour that never happens.
/// </summary>
public sealed class DuckDbFixture : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"meridian-{Guid.NewGuid():N}.duckdb");
    public string ConnectionString => $"Data Source={FilePath}";

    public DuckDbFixture()
    {
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE datapoints AS
            WITH raw AS (
                SELECT e.entity_id, h.h, hash(e.entity_id * 1000003 + h.h) AS hv
                FROM range(1, 4) AS e(entity_id), range(0, 2880, 5) AS h(h)   -- 120 days, every 5 hours
            )
            SELECT entity_id,
                   m.metric,
                   TIMESTAMP '2025-01-01' + to_hours(h) AS ts,
                   CASE
                       WHEN hv % 11 = 0 THEN NULL
                       WHEN entity_id = 1 AND h < 48 THEN NULL                       -- leading all-NULL days
                       WHEN entity_id = 3 AND h BETWEEN 60 * 24 AND 67 * 24 THEN NULL -- an all-NULL week
                       ELSE (hv % 1000) / 10.0 + CASE WHEN m.metric = 'hr' THEN 1000 ELSE 0 END
                   END AS value
            FROM raw, (VALUES ('load'), ('hr')) AS m(metric)
            WHERE hv % 7 <> 0
              AND NOT (entity_id = 2 AND h BETWEEN 30 * 24 AND 45 * 24);         -- a multi-week hole

            CREATE TABLE wellness AS
            SELECT e.entity_id, 'wellness' AS metric, DATE '2025-01-01' + CAST(d.d AS INTEGER) AS ts,
                   CAST(hash(e.entity_id * 7919 + d.d) % 10 AS DOUBLE) AS value
            FROM range(1, 4) AS e(entity_id), range(0, 120) AS d(d)
            WHERE hash(e.entity_id * 31 + d.d) % 6 <> 0;

            -- The same readings in the wide layout (a column per metric), with extra NULLs in hr; and that wide
            -- table unpivoted back to the long layout, NULLs kept, as the reference it must match.
            CREATE TABLE wide AS
            SELECT entity_id, ts,
                   max(value) FILTER (WHERE metric = 'load') AS load,
                   CASE WHEN hash(entity_id * 131 + epoch(ts)) % 5 = 0 THEN NULL
                        ELSE max(value) FILTER (WHERE metric = 'hr') END AS hr
            FROM datapoints GROUP BY entity_id, ts;
            CREATE VIEW wide_as_long AS SELECT * FROM wide UNPIVOT INCLUDE NULLS (value FOR metric IN (load, hr));

            -- Goals with the metadata dashboards group by: venue (sometimes unknown) and competition.
            CREATE TABLE goals AS
            SELECT e.entity_id, 'goals' AS metric, TIMESTAMP '2025-01-01' + to_hours(m.m * 37) AS ts,
                   CAST(hash(e.entity_id * 11 + m.m) % 3 AS DOUBLE) AS value,
                   CASE WHEN hash(e.entity_id * 5 + m.m) % 7 = 0 THEN NULL
                        WHEN hash(e.entity_id * 13 + m.m) % 2 = 0 THEN 'Home' ELSE 'Away' END AS venue,
                   CASE hash(e.entity_id * 3 + m.m) % 3 WHEN 0 THEN 'League' WHEN 1 THEN 'Cup' ELSE 'Europe' END AS competition
            FROM range(1, 4) AS e(entity_id), range(0, 80) AS m(m);
            CREATE TABLE goals_wide AS SELECT entity_id, ts, value AS goals, venue, competition FROM goals;

            -- Appearances: one minutes row per match; goal rows only when a goal was scored (events).
            CREATE TABLE apps AS
            WITH m AS (
                SELECT e.entity_id, TIMESTAMP '2025-01-04 15:00:00' + to_days(g.g * 7) AS ts,
                       CASE WHEN hash(e.entity_id * 17 + g.g) % 2 = 0 THEN 'Home' ELSE 'Away' END AS venue,
                       CAST(10 + hash(e.entity_id * 29 + g.g) % 81 AS DOUBLE) AS minutes,
                       CAST(hash(e.entity_id * 31 + g.g) % 4 AS DOUBLE) - 1 AS goals
                FROM range(1, 4) AS e(entity_id), range(0, 22) AS g(g)
                WHERE hash(e.entity_id * 7 + g.g) % 5 <> 0 AND NOT (e.entity_id = 3 AND g.g BETWEEN 9 AND 14)
            )
            SELECT entity_id, 'minutes' AS metric, ts, minutes AS value, venue FROM m
            UNION ALL
            SELECT entity_id, 'goals', ts, goals, venue FROM m WHERE goals > 0;

            CREATE TABLE typed (entity_id INTEGER, metric VARCHAR, ts TIMESTAMP, value DECIMAL(10, 2));
            INSERT INTO typed VALUES (1, 'typed', TIMESTAMP '2025-02-01 10:00', 12.50), (1, 'typed', TIMESTAMP '2025-02-02 10:00', 7.25);

            CREATE TABLE legacy (entity_id BIGINT, metric VARCHAR, ts TIMESTAMP, value DOUBLE);
            INSERT INTO legacy VALUES
                (1, 'legacy', TIMESTAMP '2025-03-30 00:30:00', 1),   -- before the spring gap
                (1, 'legacy', TIMESTAMP '2025-03-30 01:30:00', 2),   -- never happens in London
                (1, 'legacy', TIMESTAMP '2025-07-01 12:00:00', 3),   -- summer: UTC+1
                (1, 'legacy', TIMESTAMP '2025-10-26 01:30:00', 4);   -- happens twice in London

            -- New York wall-clock readings around both 2025 DST changes, as a system that logs local time
            -- would store them: the skipped spring hour has rows, and the repeated autumn hour is logged twice.
            -- Spacing avoids exact ties after conversion ("last" among identical instants is unspecified):
            -- March readings are 7 minutes apart, so a skipped time shifted forward an hour never lands on a
            -- real one; the autumn second pass is offset by 5 minutes.
            CREATE TABLE legacy_ny AS
            WITH walls AS (
                SELECT e.entity_id, TIMESTAMP '2025-03-01' + to_minutes(m.m * 7) AS ts, m.m
                FROM range(1, 3) AS e(entity_id), range(0, 20 * 1440 // 7) AS m(m)
                UNION ALL
                SELECT e.entity_id, TIMESTAMP '2025-10-20' + to_minutes(m.m * 10), m.m + 100000
                FROM range(1, 3) AS e(entity_id), range(0, 26 * 144) AS m(m)
                UNION ALL
                SELECT e.entity_id, TIMESTAMP '2025-11-02 01:05' + to_minutes(m.m * 10), m.m + 200000
                FROM range(1, 3) AS e(entity_id), range(0, 6) AS m(m)
            )
            SELECT entity_id, 'legacy-ny' AS metric, ts,
                   CASE WHEN hash(entity_id * 131 + m) % 13 = 0 THEN NULL
                        ELSE CAST(hash(entity_id * 977 + m) % 500 AS DOUBLE) / 10 END AS value
            FROM walls
            WHERE hash(entity_id * 17 + m) % 9 <> 0;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The datapoints table as a Parquet file, for sources that query files in place.</summary>
    public string ParquetPath { get; } = Path.Combine(Path.GetTempPath(), $"meridian-{Guid.NewGuid():N}.parquet");

    public void ExportParquet()
    {
        if (File.Exists(ParquetPath)) return;
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"COPY datapoints TO '{ParquetPath.Replace(Path.DirectorySeparatorChar, '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        try { File.Delete(FilePath); File.Delete(FilePath + ".wal"); File.Delete(ParquetPath); } catch (IOException) { }
    }
}

public class DuckDbSourceTests(DuckDbFixture db) : IClassFixture<DuckDbFixture>
{
    private static readonly DimensionId Athlete = new("athlete");
    private static readonly MetricDefinition LoadDef = new(new MetricId("load"), "Load", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition WellnessDef = new(new MetricId("wellness"), "Wellness", new Unit("score"), "mean", [Athlete], TimeGrain.Daily, TimeKind.Local);
    private static readonly MetricDefinition LegacyDef = new(new MetricId("legacy"), "Legacy", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition LegacyNyDef = new(new MetricId("legacy-ny"), "Legacy NY", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition HrDef = new(new MetricId("hr"), "HR", new Unit("bpm"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly InMemoryMetricCatalog Catalog = new([LoadDef, HrDef, WellnessDef, LegacyDef, LegacyNyDef]);

    // Starts mid-day and ends mid-week so partial first/last buckets are exercised on both paths.
    private static readonly DateInterval Timeframe = new(
        Instant.FromUtc(new DateTime(2025, 1, 2, 6, 0, 0, DateTimeKind.Utc)),
        Instant.FromUtc(new DateTime(2025, 4, 20, 0, 0, 0, DateTimeKind.Utc)));

    private static readonly string[] Zones = ["UTC", "Europe/London", "America/New_York", "Australia/Sydney"];

    private DuckDbPointSource Source(string table = "datapoints", StoredTime? time = null) =>
        new(new DuckDbSourceOptions(db.ConnectionString, Relation: table, StoredTime: time), Athlete);

    private static ProjectionOptions In(string zone, DayOfWeek weekStart = DayOfWeek.Monday) =>
        ProjectionOptions.Default with { Calendar = CalendarContext.For(zone, weekStart) };

    [Fact]
    public async Task Raw_fetch_returns_only_the_requested_metric_entities_and_timeframe()
    {
        var block = await Source().FetchAsync(LoadDef, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 3)], Timeframe);

        long expected = db.Scalar("""
            SELECT count(*) FROM datapoints
            WHERE metric = 'load' AND entity_id IN (1, 3)
              AND ts >= TIMESTAMP '2025-01-02 06:00:00' AND ts < TIMESTAMP '2025-04-20'
            """);
        Assert.Equal(expected, block.Count);
        Assert.Equal(TimeKind.Instant, block.Time.Kind);

        var ids = new HashSet<long>();
        for (int i = 0; i < block.Count; i++)
        {
            Assert.True(block.Keys[i].TryGet(Athlete, out var part));
            ids.Add(part.Numeric);
            Assert.InRange(block.AtTicks[i], Timeframe.Start.UtcTicks, Timeframe.End.UtcTicks - 1);
            if ((block.Flags[i] & MeasureFlags.Missing) == 0) Assert.True(block.Values[i] < 1000, "hr rows leaked in");
        }
        Assert.Equal([1L, 3L], ids.Order());
    }

    public static TheoryData<string, string, string, GapPolicy> PushdownCases()
    {
        var data = new TheoryData<string, string, string, GapPolicy>();
        foreach (var zone in Zones)
        foreach (var period in new[] { "15m", "hour", "day", "week", "month" })
        foreach (var agg in new[] { "mean", "sum", "min", "max", "count", "median", "last" })
        foreach (var gap in Enum.GetValues<GapPolicy>())
        {
            data.Add(zone, period, agg, gap);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PushdownCases))]
    public async Task Pushed_down_rollup_matches_the_in_engine_resample(string zone, string period, string aggregator, GapPolicy gap)
    {
        Assert.True(Aggregators.TryResolve(aggregator, out var agg));
        await AssertParity(LoadDef, Source(), Spec(LoadDef, PeriodNamed(period), agg, gap), In(zone));
    }

    [Theory]
    [InlineData("UTC", DayOfWeek.Sunday)]
    [InlineData("Europe/London", DayOfWeek.Sunday)]
    [InlineData("America/New_York", DayOfWeek.Saturday)]
    [InlineData("Australia/Sydney", DayOfWeek.Wednesday)]
    public async Task Weeks_starting_on_any_day_push_down(string zone, DayOfWeek weekStart)
    {
        await AssertParity(LoadDef, Source(), Spec(LoadDef, Period.Week, Aggregators.Mean, GapPolicy.ZeroFill), In(zone, weekStart));
    }

    [Fact]
    public async Task The_zone_decides_the_buckets_not_just_the_labels()
    {
        var spec = Spec(LoadDef, Period.Day, Aggregators.Count, GapPolicy.LeaveMissing);
        var utc = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunAsync(spec, In("UTC"));
        var sydney = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunAsync(spec, In("Australia/Sydney"));

        // Same readings, different days: the per-day counts differ because the day boundaries differ.
        Assert.NotEqual(Counts(utc), Counts(sydney));
        Assert.Equal(TimeKind.Local, sydney.Axes[0].Time);
        Assert.Equal("Australia/Sydney", sydney.Axes[0].TimeZone);
        Assert.Equal("day", sydney.Axes[0].Grain);

        static double[] Counts(ChartView v) => [.. v.Series[0].Marks.Select(m => m.Value ?? 0)];
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Pacific/Kiritimati")] // UTC+14: the zone most likely to shift a date if anything did
    public async Task Calendar_dates_are_never_shifted_by_the_report_zone(string zone)
    {
        var dates = new DateInterval(Instant.FromUtc(new DateTime(2025, 3, 1)), Instant.FromUtc(new DateTime(2025, 3, 8)));
        var spec = PipelineSpec.Create("test", WellnessDef.Id, [new EntityRef(Athlete, 1)], dates,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete));

        var view = await MeridianRuntime.InMemory(Catalog, Source("wellness", StoredTime.Local)).Engine.RunAsync(spec, In(zone));

        var labels = view.Series[0].Marks.Select(m => m.Label).ToList();
        Assert.All(labels, l => Assert.Matches(@"^2025-03-0[1-7]$", l));
        Assert.Equal(labels.Order(), labels);
        Assert.Equal(TimeKind.Local, view.Axes[0].Time);
        Assert.Null(view.Axes[0].TimeZone); // as given: no zone drew these
    }

    [Theory]
    [InlineData("UTC", "week", "mean")]
    [InlineData("Europe/London", "month", "sum")]
    [InlineData("Pacific/Kiritimati", "day", "last")]
    public async Task Calendar_date_rollups_push_down_and_match_the_engine(string zone, string period, string aggregator)
    {
        Assert.True(Aggregators.TryResolve(aggregator, out var agg));
        var dates = new DateInterval(Instant.FromUtc(new DateTime(2025, 1, 1)), Instant.FromUtc(new DateTime(2025, 5, 1)));
        await AssertParity(WellnessDef, Source("wellness", StoredTime.Local), Spec(WellnessDef, PeriodNamed(period), agg, GapPolicy.ZeroFill, dates), In(zone));
    }

    [Theory]
    [InlineData(AmbiguousTime.Earlier, "2025-10-26T00:30:00Z")] // first 01:30, still on BST
    [InlineData(AmbiguousTime.Later, "2025-10-26T01:30:00Z")]   // second 01:30, back on GMT
    public async Task Wall_clock_data_resolves_the_repeated_autumn_hour_as_configured(AmbiguousTime ambiguous, string expectedUtc)
    {
        var block = await Legacy(new LocalTimeResolution(ambiguous));
        Assert.Contains(Instant.FromUtc(DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal)).UtcTicks, block.AtTicks.ToArray());
    }

    [Fact]
    public async Task Wall_clock_data_shifts_the_missing_spring_hour_forward_by_default()
    {
        var block = await Legacy(LocalTimeResolution.Default);
        var utc = block.AtTicks.ToArray().Select(t => new DateTime(t, DateTimeKind.Utc)).ToList();

        Assert.Contains(new DateTime(2025, 3, 30, 0, 30, 0), utc);  // 00:30 GMT
        Assert.Contains(new DateTime(2025, 3, 30, 1, 30, 0), utc);  // "01:30" never happened → 02:30 BST
        Assert.Contains(new DateTime(2025, 7, 1, 11, 0, 0), utc);   // 12:00 BST
        Assert.Equal(TimeKind.Instant, block.Time.Kind);
    }

    [Theory]
    [InlineData(AmbiguousTime.Reject, SkippedTime.ShiftForward)]
    [InlineData(AmbiguousTime.Earlier, SkippedTime.Reject)]
    public async Task Wall_clock_data_can_reject_times_that_are_ambiguous_or_missing(AmbiguousTime ambiguous, SkippedTime skipped)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Legacy(new LocalTimeResolution(ambiguous, skipped)));
    }

    [Fact]
    public async Task Wall_clock_timeframes_are_applied_to_the_converted_instants()
    {
        // 00:00–00:59 UTC on 30 March: only the 00:30 GMT row, not the wall-clock "01:30" that became 01:30 UTC.
        var hour = new DateInterval(Instant.FromUtc(new DateTime(2025, 3, 30, 0, 0, 0)), Instant.FromUtc(new DateTime(2025, 3, 30, 1, 0, 0)));
        var block = await Source("legacy", StoredTime.InZone("Europe/London")).FetchAsync(LegacyDef, [new EntityRef(Athlete, 1)], hour);
        Assert.Equal(1, block.Count);
        Assert.Equal(1.0, block.Values[0]);
    }

    public static TheoryData<AmbiguousTime, string, string, string> WallClockCases()
    {
        var data = new TheoryData<AmbiguousTime, string, string, string>();
        foreach (var ambiguous in new[] { AmbiguousTime.Earlier, AmbiguousTime.Later })
        foreach (var zone in new[] { "UTC", "America/New_York", "Europe/London" })
        foreach (var period in new[] { "15m", "hour", "day", "week", "month" })
        foreach (var agg in new[] { "mean", "sum", "min", "max", "count", "median", "last" })
        {
            data.Add(ambiguous, zone, period, agg);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WallClockCases))]
    public async Task Wall_clock_data_pushes_down_with_the_same_dst_resolution_as_the_engine(
        AmbiguousTime ambiguous, string zone, string period, string aggregator)
    {
        Assert.True(Aggregators.TryResolve(aggregator, out var agg));
        var source = Source("legacy_ny", StoredTime.InZone("America/New_York", new LocalTimeResolution(ambiguous)));
        var year = new DateInterval(Instant.FromUtc(new DateTime(2025, 1, 1, 7, 30, 0)), Instant.FromUtc(new DateTime(2025, 12, 31)));
        await AssertParity(LegacyNyDef, source, Spec(LegacyNyDef, PeriodNamed(period), agg, GapPolicy.LeaveMissing, year), In(zone));
    }

    [Theory]
    [InlineData(AmbiguousTime.Earlier)]
    [InlineData(AmbiguousTime.Later)]
    public async Task Wall_clock_pushdown_cuts_the_timeframe_at_the_exact_instant_inside_the_repeated_hour(AmbiguousTime ambiguous)
    {
        // Ends at 05:30 UTC on 2 Nov 2025: 01:30 on the first pass through New York's repeated hour.
        var toMidRepeat = new DateInterval(Instant.FromUtc(new DateTime(2025, 10, 25)), Instant.FromUtc(new DateTime(2025, 11, 2, 5, 30, 0)));
        var source = Source("legacy_ny", StoredTime.InZone("America/New_York", new LocalTimeResolution(ambiguous)));
        await AssertParity(LegacyNyDef, source, Spec(LegacyNyDef, Period.Day, Aggregators.Count, GapPolicy.LeaveMissing, toMidRepeat), In("America/New_York"));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/London")]
    public async Task Season_rollups_fall_back_to_a_raw_fetch_and_stay_correct(string zone)
    {
        // Domain seasons have no SQL equivalent.
        var wide = new DateInterval(Instant.FromUtc(new DateTime(2024, 1, 1)), Instant.FromUtc(new DateTime(2026, 1, 1)));
        var spec = Spec(LoadDef, Period.Season, Aggregators.Mean, GapPolicy.LeaveMissing, wide);

        var counting = new CountingSource(Source());
        var result = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunAsync(spec, In(zone));
        var expected = await MeridianRuntime.InMemory(Catalog, new RawOnly(Source())).Engine.RunAsync(spec, In(zone));

        Assert.Equal(0, counting.Rollups);
        Assert.Equal(1, counting.Raws);
        AssertSameView(expected, result);
    }

    [Theory]
    [InlineData(AmbiguousTime.Reject, SkippedTime.ShiftForward)]
    [InlineData(AmbiguousTime.Earlier, SkippedTime.Reject)]
    public void Wall_clock_policies_that_reject_times_are_not_pushed_down(AmbiguousTime ambiguous, SkippedTime skipped)
    {
        // Rejecting a time is an error only the engine can raise, so SQL must not quietly bucket it.
        var source = Source("legacy_ny", StoredTime.InZone("America/New_York", new LocalTimeResolution(ambiguous, skipped)));
        Assert.False(source.CanRollup(LegacyNyDef, new SourceRollup(Period.Day, Aggregators.Mean, CalendarContext.Default)));
    }

    [Fact]
    public async Task An_in_memory_source_over_parquet_serves_concurrent_reports_from_one_database()
    {
        lock (db) db.ExportParquet();
        using var source = new DuckDbPointSource(new DuckDbSourceOptions("Data Source=:memory:",
            Relation: $"read_parquet('{db.ParquetPath.Replace(Path.DirectorySeparatorChar, '/')}')"), Athlete);
        var fileBacked = Source();

        // Ten reports at once through one source: each gets its own connection to the shared database.
        var specs = Enumerable.Range(0, 10).Select(i => Spec(LoadDef, i % 2 == 0 ? Period.Week : Period.Day, Aggregators.Mean, GapPolicy.LeaveMissing)).ToList();
        var fromParquet = await Task.WhenAll(specs.Select(s => MeridianRuntime.InMemory(Catalog, source).Engine.RunAsync(s, ProjectionOptions.Default)));
        var fromTable = await Task.WhenAll(specs.Select(s => MeridianRuntime.InMemory(Catalog, fileBacked).Engine.RunAsync(s, ProjectionOptions.Default)));

        for (int i = 0; i < specs.Count; i++) AssertSameView(fromTable[i], fromParquet[i]);
    }

    private static PipelineSpec[] LoadAndHr(IPeriod? period = null, long[]? entities = null, DateInterval? timeframe = null)
    {
        PipelineSpec For(MetricDefinition m) => PipelineSpec.Create("test", m.Id,
            [.. (entities ?? [1, 2, 3]).Select(id => new EntityRef(Athlete, id))], timeframe ?? Timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
            period is null ? [] : [Transform.Resample(period, Aggregators.Mean, GapPolicy.ZeroFill)]);
        return [For(LoadDef), For(HrDef)];
    }

    [Theory]
    [InlineData(true)]   // weekly means pushed down
    [InlineData(false)]  // raw rows
    public async Task A_cold_multi_metric_request_is_one_source_query_and_matches_single_reports(bool pushdown)
    {
        var specs = LoadAndHr(pushdown ? Period.Week : null);
        var counting = new CountingSource(Source());

        var batched = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunManyAsync(specs, In("Europe/London"));

        var batch = Assert.Single(counting.Batches);
        Assert.Equal(["hr", "load"], batch.Metrics);
        Assert.Equal(pushdown, batch.Rollup);
        Assert.Equal(0, counting.Raws + counting.Rollups); // nothing went the one-metric-at-a-time way

        for (int i = 0; i < specs.Length; i++)
        {
            var alone = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunAsync(specs[i], In("Europe/London"));
            AssertSameView(alone, batched[i]);
        }
    }

    [Fact]
    public async Task A_warm_multi_metric_request_touches_no_source()
    {
        var counting = new CountingSource(Source());
        var engine = MeridianRuntime.InMemory(Catalog, counting).Engine;
        await engine.RunManyAsync(LoadAndHr(Period.Week), ProjectionOptions.Default);
        counting.Batches.Clear();

        await engine.RunManyAsync(LoadAndHr(Period.Week), ProjectionOptions.Default);

        Assert.Empty(counting.Batches);
        Assert.Equal(0, counting.Raws + counting.Rollups);
    }

    [Fact]
    public async Task A_partly_cached_request_fetches_only_what_is_missing_grouped_by_missing_set()
    {
        var counting = new CountingSource(Source());
        var engine = MeridianRuntime.InMemory(Catalog, counting).Engine;
        // Load is cached for athlete 1 only; HR not at all.
        await engine.RunAsync(LoadAndHr(Period.Week, entities: [1])[0], ProjectionOptions.Default);
        counting.Batches.Clear();

        var views = await engine.RunManyAsync(LoadAndHr(Period.Week), ProjectionOptions.Default);

        // Load misses {2, 3}; HR misses {1, 2, 3}: two different gaps, so two calls — and nothing refetched.
        Assert.Equal(2, counting.Batches.Count);
        Assert.Contains(counting.Batches, b => b.Metrics.SequenceEqual(["load"]) && b.Entities.SequenceEqual([2L, 3L]));
        Assert.Contains(counting.Batches, b => b.Metrics.SequenceEqual(["hr"]) && b.Entities.SequenceEqual([1L, 2L, 3L]));

        var fresh = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunManyAsync(LoadAndHr(Period.Week), ProjectionOptions.Default);
        for (int i = 0; i < views.Count; i++) AssertSameView(fresh[i], views[i]);
    }

    [Fact]
    public async Task Reports_over_different_timeframes_are_not_batched_together()
    {
        var early = new DateInterval(Timeframe.Start, Instant.FromUtc(new DateTime(2025, 2, 1)));
        var specs = new[] { LoadAndHr(Period.Week)[0], LoadAndHr(Period.Week, timeframe: early)[1] };
        var counting = new CountingSource(Source());

        var views = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunManyAsync(specs, ProjectionOptions.Default);

        Assert.Empty(counting.Batches);       // each group has one metric: served the ordinary way
        Assert.Equal(2, counting.Rollups);
        Assert.Equal(2, views.Count);
    }

    [Fact]
    public async Task A_source_without_batching_still_runs_many_reports()
    {
        var specs = LoadAndHr(Period.Month);
        var views = await MeridianRuntime.InMemory(Catalog, new RawOnly(Source())).Engine.RunManyAsync(specs, ProjectionOptions.Default);
        var batched = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunManyAsync(specs, ProjectionOptions.Default);
        for (int i = 0; i < specs.Length; i++) AssertSameView(views[i], batched[i]);
    }

    private DuckDbPointSource Wide() => new(new DuckDbSourceOptions(db.ConnectionString, Relation: "wide",
        MetricColumns: new Dictionary<string, string> { ["load"] = "load", ["hr"] = "hr" }), Athlete);

    public static TheoryData<string, string, string> WideCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var zone in new[] { "UTC", "Europe/London" })
        foreach (var period in new[] { "raw", "hour", "day", "week", "month" })
        foreach (var agg in new[] { "mean", "sum", "min", "max", "count", "median", "last" })
        {
            if (period == "raw" && agg != "mean") continue; // raw rows have no aggregator
            data.Add(zone, period, agg);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WideCases))]
    public async Task A_wide_table_gives_the_same_reports_as_its_long_equivalent(string zone, string period, string aggregator)
    {
        Assert.True(Aggregators.TryResolve(aggregator, out var agg));
        PipelineSpec For(MetricDefinition m) => PipelineSpec.Create("test", m.Id,
            [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], Timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
            period == "raw" ? [] : [Transform.Resample(PeriodNamed(period), agg, GapPolicy.LeaveMissing)]);
        PipelineSpec[] specs = [For(LoadDef), For(HrDef)];

        var counting = new CountingSource(Wide());
        var wide = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunManyAsync(specs, In(zone));
        var longForm = await MeridianRuntime.InMemory(Catalog, Source("wide_as_long")).Engine.RunManyAsync(specs, In(zone));

        var batch = Assert.Single(counting.Batches); // both metrics from one scan of the wide table
        Assert.Equal(["hr", "load"], batch.Metrics);
        for (int i = 0; i < specs.Length; i++) AssertSameView(longForm[i], wide[i]);
    }

    [Fact]
    public async Task A_wide_source_explains_a_metric_it_has_no_column_for()
    {
        var wide = Wide();
        Assert.False(wide.CanRollup(WellnessDef, new SourceRollup(Period.Day, Aggregators.Mean, CalendarContext.Default)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wide.FetchAsync(WellnessDef, [new EntityRef(Athlete, 1)], Timeframe));
        Assert.Contains("MetricColumns", error.Message);
    }

    private static readonly DimensionId Venue = new("venue");
    private static readonly DimensionId Competition = new("competition");
    private static readonly MetricDefinition GoalsDef = new(new MetricId("goals"), "Goals", new Unit(""), "sum", [Athlete, Venue, Competition], TimeGrain.Instant);
    private static readonly InMemoryMetricCatalog GoalsCatalog = new([GoalsDef]);
    private static readonly Dictionary<string, string> GoalDimensions = new() { ["venue"] = "venue", ["competition"] = "competition" };
    private static readonly DateInterval Season = new(Instant.FromUtc(new DateTime(2025, 1, 1)), Instant.FromUtc(new DateTime(2025, 6, 1)));

    private DuckDbPointSource GoalsSource(bool wide = false) => wide
        ? new(new DuckDbSourceOptions(db.ConnectionString, Relation: "goals_wide",
            MetricColumns: new Dictionary<string, string> { ["goals"] = "goals" }, DimensionColumns: GoalDimensions), Athlete)
        : new(new DuckDbSourceOptions(db.ConnectionString, Relation: "goals", DimensionColumns: GoalDimensions), Athlete);

    private static PipelineSpec GoalsReport(ViewSpec view, params ITransform[] transforms) => PipelineSpec.Create(
        "club", GoalsDef.Id, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], Season, view, transforms);

    private static Dictionary<string, double?> Totals(ChartView view) =>
        view.Series.SelectMany(s => s.Marks).ToDictionary(m => m.Label, m => m.Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Goals_by_venue_come_from_the_venue_column(bool wide)
    {
        var report = GoalsReport(new ViewSpec(ChartKind.Column, AxisSource.Category(Venue)), Transform.Total(Aggregators.Sum, Venue))
            .WithDimensions(Venue);

        var view = await MeridianRuntime.InMemory(GoalsCatalog, GoalsSource(wide)).Engine.RunAsync(report, ProjectionOptions.Default);

        long home = db.Scalar("SELECT sum(value) FROM goals WHERE venue = 'Home' AND ts < TIMESTAMP '2025-06-01'");
        long away = db.Scalar("SELECT sum(value) FROM goals WHERE venue = 'Away' AND ts < TIMESTAMP '2025-06-01'");
        long unknown = db.Scalar("SELECT sum(value) FROM goals WHERE venue IS NULL AND ts < TIMESTAMP '2025-06-01'");
        var totals = Totals(view);
        Assert.Equal(home, totals["Home"]);
        Assert.Equal(away, totals["Away"]);
        Assert.Equal(unknown, totals[""]); // an unknown venue is the empty category, not dropped
    }

    [Fact]
    public async Task The_same_metric_grouped_different_ways_shares_one_fetch()
    {
        var counting = new CountingSource(GoalsSource());
        var engine = MeridianRuntime.InMemory(GoalsCatalog, counting).Engine;

        var views = await engine.RunManyAsync(
        [
            GoalsReport(new ViewSpec(ChartKind.Column, AxisSource.Category(Athlete)), Transform.Total(Aggregators.Sum, Athlete)),
            GoalsReport(new ViewSpec(ChartKind.Column, AxisSource.Category(Venue)), Transform.Total(Aggregators.Sum, Venue)).WithDimensions(Venue),
            GoalsReport(new ViewSpec(ChartKind.Column, AxisSource.Category(Competition)), Transform.Total(Aggregators.Sum, Competition)).WithDimensions(Competition),
        ], ProjectionOptions.Default);

        Assert.Equal(1, counting.Raws); // one fetch carries every dimension; each chart keeps what it needs
        Assert.Equal(3, views[0].Series[0].Marks.Count);                          // three players
        Assert.Equal(["", "Away", "Home"], Totals(views[1]).Keys.Order());         // venues
        Assert.Equal(["Cup", "Europe", "League"], Totals(views[2]).Keys.Order());  // competitions
        Assert.Equal(Totals(views[0]).Values.Sum(), Totals(views[1]).Values.Sum()); // same goals, sliced differently
    }

    [Theory]
    [InlineData("UTC", false)]
    [InlineData("Europe/London", false)]
    [InlineData("Europe/London", true)]
    public async Task Monthly_goals_by_venue_push_down_and_match_the_engine(string zone, bool wide)
    {
        var report = GoalsReport(new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Venue),
            Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.ZeroFill), Transform.GroupBy(Aggregators.Sum, Venue))
            .WithDimensions(Venue);

        var counting = new CountingSource(GoalsSource(wide));
        var viaSql = await MeridianRuntime.InMemory(GoalsCatalog, counting).Engine.RunAsync(report, In(zone));
        var inEngine = await MeridianRuntime.InMemory(GoalsCatalog, new RawOnly(GoalsSource(wide))).Engine.RunAsync(report, In(zone));

        Assert.Equal(1, counting.Rollups);
        Assert.Equal(3, viaSql.Series.Count); // Home, Away, unknown
        AssertSameView(inEngine, viaSql);
    }

    [Fact]
    public async Task Dimensions_a_report_does_not_declare_are_folded_away()
    {
        var report = GoalsReport(new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
            Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing));
        var withMetadata = await MeridianRuntime.InMemory(GoalsCatalog, new RawOnly(GoalsSource())).Engine.RunAsync(report, ProjectionOptions.Default);
        var withoutMetadata = await MeridianRuntime.InMemory(GoalsCatalog, new RawOnly(
            new DuckDbPointSource(new DuckDbSourceOptions(db.ConnectionString, Relation: "goals"), Athlete))).Engine.RunAsync(report, ProjectionOptions.Default);

        AssertSameView(withoutMetadata, withMetadata); // one monthly total per player, not split by venue
    }

    [Fact]
    public async Task A_report_can_only_keep_dimensions_its_metric_declares()
    {
        var report = GoalsReport(new ViewSpec(ChartKind.Line, AxisSource.Time)).WithDimensions(new DimensionId("weather"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            MeridianRuntime.InMemory(GoalsCatalog, GoalsSource()).Engine.RunAsync(report, ProjectionOptions.Default));
        Assert.Contains("weather", error.Message);
    }

    [Fact]
    public void A_source_that_cannot_read_a_dimension_declines_to_group_by_it()
    {
        var blind = new DuckDbPointSource(new DuckDbSourceOptions(db.ConnectionString, Relation: "goals"), Athlete);
        var byVenue = new SourceRollup(Period.Month, Aggregators.Sum, CalendarContext.Default, [Venue]);
        Assert.False(blind.CanRollup(GoalsDef, byVenue));
        Assert.True(GoalsSource().CanRollup(GoalsDef, byVenue));
    }

    private static readonly MetricDefinition AppGoals = new(new MetricId("goals"), "Goals", new Unit(""), "sum", [Athlete, Venue], TimeGrain.Instant);
    private static readonly MetricDefinition AppMinutes = new(new MetricId("minutes"), "Minutes", new Unit("min"), "sum", [Athlete, Venue], TimeGrain.Instant);
    private static readonly MetricDefinition GoalsPer90 = MetricDefinition.Ratio(
        new MetricId("goals-per-90"), "Goals per 90", new Unit("/90"), AppGoals.Id, AppMinutes.Id, 90, [Athlete, Venue]);
    private static readonly InMemoryMetricCatalog AppCatalog = new([AppGoals, AppMinutes, GoalsPer90]);
    private static readonly DateInterval FirstHalf = new(Instant.FromUtc(new DateTime(2025, 1, 1)), Instant.FromUtc(new DateTime(2025, 7, 1)));

    private DuckDbPointSource AppSource() =>
        new(new DuckDbSourceOptions(db.ConnectionString, Relation: "apps", DimensionColumns: new Dictionary<string, string> { ["venue"] = "venue" }), Athlete);

    private static PipelineSpec AppReport(MetricId metric, ViewSpec view, params ITransform[] transforms) => PipelineSpec.Create(
        "club", metric, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], FirstHalf, view, transforms);

    private double SqlRatio(string where) =>
        db.Scalar($"SELECT CAST(round(1e9 * 90 * coalesce(sum(value) FILTER (WHERE metric = 'goals'), 0) / sum(value) FILTER (WHERE metric = 'minutes')) AS BIGINT) FROM apps WHERE {where}") / 1e9;

    [Fact]
    public async Task A_derived_ratio_divides_totals_not_averages_of_ratios()
    {
        // The report asks for a mean — meaningless for a ratio — and still gets sum(goals) / sum(minutes) × 90.
        var report = AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Column, AxisSource.Category(Athlete)), Transform.Total(Aggregators.Mean, Athlete));
        var view = await MeridianRuntime.InMemory(AppCatalog, AppSource()).Engine.RunAsync(report, ProjectionOptions.Default);

        var perPlayer = view.Series[0].Marks.ToDictionary(m => m.Label, m => m.Value!.Value);
        foreach (var athlete in new[] { 1, 2, 3 })
        {
            Assert.Equal(SqlRatio($"entity_id = {athlete} AND ts < TIMESTAMP '2025-07-01'"), perPlayer[athlete.ToString()], 9);
        }
        Assert.Equal("/90", view.Axes[1].Unit);
    }

    [Fact]
    public async Task Goals_per_90_by_venue_per_month_is_one_query_and_matches_sql()
    {
        var report = AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Venue),
            Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing), Transform.GroupBy(Aggregators.Sum, Venue))
            .WithDimensions(Venue);

        var counting = new CountingSource(AppSource());
        var viaSql = await MeridianRuntime.InMemory(AppCatalog, counting).Engine.RunAsync(report, ProjectionOptions.Default);
        var inEngine = await MeridianRuntime.InMemory(AppCatalog, new RawOnly(AppSource())).Engine.RunAsync(report, ProjectionOptions.Default);

        var batch = Assert.Single(counting.Batches); // goals and minutes, bucketed and grouped in one query
        Assert.Equal(["goals", "minutes"], batch.Metrics);
        Assert.True(batch.Rollup);
        AssertSameView(inEngine, viaSql);

        var march = viaSql.Series.Single(s => s.Name == "Home").Marks.Single(m => m.Label == "2025-03");
        Assert.Equal(SqlRatio("venue = 'Home' AND ts >= TIMESTAMP '2025-03-01' AND ts < TIMESTAMP '2025-04-01'"), march.Value!.Value, 9);
    }

    [Fact]
    public async Task No_goals_is_zero_and_no_minutes_is_no_value()
    {
        var report = AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
            Transform.Resample(Period.Week, Aggregators.Sum, GapPolicy.ZeroFill));
        var view = await MeridianRuntime.InMemory(AppCatalog, AppSource()).Engine.RunAsync(report, ProjectionOptions.Default);

        long scorelessMatches = db.Scalar("""
            SELECT count(*) FROM apps m WHERE metric = 'minutes' AND ts < TIMESTAMP '2025-07-01'
              AND NOT EXISTS (SELECT 1 FROM apps g WHERE g.metric = 'goals' AND g.entity_id = m.entity_id AND g.ts = m.ts)
            """);
        long weeksPlayed = db.Scalar("SELECT count(*) FROM apps WHERE metric = 'minutes' AND ts < TIMESTAMP '2025-07-01'");
        var marks = view.Series.SelectMany(s => s.Marks).ToList();

        Assert.True(scorelessMatches > 0);
        Assert.Equal(scorelessMatches, marks.Count(m => m.Value == 0));   // played, didn't score: 0, not a gap
        Assert.Equal(weeksPlayed, marks.Count);                            // weeks without minutes (athlete 3's break): no value, even zero-filled
    }

    [Fact]
    public async Task A_rolling_ratio_is_rolling_goals_over_rolling_minutes()
    {
        ITransform[] steps = [Transform.Resample(Period.Day, Aggregators.Sum, GapPolicy.LeaveMissing), Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean)];
        var engine = MeridianRuntime.InMemory(AppCatalog, AppSource()).Engine;
        var view = new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete);

        var ratio = await engine.RunAsync(AppReport(GoalsPer90.Id, view, steps), ProjectionOptions.Default);
        ITransform[] sums = [Transform.Resample(Period.Day, Aggregators.Sum, GapPolicy.LeaveMissing), Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Sum)];
        var goals = await engine.RunAsync(AppReport(AppGoals.Id, view, sums), ProjectionOptions.Default);
        var minutes = await engine.RunAsync(AppReport(AppMinutes.Id, view, sums), ProjectionOptions.Default);

        var goalsAt = goals.Series.SelectMany(s => s.Marks.Select(m => ((s.Name, m.At), m.Value!.Value))).ToDictionary();
        foreach (var series in minutes.Series)
        foreach (var m in series.Marks)
        {
            double expected = goalsAt.GetValueOrDefault((series.Name, m.At)) / m.Value!.Value * 90;
            var actual = ratio.Series.Single(s => s.Name == series.Name).Marks.Single(r => r.At == m.At).Value!.Value;
            Assert.Equal(expected, actual, 9);
        }
    }

    [Fact]
    public async Task Transforms_after_the_last_aggregation_apply_to_the_ratio()
    {
        var report = AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Column, AxisSource.Category(Athlete)),
            Transform.Total(Aggregators.Sum, Athlete), Transform.Named("prolific", Transform.Filter(p => p.Measure.Value >= 0.5)));
        var all = await MeridianRuntime.InMemory(AppCatalog, AppSource()).Engine.RunAsync(
            AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Column, AxisSource.Category(Athlete)), Transform.Total(Aggregators.Sum, Athlete)), ProjectionOptions.Default);
        var filtered = await MeridianRuntime.InMemory(AppCatalog, AppSource()).Engine.RunAsync(report, ProjectionOptions.Default);

        Assert.Equal(all.Series[0].Marks.Where(m => m.Value >= 0.5).Select(m => m.Label), filtered.Series[0].Marks.Select(m => m.Label));
    }

    [Fact]
    public async Task A_derived_chart_reuses_the_goals_and_minutes_its_dashboard_already_loaded()
    {
        var counting = new CountingSource(AppSource());
        var engine = MeridianRuntime.InMemory(AppCatalog, counting).Engine;
        var monthly = new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete);
        ITransform month = Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing);

        await engine.RunManyAsync([AppReport(AppGoals.Id, monthly, month), AppReport(AppMinutes.Id, monthly, month)], ProjectionOptions.Default);
        int calls = counting.Batches.Count + counting.Rollups + counting.Raws;

        await engine.RunAsync(AppReport(GoalsPer90.Id, monthly, month), ProjectionOptions.Default);
        Assert.Equal(calls, counting.Batches.Count + counting.Rollups + counting.Raws); // no new source query
    }

    [Fact]
    public async Task A_derived_chart_is_rebuilt_when_an_input_changes()
    {
        var runtime = MeridianRuntime.InMemory(AppCatalog, AppSource());
        var report = AppReport(GoalsPer90.Id, new ViewSpec(ChartKind.Column, AxisSource.Category(Athlete)), Transform.Total(Aggregators.Sum, Athlete));

        var first = await runtime.Engine.RunAsync(report, ProjectionOptions.Default);
        Assert.Same(first, await runtime.Engine.RunAsync(report, ProjectionOptions.Default));

        await runtime.Invalidator.InvalidateAsync(new ChangeScope("club", "goals", new EntityRef(Athlete, 2)));
        Assert.NotSame(first, await runtime.Engine.RunAsync(report, ProjectionOptions.Default));
    }

    [Fact]
    public void Bad_derived_metrics_are_rejected_when_the_catalog_is_built()
    {
        var weather = new DimensionId("weather");
        Assert.Contains("isn't in the catalog", Assert.Throws<ArgumentException>(() => new InMemoryMetricCatalog([AppGoals, GoalsPer90])).Message);
        var nested = MetricDefinition.Ratio(new MetricId("nested"), "Nested", Unit.None, GoalsPer90.Id, AppMinutes.Id, 1, [Athlete]);
        Assert.Contains("itself derived", Assert.Throws<ArgumentException>(() => new InMemoryMetricCatalog([AppGoals, AppMinutes, GoalsPer90, nested])).Message);
        var bySky = MetricDefinition.Ratio(new MetricId("by-sky"), "By sky", Unit.None, AppGoals.Id, AppMinutes.Id, 1, [Athlete, weather]);
        Assert.Contains("can't", Assert.Throws<ArgumentException>(() => new InMemoryMetricCatalog([AppGoals, AppMinutes, bySky])).Message);
    }

    private static PipelineSpec Monthly(MetricDefinition metric, ChartKind kind, params long[] athletes) => PipelineSpec.Create(
        "club", metric.Id, [.. (athletes.Length == 0 ? [1L] : athletes).Select(a => new EntityRef(Athlete, a))], FirstHalf,
        new ViewSpec(kind, AxisSource.Time, SeriesBy: athletes.Length > 1 ? Athlete : null),
        Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing));

    [Fact]
    public async Task Goals_minutes_and_goals_per_90_on_one_chart_is_one_query()
    {
        var chart = ChartSpec.Of(
            new SeriesSpec(Monthly(AppGoals, ChartKind.Column)),
            new SeriesSpec(Monthly(AppMinutes, ChartKind.Line), Axis: ValueAxis.Secondary),
            new SeriesSpec(Monthly(GoalsPer90, ChartKind.Line)));
        var counting = new CountingSource(AppSource());

        var view = await MeridianRuntime.InMemory(AppCatalog, counting).Engine.RunChartAsync(chart, ProjectionOptions.Default);

        var batch = Assert.Single(counting.Batches); // goals, minutes, and the ratio's inputs: one query
        Assert.Equal(["goals", "minutes"], batch.Metrics);
        Assert.Equal(0, counting.Raws + counting.Rollups);

        Assert.Equal(["Goals", "Minutes", "Goals per 90"], view.Series.Select(s => s.Name)); // catalog names
        Assert.Equal([ChartKind.Column, ChartKind.Line, ChartKind.Line], view.Series.Select(s => s.Kind!.Value));
        Assert.Equal([1, 2, 1], view.Series.Select(s => s.Axis!.Value));

        // The ratio line agrees with the goals and minutes drawn beside it.
        var goals = view.Series[0].Marks.ToDictionary(m => m.At!.Value, m => m.Value!.Value);
        foreach (var minutes in view.Series[1].Marks)
        {
            var ratio = view.Series[2].Marks.Single(m => m.At == minutes.At).Value!.Value;
            Assert.Equal(goals.GetValueOrDefault(minutes.At!.Value) / minutes.Value!.Value * 90, ratio, 9);
        }
    }

    [Fact]
    public async Task A_dashboard_of_charts_shares_one_query()
    {
        var counting = new CountingSource(AppSource());
        var engine = MeridianRuntime.InMemory(AppCatalog, counting).Engine;

        var views = await engine.RunChartsAsync(
        [
            ChartSpec.Of(new SeriesSpec(Monthly(AppGoals, ChartKind.Line, 1, 2, 3))),                  // squad goals
            ChartSpec.Of(new SeriesSpec(Monthly(GoalsPer90, ChartKind.Line, 1, 2, 3), "Per 90")),       // squad rate
        ], ProjectionOptions.Default);

        Assert.Single(counting.Batches);
        Assert.Equal(["Goals · athlete 1", "Goals · athlete 2", "Goals · athlete 3"], views[0].Series.Select(s => s.Name));
        Assert.Equal("Per 90 · athlete 1", views[1].Series[0].Name);
    }

    [Fact]
    public async Task Decimal_values_and_integer_ids_are_read_on_both_paths()
    {
        var typed = new MetricDefinition(new MetricId("typed"), "Typed", new Unit("usd"), "sum", [Athlete], TimeGrain.Instant);
        var catalog = new InMemoryMetricCatalog([typed]);
        var spec = PipelineSpec.Create("test", typed.Id, [new EntityRef(Athlete, 1)], Timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete), Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing));

        var viaSql = await MeridianRuntime.InMemory(catalog, Source("typed")).Engine.RunAsync(spec, ProjectionOptions.Default);
        var inEngine = await MeridianRuntime.InMemory(catalog, new RawOnly(Source("typed"))).Engine.RunAsync(spec, ProjectionOptions.Default);

        Assert.Equal(19.75, viaSql.Series[0].Marks[0].Value);
        AssertSameView(inEngine, viaSql);
    }

    [Fact]
    public async Task A_source_that_contradicts_the_metrics_time_kind_is_rejected()
    {
        var spec = PipelineSpec.Create("test", WellnessDef.Id, [new EntityRef(Athlete, 1)], Timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete));
        var engine = MeridianRuntime.InMemory(Catalog, Source("wellness", StoredTime.Utc)).Engine; // DATEs read as UTC instants

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(spec, ProjectionOptions.Default));
        Assert.Contains("declared as Local", error.Message);
    }

    [Fact]
    public async Task Rollup_and_raw_slices_are_cached_separately()
    {
        var counting = new CountingSource(Source());
        var engine = MeridianRuntime.InMemory(Catalog, counting).Engine;
        var weekly = Spec(LoadDef, Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing);

        await engine.RunAsync(weekly, ProjectionOptions.Default);
        await engine.RunAsync(weekly, ProjectionOptions.Default);
        Assert.Equal(1, counting.Rollups); // second run served from cache

        await engine.RunAsync(weekly, In("Europe/London"));
        Assert.Equal(2, counting.Rollups); // another zone draws other weeks: a different slice

        await engine.RunAsync(Spec(LoadDef, Period.Week, Aggregators.Max, GapPolicy.LeaveMissing), ProjectionOptions.Default);
        Assert.Equal(3, counting.Rollups); // a different aggregate is a different slice

        await engine.RunAsync(PipelineSpec.Create("test", LoadDef.Id, [new EntityRef(Athlete, 1)], Timeframe,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete)), ProjectionOptions.Default);
        Assert.Equal(1, counting.Raws);    // raw data never reuses a rolled-up slice
    }

    private async Task<PointBlock> Legacy(LocalTimeResolution resolution)
    {
        var all = new DateInterval(Instant.FromUtc(new DateTime(2025, 1, 1)), Instant.FromUtc(new DateTime(2026, 1, 1)));
        return await Source("legacy", StoredTime.InZone("Europe/London", resolution)).FetchAsync(LegacyDef, [new EntityRef(Athlete, 1)], all);
    }

    private static async Task AssertParity(MetricDefinition metric, IRollupPointSource source, PipelineSpec spec, ProjectionOptions options)
    {
        var pushdown = new CountingSource(source);
        var viaSql = await MeridianRuntime.InMemory(Catalog, pushdown).Engine.RunAsync(spec, options);
        var inEngine = await MeridianRuntime.InMemory(Catalog, new RawOnly(source)).Engine.RunAsync(spec, options);

        Assert.Equal(1, pushdown.Rollups);
        Assert.Equal(0, pushdown.Raws);
        AssertSameView(inEngine, viaSql);
    }

    private static IPeriod PeriodNamed(string name) => name switch
    {
        "15m" => Period.Every(TimeSpan.FromMinutes(15)),
        "hour" => Period.Hour,
        "day" => Period.Day,
        "week" => Period.Week,
        "month" => Period.Month,
        _ => Period.Season,
    };

    private static PipelineSpec Spec(MetricDefinition metric, IPeriod period, IAggregator agg, GapPolicy gap, DateInterval? timeframe = null) => PipelineSpec.Create(
        "test", metric.Id, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], timeframe ?? Timeframe,
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
        Transform.Resample(period, agg, gap));

    private static void AssertSameView(ChartView expected, ChartView actual)
    {
        Assert.Equal(expected.Axes[0], actual.Axes[0]); // same kind, zone and grain on the time axis
        Assert.Equal(expected.Series.Count, actual.Series.Count);
        for (int s = 0; s < expected.Series.Count; s++)
        {
            var e = expected.Series[s];
            var a = actual.Series[s];
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Marks.Count, a.Marks.Count);
            for (int i = 0; i < e.Marks.Count; i++)
            {
                Assert.Equal(e.Marks[i].At, a.Marks[i].At);
                Assert.Equal(e.Marks[i].Label, a.Marks[i].Label);
                if (e.Marks[i].Value is not { } ev)
                {
                    Assert.Null(a.Marks[i].Value);
                    continue;
                }
                var av = Assert.NotNull(a.Marks[i].Value);
                Assert.True(Math.Abs(ev - av) <= 1e-9 * Math.Max(1, Math.Abs(ev)), $"{e.Name} @ {e.Marks[i].Label}: {ev} vs {av}");
            }
        }
    }

    /// <summary>Hides the rollup capability, forcing the in-engine resample.</summary>
    private sealed class RawOnly(IPointSource inner) : IPointSource
    {
        public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default) =>
            inner.FetchAsync(metric, entities, timeframe, ct);
    }

    private sealed class CountingSource(IRollupPointSource inner) : IRollupPointSource, IBatchPointSource
    {
        public int Raws { get; private set; }
        public int Rollups { get; private set; }
        public List<(string[] Metrics, long[] Entities, bool Rollup)> Batches { get; } = [];

        public Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchManyAsync(IReadOnlyList<MetricDefinition> metrics, IReadOnlyList<EntityRef> entities, DateInterval timeframe, SourceRollup? rollup, CancellationToken ct = default)
        {
            lock (Batches) Batches.Add(([.. metrics.Select(m => m.Id.Value).Order()], [.. entities.Select(e => e.Id)], rollup is not null));
            return ((IBatchPointSource)inner).FetchManyAsync(metrics, entities, timeframe, rollup, ct);
        }

        public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default)
        {
            Raws++;
            return inner.FetchAsync(metric, entities, timeframe, ct);
        }

        public bool CanRollup(MetricDefinition metric, SourceRollup rollup) => inner.CanRollup(metric, rollup);

        public Task<PointBlock> FetchRollupAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, SourceRollup rollup, CancellationToken ct = default)
        {
            Rollups++;
            return inner.FetchRollupAsync(metric, entities, timeframe, rollup, ct);
        }
    }
}
