using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core;
using Meridian.Semantics;
using Meridian.Time;
using Meridian.Transforms;
using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// A dashboard as data — what a product stores when users build and edit dashboards. Everything is
/// declarative (no code), so it can be saved, versioned, sent over an API or produced by an agent.
/// <see cref="ToDashboard"/> turns it into a runnable <see cref="Dashboard"/>, failing with the path of
/// anything it doesn't understand.
/// </summary>
/// <example>
/// <code>
/// { "title": "Squad", "charts": [
///   { "title": "Goals by venue", "series": [
///     { "metric": "goals", "dimensions": ["venue"],
///       "transforms": [{ "kind": "total", "aggregator": "sum", "by": ["venue"] }],
///       "view": { "kind": "column", "x": "venue" } } ] } ] }
/// </code>
/// </example>
public sealed record DashboardDefinition(string Title, IReadOnlyList<ChartDefinition> Charts)
{
    public Dashboard ToDashboard() =>
        new(Title, [.. Required(Charts, "charts").Select((c, i) => c.ToChart($"charts[{i}]"))]);

    public static DashboardDefinition Parse(string json) =>
        JsonSerializer.Deserialize(json, DashboardJsonContext.Default.DashboardDefinition)
        ?? throw new DashboardDefinitionException("$", "the definition is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, DashboardJsonContext.Default.DashboardDefinition);

    internal static IReadOnlyList<T> Required<T>(IReadOnlyList<T>? items, string path) =>
        items is { Count: > 0 } ? items : throw new DashboardDefinitionException(path, "needs at least one entry.");
}

public sealed record ChartDefinition(string Title, IReadOnlyList<SeriesDefinition> Series)
{
    internal DashboardChart ToChart(string path) =>
        new(Title, [.. DashboardDefinition.Required(Series, path + ".series").Select((s, i) => s.ToSeries($"{path}.series[{i}]"))]);
}

/// <param name="Axis">"primary" (default) or "secondary".</param>
/// <param name="Compare">Compare with an earlier period, e.g. <c>{ "unit": "season", "back": 1 }</c> for last season.</param>
/// <param name="MinimumDenominator">For a ratio metric, a qualifying threshold: e.g. 450 (minutes) for goals per 90.</param>
public sealed record SeriesDefinition(
    string Metric,
    IReadOnlyList<TransformDefinition>? Transforms = null,
    ViewDefinition? View = null,
    IReadOnlyList<string>? Dimensions = null,
    string? Name = null,
    string? Axis = null,
    CompareDefinition? Compare = null,
    double? MinimumDenominator = null)
{
    internal DashboardSeries ToSeries(string path)
    {
        if (string.IsNullOrWhiteSpace(Metric)) throw new DashboardDefinitionException(path + ".metric", "is required.");
        var axis = Axis?.ToLowerInvariant() switch
        {
            null or "primary" => ValueAxis.Primary,
            "secondary" => ValueAxis.Secondary,
            _ => throw new DashboardDefinitionException(path + ".axis", $"'{Axis}' isn't an axis; use 'primary' or 'secondary'."),
        };
        return new DashboardSeries(
            new MetricId(Metric),
            [.. (Transforms ?? []).Select((t, i) => t.ToTransform($"{path}.transforms[{i}]"))],
            (View ?? new ViewDefinition()).ToView(path + ".view"),
            [.. (Dimensions ?? []).Select(d => new DimensionId(d))],
            Name,
            axis,
            Compare?.ToComparison(path + ".compare"),
            MinimumDenominator is null or (> 0 and < double.PositiveInfinity) ? MinimumDenominator
                : throw new DashboardDefinitionException(path + ".minimumDenominator", "a qualifying minimum is a positive amount, e.g. 450 (minutes)."));
    }
}

/// <summary>
/// A comparison with an earlier period: <c>unit</c> (day, week, month, year or season) and <c>back</c> (how many,
/// default 1); <c>show</c> is <c>baseline</c> (default — the earlier period's values on this period's axis),
/// <c>difference</c> or <c>percent</c>.
/// </summary>
public sealed record CompareDefinition(string Unit, int Back = 1, string? Show = null)
{
    internal Comparison ToComparison(string path)
    {
        if (Back < 1) throw new DashboardDefinitionException(path + ".back", "must be at least 1.");
        var baseline = Unit?.ToLowerInvariant() switch
        {
            "day" => Baseline.DaysBack(Back),
            "week" => Baseline.WeeksBack(Back),
            "month" => Baseline.MonthsBack(Back),
            "year" => Baseline.YearsBack(Back),
            "season" => Baseline.SeasonsBack(Back),
            _ => throw new DashboardDefinitionException(path + ".unit", $"'{Unit}' isn't a unit; use day, week, month, year or season."),
        };
        var output = Show?.ToLowerInvariant() switch
        {
            null or "baseline" => ComparisonOutput.Baseline,
            "difference" => ComparisonOutput.Difference,
            "percent" => ComparisonOutput.PercentChange,
            _ => throw new DashboardDefinitionException(path + ".show", $"'{Show}' isn't something to show; use baseline, difference or percent."),
        };
        return new Comparison(baseline, output);
    }
}

/// <summary>
/// A declarative transform. <c>kind</c> is one of:
/// <c>resample</c> (<c>period</c>, <c>aggregator</c>, optional <c>gap</c>, and <c>across</c>: "observed" — the default — or
/// "timeframe" to fill every bucket of the report's timeframe),
/// <c>rolling</c> (<c>days</c>, <c>aggregator</c>),
/// <c>groupBy</c> (<c>aggregator</c>, <c>by</c> — keeps time),
/// <c>total</c> (<c>aggregator</c>, <c>by</c> — over the whole timeframe),
/// <c>where</c> (<c>dimension</c> and <c>in</c> or <c>notIn</c> — e.g. home matches only),
/// <c>range</c> (<c>min</c> and/or <c>max</c> — keep values in range),
/// <c>share</c> (<c>by</c> — each value as a % of the total across those dimensions),
/// <c>cumulative</c> (<c>aggregator</c> — a running total, e.g. goals so far),
/// <c>top</c> / <c>bottom</c> (<c>n</c>, <c>aggregator</c> to rank by, optional <c>by</c> to rank within — e.g. the top 5 scorers),
/// <c>forecast</c> (<c>model</c>: mean, trend, seasonal-naive or holt-winters, with <c>season</c> — its length in
/// buckets — where it has one; <c>n</c> buckets ahead, or <c>until</c>: "season-end"; optional <c>range</c>, a
/// percentage such as 80, and <c>runningTotal</c> for "on pace for" with its range).
/// Periods: <c>day</c>, <c>week</c>, <c>month</c>, <c>season</c>, <c>hour</c>, or a span such as <c>15m</c> / <c>6h</c>.
/// Gaps: <c>leave-missing</c> (default), <c>zero-fill</c>, <c>carry-forward</c>, <c>interpolate</c>.
/// </summary>
public sealed record TransformDefinition(
    string Kind,
    string? Period = null,
    string? Aggregator = null,
    string? Gap = null,
    int? Days = null,
    IReadOnlyList<string>? By = null,
    string? Dimension = null,
    IReadOnlyList<string>? In = null,
    IReadOnlyList<string>? NotIn = null,
    double? Min = null,
    double? Max = null,
    int? N = null,
    string? Model = null,
    int? Season = null,
    string? Until = null,
    double? Range = null,
    bool? RunningTotal = null,
    string? Across = null)
{
    internal ITransform ToTransform(string path) => Kind?.ToLowerInvariant() switch
    {
        "resample" => Transform.Resample(ParsePeriod(path), ParseAggregator(path), ParseGap(path), ParseAcross(path)),
        "rolling" => Days is > 0 ? Transform.Rolling(TimeSpan.FromDays(Days.Value), ParseAggregator(path))
            : throw new DashboardDefinitionException(path + ".days", "a rolling window needs a positive number of days."),
        "groupby" => Transform.GroupBy(ParseAggregator(path), [.. (By ?? []).Select(d => new DimensionId(d))]),
        "total" => Transform.Total(ParseAggregator(path), [.. (By ?? []).Select(d => new DimensionId(d))]),
        "where" => Where(path),
        "cumulative" => Transform.Cumulative(ParseAggregator(path)),
        "forecast" => Range is null or (> 0 and < 100)
            ? Transform.Forecast(ParseModel(path), ParseHorizon(path), Range, RunningTotal ?? false)
            : throw new DashboardDefinitionException(path + ".range", "a range is a percentage between 0 and 100, e.g. 80."),
        "top" or "bottom" => N is > 0
            ? (Kind.Equals("top", StringComparison.OrdinalIgnoreCase) ? Transform.Top : (Func<int, IAggregator, DimensionId[], ITransform>)Transform.Bottom)(
                N.Value, ParseAggregator(path), [.. (By ?? []).Select(d => new DimensionId(d))])
            : throw new DashboardDefinitionException(path + ".n", "how many to keep: a positive number."),
        "share" => By is { Count: > 0 } ? Transform.ShareOf([.. By.Select(d => new DimensionId(d))])
            : throw new DashboardDefinitionException(path + ".by", "a share needs the dimensions it's a share across, e.g. [\"athlete\"]."),
        "range" => Min is null && Max is null
            ? throw new DashboardDefinitionException(path, "a range needs a min, a max, or both.")
            : Transform.WhereValue(Min, Max),
        _ => throw new DashboardDefinitionException(path + ".kind", $"'{Kind}' isn't a transform; use resample, rolling, groupBy, total, where, range, share, cumulative, top, bottom or forecast."),
    };

    private ITransform Where(string path)
    {
        if (string.IsNullOrWhiteSpace(Dimension)) throw new DashboardDefinitionException(path + ".dimension", "a where filter needs a dimension, e.g. \"venue\".");
        return (In, NotIn) switch
        {
            ({ Count: > 0 } values, null) => Transform.WhereIn(new DimensionId(Dimension), [.. values]),
            (null, { Count: > 0 } values) => Transform.WhereNotIn(new DimensionId(Dimension), [.. values]),
            _ => throw new DashboardDefinitionException(path, "a where filter needs exactly one of \"in\" or \"notIn\", with at least one value."),
        };
    }

    private ForecastModel ParseModel(string path)
    {
        if (Season is <= 0) throw new DashboardDefinitionException(path + ".season", "a season is at least one bucket long.");
        return Model?.ToLowerInvariant() switch
        {
            "mean" => ForecastModel.Mean,
            "trend" => ForecastModel.Trend,
            "seasonal-naive" => Season is { } m ? ForecastModel.SeasonalNaive(m)
                : throw new DashboardDefinitionException(path + ".season", "a seasonal model needs the season's length in buckets, e.g. 12 for months."),
            "holt-winters" => ForecastModel.HoltWinters(Season),
            _ => throw new DashboardDefinitionException(path + ".model", $"'{Model}' isn't a forecast model; use mean, trend, seasonal-naive or holt-winters."),
        };
    }

    private ForecastHorizon ParseHorizon(string path) => (N, Until?.ToLowerInvariant()) switch
    {
        ( > 0, null) => ForecastHorizon.Buckets(N.Value),
        (null, "season-end") => ForecastHorizon.SeasonEnd,
        _ => throw new DashboardDefinitionException(path, "a forecast reaches either n buckets ahead or until \"season-end\"."),
    };

    private IAggregator ParseAggregator(string path) =>
        Aggregator is { } name && Aggregators.TryResolve(name, out var aggregator) ? aggregator
            : throw new DashboardDefinitionException(path + ".aggregator", $"'{Aggregator}' isn't an aggregator; use sum, mean, min, max, count, median, first, last, stddev, variance, or a percentile such as p90.");

    private IPeriod ParsePeriod(string path) => Time.Period.Named(Period)
        ?? throw new DashboardDefinitionException(path + ".period", $"'{Period}' isn't a period; use day, week, month, season, hour, or a span dividing a day such as 15m or 6h.");

    private FillAcross ParseAcross(string path) => Across?.ToLowerInvariant() switch
    {
        null or "observed" => FillAcross.Observed,
        "timeframe" => FillAcross.Timeframe,
        _ => throw new DashboardDefinitionException(path + ".across", $"'{Across}' isn't a fill range; use observed or timeframe."),
    };

    private GapPolicy ParseGap(string path) => Gap?.ToLowerInvariant() switch
    {
        null or "leave-missing" => GapPolicy.LeaveMissing,
        "zero-fill" => GapPolicy.ZeroFill,
        "carry-forward" => GapPolicy.CarryForward,
        "interpolate" => GapPolicy.Interpolate,
        _ => throw new DashboardDefinitionException(path + ".gap", $"'{Gap}' isn't a gap policy; use leave-missing, zero-fill, carry-forward or interpolate."),
    };
}

/// <param name="Kind">line (default), column, area, bar, pie, scatter or table.</param>
/// <param name="X">"time" (default) or a dimension to use as categories, e.g. "venue".</param>
/// <param name="SeriesBy">A dimension that splits series, e.g. "player".</param>
/// <param name="Stacked">Stack the series at each x position (stacked columns or areas).</param>
public sealed record ViewDefinition(string? Kind = null, string? X = null, string? SeriesBy = null, string? Title = null, bool? Stacked = null)
{
    internal ViewSpec ToView(string path)
    {
        var kind = Kind is null ? ChartKind.Line
            : Enum.TryParse<ChartKind>(Kind, ignoreCase: true, out var k) ? k
            : throw new DashboardDefinitionException(path + ".kind", $"'{Kind}' isn't a chart kind; use line, column, area, bar, pie, scatter or table.");
        var x = X is null || X.Equals("time", StringComparison.OrdinalIgnoreCase) ? AxisSource.Time : AxisSource.Category(new DimensionId(X));
        return new ViewSpec(kind, x, SeriesBy is null ? null : new DimensionId(SeriesBy), Title, Stacked: Stacked ?? false);
    }
}

/// <summary>A dashboard definition that can't be understood, with the JSON path of the problem.</summary>
public sealed class DashboardDefinitionException(string path, string problem)
    : ArgumentException($"{path}: {problem}")
{
    public string Path { get; } = path;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(DashboardDefinition))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
