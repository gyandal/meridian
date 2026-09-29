# Changelog

All notable changes to Meridian. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/). Preview releases may change APIs.

## [Unreleased]

### Added
- **Declarative filters**: `Transform.WhereIn(dimension, values…)` / `WhereNotIn` keep points by what
  they are (home matches, two competitions), and `Transform.WhereValue(min, max)` keeps points by their
  value (inclusive bounds; missing values dropped). Both cache and can be stored: dashboard definitions
  gain `where` (`dimension`, `in` / `notIn`) and `range` (`min`, `max`) transforms.
- A key filter written before a resample still pushes down: the database buckets by the kept dimensions
  and the engine filters the buckets.
- **Sums and differences of metrics**: `MetricDefinition.Sum` (goal involvements = goals + assists),
  `Difference` and `Linear` (coefficients). Inputs are totalled per bucket, then combined — exact, batched
  and pushed down. `MissingInput` chooses whether a missing input is 0 (events, the default) or leaves the
  bucket without a value (readings). No product of metrics, by design: see ARCHITECTURE.md.
- **Shares of a total**: `Transform.ShareOf(dimensions…)` — each value as a % of the total across those
  dimensions (each player's share of goals; the home/away split per month). The axis unit becomes `%`.
  A share of a ratio metric is an error. Dashboard definitions gain a `share` transform (`by`).
- Demo: assists, goal involvements, and a share-of-involvements chart on the example dashboard.
- **Comparisons with an earlier period**: `spec.Earlier(Baseline.SeasonsBack(1))` is last season drawn on
  this season's axis (overlay it with `ChartSpec`); `spec.ChangeFrom(baseline, Difference | PercentChange)`
  is the change per key and bucket. Baselines step back in days, weeks, months, years or seasons (through
  the season calendar); a step that wouldn't land on the report's own buckets — a year back on weekly
  buckets — is an error that says what to use (`WeeksBack(52)`). Both periods cache and push down.
  Dashboard series gain `"compare": { "unit", "back", "show" }`.
- **Running totals**: `Transform.Cumulative(aggregator)` — goals so far — per key in time order; for a
  derived metric, goals so far ÷ minutes so far. Dashboard definitions gain a `cumulative` transform.
- Demo: "goals so far, the last 12 months against the 12 before" on the example dashboard.
- **Aggregators**: `First`, `StdDev` and `Variance` (sample), and `Percentile(p)` — by name `p90`, `p99.5` —
  interpolating as `PERCENTILE.INC` / `quantile_cont`. All push down to DuckDB with exact parity.
  `Aggregators.Names` lists them for APIs.
- **Rankings**: `Transform.Top(n, rankBy, per…)` / `Bottom` keep the n highest- or lowest-scoring keys
  and all their points (top scorers as bars, or the top five as monthly lines). Dashboard definitions gain
  `top` / `bottom` (`n`, `aggregator`, `by`).
- Demo: the top three by goal involvements on the example dashboard.
- **Forecasting**: `Transform.Forecast(model, horizon)` with `ForecastModel.Mean` (pace), `Trend`,
  `SeasonalNaive(m)` and `HoltWinters()` / `HoltWinters(m)` (weights fitted deterministically), to
  `ForecastHorizon.Buckets(n)` or `SeasonEnd`. Projected points are flagged estimated, and so is anything
  computed from them — group totals, rolling windows, running totals, shares, formulas and comparisons.
  Views mark them (`MarkView.Estimated`). Dashboard definitions gain `forecast` (`model`, `season`, `n` or
  `until: "season-end"`).
- `Period.Named(name)` resolves a period from its name, including spans such as `15m` or `30s`.
- Demo: "goals on pace for" on the example dashboard, with projected points drawn dotted.

### Changed
- A report that filters or groups by a dimension it doesn't keep is now an error that says to declare it
  (`WithDimensions`), instead of silently matching nothing.
- A value filter before a derived metric's last aggregation is an error: it would filter each input by
  its own values (minutes ≥ 45 would also drop goals), which isn't what it reads as.
- The demo API's `filterMin` / `filterMax` / `categoryValue` use the declarative filters, so those
  reports now hit the view cache.
- The catalog rejects a formula that uses the same input twice.
- Resampling points that include forecasts is an error: forecast at the grain you show.
- `Measurement.Of(double.NaN)` is missing: NaN is never a value (JSON can't carry it; SQL gives NULL).
  An aggregate with no value — the spread of one reading — is a gap in resampling, as in SQL.
- The DuckDB source pushes down only built-in aggregators, matched by identity rather than name, so a
  custom aggregator named "sum" is never computed as SQL `sum`.

## [0.1.0-preview.3] — 2026-09-25

### Added
- **Dashboards**: `Dashboard` (charts defined against a shared `DashboardContext` of tenant, entities and
  timeframe) and `ReportEngine.RunDashboardAsync`. Focusing on one entity is the same dashboard with a
  narrower context, served from the per-entity cache.
- **`DashboardDefinition`**: dashboards as JSON — declarative transforms (resample, rolling, groupBy,
  total), views and axes — for products that let users build and store dashboards. Errors name the
  JSON path of the problem (`DashboardDefinitionException`).
- Demo: `GET /api/dashboard/example`, `POST /api/dashboard`, and a squad dashboard with focus-on-player
  and a live count of source fetches per view.
- **Multi-series charts**: `ChartSpec` combines several reports into one chart — goals as columns,
  minutes on a second axis, goals per 90 as a line. `ReportEngine.RunChartAsync` / `RunChartsAsync` run
  every part of every chart together (shared fetches and batches); `ChartComposer` names, colours and
  places the series. `SeriesView` gains optional `Kind` and `Axis` (omitted for single-report views).
- Dashboard: renders per-series chart kinds and a right-hand axis; a goals / minutes / goals-per-90 panel;
  demo data reshaped around matches (minutes and goals by venue).
- **Derived metrics**: `MetricDefinition.Ratio(...)` defines a metric as numerator ÷ denominator × scale
  (goals per 90). Each input is aggregated through the report's aggregating steps (resample, rolling,
  `GroupBy`, `Total`) with the formula's aggregation, then divided — a ratio of totals. A missing
  numerator is 0; a missing or zero denominator gives no value. Inputs are fetched, cached, pushed down
  and batched like any report; derived views are invalidated with their inputs. The catalog validates
  formulas when it's built.
- `IAggregatingTransform` (aggregator exposed and swappable) on resample, rolling, group-by, total and reduce.
- **Dimensions**: reports can group by attributes beyond the entity (venue, competition…).
  `DuckDbSourceOptions.DimensionColumns` maps dimensions to columns; sources return every dimension a
  metric declares, and each report keeps the ones it names (`PipelineSpec.WithDimensions`), so charts
  slicing the same metric different ways share one cached fetch. Pushdown groups by exactly the
  declared dimensions.
- `Transform.GroupBy(aggregator, dims…)` (keeps time) and `Transform.Total(aggregator, dims…)`
  (collapses time): declarative, cacheable grouping, output in key order.
- `PointKey.Only(dimensions)`.
- DuckDB source reads **wide tables** — one row per (entity, time), a column per metric — via
  `DuckDbSourceOptions.MetricColumns`. A multi-metric fetch or rollup is a single scan; NULL columns are
  missing values, exactly as in the long layout (parity tests compare the two).
- TSBS benchmark reads TSBS's native wide schema too: 10-metric queries cold drop from 79 ms (long) to
  33 ms, and `double-groupby-all` from 1,617 ms to 552 ms.

### Changed
- Reports keep only the entity plus the dimensions they declare; other key parts a source returns are
  folded away before transforms. A report that grouped by a dimension without declaring it must now call
  `WithDimensions`.

## [0.1.0-preview.2] — 2026-09-25

### Added
- **`Meridian.Sources.DuckDb`** — a source over DuckDB tables or Parquet files read in place, with
  aggregation pushdown and multi-metric batching.
- **Time model** ([docs/TIME.md](docs/TIME.md)): every `PointBlock` carries a `TimeAxis` (UTC instants vs
  local values); resampling buckets in the report's zone and produces local calendar buckets; charts
  declare the time kind, zone and grain of their axis.
- IANA time zones via NodaTime (`CalendarContext.For("Europe/London")`) — identical on every OS, no ICU
  dependency.
- `StoredTime` (`Utc`, `InZone(zone, resolution)`, `Local`) and `LocalTimeResolution` for reading real
  schemas, including DST-ambiguous and skipped wall-clock times. Supported by the DuckDB and MySQL sources.
- `MetricDefinition.TimeKind`; the engine rejects a source whose data contradicts it.
- Sub-daily periods: `Period.Every(TimeSpan)`, `Period.Hour`.
- **Pushdown** (`IRollupPointSource`): a leading resample runs in the database when it can be done
  exactly — in any zone, with conversion SQL generated from NodaTime's rules.
- **Batching** (`IBatchPointSource`, `ReportEngine.RunManyAsync`, `PointCache.GetOrLoadManyAsync`):
  reports sharing entities and timeframe fetch all their metrics in one query.
- **View cache** (`ViewCache`): identical requests return the finished `ChartView`; keyed on data
  versions and stable input identities (`ICacheIdentity`, `Transform.Named`).
- Dashboard: benchmark results and history, a time-zone picker; default port 5731.
- Benchmarks: synthetic (172M and 1B rows), NYC taxi (47.5M trips) and TSBS cpu-only
  ([docs/BENCHMARKS.md](docs/BENCHMARKS.md)).

### Changed
- `CalendarContext.Zone` is a NodaTime `DateTimeZone` (was `TimeZoneInfo`).
- `ILabelResolver.TimeLabel` takes ticks and the block's `TimeAxis`; bucket labels follow the grain.
- `AxisView` gains `Time`, `TimeZone` and `Grain`.
- `IReportEngine` gains `RunManyAsync`; `MeridianRuntime` gains `Views`.

### Fixed
- Dashboard time-axis labels were formatted in UTC regardless of the report's zone.
- DuckDB raw fetches failed on `DECIMAL` / `INTEGER` value columns.

## [0.1.0-preview.1] — 2026-09-24

First public preview: core point model, time and transforms, caching with per-entity partial hits and
invalidation, chart-agnostic views and JSON contract, report engine, REST host with dashboard, agent tool
surface, MySQL source.

[Unreleased]: https://github.com/gyandal/meridian/compare/v0.1.0-preview.3...HEAD
[0.1.0-preview.3]: https://github.com/gyandal/meridian/compare/v0.1.0-preview.2...v0.1.0-preview.3
[0.1.0-preview.2]: https://github.com/gyandal/meridian/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/gyandal/meridian/releases/tag/v0.1.0-preview.1
