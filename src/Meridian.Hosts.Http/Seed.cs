using System.Collections.Immutable;
using Meridian.Core;
using Meridian.Semantics;

namespace Meridian.Hosts.Http;

/// <summary>Deterministic demo data + catalog: two daily time-series metrics (with a real gap on one
/// athlete), and matches — minutes played and goals scored, by venue — with goals per 90 derived from them,
/// so the showcase can exercise every feature offline.</summary>
public static class Seed
{
    public const string Tenant = "demo";
    public static readonly DimensionId Player = new("player");
    public static readonly DimensionId Venue = new("venue");
    public static readonly MetricId Load = new("training-load");
    public static readonly MetricId Hr = new("resting-hr");
    public static readonly MetricId Goals = new("goals");
    public static readonly MetricId Minutes = new("minutes");
    public static readonly MetricId GoalsPer90 = new("goals-per-90");
    public static readonly MetricId Assists = new("assists");
    public static readonly MetricId Involvements = new("goal-involvements");
    public static readonly int[] Players = [1, 2, 3, 4];
    public const int HistoryDays = 540; // ~1.5 years so Season resampling has something to show

    public static IMetricCatalog Catalog() => new InMemoryMetricCatalog(
    [
        new MetricDefinition(Load, "Training Load", new Unit("au"), "mean", [Player], TimeGrain.Daily),
        new MetricDefinition(Hr, "Resting HR", new Unit("bpm"), "mean", [Player], TimeGrain.Daily),
        new MetricDefinition(Goals, "Goals", new Unit(""), "sum", [Player, Venue], TimeGrain.Instant),
        new MetricDefinition(Minutes, "Minutes", new Unit("min"), "sum", [Player, Venue], TimeGrain.Instant),
        MetricDefinition.Ratio(GoalsPer90, "Goals per 90", new Unit("/90"), Goals, Minutes, 90, [Player, Venue]),
        new MetricDefinition(Assists, "Assists", new Unit(""), "sum", [Player, Venue], TimeGrain.Instant),
        MetricDefinition.Sum(Involvements, "Goal involvements", new Unit(""), [Goals, Assists], [Player, Venue]),
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

        // Matches every fourth day across ~2+ seasons, so "this season vs last" is a real query. A player
        // appears (minutes) or doesn't; goals are events, only in matches, at a rate tied to minutes played.
        var goals = new List<Point>();
        var minutes = new List<Point>();
        var assists = new List<Point>();
        var fixtures = new Random(2024);
        for (int i = 0; i < 800; i += 4)
        {
            var venue = fixtures.NextDouble() < 0.5 ? "Home" : "Away";
            var kickOff = Instant.FromUtc(today.AddDays(-(799 - i)).Date.AddHours(15));
            foreach (var p in Players)
            {
                var rng = new Random(p * 7919 + i);
                if (rng.NextDouble() < 0.15) continue; // not in the squad
                double played = rng.NextDouble() switch { < 0.65 => 90, < 0.85 => 20 + rng.Next(25), _ => 45 + rng.Next(40) };
                var key = PointKey.Of(KeyPart.Entity(Player, p), KeyPart.Category(Venue, venue));
                minutes.Add(new Point(key, played, kickOff));

                double perChance = (0.10 + p * 0.04) * played / 90 * (venue == "Home" ? 1.2 : 0.9); // home bias
                for (int chance = 0; chance < 3; chance++)
                {
                    if (rng.NextDouble() < perChance) goals.Add(new Point(key, 1.0, kickOff));
                }
                var creator = new Random(p * 4099 + i); // its own stream, so goals are as before
                if (creator.NextDouble() < (0.30 - p * 0.04) * played / 90) assists.Add(new Point(key, 1.0, kickOff));
            }
        }

        return new Dictionary<string, List<Point>>
        {
            [Load.Value] = load,
            [Hr.Value] = hr,
            [Goals.Value] = goals,
            [Minutes.Value] = minutes,
            [Assists.Value] = assists,
        };
    }
}
