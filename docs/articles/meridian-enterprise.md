---
title: "\"The reports are too slow\": fixing in-house .NET reporting on years of data"
published: false
description: "Where the time goes in in-house reporting on years of history, and how an embeddable .NET library fixes it without a data warehouse project: SQL pushdown, per-entity caching, batching and invalidation."
tags: dotnet, csharp, sqlserver, performance
cover_image:
---

If you maintain an in-house line-of-business system, you've probably seen this ticket more than once:

> *"The monthly report takes forever to load. Can you make it faster?"*

The system has been running for five, eight or twelve years, and the tables have grown the whole time. The reporting screens were written when there were a few thousand rows. They now sit on hundreds of millions, and users open them every morning.

The usual responses are all expensive:

- **"Let's get a data warehouse."** That's a months-long project, with a new pipeline to maintain and data that's a day old.
- **"Let's buy a BI tool."** Users now switch between your app and someone else's, and permissions, tenancy and branding all have to be rebuilt.
- **"Let's add a cache."** It works until the first "why am I seeing yesterday's numbers?"
- **"Let's add an index."** This helps, but it doesn't fix the reason the report is slow.

This article covers a fourth option. It looks at where the time actually goes in a typical in-house report, and shows how [Meridian](https://github.com/gyandal/meridian), an MIT-licensed .NET library, removes most of it **inside your existing application, against your existing SQL Server or PostgreSQL database**. You don't copy data or add new infrastructure.

> Meridian is in preview (`0.1.0-preview.x`), so APIs may change before 1.0. It runs on .NET 8, 9 and 10.

## Where the time goes

Slow in-house reports are rarely slow because the database is slow. They're usually slow because of how the application uses it. Four patterns come up again and again.

### 1. Rows are aggregated in the application instead of the database

```csharp
// Looks harmless. On year 8 of the system, it isn't.
var weekly = await db.Readings
    .Where(r => siteIds.Contains(r.SiteId) && r.Timestamp >= from && r.Timestamp < to)
    .ToListAsync();                                   // every raw row crosses the wire

var chart = weekly
    .GroupBy(r => (r.SiteId, Week: StartOfWeek(r.Timestamp)))   // ...and is grouped in C#
    .Select(g => new { g.Key.SiteId, g.Key.Week, Value = g.Average(r => r.Value) });
```

Often this happens because the grouping needs something SQL makes awkward, such as local-time weeks, DST or a fiscal calendar. So the rows are pulled into the app and grouped there.

Meridian's benchmarks measure this directly. On 47.5 million real NYC taxi trips, a weekly report in New York time took:

| Approach | Time |
|---|---:|
| Pull every row into the app, convert time zones, bucket in memory | **13.3 s** |
| Push the bucketing (DST-correct) into the database | **0.735 s** |
| Hand-written `date_trunc` SQL (ignores DST) | 0.717 s |

Moving the raw rows cost 18×. The fix is to push the aggregation down to the database, even when the calendar logic is hard. Meridian generates the time-zone conversion as SQL from NodaTime's rules, so the database needs no time-zone support. Parity tests check that the result equals what the engine would compute itself.

### 2. The same question is computed again for every user

Thirty managers open the same Monday dashboard, and the database computes the same `GROUP BY` thirty times over three years of data.

Meridian caches the **finished chart**. An identical request returns in about 0.01 ms, because it's a lookup rather than a query. The harder problem is keeping that cache correct, which the next sections cover.

### 3. A dashboard runs one query per chart, or one per metric

A dashboard with twelve charts often runs twelve queries, and a chart with five metrics runs five. Each one scans the same date range again.

Meridian batches these. Reports for several metrics over the same entities are fetched in **one query**, and charts that slice the same metric in different ways (by site, by region, by month) share **one fetch**. On the TSBS benchmark, batching brought 5- and 10-metric queries from 4–7× slower than SQL to level with it.

### 4. The report fetches everything again when one thing changed

A user adds one more site to a comparison, and the report reruns in full. One site's figures are corrected, and the whole cache is flushed.

Meridian caches **per entity**. Adding a site to a warm report fetches only that site. A write to one site invalidates only that site's slice.

## The numbers

These are measured on a 6-core desktop (i7-8700K), DuckDB 1.5.5 and .NET 10. The dataset is two years of history (5 metrics × 6,000 entities, half-hourly), **1.03 billion rows**. The report is a weekly mean for 25 entities over a year. Full method and reproduction steps are in [docs/BENCHMARKS.md](https://github.com/gyandal/meridian/blob/main/docs/BENCHMARKS.md).

| Scenario | 171.7M rows | 1.03B rows |
|---|---:|---:|
| Hand-written SQL (the floor) | 83 ms | 91 ms |
| Meridian, rows pulled into the app | 227 ms | 395 ms |
| **Meridian, cold, bucketing pushed down** | **88 ms** | **78 ms** |
| **Meridian, identical request again** | **0.01 ms** | **0.01 ms** |
| One entity added to a warm report | 14 ms | 21 ms |
| One entity's data changed, then re-requested | 12 ms | 20 ms |
| 28-day rolling mean, cold | 80 ms | 85 ms |

Two points matter for systems with years of history:

- **Six times the data barely changed the timings.** Latency depends on the rows *in scope* for the report, not on the size of the table. More history doesn't make this month's report slower, provided the store can skip what's out of scope. On SQL Server or Postgres, that means an index on entity and time, or partitioning by date.
- **The first load costs what hand-written SQL costs, and later loads cost almost nothing.** A cold request pays for the database once. Everyone after it gets the finished chart from memory until the data actually changes.

**About these numbers:** they were measured on DuckDB over Parquet. The SQL Server and PostgreSQL sources use the same SQL engine and pass the same parity suite (1,265 and 1,261 cases), but I haven't published timings for them. Your database, indexes and hardware will decide the cold numbers. The warm and incremental savings come from the cache, so they don't depend on the database.

## What it looks like against SQL Server

Say you have a `readings` table in SQL Server: site, metric, value and a timestamp. Here's the setup:

```csharp
var site = new DimensionId("site");
var energy = new MetricId("energy-kwh");

var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(energy, "Energy", new Unit("kWh"), "sum", [site], TimeGrain.Instant),
]);

var source = new SqlServerPointSource(
    connectionString,
    new SqlSourceOptions(
        Relation: "dbo.readings",
        EntityColumn: "site_id",
        MetricColumn: "metric",
        ValueColumn: "value",
        TimestampColumn: "recorded_at"),
    site);

var meridian = MeridianRuntime.InMemory(catalog, source);
```

A report then looks like this:

```csharp
var report = PipelineSpec.Create(
    tenant: "acme",
    metric: energy,
    entities: siteIds.Select(id => new EntityRef(site, id)).ToList(),
    timeframe: lastThreeYears,
    view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: site),
    Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.ZeroFill));

var chart = await meridian.Engine.RunAsync(report,
    ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") });
```

The monthly `SUM` runs in SQL Server, grouped by site and by *London* month, and only one row per site per month comes back. The result is chart-ready JSON (series, points, typed axes and labels) that your existing front end can draw.

To keep the cache correct, call the invalidator wherever you already write data:

```csharp
await meridian.Invalidator.InvalidateAsync(
    new ChangeScope("acme", "energy-kwh", new EntityRef(site, siteId)));
```

Cache keys include a data version for each entity. A write increments the version, so old entries can never be served again. Missing an eviction can't cause stale data, because stale entries simply become unreachable.

### Legacy schemas that store local time

Older enterprise schemas often store `datetime` values in **server local time**. Meridian can read these without a migration:

```csharp
new SqlSourceOptions(Relation: "dbo.readings", StoredTime: StoredTime.InZone("Europe/London"))
```

The values are converted with that zone's DST rules. For the hour that occurs twice when clocks go back, you choose `Earlier`, `Later` or `Reject`. The conversion is pushed into the SQL, so correctness doesn't cost you speed. `datetimeoffset` columns are read as UTC.

## Beyond speed: fixing numbers users don't trust

When you rewrite a slow report, you often find it was also giving wrong numbers. These are the common causes, and Meridian handles each one in the library:

- **Days and weeks in UTC instead of the business's time zone.** A reading at 00:30 local time lands on the wrong day, so daily totals don't match what people see on the floor. Meridian buckets in the report's zone, including DST.
- **Averaging ratios.** An "average cost per unit" computed as the mean of per-order ratios is wrong. `MetricDefinition.Ratio` divides the totals in each bucket instead.
- **Summing levels.** Stock on hand, open tickets and account balances can't be summed across days. Declare the metric `Additivity.SemiAdditive`, and a monthly report takes the month-end level summed across warehouses rather than the sum of 30 daily snapshots.
- **Year-on-year that doesn't line up.** A weekly comparison "one year back" puts Mondays against Tuesdays. Meridian rejects that and tells you to use `WeeksBack(52)`.
- **Empty months that disappear.** A month with no orders should show 0, not vanish from the axis. `FillAcross.Timeframe` fills it.

## How to adopt it gradually

You don't need to rewrite everything. A realistic path looks like this:

1. **Pick the report users complain about most.** Leave everything else alone.
2. **Point a source at what already exists.** Use a table, or a view that joins the attributes you slice by (region, product line) onto each row. Meridian doesn't do joins, and a view is the right place for them.
3. **Define its metrics in the catalog**, in C#, reviewed like any other code.
4. **Replace the report's query code with a `PipelineSpec`.** Keep your front end, and map `ChartView` JSON to your charting library.
5. **Add one `InvalidateAsync` call to the write paths** that change those metrics.
6. **Measure first-load and repeat times, then move on to the next report.**

Each report you move also gets comparisons, rolling windows, forecasts and dashboards stored as JSON, without extra code.

## What it won't fix

It's better to know the limits before you start than to find them during a rollout:

- **A table with no usable index still gets scanned on the first load.** Pushdown makes the database do the aggregation, so the database still has to find the rows. Latency depends on the rows in scope *only if* the store can skip the rest. In the benchmarks, unsorted Parquet made adding one entity cost 280 ms instead of 14 ms.
- **The cache runs in-process today.** Each app server warms its own cache. A Redis backend, which would give a shared cache across a web farm, is on the roadmap. The cache storage is behind an interface (`IPointCacheStore`) if you need your own before then.
- **Very large results take longer to shape.** A report returning 130,000 points took 552 ms against SQL's 262 ms, because Meridian builds a labelled chart rather than returning rows. Most dashboards return a few hundred to a few thousand points.
- **SQL Server has no aggregate for median, percentiles, first or last.** With SQL Server, those run in the engine rather than in the database. The results are identical, but the rows cross the wire.
- **It isn't a BI tool.** Ad-hoc slicing by analysts belongs in Power BI or Metabase. Meridian is for the reports *your application* serves to its users, day after day.

## Compared with the usual options

| | Warehouse + BI | Indexed views / materialised rollups | Response cache | **Meridian** |
|---|---|---|---|---|
| Time to first result | months | days per report | hours | **days per report** |
| New infrastructure | yes | no | maybe (Redis) | **no (NuGet)** |
| Data freshness | batch (often daily) | live | stale until TTL | **live; invalidated on write** |
| Repeat loads | fast | fast | fast | **microseconds** |
| Change in scope (one more site) | re-query | re-query | miss | **fetches only the new site** |
| Local time / DST correct | depends on ETL | you write it | n/a | **built in, parity-tested** |
| Stays in your app, your auth, your UI | no | yes | yes | **yes** |

Indexed views and materialised rollups work well alongside Meridian. Point a source at the rollup and Meridian adds the caching, batching, comparisons and chart output on top.

## Try it on your slowest report

```bash
dotnet add package Meridian.Engine --prerelease
dotnet add package Meridian.Sources.SqlServer --prerelease   # or .PostgreSql, .DuckDb, .MySql
dotnet add package Meridian.Views.Json --prerelease
```

To see it running first:

```bash
git clone https://github.com/gyandal/meridian
cd meridian
dotnet run --project src/Meridian.Hosts.Http   # dashboard at http://localhost:5731
```

The benchmark harness can generate a billion-row dataset on your own hardware, so you can check these numbers before trying Meridian on production data.

If you run this against your own slow report, I'd like to hear how the before and after compare. Issues, questions and war stories are welcome on [GitHub](https://github.com/gyandal/meridian).
