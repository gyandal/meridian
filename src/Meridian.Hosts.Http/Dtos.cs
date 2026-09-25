using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Hosts.Http;

/// <summary>Wire request from the dashboard — the full builder surface, including filtering and grouping.</summary>
public sealed record ReportRequest(
    string Metric,
    long[] Entities,
    int PastDays,
    string Transform,       // raw | resample | rolling7 | rolling28   (used when GroupBy = time)
    string Period,          // day | week | month | season
    string Aggregator,      // mean | sum | min | max | median | last | count
    string Gap,             // leave-missing | zero-fill | carry-forward | interpolate
    string ChartKind,       // line | column | area
    bool SeriesByEntity,
    bool StatusOn,
    double StatusLow,
    double StatusHigh,
    string GroupBy = "time",          // time | player | venue  (a dimension collapses to one value each)
    double? FilterMin = null,          // keep points with value >= FilterMin
    double? FilterMax = null,          // keep points with value <= FilterMax
    string? CategoryDim = null,        // e.g. "venue"
    string? CategoryValue = null,      // e.g. "Home" — keep only this category
    string? TimeZone = null);          // IANA zone the report's days/weeks are drawn in (default UTC)

/// <summary>Run a dashboard definition for some players over the last <paramref name="PastDays"/> days.</summary>
public sealed record DashboardRequest(DashboardDefinition Definition, long[] Entities, int PastDays = 365, string? TimeZone = null);

public static class ReportRequestMapper
{
    private static DimensionId Dim(string name) => name switch
    {
        "venue" => Seed.Venue,
        _ => Seed.Player,
    };

    public static PipelineSpec ToSpec(ReportRequest request, DimensionId entityDimension, string tenant, DateTime utcNow)
    {
        var today = utcNow.Date;
        var timeframe = new DateInterval(
            Instant.FromUtc(today.AddDays(-Math.Clamp(request.PastDays, 1, 800))),
            Instant.FromUtc(today.AddDays(1)));

        var aggregator = request.Aggregator is { } an && Aggregators.TryResolve(an, out var a) ? a : Aggregators.Mean;

        var transforms = new List<ITransform>();

        // --- filtering (runs first) ---
        if (request.FilterMin is { } min) transforms.Add(Transform.Filter(p => p.Measure.IsPresent && p.Measure.Value >= min));
        if (request.FilterMax is { } max) transforms.Add(Transform.Filter(p => p.Measure.IsPresent && p.Measure.Value <= max));
        if (request.CategoryDim is { } cd && request.CategoryValue is { } cv)
        {
            var catDim = Dim(cd);
            transforms.Add(Transform.Filter(p => p.Key.TryGet(catDim, out var part) && part.Text == cv));
        }

        ViewSpec view;

        if (request.GroupBy is "player" or "venue")
        {
            // Grouping: collapse to one aggregated value per group, drawn as a category chart.
            var groupDim = Dim(request.GroupBy);
            transforms.Add(Transform.Total(aggregator, groupDim));
            view = new ViewSpec(ChartKind.Column, AxisSource.Category(groupDim), ValueUnit: null);
        }
        else
        {
            // Time path: resample / rolling / raw, plotted against the temporal axis.
            switch (request.Transform)
            {
                case "resample":
                    transforms.Add(Transform.Resample(ResolvePeriod(request.Period), aggregator, ResolveGap(request.Gap)));
                    break;
                case "rolling7":
                    transforms.Add(Transform.Rolling(TimeSpan.FromDays(7), aggregator));
                    break;
                case "rolling28":
                    transforms.Add(Transform.Rolling(TimeSpan.FromDays(28), aggregator));
                    break;
            }

            var status = request.StatusOn
                ? new TargetBand(request.StatusLow, request.StatusHigh, (request.StatusHigh - request.StatusLow) * 0.5)
                : null;

            view = new ViewSpec(
                ResolveKind(request.ChartKind),
                AxisSource.Time,
                SeriesBy: request.SeriesByEntity ? entityDimension : null,
                Status: status);
        }

        // Keep the non-entity dimensions this report filters or groups on (sources return all they know).
        var dimensions = new[] { request.CategoryDim, request.GroupBy }
            .Where(d => d == "venue").Select(d => Dim(d!)).Distinct().ToArray();

        return PipelineSpec.Create(
            tenant,
            new MetricId(request.Metric),
            [.. request.Entities.Select(id => new EntityRef(entityDimension, id))],
            timeframe,
            view,
            [.. transforms]).WithDimensions(dimensions);
    }

    private static IPeriod ResolvePeriod(string period) => period switch
    {
        "day" => Period.Day,
        "month" => Period.Month,
        "season" => Period.Season,
        _ => Period.Week,
    };

    private static GapPolicy ResolveGap(string gap) => gap switch
    {
        "zero-fill" => GapPolicy.ZeroFill,
        "carry-forward" => GapPolicy.CarryForward,
        "interpolate" => GapPolicy.Interpolate,
        _ => GapPolicy.LeaveMissing,
    };

    private static ChartKind ResolveKind(string kind) => kind switch
    {
        "column" => ChartKind.Column,
        "area" => ChartKind.Area,
        _ => ChartKind.Line,
    };
}
