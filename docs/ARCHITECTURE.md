# Architecture

How Meridian is put together and why. For time handling specifically, see [TIME.md](TIME.md).

## The shape

```
metric catalog ─┐
                ├─► report engine ─► cache (raw slices · finished views) ─► transforms ─► ChartView ─► any front-end
your data ──────┘        │                     ▲                                             ├─► REST (Hosts.Http)
 (IPointSource)          └── pushdown / batch ─┘                                             └─► agents (Hosts.Mcp)
```

You write two things for your system: a **metric catalog** (what can be asked) and an **`IPointSource`**
(how to read your store). Everything else — bucketing, caching, invalidation, transforms, presentation,
the HTTP and agent surfaces — is the library. A report is a declarative `PipelineSpec`: tenant, metric,
entities, timeframe, transforms, view.

## Packages

| Package | Responsibility |
|---|---|
| `Meridian.Core` | `Instant`, typed composite keys (`PointKey`), `Measurement` (missing is a flag, never a null), the columnar `PointBlock` with its `TimeAxis`, aggregators, deterministic hashing |
| `Meridian.Time` | `CalendarContext` (IANA zones via NodaTime, week start, seasons), periods (`Every`, `Hour`, `Day`, `Week`, `Month`, `Season`), `GapPolicy`, the `Resampler`, `StoredTime` and DST resolution |
| `Meridian.Transforms` | `ITransform` (`PointBlock → PointBlock`) and the algebra: `Filter`, `Map`, `Rekey`, `Reduce`, `PerGroup`, `Resample`, `Rolling`, `Named`; `Binary.Combine`/`Compare` for two-input joins |
| `Meridian.Semantics` | `MetricDefinition` and `IMetricCatalog` |
| `Meridian.Caching` | `IPointCacheStore` / `IKeyValueStore` backends, tag decorator, layering, `PointCache` (per-entity merge, batched loads, single-flight), version and tag invalidation |
| `Meridian.Views` | `ChartView` (series → marks, typed axes, legend, annotations), `ViewSpec`, themes, label resolvers, status rules, `ChartProjector` |
| `Meridian.Views.Json` | the source-generated JSON wire contract for `ChartView` |
| `Meridian.Engine` | `PipelineSpec`, `ReportEngine` (`RunAsync`, `RunManyAsync`), `IPointSource` / `IRollupPointSource` / `IBatchPointSource`, `ViewCache`, `MeridianRuntime` wiring |
| `Meridian.Sources.DuckDb` | DuckDB tables or Parquet in place — long (row per value) or wide (column per metric) — with pushdown and batching |
| `Meridian.Sources.MySql` | a MySQL datapoints table |
| `Meridian.Hosts.Mcp` | agent tools: `describe` the catalog, `query` a bounded typed report |
| `Meridian.Hosts.Http` (sample host) | minimal REST API and the dashboard |

Dependencies point one way: `Core ← Time ← Transforms/Semantics ← Caching/Views ← Engine ← Sources/Hosts`.

## Design principles

**The datum carries no presentation.** A point is key + measurement + time, nothing else. Colour,
labels, number formats and status colours are applied once, at projection. Reporting engines that let
presentation leak into data end up with every chart type reimplementing the pipeline.

**Time is a typed dimension, never a label.** Buckets are identified and ordered by their local start
and grain, not by formatted strings — so months never sort alphabetically, and there's one rule for
time zones and DST (see [TIME.md](TIME.md)).

**Transforms are closed under composition.** Every transform is `PointBlock → PointBlock`, so any
transform chains with any other, and chart quirks are transforms or view specs rather than forks of the
pipeline. `PerGroup` runs any transform per partition, so two athletes' rolling windows never bleed.

**The server returns finished series.** Filtering, grouping, bucketing, joins, ratios and time-zone
handling happen server-side; a client only draws. `ChartView` is chart-library-agnostic JSON, and the
same view feeds a chart, the REST API and an agent.

**Embeddable, not infrastructure.** Meridian is a set of NuGet packages you add to a system that already
owns its data — not a service to host. The per-system work is a source adapter and a catalog.

## The report path

1. **Plan.** Resolve the metric, and if the report starts with a resample the source can compute exactly
   (`IRollupPointSource.CanRollup`), plan to push it down — the remaining transforms run in the engine.
2. **View cache.** If every input has a stable identity (`ICacheIdentity`), look up the finished view.
   The key includes the data version of every entity involved, so a write makes old views unreachable.
3. **Raw cache.** Probe per `(metric, entity)` slice; load only the misses — in one `IBatchPointSource`
   call across metrics when running many reports (`RunManyAsync`) — and store each slice.
4. **Transform and project.** Apply the remaining transforms, project to a `ChartView`, cache the view.

## Caching and invalidation

The store contract is small: a backend implements get/set/remove (`IKeyValueStore`), and the
`TaggedStoreDecorator` adds tag-group eviction on top, so any key-value store gains tags for free. Keys
are deterministic hashes of the logical request (tenant, metric, entity, timeframe, rollup signature,
data version) — never per-instance ids.

Writes call one hook, `ICacheInvalidator.InvalidateAsync(ChangeScope)`, with composable strategies:

- **Version bump** — the correctness backbone. The data version is in every key, so a bump makes stale
  entries unreachable on any backend, even one that can't evict, and survives a missed event.
- **Tag eviction** — reclaims raw slices immediately.
- **View cache** — drops the finished views an entity fed.

`StoreConformance` is one test suite every cache backend must pass (determinism, invalidation, partial
hits, tag grouping, single-flight).

## Sources

`IPointSource.FetchAsync` returns raw points for one metric, some entities and a timeframe. Two optional
capabilities make large stores fast:

- **`IRollupPointSource`** — compute a leading resample where the data lives. The engine only uses it
  when the source says it can do so *exactly*; parity tests compare every pushed-down result with the
  in-engine one.
- **`IBatchPointSource`** — fetch several metrics in one round trip.

Sources declare how their timestamps are stored (`StoredTime`: UTC, wall clock in a zone, or local dates)
and normalise once, at fetch.

## Testing

Property-style and golden tests for keys, time and transforms; parity tests for every pushdown path
(period × aggregator × gap policy × zone, across real DST changes); a shared conformance suite for cache
backends; in-process integration tests for the hosts. Changes that touch pushdown or time are checked by
deliberately breaking them and confirming the tests fail. Warnings are errors.
