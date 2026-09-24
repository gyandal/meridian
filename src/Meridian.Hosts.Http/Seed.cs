using System.Collections.Immutable;
using Meridian.Core;
using Meridian.Semantics;

namespace Meridian.Hosts.Http;

/// <summary>Deterministic demo data + catalog. Two daily time-series metrics (with a real gap on one
/// athlete) and one categorical metric, so the showcase can exercise every feature offline.</summary>
public static class Seed
{
    public const string Tenant = "demo";
    public static readonly DimensionId Player = new("player");
    public static readonly DimensionId Venue = new("venue");
    public static readonly MetricId Load = new("training-load");
    public static readonly MetricId Hr = new("resting-hr");
    public static readonly MetricId Goals = new("goals");
    public static readonly int[] Players = [1, 2, 3, 4];
    public const int HistoryDays = 540; // ~1.5 years so Season resampling has something to show

    public static IMetricCatalog Catalog() => new InMemoryMetricCatalog(
    [
        new MetricDefinition(Load, "Training Load", new Unit("au"), "mean", [Player], TimeGrain.Daily),
        new MetricDefinition(Hr, "Resting HR", new Unit("bpm"), "mean", [Player], TimeGrain.Daily),
        new MetricDefinition(Goals, "Goals", new Unit(""), "sum", [Player, Venue], TimeGrain.Instant),
    ]);

    public static Dictionary<string, List<Point>> Data(DateTime today)
    {
        var load = new List<Point>();
        var hr = new List<Point>();
        foreach (var p in Players)
        {
            var key = PointKey.Of(KeyPart.Entity(Player, p));
            var rng = new Random(p * 131);
            double baseLoad = 55 + p * 6, baseHr = 52 + p * 2;
            for (int i = 0; i < HistoryDays; i++)
            {
                var at = Instant.FromUtc(today.AddDays(-(HistoryDays - 1 - i)));
                double wave = 12 * Math.Sin(i / 7.0 * Math.PI);
                double step = i > HistoryDays - 40 ? 14 : 0;
                double noise = rng.NextDouble() * 14 - 7;
                bool gap = p == 2 && i >= HistoryDays - 24 && i < HistoryDays - 10; // a real ~2-week gap
                if (!gap) load.Add(new Point(key, Math.Round(baseLoad + wave + step + noise, 1), at));
                hr.Add(new Point(key, Math.Round(baseHr + Math.Sin(i / 30.0) * 4 + (rng.NextDouble() * 6 - 3), 1), at));
            }
        }

        // Goals as dated events across ~2+ seasons, so "this season vs last" is a real query.
        var goals = new List<Point>();
        foreach (var p in Players)
        {
            var rng = new Random(p * 17 + 3);
            double scoringRate = 0.10 + p * 0.03; // goals-per-match-ish, per player
            for (int i = 0; i < 800; i++)
            {
                if (rng.NextDouble() > scoringRate) continue;         // most days, no goal
                var venue = rng.NextDouble() < 0.6 ? "Home" : "Away"; // home bias
                var key = PointKey.Of(KeyPart.Entity(Player, p), KeyPart.Category(Venue, venue));
                goals.Add(new Point(key, 1.0, Instant.FromUtc(today.AddDays(-(799 - i)))));
            }
        }

        return new Dictionary<string, List<Point>>
        {
            [Load.Value] = load,
            [Hr.Value] = hr,
            [Goals.Value] = goals,
        };
    }
}
