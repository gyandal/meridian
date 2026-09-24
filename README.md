# Meridian

A .NET 10 framework for longitudinal data: **define metrics once → transform → present → cache →
query (by humans and agents)**, with time as a first-class citizen. Embeddable — drop it into a
system that already sits on the data. See `PLAN.md` for the architecture.

## What's here

```
src/
  Meridian.Core/        Instant, DateInterval, typed keys (PointKey/KeyPart), Measurement,
                        PointBlock (columnar), Aggregators, StableHash (deterministic cache keys)
  Meridian.Time/        CalendarContext (IANA zones via NodaTime), seasons, tumbling Periods, GapPolicy,
                        Resampler (instants → local calendar buckets), StoredTime + DST resolution
  Meridian.Transforms/  ITransform + Pipeline; Filter/Map/Rekey/Reduce/PerGroup/Resample/Rolling;
                        Binary.Combine + Compare (the two-input joins)
  Meridian.Caching/     IPointCacheStore + IKeyValueStore backend contract; InMemory store;
                        TaggedStoreDecorator (batteries-included tags); LayeredPointCacheStore;
                        PointCache (per-entity partial-hit merge + single-flight); version + tag invalidation
  Meridian.Views/       ChartView view model (chart-agnostic); ViewSpec; ITheme/ILabelResolver/
                        IValueFormatter; ChartProjector (data → view; colour/label/status live HERE)
  Meridian.Views.Json/  source-generated System.Text.Json wire contract for ChartView
  Meridian.Engine/      IPointSource seam (+ optional IRollupPointSource pushdown); PipelineSpec;
                        ReportEngine (catalog → cache → transforms → project); MeridianRuntime.InMemory
  Meridian.Hosts.Http/  minimal API over the engine (/api/catalog, /api/report) + the dashboard page
  Meridian.Hosts.Mcp/   agent tool surface: describe + query over the engine (transport-agnostic)
  Meridian.Sources.MySql/  a real IPointSource over a MySQL datapoints table (MySqlConnector)
  Meridian.Sources.DuckDb/ IRollupPointSource over DuckDB tables or Parquet files, resample pushed into SQL
  Meridian.Semantics/   MetricDefinition / IMetricCatalog shape sketch
tests/                  Core (15) + Time (24) + Transforms (12) + Caching (17) + Views (14) + Engine (5) + Hosts (11)
                        + DuckDb source (358: pushdown-vs-engine parity for every period × aggregator × gap × 4 zones)
bench/
  Meridian.Benchmarks/  BenchmarkDotNet hot-path suite ([MemoryDiagnoser])
  Meridian.Bench.Scale/ generate millions–billions of rows as Parquet, time cold/warm/pushdown → bench/results/*.json
  results/              committed scale-run results, charted by the dashboard's Benchmarks section
samples/
  Meridian.Sample/          load → weekly resample → project → JSON (offline)
  Meridian.Sample.Weather/  real Open-Meteo public API → engine → JSON (needs internet)
  Meridian.Bench.Source/    cold-vs-warm timing harness for any IPointSource (synthetic, or --mysql "<conn>")
  dashboard-snapshot.html   a rendered ChartView gallery (static, shareable)
```

`dotnet test` → 456 passing. Targets **net10.0**, nullable + warnings-as-errors.
Run the dashboard: `dotnet run --project src/Meridian.Hosts.Http` → http://localhost:5731.
Time a source: `dotnet run -c Release --project samples/Meridian.Bench.Source` (add `-- --mysql "<conn>" <metric> 1,2,3` for a real DB).

## The transform algebra (Phase 1)

`ITransform` is `PointBlock → PointBlock`, closed under composition. The headline proof is the
acute:chronic workload ratio — a canonical longitudinal calc that is usually bespoke code —
as pure composition:

```csharp
var acute   = Transform.Rolling(TimeSpan.FromDays(7),  Aggregators.Mean).Apply(load, ctx);
var chronic = Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(load, ctx);
var acwr    = Binary.Combine(acute, chronic, (a, c) => a / c, matchTime: true);
```

`PerGroup(keySelector, inner)` runs any inner transform per-partition (so two athletes' rolling
windows never bleed), and `Pipeline.Of(...)` threads a block through an ordered chain.

## Caching (Phase 2)

The Hangfire model: a small store contract with pluggable backends, and write-driven invalidation.

- **Minimal backend contract** — a backend implements only `IKeyValueStore` (Get/Set/Remove).
  `TaggedStoreDecorator` adds tag-group eviction over *any* such store via a maintained `tag → keys`
  index, so a backend inherits `RemoveByTag` for free. `LayeredPointCacheStore` stacks L1/L2.
- **Deterministic keys** — `CacheKeyBuilder` hashes a canonical string (via `StableHash`), so a
  logical request always keys the same. No random per-instance keys.
- **Per-entity partial-hit merge** — `PointCache` caches one slice per entity; a request for
  `{1,2,3}` after `{1,2}` loads *only* `{3}` and merges. With single-flight stampede protection.
- **Invalidation on write** — no stale window waiting on expiry. The write path raises a `ChangeScope` to one `ICacheInvalidator` hook, with two strategies:
  `VersionBumpInvalidator` (version is in the key → clean miss on any backend) and
  `TagEvictionInvalidator` (immediate `RemoveByTag`). A write→read test proves staleness is gone.
- **Conformance suite** — `StoreConformance` is one xUnit suite every backend must pass; it runs
  against InMemory and Layered here, and Redis/SQL would subclass and pass the same tests.

## Views (Phase 3)

The presentation layer — where colour, label, epoch-millis, and status→colour finally live, from a
datum that carried none of them.

- **`ChartView`** is chart-agnostic: series → marks, typed axes (Temporal/Linear/Category), legend,
  annotations-as-data. No charting-library concepts; a front-end maps it to Highcharts/Recharts/D3.
- **`ViewSpec`** is the per-chart difference — line vs column vs pie is a spec swap, not a new
  provider. This is the concrete form of "unify toward multi-series."
- **`ChartProjector`** applies theme colour (by series index or semantic status), formats labels via
  an injected `ILabelResolver`/`IValueFormatter`, computes `At` epoch-millis once, and orders temporal
  marks by instant (not by formatted name, so months never sort alphabetically).
- **`Meridian.Views.Json`** is a source-generated wire contract (camelCase, string enums, nulls
  omitted). Run `dotnet run -c Release --project samples/Meridian.Sample` to print a real `ChartView`.

## Engine (Phase 4)

The keystone that turns six libraries into "give a spec, get a chart".

- **`PipelineSpec`** declares a report: tenant + metric + entities + timeframe + transforms + view.
- **`ReportEngine.RunAsync`** resolves the metric from the catalog, does a cache-aware **per-entity**
  fetch (calling `IPointSource` only for misses), applies the transform pipeline, and projects to a
  `ChartView`. Raw source data is cached (compact `PointBlock`); transforms/projection run per request.
- **`IPointSource`** is the per-client data seam. `InMemoryPointSource` for tests; the Weather sample
  ships an `HttpWeatherSource` over the free Open-Meteo API — the *same engine* runs both.
- **`MeridianRuntime.InMemory`** wires the default composition (tagged store + version/tag invalidation
  + projector); swap the store for Redis and nothing else changes.

Proven by tests: end-to-end projection, second identical run served from cache, adding an entity
fetches only the new one, invalidating an entity forces just its refetch, unknown metric errors clearly.

## Hosts + dashboard (Phase 5)

Thin query surfaces over the one Engine.

- **`Meridian.Hosts.Http`** — a minimal API: `GET /api/catalog` (describe), `POST /api/report`
  (run → `ChartView` JSON), `GET /api/showcase` (server-computed feature panels), `GET /api/stats`
  (live cache-fetch counter), plus a **self-contained dashboard** (`wwwroot/index.html`) that renders
  ChartViews with a hand-written SVG renderer — *no charting library*. Two sections: an **interactive
  builder** (every period, aggregator, gap policy, status band, grouping, chart kind) and a **feature
  showcase** gallery of 10 panels covering rolling/ACWR-with-status/gap-policies/category-axis/season/
  year-on-year-goals/aggregator-sweep/second-metric. See `docs/USAGE.md` for a worked real-world example.
- **`Meridian.Hosts.Mcp`** — the agent tool surface: two tools, `describe` (the catalog an agent needs)
  and `query` (a bounded typed request → finished ChartView). Because a query is a typed spec, not free
  SQL, every call is safe, auditable, cacheable. Transport-agnostic; a real MCP server binds these two
  methods — the protocol wrapper is intentionally out of the spike.

Both are covered by in-process integration tests (`WebApplicationFactory` for HTTP, direct calls for MCP).

## Benchmark baseline (Apple M5, .NET 10, ShortRun)

| Benchmark | Mean | Allocated |
|---|---|---|
| `AggregateMean_100k` (SoA span) | ~55 µs | **0 B** |
| `Acwr_OneSeason` (365d, 7d+28d rolling + combine) | ~70 µs | 146 KB |

Aggregation is already allocation-free. ACWR's 146 KB is the dictionary-grouping in `Rolling`/
`Resampler` — the exact thing the columnar/SIMD pass exists to remove. Run: `dotnet run -c Release
--project bench/Meridian.Benchmarks`.

## Scale benchmarks

`bench/Meridian.Bench.Scale` generates a synthetic dataset as Parquet and times one report — weekly mean
of one metric for 25 entities over a year — through every path. Measured on an i7-8700K (6 cores),
32 GB, Windows 11, DuckDB 1.5.5, at two scales:

- **171.7M rows** — 5 metrics × 2,000 entities × 2 years × hourly · 577 MB Parquet · 215k raw rows in the report's scope
- **1.03B rows** — 5 metrics × 6,000 entities × 2 years × half-hourly · 3.3 GB Parquet · 429k raw rows in scope

| Scenario | 171.7M rows | 1.03B rows | What it shows |
|---|---:|---:|---|
| Hand-written SQL | 88 ms | 93 ms | the floor: one DuckDB `GROUP BY`, results read into memory |
| Cold · raw fetch | 295 ms | 313 ms | empty cache, every raw row moved into Meridian and resampled there |
| **Cold · pushdown** | **97 ms** | **94 ms** | empty cache, resample pushed into SQL — level with hand-written SQL |
| Cold · pushdown, London weeks | 102 ms | — | as above, weeks drawn in Europe/London: the zone conversion runs inside DuckDB |
| **Warm · cached** | **0.72 ms** | **0.59 ms** | repeat report, source untouched — 120–150× faster than querying the store |
| Warm · +1 entity | 33 ms | 35 ms | only the new entity is fetched; the rest merge from cache |
| Warm · 1 entity changed | 31 ms | 33 ms | a write invalidates one entity; only its slice is refetched |
| Cold · 28-day rolling mean | 91 ms | 99 ms | daily means pushed down, rolling window per entity in the engine |

Six times the data costs almost nothing: latency tracks the rows *in scope*, not the size of the table
— consistent with DuckDB skipping Parquet row groups outside the requested entities (the generator
writes rows sorted by metric → entity → time). Pushdown makes Meridian's cold path cost about the same as writing the SQL yourself, and
everything after the first run is served from cache. The incremental scenarios are dominated by the
store's fixed per-query cost (opening Parquet metadata), not by row volume. "Cold" means Meridian's cache
is empty; the OS file cache is warm, as on a live server. Full results, including p95, are in
`bench/results/` and charted in the dashboard's **Benchmarks** section.

**Pushdown** (`IRollupPointSource`): when a report starts with a resample the source can compute exactly
(DuckDB: day / week from any start day / month buckets, in any IANA zone, with mean, sum, min, max, count,
median or last), the engine asks for one point per entity-bucket instead of every raw row. Anything
else — seasons, wall-clock-in-zone columns, custom aggregators — falls back to a raw fetch, so results
never change; parity tests check every period × aggregator × gap policy in four zones across real DST
changes.

## Time

Instants are stored and moved as UTC; calendar values (a date of birth, a match day, a daily wellness
answer) are kept exactly as given. Bucketing into days, weeks and months happens in the report's time
zone and produces calendar buckets — so a London day holds exactly that London date's readings, across
DST — and charts say which zone drew them. Sources declare how their timestamps are stored (UTC, wall
clock in a zone with a DST policy, or local dates). The full model is in [`docs/TIME.md`](docs/TIME.md).

## What the design guarantees

- **Deterministic, order-independent keys.** `PointKey.Of(a, b) == PointKey.Of(b, a)`, and
  `StableHash64()` is identical across constructions/processes. Equality is on typed values, so `Category("1")` and `Ordinal(1)` no longer collide.
- **Time is chronological, never alphabetical.** A month bucket sorts by instant, so February
  precedes August — never a `date.ToString("MMMM")` grouping.
- **DST-correct buckets.** A London "day" is 23h across spring-forward and 25h across fall-back
  (tested). UTC internal, timezone applied only at bucket boundaries.
- **Resample is one primitive** covering spreading, gap-skipping and month grouping,
  with explicit `GapPolicy` (LeaveMissing / ZeroFill / CarryForward / Interpolate), missing values
  excluded from aggregation, and entities kept independent.

## Deliberately deferred (not in Phase 0)

- The columnar/SIMD hot path — `PointBlock` exposes the SoA layout but `Resampler` still groups via
  dictionaries. This is where the benchmark-driven perf pass lands (Phase 1).
- Bucketing by the local day where each event happened (instant + stored offset), for entities that
  travel across zones — the time model leaves room for it (`docs/TIME.md`).
- Quartiles/SD/percentage aggregators, and the full metric catalog (source binding, change→tag).

## License

[MIT](LICENSE) © 2026 Stephen Wood
