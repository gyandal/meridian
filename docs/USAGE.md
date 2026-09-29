# Meridian in practice

A worked, real-world example plus the pattern for pointing Meridian at your own data. Everything
below is code that runs against the libraries in this repo.

## The mental model

```
metric catalog  →  IPointSource (your data)  →  cache  →  transforms  →  ChartView  →  any front-end
```

You author two client-specific things — a **metric catalog** and an **`IPointSource`** — and every
report is then a **`PipelineSpec`** (or a few lines of the transform algebra). Colour, formatting, and
chart-library choices live only at the very end, in projection.

---

## Real example: goals per athlete, this season vs last

The question a coach actually asks. Two seasons, one number each, side by side.

### 1. Describe the metric

```csharp
var player = new DimensionId("player");
var venue  = new DimensionId("venue");
var goals  = new MetricId("goals");

var catalog = new InMemoryMetricCatalog([
    new MetricDefinition(goals, "Goals", new Unit(""), "sum", [player, venue], TimeGrain.Instant),
]);
```

### 2. Point at your data

Goals are dated events: one point per goal, value `1`, keyed by player (and venue, if you want the
home/away split later).

```csharp
public sealed class GoalsSource : IPointSource
{
    public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities,
                                       DateInterval timeframe, CancellationToken ct)
    {
        // SELECT player_id, scored_at FROM goals WHERE player_id IN (...) AND scored_at IN [start,end)
        var b = new PointBlock.Builder(metric.Unit);
        foreach (var g in QueryYourStore(entities, timeframe))
            b.Add(PointKey.Of(KeyPart.Entity(new DimensionId("player"), g.PlayerId)), 1.0,
                  Instant.FromUtc(g.ScoredAtUtc));
        return Task.FromResult(b.Build());
    }
}
```

### 3. Ask for this season, and compare it with last

A report is a spec. A comparison is the same spec with a baseline: `Earlier(baseline)` gives the earlier
period's values drawn on this period's axis, and `ChangeFrom(baseline)` gives the change.

```csharp
var runtime  = MeridianRuntime.InMemory(catalog, new GoalsSource());
var calendar = CalendarContext.Default;                 // Jul–Jun season, here
var season   = calendar.Season.SeasonFor(Instant.FromUtc(DateTime.UtcNow), calendar);
var squad    = new[] { 1L, 2L, 3L, 4L }.Select(id => new EntityRef(player, id)).ToList();

// Total goals per player this season.
var goalsPerPlayer = PipelineSpec.Create("club", goals, squad, season,
    new ViewSpec(ChartKind.Column, AxisSource.Category(player)),
    Transform.Total(Aggregators.Sum, player));

// This season and last, side by side: two series on one chart, fetched together.
ChartView sideBySide = await runtime.Engine.RunChartAsync(ChartSpec.Of(
    new SeriesSpec(goalsPerPlayer, "This season"),
    new SeriesSpec(goalsPerPlayer.Earlier(Baseline.SeasonsBack(1)), "Last season")), ProjectionOptions.Default);

// Or the change per player, in %.
ChartView change = await runtime.Engine.RunAsync(
    goalsPerPlayer.ChangeFrom(Baseline.SeasonsBack(1)), ProjectionOptions.Default);
```

For "so far this season against the same point last season", run the season to date with a running
total. The baseline covers the same stretch of last season, so the lines end at the same point:

```csharp
var soFar = PipelineSpec.Create("club", goals, squad, new DateInterval(season.Start, Instant.FromUtc(DateTime.UtcNow)),
    new ViewSpec(ChartKind.Line, AxisSource.Time),
    Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.ZeroFill),
    Transform.GroupBy(Aggregators.Sum),              // the squad as one line
    Transform.Cumulative(Aggregators.Sum));          // goals so far

await runtime.Engine.RunChartAsync(ChartSpec.Of(
    new SeriesSpec(soFar, "This season"),
    new SeriesSpec(soFar.Earlier(Baseline.SeasonsBack(1)), "Last season")), ProjectionOptions.Default);
```

Everything above caches, pushes the monthly bucketing down to a SQL source, and can be stored in a
dashboard definition (`"compare": { "unit": "season" }` on a series, and a `cumulative` transform).

### 4. The result (chart-agnostic JSON)

```json
{
  "kind": "Column",
  "series": [
    { "name": "This season", "colorToken": "#4E79A7",
      "marks": [ { "label": "1", "value": 7 },  { "label": "2", "value": 12 }, … ] },
    { "name": "Last season", "colorToken": "#F28E2B",
      "marks": [ { "label": "1", "value": 39 }, { "label": "2", "value": 55 }, … ] }
  ],
  "axes": [ { "kind": "Category", "title": "player" },
            { "kind": "Linear", "unit": "" } ]
}
```

Any front-end draws it. Swap `ChartKind.Column` for `Line`, or `Earlier` for `ChangeFrom` — same data,
no new code upstream.

---

## Pointing it at your own store

The only bespoke piece is `IPointSource.FetchAsync` — "give me this metric, for these entities, in
this window". It's called **only on cache misses**, per entity, so:

- implement one method,
- author metric definitions,
- and reporting, caching, the API, the dashboard, and the agent tools all work unchanged.

When a metric value changes, call the invalidation hook so the cache stays correct:

```csharp
await runtime.Invalidator.InvalidateAsync(new ChangeScope(tenant, "goals", new EntityRef(player, id)));
```

---

## Try it live

```
dotnet run --project src/Meridian.Hosts.Http     # then open the printed URL
```

The dashboard's **Feature showcase** renders this exact year-on-year query (panel "Goals per athlete:
this season vs last"), alongside panels for rolling loads, ACWR with status bands, gap policies,
category axes, the season calendar, and a second metric — every capability, server-computed.
