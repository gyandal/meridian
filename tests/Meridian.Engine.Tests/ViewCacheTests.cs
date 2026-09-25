using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Xunit;

namespace Meridian.Engine.Tests;

public class ViewCacheTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly MetricId Load = new("training-load");
    private static readonly MetricId Hr = new("heart-rate");

    private static Instant Day(int d) => Instant.FromUtc(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc).AddDays(d - 1));

    private static IMetricCatalog Catalog() => new InMemoryMetricCatalog(
    [
        new MetricDefinition(Load, "Training Load", new Unit("au"), "mean", [Player], TimeGrain.Daily),
        new MetricDefinition(Hr, "Heart rate", new Unit("bpm"), "mean", [Player], TimeGrain.Daily),
    ]);

    private static InMemoryPointSource Source()
    {
        var rows = new List<Point>();
        for (int player = 1; player <= 3; player++)
        {
            var key = PointKey.Of(KeyPart.Entity(Player, player));
            for (int d = 1; d <= 21; d++) rows.Add(new Point(key, player * 100 + d, Day(d)));
        }
        return new InMemoryPointSource(rows, Player, new Unit("au"));
    }

    /// <summary>Counts how often the pipeline actually runs.</summary>
    private sealed class CountingTransform : ITransform
    {
        public int Runs { get; private set; }

        public PointBlock Apply(PointBlock input, TransformContext ctx)
        {
            Runs++;
            return input;
        }
    }

    private static PipelineSpec Spec(ITransform extra, MetricId? metric = null, params long[] players) => PipelineSpec.Create(
        "football", metric ?? Load, [.. (players.Length == 0 ? [1L, 2L] : players).Select(p => new EntityRef(Player, p))],
        new DateInterval(Day(1), Day(22)),
        new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Player, Status: new TargetBand(100, 200)),
        Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing), extra);

    [Fact]
    public async Task An_identical_request_returns_the_finished_view_without_rerunning_the_pipeline()
    {
        var counter = new CountingTransform();
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());

        var first = await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);
        var second = await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);

        Assert.Same(first, second);
        Assert.Equal(1, counter.Runs);
    }

    [Fact]
    public async Task Invalidating_an_entity_rebuilds_the_views_it_fed()
    {
        var counter = new CountingTransform();
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());
        await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);

        await runtime.Invalidator.InvalidateAsync(new ChangeScope("football", Load.Value, new EntityRef(Player, 2)));
        await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);

        Assert.Equal(2, counter.Runs);
        Assert.Equal(1, runtime.Views.Count); // the stale view was evicted, the rebuilt one stored
    }

    [Fact]
    public async Task A_version_bump_alone_makes_old_views_unreachable()
    {
        // No view-cache invalidation hook: the data version in the key is enough on its own.
        var counter = new CountingTransform();
        var versions = new InMemoryDataVersionStore();
        var views = new ViewCache();
        var engine = new ReportEngine(Catalog(), Source(), new PointCache(new TaggedStoreDecorator(new InMemoryKeyValueStore()), versions),
            ChartProjector.Instance, views);
        await engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);

        versions.Bump(new ChangeScope("football", Load.Value, new EntityRef(Player, 1)));
        await engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);

        Assert.Equal(2, counter.Runs);
    }

    [Fact]
    public async Task Requests_with_an_unnamed_lambda_transform_are_never_cached()
    {
        var counter = new CountingTransform();
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());

        await runtime.Engine.RunAsync(Spec(counter), ProjectionOptions.Default);
        await runtime.Engine.RunAsync(Spec(counter), ProjectionOptions.Default);

        Assert.Equal(2, counter.Runs);
        Assert.Equal(0, runtime.Views.Count);
    }

    [Fact]
    public async Task Calendar_and_presentation_are_part_of_the_identity()
    {
        var counter = new CountingTransform();
        var runtime = MeridianRuntime.InMemory(Catalog(), Source());
        var london = ProjectionOptions.Default with { Calendar = CalendarContext.For("Europe/London") };
        var otherTheme = ProjectionOptions.Default with { Theme = new DefaultTheme() };

        await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), ProjectionOptions.Default);
        await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), london);      // other zone: other buckets
        await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter)), otherTheme);  // other theme instance

        Assert.Equal(3, counter.Runs);
    }

    [Fact]
    public async Task The_cache_is_bounded_by_marks_and_evicts_the_least_recently_used()
    {
        var counter = new CountingTransform();
        var runtime = MeridianRuntime.InMemory(Catalog(), Source(), viewCacheMarks: 14); // room for two views (6 marks + 1 each)
        async Task Run(params long[] players) => await runtime.Engine.RunAsync(Spec(Transform.Named("count", counter), null, players), ProjectionOptions.Default);

        await Run(1, 2);   // A
        await Run(2, 3);   // B
        await Run(1, 2);   // A again: a hit, and now most recent
        await Run(1, 3);   // C evicts B, the least recently used
        Assert.Equal(3, counter.Runs);

        await Run(1, 2);   // A still cached
        Assert.Equal(3, counter.Runs);
        await Run(2, 3);   // B was evicted: rebuilt
        Assert.Equal(4, counter.Runs);
        Assert.True(runtime.Views.Marks <= 14);
    }

    [Fact]
    public async Task Views_bigger_than_the_whole_budget_are_not_cached()
    {
        var runtime = MeridianRuntime.InMemory(Catalog(), Source(), viewCacheMarks: 3);
        await runtime.Engine.RunAsync(Spec(Transform.Named("noop", new CountingTransform())), ProjectionOptions.Default);
        Assert.Equal(0, runtime.Views.Count);
    }

    [Fact]
    public async Task RunMany_serves_cached_views_and_fetches_only_for_the_rest()
    {
        var source = Source();
        var runtime = MeridianRuntime.InMemory(Catalog(), source);
        var named = Transform.Named("noop", new CountingTransform());
        var first = await runtime.Engine.RunAsync(Spec(named, Load), ProjectionOptions.Default);
        int fetches = source.FetchCount;

        var both = await runtime.Engine.RunManyAsync([Spec(named, Load), Spec(named, Hr)], ProjectionOptions.Default);

        Assert.Same(first, both[0]);                 // straight from the view cache
        Assert.Equal(fetches + 1, source.FetchCount); // only heart-rate needed data
    }
}
