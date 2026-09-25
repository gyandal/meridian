using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Meridian.Hosts.Tests;

public class HttpHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task Catalog_lists_metrics_entities_and_transforms()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/catalog"));
        var root = doc.RootElement;

        Assert.Equal("training-load", root.GetProperty("metrics")[0].GetProperty("id").GetString());
        Assert.Equal(4, root.GetProperty("entities").GetArrayLength());
        Assert.Contains("resample", root.GetProperty("transforms").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("season", root.GetProperty("periods").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Report_runs_the_engine_and_returns_a_chart_view()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/report", new
        {
            metric = "training-load",
            entities = new[] { 1, 2, 3 },
            pastDays = 90,
            transform = "resample",
            period = "week",
            aggregator = "mean",
            gap = "leave-missing",
            chartKind = "line",
            seriesByEntity = true,
            statusOn = false,
            statusLow = 0.0,
            statusHigh = 0.0,
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Line", root.GetProperty("kind").GetString());
        Assert.Equal(3, root.GetProperty("series").GetArrayLength());
        Assert.Equal("player 1", root.GetProperty("series")[0].GetProperty("name").GetString());
        Assert.Equal("Temporal", root.GetProperty("axes")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Report_can_filter_and_group_by_a_dimension()
    {
        var client = _factory.CreateClient();
        // Home goals only, grouped by venue → a single category series with just the "Home" bucket.
        var response = await client.PostAsJsonAsync("/api/report", new
        {
            metric = "goals",
            entities = new[] { 1, 2, 3, 4 },
            pastDays = 800,
            transform = "raw",
            period = "week",
            aggregator = "sum",
            gap = "leave-missing",
            chartKind = "column",
            seriesByEntity = false,
            statusOn = false,
            statusLow = 0.0,
            statusHigh = 0.0,
            groupBy = "venue",
            categoryDim = "venue",
            categoryValue = "Home",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Column", root.GetProperty("kind").GetString());
        Assert.Equal("Category", root.GetProperty("axes")[0].GetProperty("kind").GetString());
        var marks = root.GetProperty("series")[0].GetProperty("marks");
        Assert.Equal(1, marks.GetArrayLength());                       // filtered to Home only
        Assert.Equal("Home", marks[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Showcase_returns_a_panel_per_feature_including_year_on_year()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/showcase"));
        var panels = doc.RootElement.EnumerateArray().ToList();

        Assert.True(panels.Count >= 10, "expected the full feature gallery");
        var ids = panels.Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("yoy", ids);        // year-on-year goals
        Assert.Contains("acwr", ids);       // binary combine + status
        Assert.Contains("gaps", ids);       // gap policies
        Assert.Contains("venue", ids);      // category axis

        // The multi-series panel: goals, minutes (right-hand axis) and derived goals per 90.
        var per90 = panels.Single(p => p.GetProperty("id").GetString() == "per90").GetProperty("chartView");
        Assert.Equal(["Goals", "Minutes", "Goals per 90"], per90.GetProperty("series").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(2, per90.GetProperty("series")[1].GetProperty("axis").GetInt32());
        Assert.Equal(3, per90.GetProperty("axes").GetArrayLength());

        // Every panel carries a real ChartView.
        Assert.All(panels, p => Assert.True(p.GetProperty("chartView").GetProperty("series").GetArrayLength() >= 1));
    }

    [Fact]
    public async Task Report_buckets_in_the_requested_time_zone_and_says_so()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/report", new
        {
            metric = "training-load", entities = new[] { 1 }, pastDays = 60, transform = "resample", period = "week",
            aggregator = "mean", gap = "leave-missing", chartKind = "line", seriesByEntity = true,
            statusOn = false, statusLow = 0.0, statusHigh = 0.0, timeZone = "Australia/Sydney",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var axis = doc.RootElement.GetProperty("axes")[0];
        Assert.Equal("Local", axis.GetProperty("time").GetString());
        Assert.Equal("Australia/Sydney", axis.GetProperty("timeZone").GetString());
        Assert.Equal("week", axis.GetProperty("grain").GetString());
    }

    [Fact]
    public async Task Report_rejects_an_unknown_time_zone_with_400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/report", new
        {
            metric = "training-load", entities = new[] { 1 }, pastDays = 60, transform = "resample", period = "week",
            aggregator = "mean", gap = "leave-missing", chartKind = "line", seriesByEntity = true,
            statusOn = false, statusLow = 0.0, statusHigh = 0.0, timeZone = "Mars/Olympus_Mons",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Benchmarks_serves_runs_newest_first_with_a_history_chart_per_dataset()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-bench-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), RunJson("2026-01-01T00:00:00Z", rows: 1000, warmMs: 2.0));
            File.WriteAllText(Path.Combine(dir, "b.json"), RunJson("2026-02-01T00:00:00Z", rows: 1000, warmMs: 1.0));
            File.WriteAllText(Path.Combine(dir, "c.json"), RunJson("2026-03-01T00:00:00Z", rows: 5000, warmMs: 3.0));
            File.WriteAllText(Path.Combine(dir, "notes.json"), """{"not":"a run"}""");

            var client = _factory.WithWebHostBuilder(b => b.UseSetting("Meridian:BenchmarkResults", dir)).CreateClient();
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/benchmarks"));
            var root = doc.RootElement;

            var runs = root.GetProperty("runs").EnumerateArray().ToList();
            Assert.Equal(3, runs.Count); // the non-run file is ignored
            Assert.Equal("2026-03-01T00:00:00Z", runs[0].GetProperty("timestampUtc").GetString());

            var histories = root.GetProperty("histories").EnumerateArray().ToList();
            Assert.Equal([5000L, 1000L], histories.Select(h => h.GetProperty("datasetRows").GetInt64()));
            var series = histories[1].GetProperty("chartView").GetProperty("series")[0];
            Assert.Equal("warm", series.GetProperty("name").GetString());
            Assert.Equal([2.0, 1.0], series.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("value").GetDouble()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static string RunJson(string timestamp, long rows, double warmMs) => $$"""
            {"timestampUtc":"{{timestamp}}","dataset":{"rows":{{rows}}},
             "scenarios":[{"id":"warm","name":"Warm","medianMs":{{warmMs}}}]}
            """;
    }

    [Fact]
    public async Task Benchmarks_is_empty_not_an_error_when_there_are_no_results()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"meridian-none-{Guid.NewGuid():N}");
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Meridian:BenchmarkResults", missing)).CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/benchmarks"));
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task The_example_dashboard_runs_and_focusing_on_a_player_needs_no_new_fetches()
    {
        var client = _factory.CreateClient();
        using var definition = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard/example"));

        async Task<JsonDocument> Run(long[] players)
        {
            var response = await client.PostAsJsonAsync("/api/dashboard", new { definition = definition.RootElement, entities = players, pastDays = 365 });
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        using var squad = await Run([1, 2, 3, 4]);
        Assert.Equal(5, squad.RootElement.GetProperty("charts").GetArrayLength());
        Assert.Equal(2, squad.RootElement.GetProperty("charts")[0].GetProperty("chartView").GetProperty("series").GetArrayLength());

        using var focused = await Run([3]);
        Assert.Equal(0, focused.RootElement.GetProperty("sourceFetches").GetInt32()); // player 3's slices were already cached
    }

    [Fact]
    public async Task A_bad_dashboard_definition_is_a_400_naming_the_problem()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/dashboard", new
        {
            definition = new { title = "x", charts = new[] { new { title = "c", series = new[] { new { metric = "goals", transforms = new[] { new { kind = "smooth" } } } } } } },
            entities = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("charts[0].series[0].transforms[0].kind", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_page_is_served()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Meridian", await response.Content.ReadAsStringAsync());
    }
}
