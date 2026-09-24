using Meridian.Core;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;
using Meridian.Views.Json;

// End-to-end: raw daily load for two athletes → weekly-mean pipeline → project → chart-agnostic JSON.
// This is the whole stack (Core → Time → Transforms → Views → Views.Json) in one screen.

var player = new DimensionId("player");
var start = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc); // a Monday

var rng = new Random(1);
var rows = new List<Point>();
for (int athlete = 1; athlete <= 2; athlete++)
{
    var key = PointKey.Of(KeyPart.Entity(player, athlete));
    for (int day = 0; day < 21; day++)
    {
        rows.Add(new Point(key, 50 + rng.Next(0, 100), Instant.FromUtc(start.AddDays(day))));
    }
}
var load = PointBlock.FromRows(rows, new Unit("au"));

// Transform: weekly mean per athlete.
var weekly = Transform
    .Resample(Period.Week, Aggregators.Mean, GapPolicy.LeaveMissing)
    .Apply(load, TransformContext.Default);

// Project: one line series per athlete, ACWR-style sweet-spot status band on the value.
var view = ChartProjector.Instance.Project(
    weekly,
    new ViewSpec(
        ChartKind.Line,
        AxisSource.Time,
        SeriesBy: player,
        ValueAxisTitle: "Weekly mean load",
        ValueUnit: new Unit("au"),
        Status: new TargetBand(low: 80, high: 110, watchMargin: 20)),
    ProjectionOptions.Default);

Console.WriteLine(ChartViewJson.Serialize(view));
