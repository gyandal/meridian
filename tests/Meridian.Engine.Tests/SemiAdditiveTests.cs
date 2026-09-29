using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Xunit;

namespace Meridian.Engine.Tests;

public class SemiAdditiveTests
{
    private static readonly DimensionId Site = new("site");
    private static readonly DimensionId Family = new("family");
    private static readonly DimensionId Priority = new("priority");
    private static readonly MetricId OpenIssues = new("open-issues");
    private static readonly string[] Families = ["billing", "search"];
    private static readonly string[] Priorities = ["p1", "p2", "p3"];

    /// <summary>A daily snapshot of open issues per family and priority, for August and September.</summary>
    private static double Open(int day, int family, int priority) => 10 * (family + 1) + 3 * priority + day % 7;

    private static IEnumerable<Point> Snapshots()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int day = 0; day < 61; day++)
        for (int f = 0; f < Families.Length; f++)
        for (int p = 0; p < Priorities.Length; p++)
        {
            var key = PointKey.Of(KeyPart.Entity(Site, 1), KeyPart.Category(Family, Families[f]), KeyPart.Category(Priority, Priorities[p]));
            yield return new Point(key, Open(day, f, p), Instant.FromUtc(start.AddDays(day)));
        }
    }

    private static ReportEngine Engine(Additivity additivity) => MeridianRuntime.InMemory(
        new InMemoryMetricCatalog([new MetricDefinition(OpenIssues, "Open issues", Unit.None, "last", [Site, Family, Priority], TimeGrain.Daily) { Additivity = additivity }]),
        new InMemoryPointSource(Snapshots(), Site, Unit.None)).Engine;

    private static PipelineSpec PerFamilyMonthly(IAggregator aggregator) => PipelineSpec.Create(
        "ops", OpenIssues, [new EntityRef(Site, 1)],
        new DateInterval(Instant.FromUtc(new DateTime(2026, 8, 1)), Instant.FromUtc(new DateTime(2026, 10, 1))),
        new ViewSpec(ChartKind.Column, AxisSource.Time, SeriesBy: Family),
        Transform.Resample(Period.Month, aggregator, GapPolicy.LeaveMissing)).WithDimensions(Family); // priority is dropped

    [Fact]
    public async Task A_level_at_month_end_counts_every_part_of_a_dropped_dimension()
    {
        var view = await Engine(Additivity.SemiAdditive).RunAsync(PerFamilyMonthly(Aggregators.Last), ProjectionOptions.Default);

        for (int f = 0; f < Families.Length; f++)
        {
            var marks = view.Series.Single(s => s.Name.EndsWith(Families[f], StringComparison.Ordinal)).Marks;
            // 31 August is day 30; 30 September is day 60: the month's last snapshot, summed over p1–p3.
            Assert.Equal(Priorities.Select((_, p) => Open(30, f, p)).Sum(), marks[0].Value);
            Assert.Equal(Priorities.Select((_, p) => Open(60, f, p)).Sum(), marks[1].Value);
        }
    }

    [Fact]
    public async Task A_levels_mean_is_the_mean_of_daily_totals_and_all_missing_parts_stay_missing()
    {
        var view = await Engine(Additivity.SemiAdditive).RunAsync(PerFamilyMonthly(Aggregators.Mean), ProjectionOptions.Default);
        var billing = view.Series.Single(s => s.Name.EndsWith("billing", StringComparison.Ordinal)).Marks;
        double augustMean = Enumerable.Range(0, 31).Average(d => Priorities.Select((_, p) => Open(d, 0, p)).Sum());
        Assert.Equal(augustMean, billing[0].Value!.Value, 9);

        // The fold itself: a level whose every part is missing at a time is missing, not zero.
        var key = PointKey.Of(KeyPart.Entity(Site, 1), KeyPart.Category(Family, "billing"));
        var at = Instant.FromUtc(new DateTime(2026, 8, 1));
        var source = new InMemoryPointSource(
            [new Point(key.With(KeyPart.Category(Priority, "p1")), Measurement.Missing, at, Unit.None),
             new Point(key.With(KeyPart.Category(Priority, "p2")), Measurement.Missing, at, Unit.None)], Site, Unit.None);
        var catalog = new InMemoryMetricCatalog([new MetricDefinition(OpenIssues, "Open issues", Unit.None, "last", [Site, Family, Priority], TimeGrain.Daily) { Additivity = Additivity.SemiAdditive }]);
        var raw = await MeridianRuntime.InMemory(catalog, source).Engine.RunAsync(
            PerFamilyMonthly(Aggregators.Last) with { Transforms = [] }, ProjectionOptions.Default);
        Assert.Null(Assert.Single(Assert.Single(raw.Series).Marks).Value);
    }

    [Fact]
    public async Task An_additive_metric_is_unchanged()
    {
        // The same data as an additive metric keeps today's behaviour: its rows are pooled by the aggregation.
        var sum = await Engine(Additivity.Additive).RunAsync(PerFamilyMonthly(Aggregators.Sum), ProjectionOptions.Default);
        var billing = sum.Series.Single(s => s.Name.EndsWith("billing", StringComparison.Ordinal)).Marks;
        Assert.Equal(Enumerable.Range(0, 31).Sum(d => Priorities.Select((_, p) => Open(d, 0, p)).Sum()), billing[0].Value);
        Assert.Equal(Additivity.Additive, new MetricDefinition(OpenIssues, "x", Unit.None, "sum", [Site], TimeGrain.Daily).Additivity);
    }
}
