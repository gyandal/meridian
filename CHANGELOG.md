# Changelog

All notable changes to Meridian. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/). Preview releases may change APIs.

## [Unreleased]

### Added
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

[0.1.0-preview.2]: https://github.com/gyandal/meridian/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/gyandal/meridian/releases/tag/v0.1.0-preview.1
