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
/// </summary>
public sealed record MarkView(
    string Label,
    double? Value,
    long? At,
    SemanticStatus Status,
    string? ColorToken,
    double? X = null,
    double? Y = null,
    double? Z = null);

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
public sealed record ChartView(
    ChartKind Kind,
    IReadOnlyList<SeriesView> Series,
    IReadOnlyList<AxisView> Axes,
    LegendView Legend,
    IReadOnlyList<AnnotationView> Annotations);
