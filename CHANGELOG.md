# Changelog

All notable changes to Meridian. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/). Preview releases may change APIs.

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
