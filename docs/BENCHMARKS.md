# Benchmarks

How fast Meridian is, measured rather than claimed — on synthetic data at 172 million and 1 billion
rows, on 47.5 million real NYC taxi trips, and on the standard TSBS time-series workload. Every number
here comes from `bench/Meridian.Bench.Scale`; raw results (with p95) are in [`bench/results/`](../bench/results)
and charted in the dashboard's **Benchmarks** section, one history line per dataset.

**Method.** Each scenario runs a warm-up call (untimed), then N timed iterations; tables show medians.
"Cold" means Meridian's cache is empty while the database and OS file cache are warm, as on a live
server. "Warm" is the same request again. Before timing, every run checks that Meridian's answer equals
the database's own for the same query. SQL baselines run against the same DuckDB instance and files.

**Machine.** Intel i7-8700K (6 cores / 12 threads), 32 GB, Windows 11, .NET 10, DuckDB 1.5.5.

## Synthetic: 172M and 1B rows

`bench/Meridian.Bench.Scale` generates a longitudinal dataset as Parquet and times one report — weekly
mean of one metric for 25 entities over a year — through every path.

- **171.7M rows** — 5 metrics × 2,000 entities × 2 years × hourly · 577 MB Parquet · 215k raw rows in the report's scope
- **1.03B rows** — 5 metrics × 6,000 entities × 2 years × half-hourly · 3.3 GB Parquet · 429k raw rows in scope

| Scenario | 171.7M rows | 1.03B rows | What it shows |
|---|---:|---:|---|
| Hand-written SQL | 83 ms | 91 ms | the floor: one DuckDB `GROUP BY`, results read into memory |
| Cold · raw fetch | 227 ms | 395 ms | every raw row moved into Meridian and resampled there |
| **Cold · pushdown** | **88 ms** | **78 ms** | resample pushed into SQL — level with hand-written SQL |
| Cold · pushdown, London weeks | 74 ms | 84 ms | weeks drawn in Europe/London, zone conversion inside the SQL |
| **Warm · cached** | **0.01 ms** | **0.01 ms** | the identical report again: the finished chart comes from cache |
| Warm · +1 entity | 14 ms | 21 ms | only the new entity is fetched; the rest merge from cache |
| Warm · 1 entity changed | 12 ms | 20 ms | a write invalidates one entity; only its slice is refetched |
| Cold · 28-day rolling mean | 80 ms | 85 ms | daily means pushed down, rolling window per entity in the engine |

Six times the data costs almost nothing: latency tracks the rows *in scope*, not the size of the table —
consistent with DuckDB skipping Parquet row groups outside the requested entities (the generator writes
rows sorted by metric → entity → time).

## Real data: NYC taxi trips

**47.5M real trips** — NYC TLC yellow-taxi records, Jul 2025 → Jun 2026, 775 MB of public Parquet read in
place. Pickup times are New York wall-clock times with no zone, so the source is declared
`StoredTime.InZone("America/New_York")` and weeks are New York weeks, across both DST changes. Report:
weekly trips for the 10 busiest pickup zones (15.9M trips in scope).

| Scenario | Median | |
|---|---:|---|
| Hand-written SQL | 717 ms | `date_trunc('week')` on the logged wall clock |
| Cold · raw fetch | 13.3 s | every trip into Meridian, converted from New York time, then bucketed |
| **Cold · pushdown** | **735 ms** | DST-correct conversion and bucketing inside DuckDB — within 3% of hand-written SQL |
| **Warm · cached** | **0.01 ms** | |
| Warm · +1 zone | 280 ms | the files aren't sorted by zone, so one zone still scans them all |
| Cold · 28-day rolling mean | 750 ms | New York days pushed down, rolling window in the engine |

The "+1 zone" row contrasts with the synthetic data: there, entity-sorted Parquet made adding an entity
~14 ms; here the store's layout decides.

## Standard workload: TSBS cpu-only

[TSBS](https://github.com/timescale/tsbs) is the Time Series Benchmark Suite used to compare time-series
databases. Data from TSBS's own generator — `cpu-only`, 1,000 hosts × 3 days at 10 s, **25.9M readings ×
10 metrics = 259M values** — and eight TSBS query types, 20 random instances each (medians):

| Query | SQL, TSBS wide schema | SQL, long layout | Meridian cold | Meridian warm |
|---|---:|---:|---:|---:|
| single-groupby-1-1-1 | 20 ms | 17 ms | 16 ms | 0.01 ms |
| single-groupby-1-1-12 | 16 ms | 16 ms | 21 ms | < 0.01 ms |
| single-groupby-1-8-1 | 20 ms | 20 ms | 20 ms | 0.01 ms |
| single-groupby-5-1-1 | 22 ms | 30 ms | 29 ms | 0.02 ms |
| single-groupby-5-8-1 | 22 ms | 39 ms | 40 ms | 0.02 ms |
| cpu-max-all-8 | 29 ms | 74 ms | 75 ms | 0.03 ms |
| double-groupby-1 | 130 ms | 138 ms | 190 ms | 0.39 ms |
| double-groupby-all | 292 ms | 1,258 ms | 1,664 ms | 2.4 ms |

What it says, plainly:

- **Cold, Meridian costs about what the same query costs in SQL over the same data.** Pushdown makes the
  first run cost what the SQL costs.
- **Warm, it's a lookup.** Identical requests return the finished chart from an in-process view cache
  (bounded, invalidated with the data), so even the 130,000-point `double-groupby-all` answers in 2.4 ms
  (before view caching: 120 ms, since transforms and projection re-ran per request).
- **Multi-metric queries are one source query.** A Meridian report covers one metric, but
  `RunManyAsync` batches reports that share entities and timeframe into a single `metric IN (…)` fetch
  (`IBatchPointSource`). Before batching, the 5- and 10-metric queries above took 72 / 95 / 197 ms cold;
  now 29 / 40 / 75 ms — level with SQL on the long layout.
- **The remaining gap is storage layout, not Meridian.** TSBS's wide schema stores all 10 metrics in one
  row, so a 10-metric query reads a tenth as many rows as the long layout (one row per metric value)
  that Meridian's source reads. A source over a wide table could close it.
- The published TSBS results for other databases ran on other hardware, so compare shapes, not absolute
  numbers. Reproduce with `tsbs-generate` and `tsbs-run` (see [Reproduce](#reproduce)).

## Why the cold path is fast: pushdown

When a report starts with a resample the source can compute exactly
(`IRollupPointSource`; DuckDB covers sub-daily, day, week from any start day and month buckets, in any IANA
zone, for UTC, wall-clock-in-a-zone or date columns, with mean, sum, min, max, count, median or last), the engine asks for one point per
entity-bucket instead of every raw row. Zone conversions in the SQL are generated from NodaTime's rules,
so they can't disagree with the engine. Anything else — seasons, custom aggregators — falls back to a raw
fetch, so results never change; parity tests check every period × aggregator × gap policy in several
zones across real DST changes.


## Micro-benchmarks

`bench/Meridian.Benchmarks` (BenchmarkDotNet, `[MemoryDiagnoser]`) guards the in-engine hot paths —
aggregation, resampling, rolling windows, key hashing — for 25 and 100 entities × 10 metrics × a year
of daily points. Allocation is a first-class number: the aggregation path is allocation-free.

Short run (BenchmarkDotNet `--job short`), same machine:

| Benchmark | 25 entities | 100 entities | Allocated (100) |
|---|---:|---:|---:|
| `AggregateMean_100k` — mean of 100k values | 92 µs | 93 µs | **0 B** |
| `ResampleWeekly_Season` — 10 metrics × a year, daily → weekly, in the engine | 18.6 ms | 69.4 ms | 98 MB |
| `Acwr_OneSeason` — 7-day / 28-day rolling ratio, one series | 0.15 ms | 0.15 ms | 146 KB |
| `KeyBuildAndStableHash` — composite key + stable hash | 0.16 µs | 0.16 µs | 448 B |

The in-engine resample is the known hot spot: it groups through dictionaries and allocates heavily
(365k points → 98 MB). That's why reports push resampling down to the database where they can, and
it's the target of the columnar pass on the [roadmap](../ROADMAP.md).

## Reproduce

```bash
# synthetic (rows = entities × metrics × days × per-day)
dotnet run -c Release --project bench/Meridian.Bench.Scale -- generate --entities 2000 --metrics 5 --days 730 --per-day 24 --data data/bench-175m
dotnet run -c Release --project bench/Meridian.Bench.Scale -- run --data data/bench-175m --label my-machine

# NYC taxi (public Parquet, ~65 MB per month)
dotnet run -c Release --project bench/Meridian.Bench.Scale -- taxi-download --from 2025-07 --to 2026-06
dotnet run -c Release --project bench/Meridian.Bench.Scale -- taxi-run --label my-machine

# TSBS cpu-only (needs Go to build TSBS's generator)
go install github.com/timescale/tsbs/cmd/tsbs_generate_data@latest
dotnet run -c Release --project bench/Meridian.Bench.Scale -- tsbs-generate --scale 1000 --days 3
dotnet run -c Release --project bench/Meridian.Bench.Scale -- tsbs-run --instances 20 --label my-machine

# micro-benchmarks
dotnet run -c Release --project bench/Meridian.Benchmarks -- --job short
```

Generated data goes under `data/` (git-ignored; ~172M rows is ~0.6 GB, ~1B rows ~3.3 GB). Results are
written to `bench/results/*.json` — commit them to add a point to the dashboard's history. CI runs a
tiny scale benchmark on every change so the harness can't rot; its timings (shared runners) aren't
comparable and aren't committed.
