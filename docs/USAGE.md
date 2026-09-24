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

### 3. Ask the two seasons and compare

The season calendar gives you the boundaries; `Reduce` sums each athlete's goals; `Compare` (or a
side-by-side view) does the year-on-year.

```csharp
var runtime = MeridianRuntime.InMemory(catalog, new GoalsSource());
var cal = CalendarContext.Default;                       // Jul–Jun season, here
var tctx = new TransformContext(cal);

var thisSeason = cal.Season.SeasonFor(Instant.FromUtc(DateTime.UtcNow), cal);
var lastSeason = cal.Season.SeasonFor(new Instant(thisSeason.Start.UtcTicks - 1), cal);

// total goals per athlete in a season = drop the date axis, sum
PointBlock GoalsPerPlayer(DateInterval season) =>
    Transform.Reduce(p => p.Key, Aggregators.Sum)
             .Apply(FetchRaw(goals, [1, 2, 3, 4], season), tctx);

var current  = GoalsPerPlayer(thisSeason);
var previous = GoalsPerPlayer(lastSeason);

// % change season-over-season, per athlete
var change = Binary.Compare(previous, current, CompareMode.PercentChange);
```

Or, to render the two seasons as grouped columns (what the dashboard's showcase does), tag each with
a season label and project:

```csharp
var seasonDim = new DimensionId("season");
var merged = Merge(
    Tag(previous, seasonDim, cal.Season.Label(lastSeason, cal)),   // "2025/26"
    Tag(current,  seasonDim, cal.Season.Label(thisSeason, cal)));  // "2026/27"

ChartView view = ChartProjector.Instance.Project(merged,
    new ViewSpec(ChartKind.Column, AxisSource.Category(player), SeriesBy: seasonDim,
                 ValueAxisTitle: "goals"),
    ProjectionOptions.Default);
```

### 4. The result (chart-agnostic JSON)

```json
{
  "kind": "Column",
  "series": [
    { "name": "2025/26", "colorToken": "#4E79A7",
      "marks": [ { "label": "1", "value": 39 }, { "label": "2", "value": 55 }, … ] },
    { "name": "2026/27", "colorToken": "#F28E2B",
      "marks": [ { "label": "1", "value": 7 },  { "label": "2", "value": 12 }, … ] }
  ],
  "axes": [ { "kind": "Category", "title": "player" },
            { "kind": "Linear", "title": "goals", "unit": "" } ]
}
```

Any front-end draws it. Swap `ChartKind.Column` for `Line`, or `Compare(...)` for the grouped view —
same data, no new code upstream.

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
