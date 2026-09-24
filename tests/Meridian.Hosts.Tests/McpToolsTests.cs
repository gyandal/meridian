using System.Text.Json;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Hosts.Mcp;
using Meridian.Semantics;
using Xunit;

namespace Meridian.Hosts.Tests;

public class McpToolsTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly MetricId Load = new("training-load");

    private static MeridianTools Tools()
    {
        var catalog = new InMemoryMetricCatalog(
        [
            new MetricDefinition(Load, "Training Load", new Unit("au"), "mean", [Player], TimeGrain.Daily),
        ]);

        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Point>();
        for (int player = 1; player <= 2; player++)
        {
            var key = PointKey.Of(KeyPart.Entity(Player, player));
            for (int d = 0; d < 120; d++) rows.Add(new Point(key, player * 50 + d, Instant.FromUtc(start.AddDays(d))));
        }
        var source = new InMemoryPointSource(rows, Player, new Unit("au"));
        var runtime = MeridianRuntime.InMemory(catalog, source);
        return new MeridianTools(catalog, runtime.Engine, Player, tenant: "demo");
    }

    [Fact]
    public void Describe_returns_the_catalog_an_agent_needs()
    {
        var described = Tools().Describe();

        Assert.Single(described.Metrics);
        Assert.Equal("training-load", described.Metrics[0].Id);
        Assert.Equal("au", described.Metrics[0].Unit);
        Assert.Contains("player", described.Metrics[0].Dimensions);
        Assert.Contains("rolling28-mean", described.Transforms);
    }

    [Fact]
    public async Task Query_returns_a_valid_chart_view_json()
    {
        // Wide window so the fixed 2026-06 seed data falls inside "past N days" regardless of today.
        var json = await Tools().QueryAsync(new McpQuery(
            Metric: "training-load",
            Entities: [1, 2],
            PastDays: 400,
            Transform: "weekly-mean",
            ChartKind: "line"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Line", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("series").GetArrayLength());
    }
}
