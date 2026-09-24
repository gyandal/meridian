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

            CREATE TABLE legacy (entity_id BIGINT, metric VARCHAR, ts TIMESTAMP, value DOUBLE);
            INSERT INTO legacy VALUES
                (1, 'legacy', TIMESTAMP '2025-03-30 00:30:00', 1),   -- before the spring gap
                (1, 'legacy', TIMESTAMP '2025-03-30 01:30:00', 2),   -- never happens in London
                (1, 'legacy', TIMESTAMP '2025-07-01 12:00:00', 3),   -- summer: UTC+1
                (1, 'legacy', TIMESTAMP '2025-10-26 01:30:00', 4);   -- happens twice in London
            """;
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
        try { File.Delete(FilePath); File.Delete(FilePath + ".wal"); } catch (IOException) { }
    }
}

public class DuckDbSourceTests(DuckDbFixture db) : IClassFixture<DuckDbFixture>
{
    private static readonly DimensionId Athlete = new("athlete");
    private static readonly MetricDefinition LoadDef = new(new MetricId("load"), "Load", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition WellnessDef = new(new MetricId("wellness"), "Wellness", new Unit("score"), "mean", [Athlete], TimeGrain.Daily, TimeKind.Local);
    private static readonly MetricDefinition LegacyDef = new(new MetricId("legacy"), "Legacy", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly InMemoryMetricCatalog Catalog = new([LoadDef, WellnessDef, LegacyDef]);

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
        foreach (var period in new[] { "day", "week", "month" })
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

    [Theory]
    [InlineData("season", "UTC", false)]            // domain seasons have no SQL equivalent
    [InlineData("day", "Europe/London", true)]      // wall-clock-in-zone data: DST resolution stays in the engine
    public async Task Unsupported_rollups_fall_back_to_a_raw_fetch_and_stay_correct(string period, string zone, bool legacyWallClock)
    {
        var (metric, source) = legacyWallClock
            ? (LegacyDef, Source("legacy", StoredTime.InZone("Europe/London")))
            : (LoadDef, Source());
        var wide = new DateInterval(Instant.FromUtc(new DateTime(2025, 1, 1)), Instant.FromUtc(new DateTime(2026, 1, 1)));
        var spec = Spec(metric, PeriodNamed(period), Aggregators.Mean, GapPolicy.LeaveMissing, wide);

        var counting = new CountingSource(source);
        var result = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunAsync(spec, In(zone));
        var expected = await MeridianRuntime.InMemory(Catalog, new RawOnly(source)).Engine.RunAsync(spec, In(zone));

        Assert.Equal(0, counting.Rollups);
        Assert.Equal(1, counting.Raws);
        AssertSameView(expected, result);
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

    private sealed class CountingSource(IRollupPointSource inner) : IRollupPointSource
    {
        public int Raws { get; private set; }
        public int Rollups { get; private set; }

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
