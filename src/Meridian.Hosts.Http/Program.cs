using System.Text.Json;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Hosts.Http;
using Meridian.Time;
using Meridian.Views;
using Meridian.Views.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var today = DateTime.UtcNow;
var catalog = Seed.Catalog();
var source = new SeededPointSource(Seed.Data(today), Seed.Player);
var runtime = MeridianRuntime.InMemory(catalog, source);

app.UseDefaultFiles();
app.UseStaticFiles();

// Describe — everything the dashboard/an agent can choose.
app.MapGet("/api/catalog", () => Results.Json(new
{
    metrics = catalog.Metrics.Select(m => new { id = m.Id.Value, name = m.Name, unit = m.Unit.Symbol, grain = m.NativeGrain.ToString(), dimensions = m.ValidDimensions.Select(d => d.Name) }),
    entities = Seed.Players.Select(p => new { id = p, name = $"Player {p}" }),
    transforms = new[] { "raw", "resample", "rolling7", "rolling28" },
    periods = new[] { "day", "week", "month", "season" },
    aggregators = new[] { "mean", "sum", "min", "max", "median", "last", "count" },
    gaps = new[] { "leave-missing", "zero-fill", "carry-forward", "interpolate" },
    chartKinds = new[] { "line", "column", "area" },
    timeZones = new[] { "UTC", "Europe/London", "America/New_York", "Australia/Sydney", "Asia/Tokyo" },
}));

// Query — the interactive builder path (cache-backed).
app.MapPost("/api/report", async (ReportRequest request, CancellationToken ct) =>
{
    var spec = ReportRequestMapper.ToSpec(request, Seed.Player, Seed.Tenant, today);
    var options = ProjectionOptions.Default;
    if (request.TimeZone is { Length: > 0 } zone)
    {
        try { options = options with { Calendar = CalendarContext.For(zone) }; }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    }
    var view = await runtime.Engine.RunAsync(spec, options, ct);
    return Results.Text(ChartViewJson.Serialize(view), "application/json");
});

// Showcase — server-computed panels, one per feature.
app.MapGet("/api/showcase", async () =>
{
    var panels = Showcase.Build(source, today);
    panels.Insert(0, await Showcase.GoalsMinutesPer90Async(runtime.Engine, today));
    var payload = panels.Select(p => new
    {
        id = p.Id,
        title = p.Title,
        feature = p.Feature,
        caption = p.Caption,
        chartView = JsonSerializer.Deserialize<JsonElement>(ChartViewJson.Serialize(p.View)),
    });
    return Results.Json(payload);
});

// Dashboards — a stored definition run over a context; focusing on one player reuses the cache.
app.MapGet("/api/dashboard/example", () => Results.Text(DashboardExample.Json, "application/json"));
app.MapPost("/api/dashboard", async (DashboardRequest request, CancellationToken ct) =>
{
    try
    {
        var dashboard = request.Definition.ToDashboard();
        var options = request.TimeZone is { Length: > 0 } zone ? ProjectionOptions.Default with { Calendar = CalendarContext.For(zone) } : ProjectionOptions.Default;
        var end = today.Date.AddDays(1);
        var context = new DashboardContext(Seed.Tenant, [.. request.Entities.Select(id => new EntityRef(Seed.Player, id))],
            new DateInterval(Instant.FromUtc(end.AddDays(-Math.Clamp(request.PastDays, 7, 800))), Instant.FromUtc(end)));

        int before = source.EntitySlicesFetched;
        var view = await runtime.Engine.RunDashboardAsync(dashboard, context, options, ct);
        return Results.Json(new
        {
            title = view.Title,
            charts = view.Charts.Select(c => new { title = c.Title, chartView = JsonSerializer.Deserialize<JsonElement>(ChartViewJson.Serialize(c.View)) }),
            sourceFetches = source.EntitySlicesFetched - before, // 0 when everything came from cache
        });
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message }); }
});

// Scale-benchmark results (bench/results/*.json), newest first, plus per-dataset history charts.
app.MapGet("/api/benchmarks", (IConfiguration config, IWebHostEnvironment env) =>
    Results.Json(Benchmarks.Load(Benchmarks.ResultsDirectory(config, env.ContentRootPath))));

// Live cache stat — proves repeat runs don't re-hit the source.
app.MapGet("/api/stats", () => Results.Json(new { entitySlicesFetched = source.EntitySlicesFetched }));

app.Run();

public partial class Program;
