using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Sample.Weather;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Meridian.Views.Json;

// Real public data through the whole stack:
//   Open-Meteo (a source you don't own) → cache → weekly-mean transform → project → chart-agnostic JSON.

var city = new DimensionId("city");
var tempMax = new MetricId("temp-max");

var catalog = new InMemoryMetricCatalog(
[
    new MetricDefinition(tempMax, "Daily Max Temperature", new Unit("°C"), "mean", [city], TimeGrain.Daily),
]);

var cities = new Dictionary<long, (double Lat, double Lon)>
{
    [1] = (51.5074, -0.1278),  // London
    [2] = (53.3498, -6.2603),  // Dublin
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var runtime = MeridianRuntime.InMemory(catalog, new HttpWeatherSource(http, cities));

var now = DateTime.UtcNow.Date;
var timeframe = new DateInterval(
    Instant.FromUtc(now.AddDays(-28)),
    Instant.FromUtc(now.AddDays(1)));

var spec = PipelineSpec.Create(
    tenant: "demo",
    metric: tempMax,
    entities: [new EntityRef(city, 1), new EntityRef(city, 2)],
    timeframe: timeframe,
    view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: city, ValueAxisTitle: "Weekly mean max °C"),
    transforms: Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing));

try
{
    var view = await runtime.Engine.RunAsync(spec, ProjectionOptions.Default);

    // Second run: served from cache (no second HTTP call) — proves §7 end to end.
    _ = await runtime.Engine.RunAsync(spec, ProjectionOptions.Default);

    Console.WriteLine(ChartViewJson.Serialize(view));
}
catch (HttpRequestException ex)
{
    Console.WriteLine($"# Network unavailable ({ex.Message}). The pipeline is exercised by the offline tests; " +
                      "this sample needs internet to reach api.open-meteo.com.");
}
