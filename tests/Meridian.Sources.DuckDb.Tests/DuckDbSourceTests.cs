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

/// <summary>A small DuckDB file with the awkward cases: sub-daily readings, dropped rows, NULL values,
/// a multi-week hole, an all-NULL week, leading all-NULL days, and a second metric to filter out.</summary>
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
    private static readonly MetricId Load = new("load");
    private static readonly MetricDefinition LoadDef = new(Load, "Load", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly InMemoryMetricCatalog Catalog = new([LoadDef]);

    // Starts mid-day and ends mid-week so partial first/last buckets are exercised on both paths.
    private static readonly DateInterval Timeframe = new(
        Instant.FromUtc(new DateTime(2025, 1, 2, 6, 0, 0, DateTimeKind.Utc)),
        Instant.FromUtc(new DateTime(2025, 4, 20, 0, 0, 0, DateTimeKind.Utc)));

    private DuckDbPointSource Source() => new(new DuckDbSourceOptions(db.ConnectionString), Athlete);

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

    public static TheoryData<string, string, GapPolicy> PushdownCases()
    {
        var data = new TheoryData<string, string, GapPolicy>();
        foreach (var period in new[] { "day", "week", "month" })
        foreach (var agg in new[] { "mean", "sum", "min", "max", "count", "median", "last" })
        foreach (var gap in Enum.GetValues<GapPolicy>())
        {
            data.Add(period, agg, gap);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PushdownCases))]
    public async Task Pushed_down_rollup_matches_the_in_engine_resample(string period, string aggregator, GapPolicy gap)
    {
        Assert.True(Aggregators.TryResolve(aggregator, out var agg));
        var spec = Spec(PeriodNamed(period), agg, gap);

        var pushdown = new CountingSource(Source());
        var viaSql = await MeridianRuntime.InMemory(Catalog, pushdown).Engine.RunAsync(spec, ProjectionOptions.Default);
        var inEngine = await MeridianRuntime.InMemory(Catalog, new RawOnly(Source())).Engine.RunAsync(spec, ProjectionOptions.Default);

        Assert.Equal(1, pushdown.Rollups);
        Assert.Equal(0, pushdown.Raws);
        AssertSameMarks(inEngine, viaSql);
    }

    [Theory]
    [InlineData("season", "UTC", DayOfWeek.Monday)]            // no SQL equivalent for a domain season
    [InlineData("week", "UTC", DayOfWeek.Sunday)]              // date_trunc('week') is Monday-start only
    [InlineData("day", "Europe/London", DayOfWeek.Monday)]     // local-time buckets stay in the engine
    public async Task Unsupported_rollups_fall_back_to_a_raw_fetch_and_stay_correct(string period, string zone, DayOfWeek weekStart)
    {
        var options = ProjectionOptions.Default with
        {
            Calendar = new CalendarContext(TimeZoneInfo.FindSystemTimeZoneById(zone), weekStart, new FixedSeasonCalendar()),
        };
        var spec = Spec(PeriodNamed(period), Aggregators.Mean, GapPolicy.LeaveMissing);

        var counting = new CountingSource(Source());
        var result = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunAsync(spec, options);
        var expected = await MeridianRuntime.InMemory(Catalog, new RawOnly(Source())).Engine.RunAsync(spec, options);

        Assert.Equal(0, counting.Rollups);
        Assert.Equal(1, counting.Raws);
        AssertSameMarks(expected, result);
    }

    [Fact]
    public async Task Rollup_and_raw_slices_are_cached_separately()
    {
        var counting = new CountingSource(Source());
        var engine = MeridianRuntime.InMemory(Catalog, counting).Engine;

        await engine.RunAsync(Spec(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing), ProjectionOptions.Default);
        await engine.RunAsync(Spec(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing), ProjectionOptions.Default);
        Assert.Equal(1, counting.Rollups); // second run served from cache

        await engine.RunAsync(Spec(Period.Week, Aggregators.Max, GapPolicy.LeaveMissing), ProjectionOptions.Default);
        Assert.Equal(2, counting.Rollups); // a different aggregate is a different slice

        await engine.RunAsync(RawSpec(), ProjectionOptions.Default);
        Assert.Equal(1, counting.Raws);    // raw data never reuses a rolled-up slice
    }

    private static IPeriod PeriodNamed(string name) => name switch
    {
        "day" => Period.Day,
        "week" => Period.Week,
        "month" => Period.Month,
        _ => Period.Season,
    };

    private static PipelineSpec Spec(IPeriod period, IAggregator agg, GapPolicy gap) => PipelineSpec.Create(
        "test", Load, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], Timeframe,
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
        Transform.Resample(period, agg, gap));

    private static PipelineSpec RawSpec() => PipelineSpec.Create(
        "test", Load, [new EntityRef(Athlete, 1)], Timeframe,
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete));

    private static void AssertSameMarks(ChartView expected, ChartView actual)
    {
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
                if (e.Marks[i].Value is not { } ev)
                {
                    Assert.Null(a.Marks[i].Value);
                    continue;
                }
                var av = Assert.NotNull(a.Marks[i].Value);
                Assert.True(Math.Abs(ev - av) <= 1e-9 * Math.Max(1, Math.Abs(ev)), $"{e.Name} @ {e.Marks[i].At}: {ev} vs {av}");
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
