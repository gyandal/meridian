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
| `Meridian.Transforms` | `ITransform` (`PointBlock → PointBlock`) and the algebra: `Filter`, `WhereIn` / `WhereNotIn` / `WhereValue`, `ShareOf`, `Map`, `Rekey`, `Reduce`, `PerGroup`, `Resample`, `Rolling`, `Named`; `Binary.Combine`/`Compare` for two-input joins |
| `Meridian.Semantics` | `MetricDefinition` and `IMetricCatalog` |
| `Meridian.Caching` | `IPointCacheStore` / `IKeyValueStore` backends, tag decorator, layering, `PointCache` (per-entity merge, batched loads, single-flight), version and tag invalidation |
| `Meridian.Views` | `ChartView` (series → marks, typed axes, legend, annotations), `ViewSpec`, themes, label resolvers, status rules, `ChartProjector` |
| `Meridian.Views.Json` | the source-generated JSON wire contract for `ChartView` |
| `Meridian.Engine` | `PipelineSpec`, `ReportEngine` (`RunAsync`, `RunManyAsync`), `IPointSource` / `IRollupPointSource` / `IBatchPointSource`, `ViewCache`, `MeridianRuntime` wiring |
| `Meridian.Sources.Sql` | the SQL engine every database source shares: layouts, dimensions, batching, exact time handling and pushdown, through a `SqlDialect` |
| `Meridian.Sources.DuckDb` | DuckDB tables or Parquet in place — long (row per value) or wide (column per metric) — with pushdown and batching |
| `Meridian.Sources.PostgreSql` | PostgreSQL / TimescaleDB tables and views — long or wide — with pushdown and batching |
| `Meridian.Sources.SqlServer` | SQL Server and Azure SQL tables and views — long or wide — with pushdown and batching |
| `Meridian.Sources.MySql` | MySQL 8 tables and views — long or wide — with pushdown and batching |
| `Meridian.Sources.ClickHouse` | ClickHouse tables and views — long or wide — with pushdown and batching |
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

**Levels are semi-additive.** Open issues per family and priority add up across priorities, but a month's
open issues aren't the sum of its days. Declare such a metric `Additivity = Additivity.SemiAdditive`: when a
report drops a dimension (keeps family, not priority), the engine sums the rows that fold together *at the
same time* before any transform runs, so `Resample(Month, Last)` is the month-end total across priorities
rather than one priority's count. The parts must share a timestamp — a snapshot per day, say — to be summed.
A pushed-down rollup would group by the kept dimensions and apply Last to the parts, so for a semi-additive
metric that drops a dimension, only a Sum is pushed down; any other aggregator runs in the engine, and the
results are identical either way.

Attributes rarely sit on the fact row: the venue belongs to the match, a player's team changes over time.
Meridian doesn't model joins; make the source's relation a view that joins them onto each row — for
time-varying attributes, the value *as of the row's date* — and map the resulting columns.

## Aggregators and rankings

`Aggregators` has sum, mean, min, max, count, median, first, last, `StdDev` and `Variance` (sample, n − 1)
and `Percentile(p)` (linear interpolation, as `PERCENTILE.INC` / `quantile_cont`), resolvable by name —
`p90`, `p99.5` — from APIs and dashboard definitions. Every one is pushed down to DuckDB, and the parity
suite checks each against the in-engine result across zones, periods and gap policies. An aggregate with no
value — the spread of a single reading — is a gap, as SQL's NULL is; NaN is never a value anywhere.

Two things are deliberately not aggregators:

- **Distinct count.** Counting distinct *values* of a measure is rarely meaningful; what's usually meant is
  distinct *things* — players who scored this month — and that's a group then a count:
  `Resample(Month, Sum)`, then `GroupBy(Count)` counts the players with a value in each month.
- **Weighted mean.** Σ(value × weight) ÷ Σweight needs each row's product, like any product of metrics;
  put the product in the source and define a ratio of it to the weight.

`Transform.Top(n, rankBy)` and `Bottom(n, rankBy)` keep the n keys with the highest or lowest score, and all
of their points: after `Total(Sum, player)` that's the top scorers as bars; on monthly points it picks the
players whose months sum highest and keeps each of their months, for a line chart of the top five. `per`
ranks within groups (the top three at each venue). Exactly n are kept — ties go to the lower key — and keys
with no score aren't ranked. A ranking reads values, so for a derived metric it must come after the last
aggregation; before it, each input would be ranked on its own.

Ranking a rate has a trap Meridian doesn't yet guard: a ten-minute cameo with a goal is 9 goals per 90.
Qualifying thresholds on a ratio's denominator are on the roadmap.

## Derived metrics

A derived metric is defined once in the catalog from stored ones — goals per 90 is
`MetricDefinition.Ratio(goals-per-90, …, numerator: goals, denominator: minutes, scale: 90, …)` — and then
used in any report like a stored metric. The rules that make it right:

- **Ratio of totals.** Each input is aggregated with the formula's aggregation (sum) through every
  aggregating step of the report — resample, rolling window, `GroupBy`, `Total` — and the division happens
  after the last of them. "Goals per 90 by venue per month" divides monthly goal totals per venue by monthly
  minutes per venue; it never averages per-match ratios (where a one-goal, ten-minute cameo would swamp a
  season). Transforms after the last aggregation apply to the ratio. A value filter or ranking before
  then is an error — it would filter or rank goals and minutes each by their own values.
- **No rows is zero, no denominator is no value.** Goals are events, so a bucket with minutes but no goal
  rows is 0 goals per 90. A bucket with no (or zero) minutes has no value, even with a zero-fill gap policy.
- **Inputs are ordinary fetches.** Each input goes through the cache, pushdown and batching like any
  report, so goals and minutes arrive in one query and are shared with plain goals and minutes charts.
  A derived chart's cache entry depends on its inputs' data versions, so it's rebuilt when either changes.

The same rules hold for **sums and differences**: `MetricDefinition.Sum(goal-involvements, …, [goals,
assists])`, `Difference(…)`, or `Linear(…)` with coefficients. Each input is totalled per bucket and the
totals combined, which with sums is exact — and pushes down, since both inputs arrive in one query. By
default a bucket with no rows for an input counts it as 0 (events: no assist rows is no assists) and has a
value if any input does; `MissingInput.NoValue` needs every input instead (readings, where no row means
"not measured").

There is deliberately **no product** of metrics: revenue is Σ(price × quantity), not Σprice × Σquantity.
A product has to be taken per row, so it belongs in the source — a column, or a view over the table.

The catalog rejects bad definitions when it's built: unknown or derived inputs, an input used twice,
mismatched time kinds, and dimensions an input can't be sliced by.

### Shares of a total

"Each player's share of the squad's goals" isn't a formula over two metrics — it's one metric divided by
its own total — so it's a transform: `Transform.ShareOf(player)` after `Total(Sum, player)`. The total is
over points at the same time whose keys differ only in the given dimensions, so `ShareOf(venue)` after a
monthly `GroupBy(Sum, player, venue)` is each player's home/away split per month. Values are percentages
and the axis unit becomes `%`. Two things to know:

- The total is of what the report has: its entities and filters. Focused on one player, a share is 100%.
- Rates don't add up, so a share of a ratio metric (goals per 90) is refused; take shares of its numerator.

## Comparisons over time

A comparison is a report plus a `Baseline` — `SeasonsBack(1)`, `YearsBack(1)`, `MonthsBack(3)`,
`WeeksBack(52)`, `DaysBack(7)`. The engine runs the report twice, over its timeframe and over the timeframe
stepped back by the baseline, as two ordinary reports: both go through the cache and pushdown, and they
load together. Then it moves the baseline's points forward by the same step, so last March's bucket becomes
this March's, and either returns them (`Earlier` — an overlay for a multi-series chart) or joins them to the
current points (`ChangeFrom` — difference or % change, only where both sides have a value; zero-fill to
count empty buckets as 0).

Steps are calendar steps in the report's zone, and a shifted bucket must be one of the report's own buckets,
or the comparison would join the wrong things. So the engine checks the step against the report's buckets
before fetching anything:

| Buckets | Can step back in | Why not the others |
|---|---|---|
| none (raw points, or totals) | anything | — |
| month | months, years, seasons | a week or day step lands mid-month |
| week | weeks (`WeeksBack(52)` for "last year") | a year back from a Monday isn't a Monday |
| day, hour… | days, weeks | a month or year step lands on another weekday, and 29 February has no partner |
| season | seasons (or whole years) | — |

A season step goes through the season calendar: whole years when seasons start on the same date each year,
otherwise the days between the two seasons' starts. A timeframe that ends today steps back to the same day
last season, so "so far this season" meets "so far last season" — combine it with `Transform.Cumulative`
for the running total. A running total is an aggregation like any other, so for goals per 90 it's goals so
far ÷ minutes so far.

A comparison's view is cached, keyed to both periods' data versions.

## Forecasting

`Transform.Forecast(model, horizon)` projects each key's series of buckets forward — `ForecastHorizon.Buckets(6)`
or `ForecastHorizon.SeasonEnd`. It needs regular buckets, so it comes after a resample, and it runs at the
grain it forecasts: resampling a forecast is an error, since it would blend projected and observed values
into one bucket.

| Model | Projects | Needs |
|---|---|---|
| `Mean` | the average bucket so far — the pace | 1 bucket |
| `Trend` | a least-squares line | 3 buckets |
| `SeasonalNaive(m)` | the same bucket a season (m buckets) ago | 1 season |
| `HoltWinters()` / `HoltWinters(m)` | smoothed level and trend (Holt), plus a seasonal pattern (additive Holt-Winters) | 4 buckets / 2 seasons |

A range needs a little more history than a projection: two buckets for `Mean`, and a season-on-season change
for `SeasonalNaive`; with less, points are projected without one.

Holt-Winters picks its smoothing weights from a fixed grid by one-step-ahead error, so a forecast is a pure
function of the data (and caches like any view). A key with too little history gets no forecast rather
than a confident one. Future buckets are shared across keys — they follow the last bucket any key has — and
each key projects from its own history, so a player whose data stopped a month earlier is projected one
step further.

**Projected is a flag, not a guess the reader has to make.** Every forecast point is
`MeasureFlags.Estimated`, and so is anything computed from one: a group total, a rolling window, a running
total, a share, a formula, a comparison. Views carry it as `MarkView.Estimated`, omitted for observed
values. So "on pace for" — `Forecast(Mean, SeasonEnd)` then `Cumulative(Sum)` — is observed through today and
marked projected after, and for goals per 90 the forecast runs on goals and minutes and the rate divides the
projected totals.

**Zero-fill events before forecasting.** A month with no goal rows is 0 goals, but without zero-fill it's a
gap, and gaps are bridged on a straight line for fitting — a pace that ignores the blank months.

### Forecast ranges

`Forecast(model, horizon, range: 80)` gives each projected point the range it's expected to fall in
(`Point.Range`, `MarkView.Low` / `High`); views stretch the value axis to fit it. Each model states its
forecast errors as weights on independent errors of a common spread (`ForecastModel.Errors`), which makes the
range of any *sum* of future values exact too, not just of each one:

| Model | Range from |
|---|---|
| `Mean` | the spread of the history, plus the error in the estimated pace; Student's t on n − 1 |
| `Trend` | the residual spread, plus the fitted line's error at that point; Student's t on n − 2 |
| `SeasonalNaive(m)` | the spread of season-on-season changes; wider by one change per season ahead |
| `HoltWinters` | the one-step errors, carried forward by the smoothing weights (the ETS innovations form) |

The mean and trend ranges are the textbook prediction intervals, and a simulation in the tests checks that an
80% range holds the true value 78–82% of the time, for next month and for a six-month total. Holt-Winters
treats its fitted weights as known, so its ranges are approximate: simulating a drifting trend, an 80% range
held the next value about 75% of the time with 12 buckets of history (80% with 40), and six steps ahead about
85%.

**Ranges don't survive steps that combine values.** The lower bound of a sum isn't the sum of the lower
bounds — errors partly cancel, and some (the error in the estimated pace) don't cancel at all. So a group
total, running total, rolling window, share, formula or comparison refuses ranged points with an error,
rather than drawing a band that means nothing. Steps that keep rows — filters, top/bottom, an earlier
period drawn on this one's axis — keep ranges. To get a range:

- **Combine first, then forecast.** Total the squad by month, then forecast the total.
- **For "on pace for", forecast the running total:** `Forecast(Mean, SeasonEnd, range: 80, runningTotal: true)`
  returns the observed running total, then projected totals with the range of the whole sum.
- **For a rate, forecast the rate** (after its last aggregation), not goals and minutes separately.

## Multi-series charts

A `ChartSpec` is a list of `SeriesSpec`s, each an ordinary report plus a display name and a value axis
(primary or secondary). The engine runs every part of every chart in one `RunManyAsync` — so goals,
minutes and goals per 90 are one query — and `ChartComposer` combines the parts: each keeps its own chart
kind (columns, line, area), series get distinct colours and names ("Goals", or "Goals · player 7" when a
part has a series per entity), and each side gets one value axis built from its parts. Parts must share
the x-axis. A dashboard runs all its charts through `RunChartsAsync` to share fetches across them.

**Stacking** is part of the view: `ViewSpec(…, Stacked: true)` (`"stacked": true` in a dashboard definition)
sets `ChartView.Stacked`, and the value axis then spans each x position's stacked total — positives up from 0,
negatives down — rather than its largest single value, so stacks aren't clipped. A composed chart takes
stacking from its first part, as it does the chart kind, and stacks across all parts on an axis. Views that
don't stack omit the property, so their JSON is unchanged.

## Dashboards

A `Dashboard` is a set of charts defined against a shared `DashboardContext` — tenant, entities,
timeframe — rather than hard-coded ones. `RunDashboardAsync` runs every series of every chart together
(shared fetches, batched queries); focusing on one player is the same dashboard with
`context.Focus(player)`, and because data is cached per entity it needs no new queries.

Products that let users build dashboards store them as a `DashboardDefinition`: plain JSON with
declarative transforms (`resample`, `rolling`, `groupBy`, `total`, `where`, `range`, `share`, `cumulative`,
`top`, `bottom`, `forecast`), views (`kind`, `x`, `seriesBy`) and
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

Database sources share one engine, `SqlPointSource` (`Meridian.Sources.Sql`): long and wide layouts,
dimension columns, one query for many metrics, the exact timeframe filter for wall-clock data, and
pushdown whose zone conversions are generated from NodaTime's rules. What differs between databases is a
`SqlDialect` — connections, parameters, casts, timestamp arithmetic, calendar truncation, aggregate SQL. A
dialect returns null for anything its database can't compute exactly, and that rollup falls back to a raw
fetch, so a new database gets correctness from the shared engine and speed where its SQL allows.

Every database source is held to one parity suite (`tests/Meridian.Sources.Sql.Tests`): the DuckDB test data —
DST changes, wall-clock tables, NULLs, holes — is copied into the real database in a container, and for every
zone, bucket, aggregator and gap policy the pushed-down result must equal the engine's result from raw rows
*and* DuckDB's result for the same report; raw rows must read exactly as DuckDB reads them. Where Docker isn't
available the suite is skipped rather than failed.

**PostgreSQL** pushes down every built-in aggregator (ordered-set `percentile_cont`, an ordered `array_agg` for
first and last) and every bucket; sub-daily buckets use `date_bin`, so PostgreSQL 14 or later. Sessions run in
UTC — the source's own pool sets `Timezone=UTC`, and a borrowed `NpgsqlDataSource` has each connection switched
to UTC when opened (set `Timezone=UTC` in its connection string to save that round trip) — so a `timestamptz`
column reads exactly like a `timestamp` column holding UTC, whatever the server's or role's zone. Create one
source per database and keep it: it owns (or borrows) a connection pool.

**SQL Server** (2016 and later) pushes down mean, sum, min, max, count, standard deviation and variance, over every
bucket. It has no aggregate form of a median, a percentile, or the first or last value in time — `PERCENTILE_CONT`
and `FIRST_VALUE` are window functions only — so reports using those are computed in the engine from raw rows: the
same answer, fetched differently (the parity suite asserts exactly which fall back). `datetimeoffset` columns are
normalised to UTC with each value's own offset before bucketing; `datetime2`, `datetime` and `date` are read as
stored. Means and sums are taken in `float`, so an `int` column's mean isn't truncated. Metric names are sent as
`varchar`, which keeps index seeks on a `varchar` or `nvarchar` metric column. Microsoft.Data.SqlClient doesn't
run in globalization-invariant mode: an app using this source must leave `InvariantGlobalization` off.

**MySQL** (8.0.17 and later) pushes down the same aggregators as SQL Server, for every bucket, and computes median,
percentiles, first and last in the engine for the same reason. Each connection's session is put in UTC when it's
opened, so a `TIMESTAMP` column — which MySQL converts through the session zone — reads as the instant it stores,
whatever the server's zone; `DATETIME` and `DATE` are read as stored. The original constructor
(`MySqlSourceOptions`, one table of one row per value) still works; `SqlSourceOptions` adds views, wide tables and
dimensions. Quote reserved words (`load`) with backticks in names you pass.

**ClickHouse** pushes down every built-in aggregator. Its own `median` and `quantile` sample above 8,192 values,
so medians and percentiles use `quantileExactInclusive`, which interpolates as the engine does; first and last use
`argMinIf` / `argMaxIf`; spread uses the numerically stable `stddevSampStable` / `varSampStable`, returning no value
(not +∞) for a single reading. ClickHouse timestamps carry a zone that its calendar functions work in, so every
value is read as a UTC `DateTime64` and all arithmetic is in UTC: a column's zone never moves a bucket. Store
wall-clock readings and dates as given in a `DateTime64(…, 'UTC')` or `Date` column, so their wall clock is what's
stored.

Times are compared as timestamps whatever the column's type: a `DATE` compared with 07:30 on 1 January is its
midnight, so it's outside a timeframe starting then — as the engine places dates.

## Testing

Property-style and golden tests for keys, time and transforms; parity tests for every pushdown path
(period × aggregator × gap policy × zone, across real DST changes); a shared conformance suite for cache
backends; in-process integration tests for the hosts. Changes that touch pushdown or time are checked by
deliberately breaking them and confirming the tests fail. Warnings are errors.
