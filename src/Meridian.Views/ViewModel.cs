using Meridian.Core;

namespace Meridian.Views;

/// <summary>Semantic state of a mark — an enum, never a CSS class or hex. The theme turns it into a
/// colour at projection. Never a CSS class on the datum.</summary>
public enum SemanticStatus
{
    Neutral,
    Good,
    Watch,
    Bad,
}

/// <summary>Chart kinds, named agnostically. A front-end (Highcharts, Recharts, D3) maps these to its
/// own config — the library never references a charting library.</summary>
public enum ChartKind
{
    Line,
    Area,
    Column,
    Bar,
    Pie,
    Scatter,
    Sparkline,
    Table,
}

public enum AxisKind
{
    Temporal,
    Linear,
    Category,
}

public enum AnnotationKind
{
    VerticalLine,
    Band,
    Target,
}

/// <summary>
/// One plotted point, ready to render. Everything presentational is HERE and nowhere upstream:
/// the label (produced from the typed key + a formatter), the epoch-millis <see cref="At"/> (computed
/// once), the semantic <see cref="Status"/>, and the theme-resolved <see cref="ColorToken"/>.
/// <see cref="Estimated"/> is true for a projected value — a forecast, or a total that includes one — so it can
/// be drawn differently (dashed, faded); it's omitted for observed values. <see cref="Low"/> and <see cref="High"/>
/// are a projected value's range (e.g. 80%), for a band or error bar; omitted when there's none.
/// </summary>
public sealed record MarkView(
    string Label,
    double? Value,
    long? At,
    SemanticStatus Status,
    string? ColorToken,
    double? X = null,
    double? Y = null,
    double? Z = null,
    bool? Estimated = null,
    double? Low = null,
    double? High = null);

/// <summary>A series. In a multi-series chart built from several reports, <see cref="Kind"/> says how this
/// series is drawn (columns for goals, a line for goals per 90) and <see cref="Axis"/> which value axis it
/// uses (an index into <see cref="ChartView.Axes"/>: 1 = primary, 2 = secondary); both are omitted when the
/// chart's own kind and primary axis apply.</summary>
public sealed record SeriesView(string Name, string ColorToken, IReadOnlyList<MarkView> Marks, ChartKind? Kind = null, int? Axis = null);

/// <summary>An axis. A temporal axis also says what its times are (docs/TIME.md): <see cref="TimeKind.Instant"/>
/// values are UTC epoch-millis to display in <see cref="TimeZone"/>; <see cref="TimeKind.Local"/> values are
/// calendar positions (e.g. week buckets) whose epoch-millis encode the wall clock — display them in UTC,
/// never convert them. <see cref="TimeZone"/> on a local axis is the zone that drew the bucket boundaries.</summary>
public sealed record AxisView(
    AxisKind Kind,
    string Title,
    double? Min = null,
    double? Max = null,
    string? Unit = null,
    TimeKind? Time = null,
    string? TimeZone = null,
    string? Grain = null);

public sealed record LegendView(IReadOnlyList<string> Series);

/// <summary>Fixture lines, injury bands, targets — carried as DATA (kind + value + status), not as
/// hard-coded hex/HTML overlays baked into a transform.</summary>
public sealed record AnnotationView(
    AnnotationKind Kind,
    string Label,
    SemanticStatus Status,
    long? At = null,
    double? Value = null,
    double? From = null,
    double? To = null);

/// <summary>The chart-agnostic, serialisable render model. The single output every front-end and the
/// MCP `query` tool consume.</summary>
/// <param name="Stacked">True when the series stack at each x position (stacked columns or areas); the value axis
/// then spans the stacks' totals. Omitted (null) when they don't, so existing JSON is unchanged.</param>
public sealed record ChartView(
    ChartKind Kind,
    IReadOnlyList<SeriesView> Series,
    IReadOnlyList<AxisView> Axes,
    LegendView Legend,
    IReadOnlyList<AnnotationView> Annotations,
    bool? Stacked = null);

/// <summary>The value range stacked series need: at each x position, positives stack up from 0 and negatives down.</summary>
internal static class Stacks
{
    public static (double? Min, double? Max) Extent(IEnumerable<SeriesView> series)
    {
        var up = new Dictionary<(long?, string), double>();
        var down = new Dictionary<(long?, string), double>();
        foreach (var mark in series.SelectMany(s => s.Marks))
        {
            if (mark.Value is not { } v) continue;
            var position = (mark.At, mark.At is null ? mark.Label : "");
            var stack = v >= 0 ? up : down;
            stack[position] = stack.GetValueOrDefault(position) + v;
        }
        if (up.Count == 0 && down.Count == 0) return (null, null);
        return (Math.Min(0, down.Count > 0 ? down.Values.Min() : 0), Math.Max(0, up.Count > 0 ? up.Values.Max() : 0));
    }
}
