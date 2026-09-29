using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.DuckDb;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Sources.Sql.Tests;

/// <summary>
/// What every database source must do, checked against a real database holding the same awkward data as the DuckDB
/// suite (DST changes, wall-clock tables, NULLs, holes). Three kinds of check: raw rows read exactly as DuckDB reads
/// them; every pushed-down rollup equals the engine's own result from those raw rows; and it equals DuckDB's result
/// for the same report. A database's test class supplies the fixture; the cases are shared.
/// </summary>
public abstract class SqlParityTests(IDatabaseFixture db)
{
    private static readonly DimensionId Athlete = new("athlete");
    private static readonly DimensionId Venue = new("venue");
    private static readonly MetricDefinition LoadDef = new(new MetricId("load"), "Load", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition HrDef = new(new MetricId("hr"), "HR", new Unit("bpm"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition WellnessDef = new(new MetricId("wellness"), "Wellness", new Unit("score"), "mean", [Athlete], TimeGrain.Daily, TimeKind.Local);
    private static readonly MetricDefinition LegacyDef = new(new MetricId("legacy"), "Legacy", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition LegacyNyDef = new(new MetricId("legacy-ny"), "Legacy NY", new Unit("au"), "mean", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition GoalsDef = new(new MetricId("goals"), "Goals", Unit.None, "sum", [Athlete, Venue], TimeGrain.Instant);
    private static readonly MetricDefinition TypedDef = new(new MetricId("typed"), "Typed", Unit.None, "sum", [Athlete], TimeGrain.Instant);
    private static readonly MetricDefinition IntsDef = new(new MetricId("ints"), "Ints", Unit.None, "mean", [Athlete], TimeGrain.Instant);
    private static readonly InMemoryMetricCatalog Catalog = new([LoadDef, HrDef, WellnessDef, LegacyDef, LegacyNyDef, GoalsDef, TypedDef, IntsDef]);

    // Starts mid-day and ends mid-week so partial first/last buckets are exercised on every path.
    private static readonly DateInterval Timeframe = new(
        Instant.FromUtc(new DateTime(2025, 1, 2, 6, 0, 0, DateTimeKind.Utc)),
        Instant.FromUtc(new DateTime(2025, 4, 20, 0, 0, 0, DateTimeKind.Utc)));

    private static readonly DateInterval Year = new(Instant.FromUtc(new DateTime(2025, 1, 1, 7, 30, 0)), Instant.FromUtc(new DateTime(2025, 12, 31)));

    private static readonly string[] Aggregations = ["mean", "sum", "min", "max", "count", "median", "last", "first", "stddev", "variance", "p90"];
    private static readonly string[] Periods = ["15m", "hour", "day", "week", "month"];

    private static readonly Dictionary<string, string> GoalDimensions = new() { ["venue"] = "venue" };

    private SqlPointSource Source(string table = "datapoints", StoredTime? time = null, IReadOnlyDictionary<string, string>? dimensions = null,
        IReadOnlyDictionary<string, string>? metricColumns = null) =>
        db.Source(new SqlSourceOptions(Relation: table, StoredTime: time, DimensionColumns: dimensions, MetricColumns: metricColumns), Athlete);

    private DuckDbPointSource Reference(string table = "datapoints", StoredTime? time = null, IReadOnlyDictionary<string, string>? dimensions = null,
        IReadOnlyDictionary<string, string>? metricColumns = null) =>
        new(new DuckDbSourceOptions(db.Reference.ConnectionString, Relation: table, StoredTime: time, DimensionColumns: dimensions, MetricColumns: metricColumns), Athlete);

    private static ProjectionOptions In(string zone, DayOfWeek weekStart = DayOfWeek.Monday) =>
        ProjectionOptions.Default with { Calendar = CalendarContext.For(zone, weekStart) };

    private static IPeriod PeriodNamed(string name) => Period.Named(name)!;

    private static IAggregator Aggregator(string name) => Aggregators.TryResolve(name, out var aggregator) ? aggregator : throw new ArgumentException(name);

    private static PipelineSpec Spec(MetricDefinition metric, IPeriod period, IAggregator aggregator, GapPolicy gap, DateInterval? timeframe = null) => PipelineSpec.Create(
        "test", metric.Id, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], timeframe ?? Timeframe,
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Athlete),
        Transform.Resample(period, aggregator, gap));

    /// <summary>
    /// Aggregators this database has no exact SQL for: reports using them must fall back to a raw fetch (and still give
    /// the same answer). Everything else must push down.
    /// </summary>
    protected virtual IReadOnlySet<string> Declined => new HashSet<string>();

    /// <summary>A column name as written in this database's SQL (a reserved word needs quoting).</summary>
    protected virtual string Column(string name) => name;

    /// <summary>
    /// The report equals the engine's result from raw rows and DuckDB's result for the same data — pushed down in one
    /// query unless <paramref name="aggregator"/> is one this database declines, when it's fetched raw instead.
    /// </summary>
    private async Task AssertParity(IRollupPointSource source, IRollupPointSource reference, PipelineSpec spec, ProjectionOptions options, string context, string? aggregator = null)
    {
        var counting = new CountingSource(source);
        var pushed = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunAsync(spec, options);
        var inEngine = await MeridianRuntime.InMemory(Catalog, new RawOnly(source)).Engine.RunAsync(spec, options);
        var duckDb = await MeridianRuntime.InMemory(Catalog, reference).Engine.RunAsync(spec, options);

        bool declined = aggregator is not null && Declined.Contains(aggregator);
        Assert.True(declined ? counting.Rollups == 0 && counting.Raws == 1 : counting.Rollups == 1 && counting.Raws == 0,
            $"{context}: expected {(declined ? "a raw fetch" : "one pushed-down query")}, got {counting.Rollups} rollups and {counting.Raws} raw fetches");
        Same.View(inEngine, pushed, context + " (pushdown vs engine)");
        Same.View(duckDb, pushed, context + " (vs DuckDB)");
    }

    private Dictionary<string, string> WideColumns() => new() { ["load"] = Column("load"), ["hr"] = Column("hr") };

    // ------------------------------------------------------------------------------------------ raw reads

    public static TheoryData<string> Tables() => ["datapoints", "datapoints_tz", "wellness", "legacy", "legacy_ny", "goals", "typed", "wide"];

    [SkippableTheory]
    [MemberData(nameof(Tables), MemberType = typeof(SqlParityTests))]
    public async Task Raw_rows_read_exactly_as_duckdb_reads_them(string table)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var (metric, time, dimensions, columns, reference) = table switch
        {
            "datapoints" or "datapoints_tz" => Table(LoadDef, reference: "datapoints"),
            "wellness" => Table(WellnessDef, StoredTime.Local),
            "legacy" => Table(LegacyDef, StoredTime.InZone("Europe/London")),
            "legacy_ny" => Table(LegacyNyDef, StoredTime.InZone("America/New_York", new LocalTimeResolution(AmbiguousTime.Later))),
            "goals" => Table(GoalsDef, dimensions: GoalDimensions),
            "typed" => Table(TypedDef),
            _ => Table(LoadDef, columns: WideColumns()),
        };
        (MetricDefinition, StoredTime?, IReadOnlyDictionary<string, string>?, IReadOnlyDictionary<string, string>?, string) Table(
            MetricDefinition m, StoredTime? t = null, IReadOnlyDictionary<string, string>? dimensions = null,
            IReadOnlyDictionary<string, string>? columns = null, string? reference = null) => (m, t, dimensions, columns, reference ?? table);
        IReadOnlyList<EntityRef> entities = [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)];

        var expected = await Reference(reference, time, dimensions, columns is null ? null : new Dictionary<string, string> { ["load"] = "load", ["hr"] = "hr" }).FetchAsync(metric, entities, Year);
        var actual = await Source(table, time, dimensions, columns).FetchAsync(metric, entities, Year);

        Assert.True(expected.Count > 0, $"{table}: the reference has no rows to compare");
        Same.Points(expected, actual, table);
    }

    // ------------------------------------------------------------------------------------------ pushdown

    public static TheoryData<string, string, string, GapPolicy> PushdownCases()
    {
        var data = new TheoryData<string, string, string, GapPolicy>();
        foreach (var zone in new[] { "UTC", "Europe/London", "America/New_York", "Australia/Sydney" })
        foreach (var period in Periods)
        foreach (var agg in Aggregations)
        foreach (var gap in Enum.GetValues<GapPolicy>())
        {
            data.Add(zone, period, agg, gap);
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(PushdownCases), MemberType = typeof(SqlParityTests))]
    public async Task Every_rollup_pushes_down_and_equals_the_engine_and_duckdb(string zone, string period, string aggregator, GapPolicy gap)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        await AssertParity(Source(), Reference(), Spec(LoadDef, PeriodNamed(period), Aggregator(aggregator), gap), In(zone),
            $"{zone} {period} {aggregator} {gap}", aggregator);
    }

    public static TheoryData<AmbiguousTime, string, string, string> WallClockCases()
    {
        var data = new TheoryData<AmbiguousTime, string, string, string>();
        foreach (var ambiguous in new[] { AmbiguousTime.Earlier, AmbiguousTime.Later })
        foreach (var zone in new[] { "UTC", "America/New_York", "Europe/London" })
        foreach (var period in Periods)
        foreach (var agg in Aggregations)
        {
            data.Add(ambiguous, zone, period, agg);
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(WallClockCases), MemberType = typeof(SqlParityTests))]
    public async Task Wall_clock_data_pushes_down_with_the_same_dst_resolution_as_the_engine(AmbiguousTime ambiguous, string zone, string period, string aggregator)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var time = StoredTime.InZone("America/New_York", new LocalTimeResolution(ambiguous));
        await AssertParity(Source("legacy_ny", time), Reference("legacy_ny", time),
            Spec(LegacyNyDef, PeriodNamed(period), Aggregator(aggregator), GapPolicy.LeaveMissing, Year), In(zone), $"{ambiguous} {zone} {period} {aggregator}", aggregator);
    }

    public static TheoryData<string, string> LocalCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var period in new[] { "day", "week", "month" })
        foreach (var agg in Aggregations)
        {
            data.Add(period, agg);
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(LocalCases), MemberType = typeof(SqlParityTests))]
    public async Task Calendar_dates_push_down_as_given(string period, string aggregator)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        await AssertParity(Source("wellness", StoredTime.Local), Reference("wellness", StoredTime.Local),
            Spec(WellnessDef, PeriodNamed(period), Aggregator(aggregator), GapPolicy.ZeroFill), In("Pacific/Auckland"), $"{period} {aggregator}", aggregator);
    }

    [SkippableTheory]
    [InlineData("mean")]
    [InlineData("sum")]
    [InlineData("stddev")]
    [InlineData("variance")]
    public async Task Integer_columns_aggregate_as_numbers_not_integers(string aggregator)
    {
        // A mean of whole numbers is rarely whole; a database that averages integers as integers would truncate it.
        Skip.If(db.SkipReason is not null, db.SkipReason);
        await AssertParity(Source("ints"), Reference("ints"), Spec(IntsDef, Period.Week, Aggregator(aggregator), GapPolicy.LeaveMissing), In("UTC"), aggregator, aggregator);
    }

    [SkippableTheory]
    [InlineData("UTC", DayOfWeek.Sunday)]
    [InlineData("Europe/London", DayOfWeek.Sunday)]
    [InlineData("America/New_York", DayOfWeek.Saturday)]
    [InlineData("Australia/Sydney", DayOfWeek.Wednesday)]
    public async Task Weeks_starting_on_any_day_push_down(string zone, DayOfWeek weekStart)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        await AssertParity(Source(), Reference(), Spec(LoadDef, Period.Week, Aggregators.Sum, GapPolicy.ZeroFill), In(zone, weekStart), $"{zone} {weekStart}");
    }

    [SkippableTheory]
    [InlineData("day")]
    [InlineData("month")]
    public async Task A_timestamptz_column_reads_like_a_utc_timestamp_column(string period)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var spec = Spec(LoadDef, PeriodNamed(period), Aggregators.Mean, GapPolicy.LeaveMissing);
        var tz = await MeridianRuntime.InMemory(Catalog, Source("datapoints_tz")).Engine.RunAsync(spec, In("Europe/London"));
        var plain = await MeridianRuntime.InMemory(Catalog, Reference()).Engine.RunAsync(spec, In("Europe/London"));
        Same.View(plain, tz, period);
    }

    [SkippableTheory]
    [InlineData("UTC")]
    [InlineData("Europe/London")]
    public async Task Grouping_by_a_dimension_pushes_down_in_the_same_series_order(string zone)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var spec = PipelineSpec.Create("test", GoalsDef.Id, [new EntityRef(Athlete, 1), new EntityRef(Athlete, 2), new EntityRef(Athlete, 3)], Timeframe,
            new ViewSpec(ChartKind.Column, AxisSource.Time, SeriesBy: Venue),
            Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.ZeroFill), Transform.GroupBy(Aggregators.Sum, Venue)).WithDimensions(Venue);
        await AssertParity(Source("goals", dimensions: GoalDimensions), Reference("goals", dimensions: GoalDimensions), spec, In(zone), zone);
    }

    [SkippableTheory]
    [InlineData("hour")]
    [InlineData("month")]
    public async Task A_wide_table_fetches_every_metric_in_one_query_and_matches_its_long_form(string period)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var columns = WideColumns();
        PipelineSpec For(MetricDefinition m) => Spec(m, PeriodNamed(period), Aggregators.Max, GapPolicy.LeaveMissing);

        var counting = new CountingSource(Source("wide", metricColumns: columns));
        var wide = await MeridianRuntime.InMemory(Catalog, counting).Engine.RunManyAsync([For(LoadDef), For(HrDef)], In("Europe/London"));
        var longForm = await MeridianRuntime.InMemory(Catalog, Source("wide_as_long")).Engine.RunManyAsync([For(LoadDef), For(HrDef)], In("Europe/London"));

        Assert.Equal(1, counting.Batches);
        Same.View(longForm[0], wide[0], "load");
        Same.View(longForm[1], wide[1], "hr");
    }
}

public sealed class PostgreSqlParityTests(PostgreSqlFixture db) : SqlParityTests(db), IClassFixture<PostgreSqlFixture>;

public sealed class SqlServerParityTests(SqlServerFixture db) : SqlParityTests(db), IClassFixture<SqlServerFixture>
{
    // No aggregate form of a median, a percentile, or first/last in time: those run in the engine.
    protected override IReadOnlySet<string> Declined { get; } = new HashSet<string> { "median", "p90", "first", "last" };

    protected override string Column(string name) => $"[{name}]";
}
