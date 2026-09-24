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

        // Every panel carries a real ChartView.
        Assert.All(panels, p => Assert.True(p.GetProperty("chartView").GetProperty("series").GetArrayLength() >= 1));
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
