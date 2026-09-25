namespace Meridian.Hosts.Http;

/// <summary>The demo's squad dashboard, as the JSON a product would store (see DashboardDefinition).</summary>
public static class DashboardExample
{
    public const string Json = """
        {
          "title": "Squad dashboard",
          "charts": [
            { "title": "Goals and goals per 90 by month",
              "series": [
                { "metric": "goals", "view": { "kind": "column" },
                  "transforms": [ { "kind": "resample", "period": "month", "aggregator": "sum" }, { "kind": "groupBy", "aggregator": "sum" } ] },
                { "metric": "goals-per-90", "axis": "secondary",
                  "transforms": [ { "kind": "resample", "period": "month", "aggregator": "sum" }, { "kind": "groupBy", "aggregator": "sum" } ] } ] },
            { "title": "Minutes by month",
              "series": [ { "metric": "minutes", "view": { "kind": "area" },
                            "transforms": [ { "kind": "resample", "period": "month", "aggregator": "sum" }, { "kind": "groupBy", "aggregator": "sum" } ] } ] },
            { "title": "Goals by venue",
              "series": [ { "metric": "goals", "dimensions": ["venue"], "view": { "kind": "column", "x": "venue" },
                            "transforms": [ { "kind": "total", "aggregator": "sum", "by": ["venue"] } ] } ] },
            { "title": "Goals per 90 by player",
              "series": [ { "metric": "goals-per-90", "view": { "kind": "column", "x": "player" },
                            "transforms": [ { "kind": "total", "aggregator": "sum", "by": ["player"] } ] } ] },
            { "title": "Share of the squad's goal involvements",
              "series": [ { "metric": "goal-involvements", "view": { "kind": "column", "x": "player" },
                            "transforms": [ { "kind": "total", "aggregator": "sum", "by": ["player"] }, { "kind": "share", "by": ["player"] } ] } ] },
            { "title": "Weekly training load",
              "series": [ { "metric": "training-load", "view": { "kind": "line", "seriesBy": "player" },
                            "transforms": [ { "kind": "resample", "period": "week", "aggregator": "mean" } ] } ] }
          ]
        }
        """;
}
