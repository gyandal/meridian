# Building & running Meridian

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` → `10.x`). Nothing else — no database server is needed for the
  tests, dashboard, or samples (they use in-memory/offline data; DuckDB is embedded via NuGet). The weather sample needs internet; the MySQL
  source needs a MySQL server only if you point it at one.

## Everything at once

```bash
dotnet build   -c Release        # build all libraries, hosts, samples and benchmarks
dotnet test    -c Debug          # run all 456 tests across 8 projects
```

## The dashboard (start here)

```bash
dotnet run -c Release --project src/Meridian.Hosts.Http
# → http://localhost:5731
```

Two sections: an **interactive builder** (metric, athletes, transform, period, aggregator, gap policy,
group-by, value/category filters, status band, chart kind) and a **feature showcase** — 11 server-computed
panels, one per capability (weekly/monthly/season resampling, rolling, ACWR + status colours, gap policies,
aggregator sweep, goals by venue, goals year-on-year, weight vs a moving target band, a second metric).

API surface (all return JSON):
- `GET  /api/catalog`   — metrics, entities, and the available options (the "describe" call)
- `POST /api/report`    — a report request → a `ChartView`
- `GET  /api/showcase`  — the pre-built feature panels
- `GET  /api/stats`     — live count of source fetches (watch it hold on a repeat run)

## Samples

```bash
# Offline: load → weekly resample → project → ChartView JSON
dotnet run -c Release --project samples/Meridian.Sample

# Real public API: Open-Meteo daily temps → engine → JSON (needs internet)
dotnet run -c Release --project samples/Meridian.Sample.Weather

# Cold-vs-warm timing harness (synthetic 50 entities × 365 days)
dotnet run -c Release --project samples/Meridian.Bench.Source
#   → point at a real DB:
dotnet run -c Release --project samples/Meridian.Bench.Source -- --mysql "Server=…;Database=…;Uid=…;Pwd=…" <metric> 1,2,3,4
```

## Scale benchmarks (millions → billions of rows)

`bench/Meridian.Bench.Scale` generates a synthetic longitudinal dataset as Parquet (entirely inside
DuckDB, so it never touches .NET memory), then times one report through every path: hand-written SQL,
cold raw fetch, cold with the resample pushed into SQL, warm cache, +1 entity, 1 entity invalidated, and
a 28-day rolling mean. Results are written to `bench/results/*.json` and appear in the dashboard under
**Benchmarks** (commit them to keep the history).

```bash
# rows = entities × metrics × days × per-day × (1 − gap%)
dotnet run -c Release --project bench/Meridian.Bench.Scale -- generate --entities 2000 --metrics 5 --days 730 --per-day 24 --data data/bench-175m
dotnet run -c Release --project bench/Meridian.Bench.Scale -- run --data data/bench-175m --label my-machine
dotnet run -c Release --project bench/Meridian.Bench.Scale -- all --entities 200 --days 365   # small, both steps
```

~172M rows is ~0.6 GB of Parquet and ~2 minutes to generate on a 6-core desktop; ~1B rows is a few GB.
`data/` is git-ignored.

## Micro-benchmarks (BenchmarkDotNet)

```bash
dotnet run -c Release --project bench/Meridian.Benchmarks                 # full run (minutes)
dotnet run -c Release --project bench/Meridian.Benchmarks -- --job short  # quick pass
```

## Layout

```
src/Meridian.Core         points, typed keys, PointBlock, aggregators, deterministic hashing
src/Meridian.Time         calendars, seasons, periods, resampling, gap policies
src/Meridian.Transforms   ITransform + Pipeline; Filter/Map/Reduce/PerGroup/Resample/Rolling; Binary joins
src/Meridian.Semantics    metric catalog
src/Meridian.Caching      pluggable store contract, InMemory backend, per-entity merge, invalidation
src/Meridian.Views(.Json) chart-agnostic ChartView + projection + source-gen JSON
src/Meridian.Engine       PipelineSpec + ReportEngine (catalog → cache → transforms → project)
src/Meridian.Hosts.Http   REST + the dashboard
src/Meridian.Hosts.Mcp    agent tool surface (describe + query)
src/Meridian.Sources.MySql a real IPointSource over a MySQL datapoints table
src/Meridian.Sources.DuckDb DuckDB/Parquet source with resample pushdown (IRollupPointSource)
bench/Meridian.Bench.Scale  dataset generator + scale timing harness → bench/results
```

See `README.md` for the design story, `docs/USAGE.md` for a worked real-world example (goals this
season vs last), and `PLAN.md` for the full architecture plan and the parked future work.

---

## Continuing with Claude

Unzip the project, `cd` into it, open Claude Code, and paste the prompt below to pick the work up. It
orients a fresh session and lists the concrete next steps.

> This is **Meridian**, a greenfield .NET 10 framework for longitudinal datapoints → transforms →
> chart-agnostic views, with a pluggable cache. Read `PLAN.md` (architecture + parked ideas §17b),
> `README.md`, and `docs/USAGE.md` first, then `dotnet test` to confirm the tests pass.
>
> Context: clean layers — the datum carries
> no presentation, time is typed/DST-correct, the cache does per-entity partial-hit merging with
> versioned + tag invalidation, and one `PipelineSpec` drives REST and an MCP tool surface. Phases 0–5
> of the plan are built and tested; there's a runnable dashboard (`src/Meridian.Hosts.Http`) and a
> cold-vs-warm timing harness (`samples/Meridian.Bench.Source`).
>
> I want to take it further. Help me with these, in roughly this order — confirm the plan for each
> before building, and keep every change green under warnings-as-errors:
>
> 1. **Performance pass.** Make `Rolling`/`Resampler` operate on the columnar `PointBlock` (Span/SIMD)
>    instead of dictionary grouping, and drive the allocation on the ACWR benchmark toward zero. Use
>    `bench/Meridian.Benchmarks` as the before/after guard.
> 2. **Benchmark at scale.** Add a `--generate <rows>` mode to the timing harness that bulk-writes
>    millions of synthetic datapoints into the target store, so cold-vs-warm reflects real volume.
> 3. **More data sources.** Add `IPointSource` adapters for common stores (Postgres, DuckDB/Parquet,
>    DynamoDB). `Meridian.Sources.MySql` is the reference shape.
> 4. **Comparative benchmark.** Load one dataset into a store, define ~5 metrics in Meridian and in a
>    comparable semantic-layer tool (e.g. Cube, Malloy), and produce a measured cold/warm/incremental
>    latency comparison. The hypothesis is that .NET columnar + caching wins on interactive/repeat queries.
> 5. **Redis cache backend.** Implement `Meridian.Caching.Redis` (`IPointCacheStore`) and run the
>    existing `StoreConformance` suite against it (Testcontainers).
> 6. **Future transforms (plan §17b).** Year-on-year via a season-phase-aligned period, `Project(horizon,
>    model)` for forecasting (emit `MeasureFlags.Estimated` future points), and scenario modelling.
>
> Start by reading the plan and running the tests, then propose which item to take first and why.
