using System.Collections.Immutable;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Xunit;

namespace Meridian.Engine.Tests;

public class EngineTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly MetricId Load = new("training-load");

    private static Instant Day(int d) =>
        Instant.FromUtc(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc).AddDays(d - 1));

    private static DateInterval ThreeWeeks() =>
        new(Day(1), Day(22));

    private static IMetricCatalog Catalog() => new InMemoryMetricCatalog(
    [
        new MetricDefinition(Load, "Training Load", new Unit("au"), "mean",
            [Player], TimeGrain.Daily),
    ]);

    private static InMemoryPointSource Source()
    {
        var rows = new List<Point>();
        for (int player = 1; player <= 2; player++)
        {
            var key = PointKey.Of(KeyPart.Entity(Player, player));
            for (int d = 1; d <= 21; d++) rows.Add(new Point(key, player * 100 + d, Day(d)));
        }
        return new InMemoryPointSource(rows, Player, new Unit("au"));
    }

    private static PipelineSpec Spec(params long[] players) => PipelineSpec.Create(
        tenant: "football",
        metric: Load,
        entities: [.. players.Select(p => new EntityRef(Player, p))],
        timeframe: ThreeWeeks(),
        view: new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Player),
        transforms: Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing));

    [Fact]
    public async Task RunAsync_returns_a_projected_chart_view_end_to_end()
    {
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());

        var view = await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);

        Assert.Equal(ChartKind.Line, view.Kind);
        Assert.Equal(2, view.Series.Count);              // one line per player
        Assert.Equal(3, view.Series[0].Marks.Count);      // three weeks
        Assert.Equal("au", view.Axes[1].Unit);            // unit defaulted from the catalog
        Assert.Equal(["player 1", "player 2"], view.Legend.Series);
    }

    [Fact]
    public async Task Second_identical_run_is_served_from_cache()
    {
        var source = Source();
        var runtime = MeridianRuntime.InMemory(Catalog(), source);

        await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);
        await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);

        Assert.Equal(1, source.FetchCount); // raw data cached; transforms/projection reran cheaply
    }

    [Fact]
    public async Task Adding_an_entity_only_fetches_the_new_one()
    {
        var source = Source();
        var runtime = MeridianRuntime.InMemory(Catalog(), source);

        await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);
        await runtime.Engine.RunAsync(Spec(1, 2, 3), ProjectionOptions.Default);

        Assert.Equal([new EntityRef(Player, 1), new EntityRef(Player, 2)], source.Fetches[0]);
        Assert.Equal([new EntityRef(Player, 3)], source.Fetches[1]); // per-entity merge through the engine
    }

    [Fact]
    public async Task Invalidating_an_entity_forces_a_refetch()
    {
        var source = Source();
        var runtime = MeridianRuntime.InMemory(Catalog(), source);

        await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);
        await runtime.Invalidator.InvalidateAsync(new ChangeScope("football", "training-load", new EntityRef(Player, 1)));
        await runtime.Engine.RunAsync(Spec(1, 2), ProjectionOptions.Default);

        Assert.Equal(2, source.FetchCount);
        Assert.Equal([new EntityRef(Player, 1)], source.Fetches[1]); // only the changed entity reloaded
    }

    [Fact]
    public async Task Unknown_metric_is_a_clear_error()
    {
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());
        var spec = Spec(1) with { Metric = new MetricId("does-not-exist") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Engine.RunAsync(spec, ProjectionOptions.Default));
        Assert.Contains("Unknown metric", ex.Message);
    }
}
