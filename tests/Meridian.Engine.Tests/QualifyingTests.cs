using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Xunit;

namespace Meridian.Engine.Tests;

public class QualifyingTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly MetricId Goals = new("goals");
    private static readonly MetricId Minutes = new("minutes");
    private static readonly MetricId GoalsPer90 = new("goals-per-90");

    private static readonly InMemoryMetricCatalog Catalog = new(
    [
        new MetricDefinition(Goals, "Goals", Unit.None, "sum", [Player], TimeGrain.Instant),
        new MetricDefinition(Minutes, "Minutes", new Unit("min"), "sum", [Player], TimeGrain.Instant),
        MetricDefinition.Ratio(GoalsPer90, "Goals per 90", new Unit("/90"), Goals, Minutes, 90, [Player]),
    ]);

    private static readonly DateTime Start = new(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateInterval TenWeeks = new(Instant.FromUtc(Start.AddDays(-1)), Instant.FromUtc(Start.AddDays(70)));

    /// <summary>
    /// Ten weekly matches. Player 1 plays 90 minutes in each and scores five (0.5 per 90); player 2 plays 90 in each and
    /// scores six (0.6 per 90); player 3 comes on for ten minutes in the last match and scores (9 per 90).
    /// </summary>
    private static ByMetricSource Source()
    {
        var rows = new Dictionary<MetricId, List<Point>> { [Goals] = [], [Minutes] = [] };
        void Match(long player, int week, double minutes, bool scored)
        {
            var key = PointKey.Of(KeyPart.Entity(Player, player));
            var at = Instant.FromUtc(Start.AddDays(7 * week));
            rows[Minutes].Add(new Point(key, minutes, at));
            if (scored) rows[Goals].Add(new Point(key, 1, at));
        }
        for (int week = 0; week < 10; week++)
        {
            Match(1, week, 90, scored: week % 2 == 0);
            Match(2, week, 90, scored: week < 6);
        }
        Match(3, 9, 10, scored: true);
        return new ByMetricSource(rows);
    }

    /// <summary>Points kept per metric, filtered by entity and timeframe as a real source would.</summary>
    private sealed class ByMetricSource(Dictionary<MetricId, List<Point>> rows) : IPointSource
    {
        public Task<PointBlock> FetchAsync(MetricDefinition metric, IReadOnlyList<EntityRef> entities, DateInterval timeframe, CancellationToken ct = default)
        {
            var ids = entities.Select(e => e.Id).ToHashSet();
            return Task.FromResult(PointBlock.FromRows(rows[metric.Id].Where(p =>
                p.Key.TryGet(Player, out var id) && ids.Contains(id.Numeric) && p.At is { } at && at.UtcTicks >= timeframe.Start.UtcTicks && at.UtcTicks < timeframe.End.UtcTicks)));
        }
    }

    private static PipelineSpec Report(params ITransform[] transforms) => PipelineSpec.Create(
        "club", GoalsPer90, [new EntityRef(Player, 1), new EntityRef(Player, 2), new EntityRef(Player, 3)], TenWeeks,
        new ViewSpec(ChartKind.Column, AxisSource.Category(Player)), transforms);

    private static PipelineSpec TopTwo => Report(Transform.Total(Aggregators.Sum, Player), Transform.Top(2, Aggregators.Sum));

    private static async Task<Dictionary<string, double>> Run(PipelineSpec spec, ReportEngine? engine = null) =>
        (await (engine ?? MeridianRuntime.InMemory(Catalog, Source()).Engine).RunAsync(spec, ProjectionOptions.Default))
            .Series.SelectMany(s => s.Marks).Where(m => m.Value is not null).ToDictionary(m => m.Label, m => m.Value!.Value);

    [Fact]
    public async Task Without_a_threshold_a_ten_minute_cameo_tops_the_rate()
    {
        var top = await Run(TopTwo);
        Assert.Equal(["3", "2"], top.Keys.Order().Reverse());
        Assert.Equal(9, top["3"], 9);
    }

    [Fact]
    public async Task With_a_minimum_of_450_minutes_only_players_who_qualify_are_ranked()
    {
        var top = await Run(TopTwo.WithMinimumDenominator(450));

        Assert.Equal(["1", "2"], top.Keys.Order());       // the cameo is out; the two regulars are in
        Assert.Equal(0.6, top["2"], 9);
        Assert.Equal(0.5, top["1"], 9);
    }

    [Fact]
    public async Task The_threshold_applies_where_the_ratio_is_computed()
    {
        // Per month (August has five matches, 450 minutes; September three by the timeframe's end): 400 isn't met in
        // a month with fewer minutes, and a month is its own bucket.
        var monthly = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunAsync(
            PipelineSpec.Create("club", GoalsPer90, [new EntityRef(Player, 1)], TenWeeks,
                new ViewSpec(ChartKind.Column, AxisSource.Time),
                Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing)).WithMinimumDenominator(400),
            ProjectionOptions.Default);
        var months = monthly.Series[0].Marks.ToDictionary(m => m.Label, m => m.Value);
        Assert.NotNull(months["2026-08"]);                  // 5 × 90 = 450 minutes
        Assert.Null(months.GetValueOrDefault("2026-10"));   // one match (90 minutes): no value

        // On a running total, a player qualifies once their minutes so far reach it.
        var soFar = await MeridianRuntime.InMemory(Catalog, Source()).Engine.RunAsync(
            PipelineSpec.Create("club", GoalsPer90, [new EntityRef(Player, 1)], TenWeeks,
                new ViewSpec(ChartKind.Line, AxisSource.Time),
                Transform.Resample(Period.Week, Aggregators.Sum, GapPolicy.ZeroFill), Transform.Cumulative(Aggregators.Sum))
                .WithMinimumDenominator(450),
            ProjectionOptions.Default);
        // 90…360 minutes so far in the first four weeks: no value, so no mark. 450 from the fifth match's week on.
        var weeks = soFar.Series[0].Marks;
        Assert.Equal("2026-08-24", weeks[0].Label);
        Assert.Equal(6, weeks.Count);
        Assert.Equal(90.0 * 3 / 450, weeks[0].Value!.Value, 9);   // three goals in the first five matches
    }

    [Fact]
    public async Task A_threshold_is_part_of_the_views_cache_key_and_only_for_ratios()
    {
        var engine = MeridianRuntime.InMemory(Catalog, Source()).Engine;
        Assert.Contains("3", (await Run(TopTwo, engine)).Keys);
        Assert.DoesNotContain("3", (await Run(TopTwo.WithMinimumDenominator(450), engine)).Keys); // not the cached view without it

        var goals = PipelineSpec.Create("club", Goals, [new EntityRef(Player, 1)], TenWeeks, new ViewSpec(ChartKind.Column, AxisSource.Time))
            .WithMinimumDenominator(450);
        Assert.Contains("isn't a ratio", (await Assert.ThrowsAsync<ArgumentException>(() => engine.RunAsync(goals, ProjectionOptions.Default))).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => TopTwo.WithMinimumDenominator(0));
    }
}
