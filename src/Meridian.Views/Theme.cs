using System.Globalization;
using Meridian.Core;
using Meridian.Time;

namespace Meridian.Views;

/// <summary>Resolves colour from role/index — the only place colour is decided. Tokens may be hex or
/// named; the front-end is free to remap them.</summary>
public interface ITheme
{
    string ColorForSeries(int seriesIndex);
    string ColorForStatus(SemanticStatus status);
}

public sealed class DefaultTheme : ITheme
{
    public static readonly DefaultTheme Instance = new();

    private static readonly string[] Palette =
        ["#4E79A7", "#F28E2B", "#59A14F", "#E15759", "#B07AA1", "#76B7B2", "#EDC948", "#FF9DA7"];

    public string ColorForSeries(int seriesIndex) => Palette[((seriesIndex % Palette.Length) + Palette.Length) % Palette.Length];

    public string ColorForStatus(SemanticStatus status) => status switch
    {
        SemanticStatus.Good => "#59A14F",
        SemanticStatus.Watch => "#EDC948",
        SemanticStatus.Bad => "#E15759",
        _ => "#9AA0A6",
    };
}

/// <summary>Turns typed keys/instants into display strings. Real deployments inject a resolver backed by
/// the catalog (so entity ids become names); the default is deliberately dumb.</summary>
public interface ILabelResolver
{
    string SeriesName(KeyPart seriesPart);
    string CategoryLabel(KeyPart categoryPart);
    /// <summary>Label for a point's time. <paramref name="ticks"/> are UTC for instants (shown in the calendar's
    /// zone) and wall-clock for local values (shown as-is, formatted by grain).</summary>
    string TimeLabel(long ticks, TimeAxis time, CalendarContext calendar);
}

public sealed class DefaultLabelResolver : ILabelResolver
{
    public static readonly DefaultLabelResolver Instance = new();

    public string SeriesName(KeyPart seriesPart) => seriesPart.Kind == KeyPartKind.Category
        ? seriesPart.Text
        : $"{seriesPart.Dimension.Name} {seriesPart.Numeric}";

    public string CategoryLabel(KeyPart categoryPart) => categoryPart.Kind == KeyPartKind.Category
        ? categoryPart.Text
        : categoryPart.Numeric.ToString(CultureInfo.InvariantCulture);

    public string TimeLabel(long ticks, TimeAxis time, CalendarContext calendar)
    {
        if (time.Kind == TimeKind.Instant)
        {
            return DateOrDateTime(new DateTime(TimeZones.ToLocalTicks(ticks, calendar.Zone)));
        }

        var local = new DateTime(ticks);
        return time.Grain switch
        {
            "month" => local.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "season" => calendar.Season.Label(new DateInterval(new Instant(ticks), new Instant(ticks)), calendar.Floating),
            _ => DateOrDateTime(local),
        };
    }

    private static string DateOrDateTime(DateTime local) => local.TimeOfDay == TimeSpan.Zero
        ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Culture-aware value formatting — culture lives here, never on the datum.</summary>
public interface IValueFormatter
{
    string Format(double value, Unit unit);
}

public sealed class DefaultValueFormatter(CultureInfo? culture = null) : IValueFormatter
{
    public static readonly DefaultValueFormatter Invariant = new(CultureInfo.InvariantCulture);
    private readonly CultureInfo _culture = culture ?? CultureInfo.InvariantCulture;

    public string Format(double value, Unit unit)
    {
        var text = value.ToString("0.###", _culture);
        return string.IsNullOrEmpty(unit.Symbol) ? text : $"{text} {unit.Symbol}";
    }
}

/// <summary>Everything the projector needs to turn data into a view: theme, labels, formatter, calendar.</summary>
public sealed record ProjectionOptions(
    ITheme Theme,
    ILabelResolver Labels,
    IValueFormatter Formatter,
    CalendarContext Calendar)
{
    public static ProjectionOptions Default { get; } = new(
        DefaultTheme.Instance,
        DefaultLabelResolver.Instance,
        DefaultValueFormatter.Invariant,
        CalendarContext.Default);
}
