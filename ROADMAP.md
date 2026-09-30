# Roadmap

Meridian is in preview (`0.1.0-preview.x`): the architecture is settled and tested, APIs may still
change before 1.0. This is the direction, roughly in order; it isn't a promise of dates. Ideas and use
cases are welcome as issues.

## Now — in the previews

- Core model, transform algebra, chart-agnostic views and JSON contract
- The time model: instants vs local values, zone-aware buckets (IANA via NodaTime), DST-safe resampling,
  sub-daily periods, seasons, `StoredTime` for reading real schemas ([TIME.md](docs/TIME.md))
- Caching: per-entity slices with partial hits, batched loads, version and tag invalidation, and a
  finished-view cache
- Engine: aggregation pushdown, multi-metric batching (`RunManyAsync`)
- Dimensions (slice a metric by venue, competition…; declarative `GroupBy` / `Total`), derived metrics
  (ratio of totals, e.g. goals per 90), multi-series charts with a second value axis, and dashboards
  (shared context, focus on one entity, stored as JSON definitions), declarative filters (by key and by
  value), sums and differences of metrics, shares of a total, comparisons with an earlier period
  (year-on-year, season-on-season, the same week last year; overlay, difference or % change) and running
  totals, and aggregators beyond the basics (first, standard deviation, variance, any percentile — all
  pushed down) with top / bottom N rankings and qualifying thresholds for rates, and forecasting (pace, trend, seasonal naïve, Holt /
  Holt-Winters) with projected points marked through every later step, and forecast ranges — including the
  range of an "on pace for" total
- Sources: DuckDB (tables and Parquet in place), PostgreSQL / TimescaleDB, SQL Server / Azure SQL, MySQL and
  ClickHouse — long or wide layouts, pushdown wherever exact, one parity suite against real databases
- Hosts: REST API with a dashboard, an agent (MCP-style) tool surface
- Benchmarks: synthetic to 1B rows, NYC taxi, TSBS ([BENCHMARKS.md](docs/BENCHMARKS.md))

## Next

- **Round-aligned comparisons** — "after 5 appearances this season vs last": number each entity's
  points in time order and compare by number rather than date. Needs a metric with a row per appearance
  (minutes, or a derived metric): goals alone only have rows for matches with a goal, so numbering them
  would count scoring matches.
- **Index to a baseline** — each value as a percentage of its value at the start of the timeframe.
- **Redis cache backend** — `IPointCacheStore` over Redis, passing the shared conformance suite.
- **MCP server** — bind the transport-agnostic agent tools to the Model Context Protocol.

## Later

- **Scenario modelling** — perturb a baseline or forecast ("load +10% from March") and compare, reusing
  the same view and compare machinery.
- **Per-event local day** — bucket by the local date where each event happened (entities that travel
  across zones), via a stored offset.
- **Performance** — a columnar/SIMD pass over resample and rolling windows; allocation-free hot paths;
  cheaper projection for very large results.
- **Clients** — an OpenAPI description of the REST host and generated Python / TypeScript clients.
- **Comparative benchmarks** — the same metrics in Meridian and in semantic-layer tools (e.g. Cube),
  published with scripts.

## Not planned

A general BI platform, a query language, or a hosted service. Meridian stays an embeddable library for
systems that already own their data.
