using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// One series of a multi-series chart: a report (its metric, entities, timeframe, transforms and view), shown
/// under <see cref="Name"/> (default: the metric's catalog name) on a value <see cref="Axis"/>. The report's
/// own view decides how the series is drawn — columns, line, area.
/// </summary>
public sealed record SeriesSpec(PipelineSpec Report, string? Name = null, ValueAxis Axis = ValueAxis.Primary);

/// <summary>
/// A chart made of several reports — goals, minutes and goals per 90 on one chart. Every part must share the
/// x-axis (e.g. months in the same zone). The engine runs all parts together, so parts that share entities
/// and timeframe cost one source query.
/// </summary>
public sealed record ChartSpec(IReadOnlyList<SeriesSpec> Series)
{
    public static ChartSpec Of(params SeriesSpec[] series) => new(series);
}
