# Meridian

Embeddable, time-correct reporting for .NET systems that already own their data: define metrics once,
point Meridian at your store, and get chart-ready series back — bucketed in the right time zone,
cached, invalidated on write, as chart-agnostic JSON.

This package is one part of Meridian. Most applications start with:

```bash
dotnet add package Meridian.Engine --prerelease
dotnet add package Meridian.Sources.DuckDb --prerelease   # or Meridian.Sources.MySql, or your own IPointSource
dotnet add package Meridian.Views.Json --prerelease
```

## Quick start

```csharp
var athlete = new DimensionId("athlete");
var trainingLoad = new MetricId("training-load");

var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(trainingLoad, "Training load", new Unit("au"), "mean", [athlete], TimeGrain.Instant),
]);

var source = new DuckDbPointSource(
    new DuckDbSourceOptions("Data Source=:memory:", Relation: "read_parquet('data/load/*.parquet')"),
    athlete);

var meridian = MeridianRuntime.InMemory(catalog, source);

var chart = await meridian.Engine.RunAsync(
    PipelineSpec.Create(
        tenant: "my-club",
        metric: trainingLoad,
        entities: [new EntityRef(athlete, 7), new EntityRef(athlete, 9)],
        timeframe: new DateInterval(Instant.FromUtc(new DateTime(2026, 1, 1)), Instant.FromUtc(new DateTime(2026, 7, 1))),
        view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: athlete),
        Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing)),
    ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") });

Console.WriteLine(ChartViewJson.Serialize(chart));
```

## Highlights

- **Time done right** — readings bucketed in the report's time zone (DST included); calendar dates never
  shift; legacy wall-clock columns read with explicit DST rules.
- **Fast on your existing data** — bucketing pushed down into the database when exact; multiple metrics
  in one query, from long (row per value) or wide (column per metric) tables; repeats from cache in
  microseconds.
- **Safe caching** — per-entity slices, finished-view caching, write-driven invalidation with versioned keys.
- **Chart-agnostic output** — plain JSON series and axes for any charting library, API or agent.

Documentation, benchmarks and the roadmap: **https://github.com/gyandal/meridian**
