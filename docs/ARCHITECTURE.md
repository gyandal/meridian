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
| `Meridian.Transforms` | `ITransform` (`PointBlock → PointBlock`) and the algebra: `Filter`, `WhereIn` / `WhereNotIn` / `WhereValue`, `Map`, `Rekey`, `Reduce`, `PerGroup`, `Resample`, `Rolling`, `Named`; `Binary.Combine`/`Compare` for two-input joins |
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

## Dimensions: slicing one metric many ways

A point's key is the entity plus any other **dimensions** the data carries — venue, competition, match
type. A metric declares the dimensions it can be sliced by (`MetricDefinition.ValidDimensions`); a source
returns every one it can read; each report declares the ones it keeps (`PipelineSpec.WithDimensions`) and
the engine folds the rest away. So a dashboard showing goals by player, by venue and by month runs three
reports over **one** cached fetch.

Grouping is declarative — `Transform.Total(Sum, venue)` for goals by venue, `Transform.GroupBy(Sum,
venue)` to keep the time axis (goals by venue per month) — so it caches and can be expressed over an API.
When bucketing is pushed down, the database groups by exactly the declared dimensions.

Filtering is declarative too, and comes in two kinds that behave differently:

- **By key** — `Transform.WhereIn(venue, "Home")`, `WhereNotIn(…)`: what a point *is*. Its result doesn't
  depend on where it runs, so a key filter written before a resample is moved after it, and the resample
  still pushes down — the database buckets by player and venue, the engine keeps the home buckets. (Pushing
  the predicate itself into SQL is a later optimisation; the rows it would save are already aggregated.)
- **By value** — `Transform.WhereValue(min, max)`: what a point *measures*. Order matters (a match with
  45+ minutes is not a month with 45+ minutes), so it runs exactly where it's written.

Filtering or grouping by a dimension the report folds away is an error, not an empty chart: declare it
with `WithDimensions`.

Attributes rarely sit on the fact row: the venue belongs to the match, a player's team changes over time.
Meridian doesn't model joins; make the source's relation a view that joins them onto each row — for
time-varying attributes, the value *as of the row's date* — and map the resulting columns.

## Derived metrics

A derived metric is defined once in the catalog from stored ones — goals per 90 is
`MetricDefinition.Ratio(goals-per-90, …, numerator: goals, denominator: minutes, scale: 90, …)` — and then
used in any report like a stored metric. The rules that make it right:

- **Ratio of totals.** Each input is aggregated with the formula's aggregation (sum) through every
  aggregating step of the report — resample, rolling window, `GroupBy`, `Total` — and the division happens
  after the last of them. "Goals per 90 by venue per month" divides monthly goal totals per venue by monthly
  minutes per venue; it never averages per-match ratios (where a one-goal, ten-minute cameo would swamp a
  season). Transforms after the last aggregation apply to the ratio. A value filter before then is an
  error — it would filter goals and minutes each by their own values.
- **No rows is zero, no denominator is no value.** Goals are events, so a bucket with minutes but no goal
  rows is 0 goals per 90. A bucket with no (or zero) minutes has no value, even with a zero-fill gap policy.
- **Inputs are ordinary fetches.** Each input goes through the cache, pushdown and batching like any
  report, so goals and minutes arrive in one query and are shared with plain goals and minutes charts.
  A derived chart's cache entry depends on its inputs' data versions, so it's rebuilt when either changes.

The catalog rejects bad definitions when it's built: unknown or derived inputs, mismatched time kinds, and
dimensions an input can't be sliced by.

## Multi-series charts

A `ChartSpec` is a list of `SeriesSpec`s, each an ordinary report plus a display name and a value axis
(primary or secondary). The engine runs every part of every chart in one `RunManyAsync` — so goals,
minutes and goals per 90 are one query — and `ChartComposer` combines the parts: each keeps its own chart
kind (columns, line, area), series get distinct colours and names ("Goals", or "Goals · player 7" when a
part has a series per entity), and each side gets one value axis built from its parts. Parts must share
the x-axis. A dashboard runs all its charts through `RunChartsAsync` to share fetches across them.

## Dashboards

A `Dashboard` is a set of charts defined against a shared `DashboardContext` — tenant, entities,
timeframe — rather than hard-coded ones. `RunDashboardAsync` runs every series of every chart together
(shared fetches, batched queries); focusing on one player is the same dashboard with
`context.Focus(player)`, and because data is cached per entity it needs no new queries.

Products that let users build dashboards store them as a `DashboardDefinition`: plain JSON with
declarative transforms (`resample`, `rolling`, `groupBy`, `total`, `where`, `range`), views (`kind`, `x`, `seriesBy`) and
axes. `ToDashboard()` validates it and reports problems by JSON path (`charts[1].series[0].transforms[2].kind`),
ready to show in an editor. Transforms that are code (a lambda `Filter`) can't be stored, by design.

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
