---
title: "Stop hand-rolling time-series reports in .NET: meet Meridian"
published: false
description: "An embeddable .NET library that turns points over time into chart-ready series: time zones and DST done right, SQL pushdown, caching that doesn't go stale, and the same metrics for charts, APIs and AI agents."
tags: dotnet, csharp, dataviz, opensource
cover_image:
---

Every product that stores data over time eventually gets the same ticket: *"Can we have a chart of this per week?"*

The first version is a `GROUP BY date_trunc('week', ts)` and an afternoon's work. Then the questions start.

- "Why does Monday's total in London not match the dashboard?" (The query bucketed in UTC.)
- "Why does March have a day with 23 hours of data?" (DST.)
- "Can we compare with last season?" (A second query and some fiddly date maths.)
- "Goals per 90 looks wrong." (It averaged per-match ratios instead of dividing totals.)
- "The dashboard takes four seconds." (Twelve charts, twelve queries, no cache.)
- "We added a cache and now it shows yesterday's numbers." (Invalidation.)

[Meridian](https://github.com/gyandal/meridian) is an MIT-licensed set of NuGet packages that handles this layer for you. You tell it what your metrics are and where the data already lives (a SQL database, Parquet files, an API), and it returns finished, chart-ready series. Bucketing in the right time zone, gap filling, rolling windows, comparisons, forecasts, caching and invalidation are all handled by the library.

It's a library, not a service. You don't run new infrastructure or copy your data anywhere.

> Meridian is in preview (`0.1.0-preview.x`), so APIs may change before 1.0. It runs on .NET 8, 9 and 10.

## Five minutes to a chart

```bash
dotnet add package Meridian.Engine --prerelease
dotnet add package Meridian.Sources.DuckDb --prerelease   # or .PostgreSql, .SqlServer, .MySql
dotnet add package Meridian.Views.Json --prerelease
```

This example reports weekly mean training load per athlete, with weeks drawn in London time and the data read from Parquet files where they sit:

```csharp
var athlete = new DimensionId("athlete");
var trainingLoad = new MetricId("training-load");

// 1. Define the metric once.
var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(trainingLoad, "Training load", new Unit("au"), "mean", [athlete], TimeGrain.Instant),
]);

// 2. Point Meridian at the data where it already lives.
var source = new DuckDbPointSource(
    new DuckDbSourceOptions("Data Source=:memory:", Relation: "read_parquet('data/load/*.parquet')"),
    athlete);

var meridian = MeridianRuntime.InMemory(catalog, source);

// 3. Ask for a report.
var report = PipelineSpec.Create(
    tenant: "my-club",
    metric: trainingLoad,
    entities: [new EntityRef(athlete, 7), new EntityRef(athlete, 9)],
    timeframe: new DateInterval(Instant.FromUtc(new DateTime(2026, 1, 1)), Instant.FromUtc(new DateTime(2026, 7, 1))),
    view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: athlete),
    Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing));

var chart = await meridian.Engine.RunAsync(report,
    ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") });

// 4. Chart-agnostic JSON: hand it to Recharts, Highcharts, D3, a table or an agent.
Console.WriteLine(ChartViewJson.Serialize(chart));
```

When data changes, you tell Meridian, and it drops only the affected cache entries:

```csharp
await meridian.Invalidator.InvalidateAsync(
    new ChangeScope("my-club", "training-load", new EntityRef(athlete, 7)));
```

You write two things per system: a **metric catalog** (what can be asked) and a **source** (how to read your store). The built-in DuckDB, PostgreSQL, SQL Server and MySQL sources cover most setups. For anything else you implement `IPointSource.FetchAsync`, which is a single method: "give me this metric, for these entities, in this window".

## Practical use cases

### 1. Sports and athlete performance

This is where Meridian started, and it's why sports vocabulary shows up throughout the API. Typical questions:

- Weekly training load per athlete, and 7-day vs 28-day rolling loads (the acute:chronic workload ratio).
- Goals per 90 minutes, computed correctly.
- This season vs last season, lined up bucket for bucket.
- "On pace for 14 goals, between 11 and 17."

Derived metrics are defined once in the catalog:

```csharp
var goalsPer90 = MetricDefinition.Ratio(
    new MetricId("goals-per-90"), "Goals per 90", new Unit(""),
    numerator: goals, denominator: minutes, scale: 90, validDimensions: [player, venue]);
```

Every chart built on that metric divides the **totals** in each bucket and group. A player who scored once in a ten-minute cameo and played 90 goalless minutes the next week has a rate of 0.9 per 90, not an average of 9 and 0 (4.5). Both inputs come from one query.

Comparing with last season is the same report with a baseline:

```csharp
var goalsPerPlayer = PipelineSpec.Create("club", goals, squad, season,
    new ViewSpec(ChartKind.Column, AxisSource.Category(player)),
    Transform.Total(Aggregators.Sum, player));

// Side by side, fetched together:
var sideBySide = await runtime.Engine.RunChartAsync(ChartSpec.Of(
    new SeriesSpec(goalsPerPlayer, "This season"),
    new SeriesSpec(goalsPerPlayer.Earlier(Baseline.SeasonsBack(1)), "Last season")),
    ProjectionOptions.Default);

// Or as a % change per player:
var change = await runtime.Engine.RunAsync(
    goalsPerPlayer.ChangeFrom(Baseline.SeasonsBack(1)), ProjectionOptions.Default);
```

Comparisons are checked for alignment. If you ask for "a year back" on weekly buckets, Meridian rejects it and tells you to use `WeeksBack(52)`, so that Mondays line up with Mondays.

### 2. IoT, fleet and infrastructure telemetry

The use case is per-device minute and hour rollups over very large tables. The DuckDB source can read TSBS's native "wide" schema (one column per metric) directly, so a query across 10 metrics is one scan:

```csharp
new DuckDbSourceOptions(connectionString, relation,
    MetricColumns: new Dictionary<string, string> { ["usage_user"] = "usage_user", /* ... */ });
```

Sub-daily buckets such as `Period.Every(TimeSpan.FromMinutes(5))` and `Period.Hour` are supported, and so are `p90`/`p99.5` percentiles, standard deviation and variance. All of them are pushed down to the database.

### 3. Operations and mobility: local time that's actually local

Many older schemas store timestamps as **wall-clock time without a zone**. NYC taxi pickups are a real example. Meridian lets a source declare how its time column is stored:

```csharp
var source = new DuckDbPointSource(
    new DuckDbSourceOptions("Data Source=:memory:", relation,
        StoredTime: StoredTime.InZone("America/New_York")),
    zone);
```

Meridian then converts those wall-clock times with New York's DST rules, including the ambiguous hour when clocks go back, which you can resolve as `Earlier`, `Later` or `Reject`. Weeks are New York weeks. The conversion runs inside the SQL, so it doesn't cost you the pushdown.

### 4. Per-customer analytics inside your product

Dashboards are stored as JSON, so your users can build and save their own:

```json
{ "title": "Goals by month, home and away stacked",
  "series": [ { "metric": "goals", "dimensions": ["venue"],
                "view": { "kind": "column", "seriesBy": "venue", "stacked": true },
                "transforms": [
                  { "kind": "resample", "period": "month", "aggregator": "sum", "gap": "zero-fill", "across": "timeframe" },
                  { "kind": "groupBy", "aggregator": "sum", "by": ["venue"] } ] } ] }
```

All charts on a dashboard share one context (tenant, entities, timeframe). Charts that slice the same metric by player, by venue and by month share **one fetch**. Narrowing the dashboard to a single player is served from the per-entity cache without going back to the database.

### 5. Letting an AI agent answer data questions safely

The `Meridian.Hosts.Mcp` package exposes two tools. `describe` returns the catalog, and `query` runs a bounded, typed report and returns a finished chart. Because the agent builds a typed spec from your catalog and never writes SQL, every call is bounded, auditable and cacheable. An assistant can answer "how did load change for these players this month?" without being handed a database connection.

## How it compares with the alternatives

These tools overlap, but they solve different problems. Here's where each one fits.

| | Hand-written SQL + LINQ | TimescaleDB continuous aggregates | Semantic layer (e.g. Cube) | BI tools (Metabase, Superset, Grafana) | **Meridian** |
|---|---|---|---|---|---|
| Runs as | your code | a DB extension | a separate service | a separate service | **NuGet packages in your app** |
| Works with your existing store | yes | Postgres only | yes (via its own service) | yes | **yes: DuckDB/Parquet, Postgres, SQL Server, MySQL, custom** |
| Time zone + DST correct buckets | you write it | per-aggregate config | configurable | varies | **built in, parity-tested** |
| Comparisons / running totals / forecasts | you write it | you write it | partially | partially | **built in** |
| Cache invalidation on writes | you write it | refresh policies | pre-aggregations + refresh | query cache TTLs | **versioned, per entity** |
| Output | rows | rows | rows / JSON | a rendered UI | **chart-ready JSON** |
| Embeds in your product's UI | yes | yes | yes (via API) | iframe/embedding | **yes, it's your API** |

### vs hand-written SQL

Hand-written SQL is the baseline, and the benchmarks measure Meridian against it directly. On a cold run (empty cache), Meridian pushes the bucketing into the database, so the first run costs roughly what the equivalent hand-written query costs. A repeated request returns the finished chart from memory.

On a 6-core desktop (i7-8700K), DuckDB 1.5.5, .NET 10:

| Workload | Hand-written SQL | Meridian, first run | Meridian, repeat |
|---|---:|---:|---:|
| Weekly report, 25 entities, **1.03 billion rows** of Parquet | 91 ms | 78 ms | 0.01 ms |
| Weekly trips per zone in New York time, **47.5M real NYC taxi trips** | 717 ms | 735 ms | 0.01 ms |
| TSBS `cpu-max-all-8` (10 metrics, 8 hosts), **259M values** | 27 ms | 33 ms | 0.03 ms |
| TSBS `double-groupby-all` (130,000-point result) | 262 ms | 552 ms | 2.35 ms |

Some details that aren't in the headline numbers:

- **The time-zone-correct path matches the naive one.** On the taxi data, hand-written SQL uses `date_trunc('week')` on the logged wall clock, which ignores DST. Meridian's DST-correct pushdown lands within 3% of it. The zone conversions are generated from NodaTime's rules as short `CASE` expressions over literal cut-offs. That means the database needs no time-zone support, and its answer can't differ from the engine's.
- **Incremental work is cheap.** Adding one entity to a warm report fetches only that entity (14 ms at 172M rows, 21 ms at 1B). When a write invalidates one entity, only that entity's slice is refetched.
- **Latency depends on the rows in scope, not the size of the table.** Going from 172M to 1.03B rows barely changed the timings.
- **Where it's slower:** `double-groupby-all` returns 130,000 values. SQL hands back rows, while Meridian builds 130,000 labelled chart marks, so it takes about twice as long cold. Cheaper projection for very large results is on the roadmap.

Every benchmark run first checks that Meridian's answer equals the database's own answer for the same query. The parity suites also compare pushdown with the in-engine result across every period × aggregator × gap policy, in several zones and across real DST changes: 1,261 cases against PostgreSQL and 1,265 against SQL Server.

The larger savings are in code you don't have to write. Comparisons, rolling windows per entity (without one athlete's window bleeding into another's), running totals, derived metrics, gap filling, the cache and its invalidation would each be a few hundred lines of SQL and C# to write and maintain yourself.

### vs TimescaleDB continuous aggregates

Continuous aggregates are very good at keeping rollups current inside Postgres, and Meridian's PostgreSQL source reads TimescaleDB tables and views. They work at different layers. A continuous aggregate is a fixed rollup (one bucket width, one zone) that you define ahead of time. Meridian handles the report on top of it: any zone per request, comparisons, derived metrics, forecasts and chart output. Meridian also works when the data isn't in Postgres.

### vs a semantic layer (Cube and similar)

Semantic layers also give you "define metrics once, query them many ways", and they have mature pre-aggregation. The difference is the deployment model. A semantic layer is a separate service with its own config language, deployment and cache. Meridian runs inside your .NET process, you define metrics in C#, and your application calls it directly. Meridian also covers the time-series specifics: DST-correct buckets, season calendars, bucket-aligned comparisons, forecasts with ranges, and chart-ready output.

A like-for-like benchmark against semantic-layer tools is on the roadmap. Until it's published, I'm not claiming a speed difference.

### vs BI tools

Metabase, Superset and Grafana are finished products for analysts, and if you need an internal BI tool you should use one. Meridian isn't a BI tool or a query language. It's the reporting layer *inside* your application, for charts your customers see, rendered in your own design system and served by your own API.

## Design choices behind it

- **Instants and calendar dates are different types of value.** A sensor reading is an instant, while a date of birth or a match day is a local value. Meridian keeps them separate, so a birthday never shows up a day early for users in America. Instants are bucketed in the report's zone, and the zone is part of the cache key.
- **Every transform is `PointBlock → PointBlock`**, so any transform can follow any other. `PerGroup` runs a transform per partition.
- **Missing is a flag, never a null, and NaN is never a value.**
- **Presentation is applied at the end.** Colours, labels and number formats are added only at projection, so switching `Column` to `Line` needs no upstream changes.
- **Projections are marked as projections.** Forecast points are flagged `Estimated`, and so is anything computed from them, so a chart can draw them dotted.

## Try it

```bash
git clone https://github.com/gyandal/meridian
cd meridian
dotnet run --project samples/Meridian.Sample.QuickStart
dotnet run --project src/Meridian.Hosts.Http   # dashboard at http://localhost:5731
```

The dashboard's feature showcase renders every capability described here, plus charts of every committed benchmark run. To reproduce the benchmarks on your own machine (synthetic data up to 1B rows, NYC taxi, TSBS), follow [docs/BENCHMARKS.md](https://github.com/gyandal/meridian/blob/main/docs/BENCHMARKS.md).

Next on the roadmap: ClickHouse, qualifying thresholds for rates ("top 5 by goals per 90, minimum 450 minutes"), round-aligned comparisons, a Redis cache backend and a full MCP server.

If your product keeps data over time and you're tired of rewriting the same reporting code, try it out. Issues, use cases and PRs are welcome on [GitHub](https://github.com/gyandal/meridian).
