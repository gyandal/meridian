using System.Diagnostics;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Sources.MySql;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

// Cold-vs-warm timing harness for any IPointSource. The point: the source (a DB, a warehouse, a
// nightly-ingested BI extract) is hit ONCE; every repeat/interactive query is served from cache.
//
//   dotnet run -c Release --project samples/Meridian.Bench.Source                 # synthetic, runs now
//   dotnet run -c Release --project samples/Meridian.Bench.Source -- --mysql "<conn>" <metric> 1,2,3,4

var entityDim = new DimensionId("entity");
string metricName;
long[] entities;
IPointSource source;

if (args.Length >= 2 && args[0] == "--mysql")
{
    metricName = args.Length >= 3 ? args[2] : "training-load";
    entities = args.Length >= 4 ? [.. args[3].Split(',').Select(long.Parse)] : [1, 2, 3, 4, 5];
    source = new MySqlPointSource(new MySqlSourceOptions(args[1]), entityDim);
    Console.WriteLine($"Source: MySQL  ·  metric='{metricName}'  ·  {entities.Length} entities");
}
else
{
    const int N = 50, days = 365;
    metricName = "series";
    entities = [.. Enumerable.Range(1, N).Select(i => (long)i)];
    source = new InMemoryPointSource(Synthesize(entityDim, N, days), entityDim, new Unit("au"));
    Console.WriteLine($"Source: synthetic in-memory  ·  {N} entities × {days} days  (pass --mysql \"<conn>\" to time a real DB)");
}

var metricId = new MetricId(metricName);
var catalog = new InMemoryMetricCatalog([new MetricDefinition(metricId, metricName, new Unit("au"), "mean", [entityDim], TimeGrain.Daily)]);
var runtime = MeridianRuntime.InMemory(catalog, source);

var today = DateTime.UtcNow.Date;
PipelineSpec Spec(long[] ids) => PipelineSpec.Create(
    "bench", metricId, [.. ids.Select(id => new EntityRef(entityDim, id))],
    new DateInterval(Instant.FromUtc(today.AddDays(-365)), Instant.FromUtc(today.AddDays(1))),
    new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: entityDim),
    Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing));

try
{
    var cold = await Time(() => runtime.Engine.RunAsync(Spec(entities), ProjectionOptions.Default));
    var warm = await Time(() => runtime.Engine.RunAsync(Spec(entities), ProjectionOptions.Default));

    // Sustained warm throughput.
    var sw = Stopwatch.StartNew();
    const int reps = 200;
    for (int i = 0; i < reps; i++) await runtime.Engine.RunAsync(Spec(entities), ProjectionOptions.Default);
    double warmAvg = sw.Elapsed.TotalMilliseconds / reps;

    // Add one entity: only the newcomer is fetched, the rest merge from cache.
    long[] plusOne = [.. entities, entities[^1] + 1];
    var partial = await Time(() => runtime.Engine.RunAsync(Spec(plusOne), ProjectionOptions.Default));

    Console.WriteLine();
    Console.WriteLine($"  cold  (source hit) : {cold,8:0.00} ms");
    Console.WriteLine($"  warm  (cached)     : {warm,8:0.00} ms   →  {(cold / Math.Max(warm, 0.001)):0.0}× faster");
    Console.WriteLine($"  warm  avg of {reps}   : {warmAvg,8:0.00} ms");
    Console.WriteLine($"  +1 entity (partial): {partial,8:0.00} ms   (only the new entity fetched)");
}
catch (Exception ex)
{
    Console.WriteLine($"\n# Source error: {ex.Message}");
    Console.WriteLine("# For MySQL, check the connection string and that the table/columns match MySqlSourceOptions.");
}

static async Task<double> Time(Func<Task> action)
{
    var sw = Stopwatch.StartNew();
    await action();
    return sw.Elapsed.TotalMilliseconds;
}

static List<Point> Synthesize(DimensionId dim, int entities, int days)
{
    var rows = new List<Point>(entities * days);
    var today = DateTime.UtcNow.Date;
    for (int e = 1; e <= entities; e++)
    {
        var key = PointKey.Of(KeyPart.Entity(dim, e));
        var rng = new Random(e * 97);
        for (int d = 0; d < days; d++)
            rows.Add(new Point(key, 50 + rng.NextDouble() * 50, Instant.FromUtc(today.AddDays(-(days - 1) + d))));
    }
    return rows;
}
