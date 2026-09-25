using DuckDB.NET.Data;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.DuckDb;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Meridian.Views.Json;

// The README's quick start, runnable: `dotnet run --project samples/Meridian.Sample.QuickStart`.
// Setup only: write a small Parquet file standing in for "your data" (two athletes, hourly load, H1 2026).
var parquet = Path.Combine(Path.GetTempPath(), "meridian-quickstart.parquet").Replace(Path.DirectorySeparatorChar, '/');
using (var db = new DuckDBConnection("Data Source=:memory:"))
{
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"""
        COPY (SELECT a AS entity_id, 'training-load' AS metric, TIMESTAMP '2026-01-01' + to_hours(h) AS ts,
                     50 + (hash(a * 7919 + h) % 500) / 10.0 AS value
              FROM range(7, 10, 2) t(a), range(0, 181 * 24) s(h))
        TO '{parquet}' (FORMAT parquet)
        """;
    cmd.ExecuteNonQuery();
}

// --- quick start ---------------------------------------------------------------------------------

var athlete = new DimensionId("athlete");
var trainingLoad = new MetricId("training-load");

// 1. Define the metric once.
var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(trainingLoad, "Training load", new Unit("au"), "mean", [athlete], TimeGrain.Instant),
]);

// 2. Point Meridian at your data where it already lives: here, Parquet files queried in place by DuckDB.
var source = new DuckDbPointSource(
    new DuckDbSourceOptions("Data Source=:memory:", Relation: $"read_parquet('{parquet}')"),
    athlete);

var meridian = MeridianRuntime.InMemory(catalog, source);

// 3. Ask for a report: weekly mean load per athlete, in London weeks.
var report = PipelineSpec.Create(
    tenant: "my-club",
    metric: trainingLoad,
    entities: [new EntityRef(athlete, 7), new EntityRef(athlete, 9)],
    timeframe: new DateInterval(Instant.FromUtc(new DateTime(2026, 1, 1)), Instant.FromUtc(new DateTime(2026, 7, 1))),
    view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: athlete),
    Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing));

var chart = await meridian.Engine.RunAsync(report,
    ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") });

// 4. A chart-agnostic view: hand the JSON to any charting library, API client or agent.
Console.WriteLine(ChartViewJson.Serialize(chart)[..400] + "…");
Console.WriteLine($"{chart.Series.Count} series × {chart.Series[0].Marks.Count} weeks, time axis: {chart.Axes[0].Time} / {chart.Axes[0].Grain} / {chart.Axes[0].TimeZone}");
