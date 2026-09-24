# Meridian — a datapoint & reporting framework

> **Meridian** — longitudinal data lives on *meridians* (lines of longitude); a meridian is also a
> *peak*. A framework for turning points-over-time into insight.

**Goal:** a clean, greenfield set of .NET 10 libraries: **define metrics once → transform →
present → cache → query (by humans and agents)** — with time/longitudinal data as a first-class
citizen and a keen eye on performance.

**Non-goals:** coupling to any charting library, or to any particular data store. No interop
constraint — we target .NET 10 cleanly.

---

## 1. Common reporting-engine pitfalls (the design rationale)

Hand-grown reporting engines tend to converge on the same problems. Every decision below is a
response to one of them.

### 1.1 Tangles to design *out*
- **God-object datum.** The data point carries its value **and** hex colour, CSS class
  (`"danger"/"warning"/"success"`), charting-library attributes, tooltip HTML, culture-formatted
  strings — no boundary between the numbers and how they look.
- **One method does everything** — maps value→point, *reconstructs dates from integer key ids*,
  formats numbers, builds tooltip HTML, picks colours, recurses groupings, applies traffic-lights.
  Transform + presentation + alerting fused.
- **Alerting masquerades as colouring** — threshold/SD logic writes CSS strings onto data.
- **Transforms do data access** — overlay transforms query the database and emit hex+HTML.
- **Time smuggled through strings/ints** — group identity is `date.ToString("MMMM")` with a parallel
  int sort key; aggregators `DateTime.Parse` dates back out of labels; key ids reused as ticks;
  point equality on a display name → collisions.
- **Chart type leaks into transforms** — zero-points dropped for pie/doughnut, render hints decided
  inside the data layer.
- **Divergent providers reimplement everything** — difference/correlation/stacked charts bypass the
  shared vocabulary and service-locate their own palette.
- **Two of everything, mid-migration** — competing aggregation enums, request contracts, provider
  hierarchies, rendering pipelines.

### 1.2 Good ideas to *keep*
- **Multi-series as the unifier** — almost every chart type fits one `series → points → axes` graph.
- **Per-entity partial-hit caching** — track which per-entity slices are cached, fetch only the
  *missing* entities and merge. Fetch cost is O(metrics × entities) round-trips, so this matters a lot.
- **A structural cache key** — `(metric, tenant, start, end, non-entity keys)`, as a real
  deterministic hash — never a per-instance `Guid`.
- **The "finished series" boundary** — the server returns already-aggregated numbers; the client
  only draws. That is the correct contract.

### 1.3 The caching defect to *fix*
Time-only invalidation: writes do not evict, so stale data is served for the whole sliding-expiry
window. **The single most important thing to get right.**

### 1.4 The client/server boundary
Front-ends commonly end up doing filtering, group-by, sum/count, date-bucketing, ID→name joins,
distinct+sort, ratios, pivot/subtotals/row-percentages, timezone→UTC and coordinate normalisation in
chart-specific JS, duplicated across files. **All of these belong server-side.** The client should
receive finished series and do only chart assembly + cheap interactive re-sort.

---

## 2. The dream, made concrete

> "`IDatapoint` passed from one transformer to another in a group, to be aggregated, filtered,
> grouped, compared, used in a calculation."

A **composable stream algebra**: `Points → Points`, closed under composition; a "group" is a
transform that partitions the stream so downstream transforms run per partition. Three refusals
keep it clean:

1. **The datum never carries presentation.** A point is key + measure + position.
2. **Time is a typed dimension, never a label.** Identity and order from a typed instant/interval;
   the display string is produced only at render.
3. **A transform is closed under composition.** Rendering is the *terminal* transform, not a field.

---

## 3. Positioning — why this is more than a dataframe

The transform algebra is table stakes (LINQ, Cube, dbt/MetricFlow, Looker exist). The value is what
the algebra is *wrapped around*:

- **The semantic / metric layer is the IP** — mapping a messy schema into named, typed, unit-aware
  metrics with aggregation rules, entity relationships, time semantics. "Define once → charts + API
  + agent query + narrative." This is what incumbents monetise and what a consultant is really paid
  to build.
- **Embeddable, not infrastructure** — a NuGet you drop into a system that already sits on the data,
  point at the store, author a catalog. Cube/dbt are services you host; Meridian is a dependency.
- **Longitudinal correctness is the wedge** — generic BI is bad at rolling loads, acute:chronic,
  season-relative windows, per-entity baselining, gaps, alignment. The Time package is the
  *differentiator*, not just a cleanup.

**Consulting shape this creates:** ~80% reusable (algebra, time, caching, views, MCP), ~20% per
client (a source adapter + a metric catalog). Each engagement = "point it at your store, author your
metrics." The catalog is a declarative deliverable the client can extend. MCP is the day-one sales
demo: "ask your data anything" before a single dashboard exists.

**Caution:** the semantic layer is where scope explodes (governance, row-level security, definition
versioning, lineage). The wedge is *narrow and deep* — longitudinal + embeddable + agent-native —
not broad. Drift toward "general BI platform" and it dies against incumbents.

---

## 4. Package architecture

```
Meridian.Abstractions        // interfaces + primitive value types. Zero deps.
Meridian.Core                // Point, Series, PointBlock (columnar), typed keys, temporal primitives
Meridian.Time                // calendars, seasons, periods, resampling, gap policy  ← the hard part
Meridian.Transforms          // the transform library (filter/group/aggregate/compare/compute/…)
Meridian.Semantics           // metric/entity/dimension catalog + change→tag mapping   ← the value layer
Meridian.Caching             // cache abstractions: deterministic keys, tags, invalidation hooks, strategies
Meridian.Caching.InMemory    // backend: IMemoryCache
Meridian.Caching.Redis       // backend: native tag SETs
Meridian.Caching.SqlServer   // backend: KeyTags table
Meridian.Views               // chart-agnostic view model + data→view projection (theme/format here)
Meridian.Views.Json          // System.Text.Json source-gen contract for any front-end
Meridian.Engine              // pipeline-spec, dispatch, orchestration
Meridian.Hosts.Mcp           // agent query surface (MCP tools over Engine)
Meridian.Hosts.Http          // REST query surface over Engine
```

Dependency direction:
`Abstractions ← Core ← {Time, Transforms, Semantics} ← {Caching, Views} ← Engine ← Hosts`.
Data sources implement `Meridian.Abstractions.IPointSource` and live in the host.

Test/bench projects (see §14–15): `Meridian.<Pkg>.Tests`, `Meridian.Caching.Conformance`,
`Meridian.Benchmarks`.

---

## 5. Core point model

Principle: **immutable, allocation-light, columnar on the hot path**. Row ergonomics for authoring;
Struct-of-Arrays for execution.

```csharp
public readonly record struct Measurement(double Value, MeasureFlags Flags)
{
    public static readonly Measurement Missing = new(double.NaN, MeasureFlags.Missing);
    public bool IsPresent => (Flags & MeasureFlags.Missing) == 0;   // missing is a flag, never a null smuggle
}

public readonly struct PointKey : IEquatable<PointKey>, IComparable<PointKey>
{
    public ImmutableArray<KeyPart> Parts { get; }   // typed parts: EntityRef | TemporalKey | CategoryKey | OrdinalKey
    // structural equality + natural ordering on the TYPED value — no string identity, no parallel sort key
}

public interface IPoint
{
    PointKey Key { get; }
    Measurement Measure { get; }
    Instant? At { get; }     // longitudinal position; null for non-temporal
    Unit Unit { get; }
}
```

- **`double`, not `decimal?`** — decimal was a formatting hangover; round at presentation.
- **Columnar `PointBlock`** (parallel `values[] / instants[] / keys[]`) is the execution unit;
  `IPoint` is the row view when materialising/authoring.
- **Typed keys** replace string groups — kills `ToString("MMMM")` identity, parallel sort keys,
  and `Equals(Name)` collisions.

---

## 6. Time / longitudinal (the tricky bit, done once)

```csharp
public readonly record struct Instant(long UtcTicks);                 // always UTC internally
public readonly record struct DateInterval(Instant Start, Instant End);

public interface IPeriod
{
    DateInterval BucketFor(Instant i, CalendarContext ctx);
    IEnumerable<DateInterval> Buckets(DateInterval range, CalendarContext ctx);
}   // Day, Week(weekStart), Month, Quarter, Season(domainCalendar), Rolling(window, step)

public enum GapPolicy { LeaveMissing, ZeroFill, CarryForward, Interpolate }
public sealed record CalendarContext(DateTimeZone Zone, DayOfWeek WeekStart, ISeasonCalendar Season); // NodaTime zone
```

- **UTC internal; timezone applied at bucketing.** Buckets are local calendar positions, and calendar
  values (dates of birth, match days) are never converted. The full model is in `docs/TIME.md`.
- **`Resample(period, aggregator, gapPolicy)`** is the core primitive — replaces string-round-trip
  aggregation, ad-hoc spreading/gap-skipping, and month-name grouping. Points positioned at bucket
  start/interval, labelled only at render.
- **Season is a first-class calendar**, not `AddMonths` loops.
- **Alignment is a time operation** — difference (two timeframes) and correlation (X/Y) align on the
  temporal axis here, not hand-rolled per provider.

---

## 7. Transforms (the algebra)

```csharp
public interface ITransform { PointBlock Apply(PointBlock input, TransformContext ctx); }
public interface IGroupingTransform : ITransform { IReadOnlyList<PointGroup> Partition(PointBlock input, TransformContext ctx); }
```

| Transform | Replaces / covers |
|---|---|
| `Filter` | client-side row filtering |
| `GroupBy` | string-keyed grouping |
| `Aggregate` | competing aggregation enums, unified |
| `Resample` | date bucketing / gap spreading / month grouping |
| `Rolling` | rolling averages (often client-only) |
| `Compute` | calculated metrics, ratios (goals-per-fixture) |
| `Compare` | difference charts (abs / % over two timeframes) |
| `Correlate` | correlation charts (zip on aligned time + Pearson r) |
| `Normalise` | normalisation trendlines |
| `Rank` / `TopN` | distinct+sort of dimension members |
| `Pivot` | cross-tab subtotals + row-percentages |
| `Annotate → Status` | traffic-lights as `enum Status`, not CSS — alerting decoupled from colour |

- **One extensible aggregator registry** (`IAggregator`): Mean/Sum/Min/Max/Median/Quartiles/SD/
  Mode/Count/Latest/Percentage — one registry, not competing enums.
- **Chart quirks become transforms**, not provider forks: "pie drops zeros" = `Filter`, "scatter
  zips X/Y" = `Correlate`. This is how "unify toward multi-series" lands: one pipeline, differences
  expressed as transforms + a view spec.

---

## 8. Semantics — the metric catalog (the value layer)

```csharp
public sealed record MetricDefinition(
    MetricId Id, string Name, Unit Unit,
    IAggregator DefaultAggregation,
    ImmutableArray<DimensionRef> ValidDimensions,
    TimeGrain NativeGrain,
    SourceBinding Source);              // how to fetch it from THIS client's store

public interface IMetricCatalog
{
    IReadOnlyCollection<MetricDefinition> Metrics { get; }
    bool TryResolve(MetricId id, out MetricDefinition def);
    IReadOnlyCollection<CacheTag> TagsFor(ChangeScope scope);   // write → which cache groups to drop
}
```

- **Declarative, authored, validated** — bad definitions rejected at load. This is the ~20% bespoke
  work per integration; everything above it comes for free.
- **Powers three surfaces from one definition**: the Engine (execution), the MCP `describe` tool
  (agent discovery), and the caching `ChangeScope → tags` mapping (§9).

---

## 9. Caching — deterministic keys, tag groups, pluggable backends

Hangfire model: a *small* storage contract, one core package owning the semantics, swappable backend
packages owning the haul.

**Core owns three concepts; backends own none:**

```csharp
// 1. Deterministic keys — stable hash over the NORMALISED request (sorted entities, invariant, tenant-scoped)
public static class CacheKeyBuilder { public static CacheKey For(PipelineSpec spec, long dataVersion); }

// 2. Groups of keys = tags — the invalidation handles (entity:player:42, metric:acwr, tenant:acme)
public readonly record struct CacheTag(string Value);

// 3. The invalidation hook — write path raises a scope; catalog maps it to tags; strategy evicts
public interface ICacheInvalidator { ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct); }
```

**The small storage contract every backend implements:**

```csharp
public interface IPointCacheStore
{
    ValueTask<CacheEntry?> GetAsync(CacheKey key, CancellationToken ct);
    ValueTask SetAsync(CacheKey key, CacheEntry entry, IReadOnlyCollection<CacheTag> tags,
                       CacheEntryOptions opts, CancellationToken ct);
    ValueTask RemoveAsync(CacheKey key, CancellationToken ct);
    ValueTask RemoveByTagAsync(CacheTag tag, CancellationToken ct);   // group-eviction primitive
}
```

- **Batteries included:** a `TaggedStoreDecorator` maintains a `tag → keys` index on top of any
  plain KV store, so a minimal backend implements only Get/Set/Remove and inherits tagging.
- **Injectable layers × pluggable backends** compose: a *layer* = `store + policy`; L1 = InMemory,
  L2 = Redis, stacked as decorators. `services.AddMeridianCache(c => c.UseRedis(conn).WithLayer(inProcess: true));`
- **Invalidation strategies behind the one hook:**
  - `VersionBumpStrategy` — data-version is in the key; a bump changes the key everywhere at once, so
    even a dumb in-process L1 stops serving stale data with zero coordination. **Correctness backbone;
    works on any backend, survives a missed event.**
  - `TagEvictionStrategy` — active `RemoveByTag` for immediate purge + memory reclaim, where the
    backend supports it. **Optimisation on top.**
- **Event-driven eviction (decided):** the metric-write path raises a domain event
  (`MetricValuesChanged(scope)`); a handler calls `ICacheInvalidator.InvalidateAsync(scope)`, the
  catalog resolves `scope → tags`, and the strategy evicts. Events give *immediate* consistency;
  version-bump is the backstop if an event is dropped. The write path stays ignorant of cache keys.
- **Per-entity grain preserved** — cache at `(metric, entity, timeframe-bucket)` so multi-player
  reports reuse per-player slices (the per-entity partial-hit merge).
- **Cache the compact `PointBlock`, not the view** — projection is cheap and per-request/theme.
- **Fixes the defect:** metric writes call `InvalidateAsync`; no stale window waiting on expiry.
- **Caution:** the `tag → keys` index is write-amplifying and needs its own bounding/eviction —
  benchmark early (Redis native, SQL fine, naïve in-memory must be capped).

---

## 10. Views — chart-agnostic, serialisable

Projection is the **only** place colour, format, label, and status→colour happen.

```csharp
public sealed record ChartView(
    IReadOnlyList<SeriesView> Series, IReadOnlyList<AxisView> Axes,   // axes typed: Temporal|Linear|Category
    LegendView Legend, IReadOnlyList<AnnotationView> Annotations);    // fixture lines/injury bands/targets = DATA

public sealed record MarkView(
    string Label, double? Value, double? X, double? Y, double? Z,
    long? At,               // epoch millis, computed once here
    SemanticStatus Status,  // enum, not a CSS class
    string? ColorToken);    // resolved from theme by role/index, not raw hex on the datum

public interface IChartProjector { ChartView Project(PipelineResult r, ViewSpec spec, ITheme theme); }
```

- **Chart-agnostic JSON** (`Meridian.Views.Json`, source-gen). Any front-end maps `ChartView` to its
  config; the library never references a charting lib. The "finished series" contract, applied to
  every report.
- **`ViewSpec` is the per-chart difference** — line vs column vs pie vs box-plot is a spec, not a
  provider.
- **Colour by theme** — hard-coded hex (win/lost/drew, injury bands) become theme tokens.

---

## 11. Engine & Hosts

```csharp
public sealed record PipelineSpec(SourceSpec Source, ImmutableArray<ITransform> Transforms, ViewSpec View);
public interface IReportEngine { Task<ChartView> RunAsync(PipelineSpec spec, ITheme theme, CancellationToken ct); }
// Run = resolve IPointSource → cache-aware fetch → apply transforms → project
```

One registry, one dispatch mechanism. A report
item *declares* a spec; new report = new spec (often just a new `ViewSpec`).

**Hosts = thin query surfaces over the same Engine:**
- `Meridian.Hosts.Mcp` — two tools: `describe`/`list_metrics` (returns the catalog so an agent knows
  what it can ask) and `query` (a constrained spec → finished `ChartView`). Bounded, auditable,
  cacheable; enables safe agentic drill-down ("break it down by position"). The same `ChartView`
  feeds a chart, an MCP result, *and* a prose summariser.
- `Meridian.Hosts.Http` — REST equivalent.

---

## 12. Performance posture (.NET 10)

- **Columnar `PointBlock`** (SoA) execution; aggregations over `Span<double>`, SIMD via
  `System.Numerics.Vector` / `TensorPrimitives` where it pays.
- **Source-generated `System.Text.Json`** — no runtime reflection.
- **`readonly record struct` / `ImmutableArray` / `FrozenDictionary`** for registries; pool
  `PointBlock` buffers.
- **`IAsyncEnumerable` streaming fetch** for large entity sets; cache the compact block, not output.
- **Benchmark from day one** (§15).

---

## 13. (reserved)

---

## 14. Testing strategy (per layer)

**Tooling:** xUnit on Microsoft.Testing.Platform; Shouldly for assertions; **CsCheck** for
property-based tests; **Verify** for snapshot/approval; **Testcontainers** for Redis/SQL backend
conformance; prefer real in-memory fakes over mocks. Coverage gate in CI; mutation testing
(**Stryker.NET**) on Core + Time + Transforms, the layers where a silent numeric bug is worst.

| Layer | What to test | Technique |
|---|---|---|
| **Core** | key canonicalisation is deterministic & order-independent; equality/ordering invariants; `Measurement` missing semantics; `PointBlock` row↔columnar round-trip | property-based (CsCheck) |
| **Time** ⚑ | resample bucket boundaries; DST transitions; week-start policies; season boundaries; each `GapPolicy`; alignment of two timeframes | property-based + golden fixtures for known calendars. **Heaviest investment — this is the make-or-break layer.** |
| **Transforms** | each transform in isolation; algebraic laws (composition associativity, identity, `sum` over partition = sum of parts); aggregator correctness incl. quartiles/SD | property-based + **known-good statistical fixtures** (canonical values) |
| **Semantics** | invalid definitions rejected; metric resolution; `ChangeScope → tags` mapping correctness | unit |
| **Caching** | see the conformance suite below | shared suite × all backends |
| **Views** | projection is deterministic; theme/format/culture applied correctly; status→colour | snapshot (Verify) on `ChartView` JSON |
| **Engine** | source→cache→transform→project wired end to end over an in-memory source; the three proof reports | integration |
| **Hosts.Mcp** | tool schema matches spec; catalog→`describe`; a scripted "agent" spec → expected series | contract + integration |

**Caching conformance suite** (`Meridian.Caching.Conformance`) — the Hangfire trick: **one test
suite every backend must pass**, run against InMemory, Redis (Testcontainers), SqlServer
(Testcontainers). It asserts the behaviours naive caches lack:
- **Determinism** — identical logical request → identical key across processes/cultures.
- **Invalidation correctness** — the write→read staleness test (version bump *and* tag eviction);
  the regression test for stale reads.
- **Partial-hit merge** — a 3-entity request after a 2-entity request fetches only the 1 missing.
- **Tag grouping** — `RemoveByTag` drops exactly the tagged entries, nothing else.
- **Concurrency** — cache stampede → single-flight (one source fetch under N concurrent readers).

---

## 15. Benchmarking

`Meridian.Benchmarks` — a BenchmarkDotNet project with `[MemoryDiagnoser]` (allocations are a
first-class metric, targeting near-zero on hot aggregations).

**Micro-benchmarks (hot paths):**
- `Aggregate` over `PointBlock` at N = 1e3/1e5/1e6 (scalar vs SIMD).
- `Resample` daily→weekly across a season.
- `GroupBy` + `Aggregate` (the report core).
- `ChartView` projection + JSON serialisation.
- Cache `Get`/`Set` per backend (InMemory vs Redis), incl. `tag → keys` index write cost.

**Macro-benchmark (realistic scenario):** a full report — 30 metrics × 200 entities × one season
daily, resampled weekly, projected — measured **cold** and **warm cache**. This is the number that
tells the "keen eye on performance" story and exposes the O(metrics × entities) fetch path.

**Regression gate:** commit baseline results; run the macro-benchmark in CI and fail on
>X% regression against baseline. Publish a perf budget per operation (e.g. weekly resample of a
season < N µs; warm-cache full report < M ms).

---

## 16. Build sequence

Foundation first — get longitudinal right before anything depends on it. Every phase ships with its
tests; benchmarks start at Phase 1.

- **Phase 0 — Core + Time (+ Semantics sketch).** Point model, typed keys, `PointBlock`, and the
  whole Time package. Sketch `MetricDefinition` alongside, because the catalog shape constrains what
  the key model must carry. Property + golden tests on bucketing/alignment/DST. *Make-or-break phase.*
- **Phase 1 — Transforms.** The `ITransform` library over `PointBlock`; unified aggregator registry;
  `Pipeline` composition. Tests against canonical statistical fixtures. First
  micro-benchmarks.
- **Phase 2 — Caching.** `IPointCacheStore` + decorator layers; deterministic keys; per-entity merge;
  version + tag strategies; **event-driven eviction**. **Conformance suite + InMemory + Redis backends.**
  Prove staleness gone with a write-event→read test.
- **Phase 3 — Views.** `ChartView`, `IChartProjector`, theme/formatter, JSON contract. Snapshot tests.
- **Phase 4 — Engine + Semantics + proof.** `PipelineSpec` dispatch; real metric catalog; reproduce
  three behaviours end to end — multi-series line, difference (two-timeframe %), correlation
  (X/Y + Pearson) — proving the algebra expresses divergent chart types without forking.
  Macro-benchmark cold vs warm.
- **Phase 5 — Hosts.** `Meridian.Hosts.Http` then `.Mcp`; contract tests; the day-one "ask your data"
  demo.
- **Phase 6 (later) — adapters.** Source adapters for common stores (SQL, warehouse, Parquet/DuckDB).

---

## 17. Decisions taken (opinionated, not a menu)

- Name: **Meridian**.
- `double` + missing-flag, not `decimal?`.
- Columnar hot path (`PointBlock`) + row view for authoring.
- Time is a dedicated package; UTC internal, tz/label at the edge.
- One aggregator registry; chart quirks are transforms + view specs, not providers.
- Alerting = `Status` enum at `Annotate`; colour = theme at projection.
- Caching = small `IPointCacheStore` contract + pluggable backend packages (Hangfire model).
  **Redis backend first** (+ InMemory for L1/tests); invalidation via one hook, driven by **domain
  events on metric write**, with swappable version-bump (backbone) / tag-eviction (immediate) strategies.
- Semantic catalog is the per-integration deliverable and powers Engine + MCP + invalidation.
- Presentation = chart-agnostic `ChartView` JSON; front-end owns the charting lib.
- Testing: property-based on Core/Time/Transforms, one conformance suite across cache backends,
  benchmarks with a CI regression gate.

## 17b. Future directions (parked — not scoped yet)

These extend the algebra along the time axis; they belong after the core layers land but are worth
keeping in view because they shape what the transform/time model must eventually support.

- **Year-on-year comparison** — align the same season-relative window across seasons (round N of
  2024/25 vs 2025/26) and diff. Already half-expressible: `Compare` + a season-relative `IPeriod`
  that buckets by "offset within season" rather than absolute date. The Time package's season
  calendar is the enabler; the missing piece is a *phase-aligned* period.
- **Forecasting / projecting next season** — extend a series forward: rolling-trend extrapolation,
  seasonal-naïve (last season's shape), or a fitted model. Shape: a transform `Project(horizon,
  model)` that emits points with `At` in the future and a `MeasureFlags.Estimated` flag (already in
  the model) so projected data is visually and semantically distinct from observed.
- **Scenario modelling** — "what if load rises 10%": a transform that takes a baseline series + a
  scenario spec and emits an alternate series, so scenarios compose with the same viz/compare
  machinery. Pairs naturally with forecasting (project, then perturb).

Common thread: all three are *transforms that produce estimated/future points*, so the estimated
flag, the season calendar, and `Compare`/`Combine` are the foundations — no new architecture, just
new transforms. Revisit once Views + Engine exist so a projection can be rendered and queried.

**Testing dashboard / harness.** A small tool to exercise the stack interactively: pick a metric +
entities + timeframe + view spec, run the Engine, and render the returned `ChartView` live. Because
`ChartView` is chart-agnostic JSON, the viewer is a thin front-end (or even a self-contained HTML
artifact) that maps it to any charting lib. Valuable as (a) a dev harness, (b) the day-one demo. Its
data comes from pluggable `IPointSource`s — including **public-API sources** (e.g. Open-Meteo daily
weather = free, no-auth longitudinal series) so the whole pipeline can be shown on real data without a
customer DB. Natural home: `Meridian.Hosts.Http` (Phase 5) serving the Engine + a static viewer page;
a couple of ready-made public-API sources ship as samples.

**Real sports-stats public source.** Beyond weather, a no-auth sports API makes the demo land with the
target audience. Candidates: TheSportsDB (free, no key; teams/events/results), an ESPN hidden JSON
endpoint (scoreboards), or a football-data feed (key-gated). Model each event as a dated point (goal =
value 1, keyed by team/player + venue), then the existing season/`Reduce`/`Compare` machinery gives
"this season vs last" on real data. Ships as a `HttpSportsSource` sample. Caveat: third-party APIs are
flaky and rate-limited, so keep it a sample, never a test dependency.

**Benchmark vs a BI / semantic-layer tool.** The whole point of `IPointSource` is that Meridian drops
into a system that already owns the data. A concrete proof-of-value: load one dataset into a store,
implement an `IPointSource` over it, author a metric catalog matching a handful of metrics defined in
a comparable tool (e.g. Cube, Malloy), and run the same report both ways to compare latency and cost.
Meridian's per-entity cache + versioned invalidation is exactly where it should win on repeat/interactive
queries. This is the most direct path from "spike" to "is this worth adopting" — a measured head-to-head,
not a rewrite. Scope it as one adapter + ~5 metric definitions + a timing harness.

## 18. Open questions worth your call before Phase 0

Resolved: **distributed cache** → Redis backend first (pluggable, so not a lock-in), InMemory for
L1/tests. **Invalidation** → build the hook; drive it with domain events raised on metric write,
version-bump as backstop. Remaining:

1. **Front-end target** — Highcharts, Recharts, D3? `ChartView` is agnostic, but a
   fixed target lets us ship a reference adapter and validate the shape.
2. **Season calendar source of truth** — config or DB? The Time package needs one authority.
3. **Multi-tenancy in the cache key** — confirm tenant scoping is part of the content hash
   (must be, for per-tenant stores).
