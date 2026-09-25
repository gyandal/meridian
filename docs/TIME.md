# Time in Meridian

Time is the part of a reporting engine that is easiest to get subtly wrong, so Meridian makes the rules
explicit. This page is the reference; the code follows it.

## Two kinds of "when"

**An instant** is a moment on the global timeline: a GPS sample, a heart-rate reading, a login. It is
the same moment everywhere. It has no calendar day until you choose a time zone — 23:30 UTC is today in
London and tomorrow in Sydney.

**A local time** is a position on a calendar, with no zone attached: a date of birth, a match day, a
wellness answer "for 14 March", a contract start date — or a wall-clock time like "training at 10:00".
It is not a moment, and converting it through a time zone is always a bug (the classic "birthday shows
a day early in America" bug). It is bucketed and displayed exactly as given.

| | Instant | Local |
|---|---|---|
| Examples | sensor readings, events, logs | date of birth, match day, daily questionnaire, "the week of 13 July" |
| Stored as | UTC ticks | wall-clock ticks, no zone |
| Bucketed by | the report's calendar zone | as given — the zone is irrelevant |
| Displayed | in the zone it was bucketed in | as given |

`TimeKind` (`Instant` / `Local`) is carried on every `PointBlock` (as `PointBlock.Time`), declared per
metric in the catalog (`MetricDefinition.TimeKind`), and emitted on the chart's time axis. The engine
rejects a source whose data contradicts the metric's declaration.

## Buckets are groupings, not moments

Bucketing is where instants become local. When the engine resamples instants into days, weeks, months
or seasons, it first converts each instant to wall-clock time **in the report's calendar zone**, then
groups by calendar. The result is a `Local` block: each point is a bucket — "Monday 13 July 2026, in
Europe/London" — identified by its local start and its grain (`day`, `week`, `month`, `season`), and
the block records the zone that decided the boundaries.

Consequences:

- **The zone changes the numbers, not just the labels.** A reading at 00:30 on 1 July in London is
  23:30 on 30 June in UTC. A daily total in London and a daily total in UTC are different totals.
  That is why the zone is part of the query (`CalendarContext`) and the cache key — never something a
  renderer can apply afterwards.
- **DST is handled once, at bucketing.** A London day is 23 or 25 hours across a clock change; the
  readings in it are exactly the ones that happened on that local date.
- **Local and bucketed data join cleanly.** A daily wellness score (`Local`) and daily GPS load
  (instants bucketed to `Local` days) line up day for day. Joining a `Local` series to an `Instant`
  series is an error with a clear message: bucket the instants first.
- **Renderers never do time-zone maths.** Bucket labels are produced server-side from local values; a
  chart's time axis says `time: Local` with its `grain` and `timeZone`, or `time: Instant` with the
  zone to display it in.

Rolling windows are durations (a 28-day window is 28 × 24 h) and keep the kind of their input.

Sub-daily buckets (`Period.Every(TimeSpan.FromMinutes(5))`, `Period.Hour`) follow the same rule: they
are positions on the local clock, aligned to midnight. So on the night clocks go back, the repeated hour
is one local bucket holding two hours of readings, and on the night they go forward the skipped hour has
no bucket. When elapsed-time windows matter more than the local clock (infrastructure metrics, say),
bucket with a UTC calendar.

## The calendar

`CalendarContext` is the report's calendar: a time zone, the first day of the week, and the domain's
season calendar. Zones are IANA ids (`Europe/London`) resolved through NodaTime's bundled tz database,
so bucketing is identical on every OS and does not depend on the host's ICU or globalization settings.

```csharp
var london = CalendarContext.For("Europe/London", weekStart: DayOfWeek.Monday);
```

A tenant's zone is the right default. Bucketing by "the local day where each event happened" (athletes
travelling across zones) needs the offset stored per value; see *Not yet supported* below.

## Reading time from a database

Everything inside Meridian is UTC or local-as-given — but databases are not always that tidy, so each
source declares how its time column is stored (`StoredTime`), and values are normalised once, at fetch:

| `StoredTime` | Column holds | Meridian sees |
|---|---|---|
| `Utc` (default) | UTC timestamps | `Instant` |
| `InZone("Europe/London")` | wall-clock times in a known zone (common in older schemas) | `Instant`, converted with a DST policy |
| `Local` | dates or wall-clock times with no zone (`DATE`, a birth date, a match day) | `Local`, unchanged |

Converting wall-clock times needs two rules, set with `LocalTimeResolution`:

- **Ambiguous** — when clocks go back, one hour happens twice (01:30 occurs at 00:30 UTC and 01:30 UTC
  in London on the last Sunday of October): `Earlier` (default), `Later`, or `Reject`.
- **Skipped** — when clocks go forward, one hour never happens (01:30 does not exist in London on the
  last Sunday of March): `ShiftForward` (default; 01:30 → 02:30) or `Reject`.

A report's timeframe is read in the data's own terms: instants for `Instant` data, wall-clock values
for `Local` data (use midnight boundaries for date-valued metrics).

## Pushdown

A source that can bucket in SQL (`IRollupPointSource`) must produce exactly what the engine would. The
DuckDB source pushes down day / week (any start day) / month buckets in any report zone, for `Utc`,
`InZone` and `Local` data.

Zone conversions in that SQL are **generated from NodaTime's rules**, not delegated to the database: over
a report's timeframe a zone has only a few offset changes, so each conversion is a short `CASE` over
literal cut-offs, with the source's DST policy built in. Two reasons:

- Databases pick their own occurrence for a repeated wall-clock time (DuckDB picks the later one; our
  default is the earlier), and their tz data can lag NodaTime's. Generated SQL can't disagree.
- It needs no time-zone support in the database at all.

Where it is provably exact, the SQL skips conversions: a timeframe boundary that isn't within hours of a
DST change is compared on the stored wall clock (so Parquet row groups can be pruned), and wall-clock data
bucketed in its own zone is bucketed as stored. On the NYC taxi data this brings DST-correct pushdown to
within ~7% of a naive `date_trunc` query.

Policies that `Reject` times, and season buckets, fall back to the engine. One thing no path can make
deterministic: `last` among readings with the *identical* instant (e.g. a skipped 02:10 shifted forward
onto a real 03:10) — which of the tied values wins is unspecified. Parity tests run every
period × aggregator × gap policy in several zones across real DST changes, including wall-clock data
with the repeated autumn hour logged twice and a timeframe that ends inside it.

## Not yet supported

- **Per-event local day** (instant + the offset where it happened). Needs an offset column on
  `PointBlock` and an option on the calendar to bucket by it. The model leaves room for it.
- Pushdown of season buckets.
