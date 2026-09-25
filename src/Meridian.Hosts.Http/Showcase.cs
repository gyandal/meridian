using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Hosts.Http;

public sealed record ShowcasePanel(string Id, string Title, string Feature, string Caption, ChartView View);

/// <summary>
/// Server-computed gallery: each panel is the engine/algebra exercising one capability, so the dashboard
/// demonstrates the whole feature set — including binary joins and season-over-season comparison the
/// single-spec path doesn't cover.
/// </summary>
public static class Showcase
{
    private static readonly TransformContext Tctx = TransformContext.Default;
    private static readonly ProjectionOptions Opts = ProjectionOptions.Default;
    private static readonly Unit Au = new("au");
    private static readonly Unit Bpm = new("bpm");
    private static readonly Unit Count = new("");

    public static List<ShowcasePanel> Build(SeededPointSource src, DateTime today)
    {
        var panels = new List<ShowcasePanel>
        {
            WeeklyMultiSeries(src, today),
            MonthlySum(src, today),
            SeasonMean(src, today),
            RollingWindows(src, today),
            AcwrWithStatus(src, today),
            WeightVsMovingTargets(today),
            GapPolicies(today),
            AggregatorSweep(src, today),
            GoalsByVenue(src, today),
            GoalsYearOnYear(src, today),
            RestingHrSecondMetric(src, today),
        };
        return panels;
    }

    private static ShowcasePanel WeeklyMultiSeries(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1, 2, 3], Back(today, 112), Au);
        var weekly = Transform.Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing).Apply(block, Tctx);
        var view = ChartProjector.Instance.Project(weekly,
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: Seed.Player, ValueAxisTitle: "au"), Opts);
        return new("weekly", "Weekly mean load", "Resample · Week · Mean · multi-series",
            "Daily load resampled to weekly means, one line per athlete.", view);
    }

    private static ShowcasePanel MonthlySum(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1, 2], Back(today, 180), Au);
        var monthly = Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing).Apply(block, Tctx);
        var view = ChartProjector.Instance.Project(monthly,
            new ViewSpec(ChartKind.Column, AxisSource.Time, SeriesBy: Seed.Player, ValueAxisTitle: "au"), Opts);
        return new("monthly", "Monthly total load", "Period · Month · Sum · columns",
            "Same data, monthly buckets, summed instead of averaged, drawn as grouped columns.", view);
    }

    private static ShowcasePanel SeasonMean(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1, 2], Back(today, Seed.HistoryDays), Au);
        var season = Transform.Resample(Period.Season, Aggregators.Mean, GapPolicy.LeaveMissing).Apply(block, Tctx);
        var view = ChartProjector.Instance.Project(season,
            new ViewSpec(ChartKind.Column, AxisSource.Time, SeriesBy: Seed.Player, ValueAxisTitle: "au"), Opts);
        return new("season", "Load by season", "Season calendar (Jul–Jun)",
            "A domain calendar, not date arithmetic: each column is one season's mean load.", view);
    }

    private static ShowcasePanel RollingWindows(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1], Back(today, 150), Au);
        var window = new DimensionId("window");
        var acute = Tag(Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(block, Tctx), window, "7-day");
        var chronic = Tag(Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(block, Tctx), window, "28-day");
        var view = ChartProjector.Instance.Project(Merge(Au, acute, chronic),
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: window, ValueAxisTitle: "au"), Opts);
        return new("rolling", "Rolling load (acute vs chronic)", "Rolling · 7-day & 28-day",
            "Two rolling means over the same series — the acute and chronic loads that feed ACWR.", view);
    }

    private static ShowcasePanel AcwrWithStatus(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1], Back(today, 150), Au);
        var acute = Transform.Rolling(TimeSpan.FromDays(7), Aggregators.Mean).Apply(block, Tctx);
        var chronic = Transform.Rolling(TimeSpan.FromDays(28), Aggregators.Mean).Apply(block, Tctx);
        var acwr = Binary.Combine(acute, chronic, static (a, c) => c == 0 ? double.NaN : a / c, matchTime: true, new Unit("ratio"));
        var view = ChartProjector.Instance.Project(acwr,
            new ViewSpec(ChartKind.Line, AxisSource.Time, ValueAxisTitle: "ACWR", Status: new TargetBand(0.8, 1.3, 0.2)), Opts);
        return new("acwr", "Acute:chronic ratio + status", "Binary.Combine · status band",
            "Acute ÷ chronic, coloured by a target band: green in the 0.8–1.3 sweet spot, amber near the edge, red beyond.", view);
    }

    private static ShowcasePanel WeightVsMovingTargets(DateTime today)
    {
        // The exact scenario a static-visual tool couldn't do: player weight against a target BAND that
        // the club changes over time. The band is just more time-series data — two stepped target lines.
        var kg = new Unit("kg");
        var key = PointKey.Of(KeyPart.Entity(Seed.Player, 1));
        var start = today.AddDays(-179).Date;
        var line = new DimensionId("line");

        var weightB = new PointBlock.Builder(kg);
        var rng = new Random(5);
        for (int d = 0; d < 180; d++)
        {
            double v = 84 - d * 0.028 + Math.Sin(d / 20.0) * 0.7 + (rng.NextDouble() - 0.5) * 1.1;
            weightB.Add(key, Measurement.Of(Math.Round(v, 1)), Instant.FromUtc(start.AddDays(d)));
        }
        var weight = Tag(weightB.Build(), line, "weight");

        // Club target revisions: (day, low, high). Carry-forward turns the revision points into step lines.
        var revisions = new[] { (0, 83.0, 87.0), (60, 80.0, 84.0), (120, 78.0, 82.0), (179, 78.0, 82.0) };
        var low = Tag(StepLine(key, kg, start, revisions.Select(r => (r.Item1, r.Item2))), line, "target ↓");
        var high = Tag(StepLine(key, kg, start, revisions.Select(r => (r.Item1, r.Item3))), line, "target ↑");

        var view = ChartProjector.Instance.Project(Merge(kg, weight, low, high),
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: line, ValueAxisTitle: "kg"), Opts);
        return new("targets", "Weight vs moving targets", "Time-varying targets · a static-viz gap",
            "Player weight against a target band the club revises over the season — the targets are just more time-series, so the band steps when they change.", view);

        static PointBlock StepLine(PointKey key, Unit unit, DateTime start, IEnumerable<(int Day, double Value)> points)
        {
            var b = new PointBlock.Builder(unit);
            foreach (var (day, value) in points) b.Add(key, Measurement.Of(value), Instant.FromUtc(start.AddDays(day)));
            return Transform.Resample(Period.Day, Aggregators.Last, GapPolicy.CarryForward).Apply(b.Build(), Tctx);
        }
    }

    private static ShowcasePanel GapPolicies(DateTime today)
    {
        var key = PointKey.Of(KeyPart.Entity(Seed.Player, 1));
        var start = today.AddDays(-63).Date;
        var sparse = new PointBlock.Builder(Au);
        foreach (var (week, value) in new[] { (0, 10.0), (2, 34.0), (5, 22.0), (7, 28.0) })
        {
            sparse.Add(key, Measurement.Of(value), Instant.FromUtc(start.AddDays(week * 7)));
        }
        var block = sparse.Build();

        var policyDim = new DimensionId("policy");
        var series = new[]
        {
            Tag(Resample(block, GapPolicy.LeaveMissing), policyDim, "leave-missing"),
            Tag(Resample(block, GapPolicy.ZeroFill), policyDim, "zero-fill"),
            Tag(Resample(block, GapPolicy.CarryForward), policyDim, "carry-forward"),
            Tag(Resample(block, GapPolicy.Interpolate), policyDim, "interpolate"),
        };
        var view = ChartProjector.Instance.Project(Merge(Au, series),
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: policyDim, ValueAxisTitle: "au"), Opts);
        return new("gaps", "Gap policies", "GapPolicy · all four",
            "One sparse series (weeks 1, 3, 4, 6 empty) under every gap policy: skip, zero, carry forward, interpolate.", view);

        static PointBlock Resample(PointBlock b, GapPolicy g) =>
            Transform.Resample(Period.Week, Aggregators.Mean, g).Apply(b, Tctx);
    }

    private static ShowcasePanel AggregatorSweep(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Load.Value, [1], Back(today, 84), Au);
        var statDim = new DimensionId("stat");
        var stats = new (string Name, IAggregator Agg)[] { ("min", Aggregators.Min), ("mean", Aggregators.Mean), ("max", Aggregators.Max), ("median", Aggregators.Median) };
        var series = stats.Select(s => Tag(Transform.Resample(Period.Week, s.Agg, GapPolicy.LeaveMissing).Apply(block, Tctx), statDim, s.Name)).ToArray();
        var view = ChartProjector.Instance.Project(Merge(Au, series),
            new ViewSpec(ChartKind.Line, AxisSource.Time, SeriesBy: statDim, ValueAxisTitle: "au"), Opts);
        return new("aggregators", "Aggregator sweep", "Aggregators · min/mean/max/median",
            "The same weekly buckets under four aggregators — the spread between min and max is the training variability.", view);
    }

    private static ShowcasePanel GoalsByVenue(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Goals.Value, [1, 2, 3], Back(today, 800), Count);
        // Sum goals per (player, venue): drop the date axis, keep both dimensions.
        var totals = Transform.Reduce(p => p.Key, Aggregators.Sum).Apply(block, Tctx);
        var view = ChartProjector.Instance.Project(totals,
            new ViewSpec(ChartKind.Column, AxisSource.Category(Seed.Venue), SeriesBy: Seed.Player, ValueAxisTitle: "goals"), Opts);
        return new("venue", "Goals: home vs away", "Category axis · grouped columns",
            "A non-temporal report: goals summed by venue, one column group per athlete.", view);
    }

    private static ShowcasePanel GoalsYearOnYear(SeededPointSource src, DateTime today)
    {
        var cal = CalendarContext.Default;
        var thisSeason = cal.Season.SeasonFor(Instant.FromUtc(today), cal);
        var lastSeason = cal.Season.SeasonFor(new Instant(thisSeason.Start.UtcTicks - TimeSpan.TicksPerDay), cal);
        var seasonDim = new DimensionId("season");

        var current = Tag(GoalsPerPlayer(src, thisSeason), seasonDim, cal.Season.Label(thisSeason, cal));
        var previous = Tag(GoalsPerPlayer(src, lastSeason), seasonDim, cal.Season.Label(lastSeason, cal));

        var view = ChartProjector.Instance.Project(Merge(Count, previous, current),
            new ViewSpec(ChartKind.Column, AxisSource.Category(Seed.Player), SeriesBy: seasonDim, ValueAxisTitle: "goals"), Opts);
        return new("yoy", "Goals per athlete: this season vs last", "Year-on-year · Reduce + season calendar",
            "The real-world query: total goals per athlete in each season, drawn side by side. Same shape works for any metric.", view);

        static PointBlock GoalsPerPlayer(SeededPointSource s, DateInterval season) =>
            Transform.Reduce(p => p.Key.Without(Seed.Venue), Aggregators.Sum)
                .Apply(s.Raw(Seed.Goals.Value, [1, 2, 3, 4], season, Count), Tctx);
    }

    private static ShowcasePanel RestingHrSecondMetric(SeededPointSource src, DateTime today)
    {
        var block = src.Raw(Seed.Hr.Value, [1, 2], Back(today, 365), Bpm);
        var monthly = Transform.Resample(Period.Month, Aggregators.Mean, GapPolicy.LeaveMissing).Apply(block, Tctx);
        var view = ChartProjector.Instance.Project(monthly,
            new ViewSpec(ChartKind.Area, AxisSource.Time, SeriesBy: Seed.Player, ValueAxisTitle: "bpm"), Opts);
        return new("hr", "Resting HR (a second metric)", "Second metric · own unit",
            "A different metric with its own unit (bpm) flows through the identical pipeline — nothing is metric-specific.", view);
    }

    /// <summary>
    /// A multi-series chart through the engine: goals (columns), minutes (a line on the right-hand axis) and
    /// goals per 90 — a derived metric, total goals ÷ total minutes × 90 per month — for one player.
    /// </summary>
    public static async Task<ShowcasePanel> GoalsMinutesPer90Async(ReportEngine engine, DateTime today)
    {
        PipelineSpec Monthly(MetricId metric, ChartKind kind) => PipelineSpec.Create(
            Seed.Tenant, metric, [new EntityRef(Seed.Player, 3)], Back(today, 365),
            new ViewSpec(kind, AxisSource.Time), Transform.Resample(Period.Month, Aggregators.Sum, GapPolicy.LeaveMissing));

        var view = await engine.RunChartAsync(ChartSpec.Of(
            new SeriesSpec(Monthly(Seed.Goals, ChartKind.Column)),
            new SeriesSpec(Monthly(Seed.Minutes, ChartKind.Line), Axis: ValueAxis.Secondary),
            new SeriesSpec(Monthly(Seed.GoalsPer90, ChartKind.Line))), Opts);
        return new("per90", "Goals, minutes and goals per 90 (player 3)", "Multi-series · derived metric · second axis",
            "Three reports on one chart, fetched together. Goals per 90 divides each month's total goals by its total minutes — never an average of per-match ratios.", view);
    }

    private static DateInterval Back(DateTime today, int days) =>
        new(Instant.FromUtc(today.Date.AddDays(-days)), Instant.FromUtc(today.Date.AddDays(1)));

    private static PointBlock Tag(PointBlock block, DimensionId dimension, string value) =>
        Transform.Rekey(k => k.With(KeyPart.Category(dimension, value))).Apply(block, Tctx);

    private static PointBlock Merge(Unit unit, params PointBlock[] blocks)
    {
        var builder = new PointBlock.Builder(unit);
        foreach (var b in blocks)
        {
            for (int i = 0; i < b.Count; i++) builder.Add(b.Row(i));
        }
        return builder.Build();
    }
}
