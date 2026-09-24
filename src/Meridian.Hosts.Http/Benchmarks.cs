using System.Globalization;
using System.Text.Json;
using Meridian.Core;
using Meridian.Views;
using Meridian.Views.Json;

namespace Meridian.Hosts.Http;

/// <summary>
/// Serves the scale-benchmark results (bench/results/*.json, written by bench/Meridian.Bench.Scale) to
/// the dashboard. The history chart is built with Meridian itself — each run is a point in time, each
/// scenario a series — so the dashboard charts its own performance with the same engine it measures.
/// </summary>
public static class Benchmarks
{
    public const string ConfigKey = "Meridian:BenchmarkResults";
    private static readonly DimensionId ScenarioDim = new("scenario");

    /// <summary>Configured directory, else the nearest <c>bench/results</c> above the content root.</summary>
    public static string? ResultsDirectory(IConfiguration config, string contentRoot)
    {
        if (config[ConfigKey] is { Length: > 0 } configured) return configured;
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "bench", "results");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static object Load(string? directory)
    {
        var runs = new List<JsonElement>();
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("scenarios", out _)) runs.Add(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // Not a results file; skip it rather than fail the whole panel.
                }
            }
        }

        runs.Sort((a, b) => Timestamp(b).CompareTo(Timestamp(a))); // newest first

        // One history per dataset size — timings are only comparable over the same data.
        var histories = runs
            .GroupBy(DatasetRows)
            .OrderByDescending(g => g.Key)
            .Select(g => new
            {
                datasetRows = g.Key,
                runs = g.Count(),
                chartView = JsonSerializer.Deserialize<JsonElement>(ChartViewJson.Serialize(History(g))),
            })
            .ToList();

        return new { found = directory is not null && Directory.Exists(directory), runs, histories };
    }

    private static ChartView History(IEnumerable<JsonElement> runs)
    {
        var builder = new PointBlock.Builder(new Unit("ms"));
        foreach (var run in runs.OrderBy(Timestamp))
        {
            var at = Instant.FromUtc(Timestamp(run));
            foreach (var scenario in run.GetProperty("scenarios").EnumerateArray())
            {
                var key = PointKey.Of(KeyPart.Category(ScenarioDim, scenario.GetProperty("id").GetString() ?? "?"));
                builder.Add(key, Measurement.Of(scenario.GetProperty("medianMs").GetDouble()), at);
            }
        }
        return ChartProjector.Instance.Project(builder.Build(),
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: ScenarioDim, ValueAxisTitle: "median ms"),
            ProjectionOptions.Default);
    }

    private static DateTime Timestamp(JsonElement run) =>
        DateTime.Parse(run.GetProperty("timestampUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    private static long DatasetRows(JsonElement run) =>
        run.TryGetProperty("dataset", out var d) && d.TryGetProperty("rows", out var r) ? r.GetInt64() : 0;
}
