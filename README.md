# Meridian

[![CI](https://github.com/gyandal/meridian/actions/workflows/ci.yml/badge.svg)](https://github.com/gyandal/meridian/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Meridian.Engine?label=nuget)](https://www.nuget.org/packages/Meridian.Engine)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Embeddable, time-correct reporting for .NET systems that already own their data.**

Meridian turns points over time — sensor readings, training loads, trips, orders, usage events — into
finished, chart-ready series. You define your metrics once and point Meridian at the store you already
have (a SQL database, Parquet files, an API); it handles bucketing in the right time zone, gap filling,
rolling windows, comparisons, caching and invalidation, and hands back chart-agnostic JSON that any front
end, API client or AI agent can use.

It's a set of NuGet packages, not a service: no new infrastructure, no data copy.

```csharp
var chart = await meridian.Engine.RunAsync(
    PipelineSpec.Create("my-club", trainingLoad, athletes, lastSixMonths,
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: athlete),
        Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing)),
    ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") });
```

## Why Meridian

- **Time done right.** Instants and calendar dates are different kinds of time, and Meridian keeps them
  apart: readings are bucketed into days and weeks *in the report's time zone* (DST included), dates of
  birth and match days never shift, and legacy wall-clock columns are read with explicit DST rules.
  [How time works →](docs/TIME.md)
- **Fast on data you already have.** Bucketing is pushed down into the database when it can be done
  exactly, so the first run costs what hand-written SQL costs; repeats come from cache in microseconds.
  Several metrics for the same entities are fetched in one query.
- **Caching that doesn't serve stale data.** Per-entity slices (adding one entity to a report fetches
  only that entity), finished-view caching, and invalidation driven by writes — versioned keys mean a
  missed event can't serve stale data.
- **One definition, every surface.** The same metric catalog drives charts, a REST API and a typed agent
  tool surface (describe the metrics, query a bounded report) — safer than letting an agent write SQL.
- **Chart-agnostic output.** `ChartView` is plain JSON: series, marks, typed axes, server-side labels and
  status. Map it to Highcharts, Recharts, D3, or a table.

## Use cases

- **Athlete and team performance** — training load per athlete per week, acute:chronic workload ratios,
  wellness scores by date, this season vs last.
- **Fleet, IoT and infrastructure telemetry** — per-device minute and hour rollups over billions of
  readings (the [TSBS](docs/BENCHMARKS.md#standard-workload-tsbs-cpu-only) workload).
- **Operations and mobility** — orders, trips or tickets per site per week in each site's local time
  (see the [NYC taxi benchmark](docs/BENCHMARKS.md#real-data-nyc-taxi-trips)).
- **Per-customer product analytics** — usage per account per week, embedded in your own product.
- **Agent-facing analytics** — let an assistant answer "how did load change for these players this month?"
  through bounded, typed queries.

Meridian is not a BI tool or a query language: it's the reporting layer inside your application.

## Install

```bash
dotnet add package Meridian.Engine --prerelease
dotnet add package Meridian.Sources.DuckDb --prerelease   # or Meridian.Sources.MySql, or your own IPointSource
dotnet add package Meridian.Views.Json --prerelease       # the JSON contract for ChartView
```

Targets .NET 10.

## Quick start

From [`samples/Meridian.Sample.QuickStart`](samples/Meridian.Sample.QuickStart/Program.cs) — runnable
with `dotnet run --project samples/Meridian.Sample.QuickStart`:

```csharp
var athlete = new DimensionId("athlete");
var trainingLoad = new MetricId("training-load");

// 1. Define the metric once.
var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(trainingLoad, "Training load", new Unit("au"), "mean", [athlete], TimeGrain.Instant),
]);

// 2. Point Meridian at your data where it already lives: here, Parquet files queried in place by DuckDB.
var source = new DuckDbPointSource(
    new DuckDbSourceOptions("Data Source=:memory:", Relation: "read_parquet('data/load/*.parquet')"),
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
Console.WriteLine(ChartViewJson.Serialize(chart));
```

When your data changes, tell Meridian and only the affected cache entries are dropped:

```csharp
await meridian.Invalidator.InvalidateAsync(new ChangeScope("my-club", "training-load", new EntityRef(athlete, 7)));
```

A longer worked example (goals per athlete, this season vs last) is in [docs/USAGE.md](docs/USAGE.md).

## Benchmarks

Measured on a 6-core desktop; full method, tables and reproduction steps in [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

| Workload | Hand-written SQL | Meridian, first run | Meridian, repeat |
|---|---:|---:|---:|
| Weekly report, 25 entities, **1.03 billion rows** of Parquet | 91 ms | 78 ms | 0.01 ms |
| Weekly trips per zone in New York time, **47.5M real NYC taxi trips** | 717 ms | 735 ms | 0.01 ms |
| TSBS `cpu-max-all-8` (10 metrics, 8 hosts), **259M values** | 27 ms | 33 ms | 0.03 ms |
| TSBS `double-groupby-all` (130,000-point result) | 262 ms | 552 ms | 2.35 ms |

TSBS rows read TSBS's native wide schema (a column per metric) on both sides.

The dashboard (`dotnet run --project src/Meridian.Hosts.Http` → http://localhost:5731) charts every
committed benchmark run, with history.

## Packages

| Package | What it is |
|---|---|
| `Meridian.Engine` | the report engine: catalog → cache → pushdown/batching → transforms → view |
| `Meridian.Core`, `Meridian.Time`, `Meridian.Transforms` | the point model, time handling, the transform algebra |
| `Meridian.Caching`, `Meridian.Semantics` | cache and invalidation; the metric catalog |
| `Meridian.Views`, `Meridian.Views.Json` | chart-agnostic views and their JSON contract |
| `Meridian.Sources.DuckDb`, `Meridian.Sources.MySql` | data sources |
| `Meridian.Hosts.Mcp` | the agent tool surface |

## Documentation

- [docs/TIME.md](docs/TIME.md) — instants vs local values, time zones, DST, reading stored timestamps
- [docs/USAGE.md](docs/USAGE.md) — a worked example and the pattern for your own data
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how the pieces fit, and why
- [docs/BENCHMARKS.md](docs/BENCHMARKS.md) — methods, results, reproduction
- [CHANGELOG.md](CHANGELOG.md) · [ROADMAP.md](ROADMAP.md)

## Roadmap

In preview (`0.1.0-preview.x`); APIs may change before 1.0. Next up: PostgreSQL / SQL Server /
ClickHouse sources, year-on-year and season-aligned comparisons, more aggregators
(percentiles, standard deviation), a Redis cache backend and an MCP server. Later: forecasting and
scenario modelling. See [ROADMAP.md](ROADMAP.md).

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and
the few rules (warnings are errors; time and pushdown changes need parity tests). Please follow the
[code of conduct](CODE_OF_CONDUCT.md), and report security issues privately as described in
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Stephen Wood
