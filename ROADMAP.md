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
- Sources: DuckDB (tables and Parquet in place), MySQL
- Hosts: REST API with a dashboard, an agent (MCP-style) tool surface
- Benchmarks: synthetic to 1B rows, NYC taxi, TSBS ([BENCHMARKS.md](docs/BENCHMARKS.md))

## Next

- **Wide-table sources** — read a row with a column per metric (TSBS-style schemas) without unpivoting,
  closing the remaining gap on multi-metric queries.
- **More sources** — PostgreSQL / TimescaleDB, SQL Server, ClickHouse; each with pushdown where exact.
- **Comparisons over time** — year-on-year and season-phase-aligned comparison ("round 5 this season vs
  last"), period-over-period change, index-to-baseline.
- **More aggregators** — percentiles and quartiles, standard deviation and variance, distinct count,
  weighted mean.
- **Redis cache backend** — `IPointCacheStore` over Redis, passing the shared conformance suite.
- **MCP server** — bind the transport-agnostic agent tools to the Model Context Protocol.

## Later

- **Forecasting** — `Project(horizon, model)`: seasonal-naïve, trend and Holt-Winters to start; projected
  points flagged `Estimated` so they're visibly distinct from observed data.
- **Scenario modelling** — perturb a baseline or forecast ("load +10% from March") and compare, reusing
  the same view and compare machinery.
- **Per-event local day** — bucket by the local date where each event happened (entities that travel
  across zones), via a stored offset.
- **Performance** — a columnar/SIMD pass over resample and rolling windows; allocation-free hot paths.
- **Clients** — an OpenAPI description of the REST host and generated Python / TypeScript clients.
- **Comparative benchmarks** — the same metrics in Meridian and in semantic-layer tools (e.g. Cube),
  published with scripts.

## Not planned

A general BI platform, a query language, or a hosted service. Meridian stays an embeddable library for
systems that already own their data.
