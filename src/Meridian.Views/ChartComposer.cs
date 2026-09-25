using Meridian.Core;

namespace Meridian.Views;

/// <summary>Which value axis a series plots against: the primary (left) or secondary (right) one.</summary>
public enum ValueAxis
{
    Primary,
    Secondary,
}

/// <summary>One report's view, and how it appears in a combined chart.</summary>
public sealed record ChartPart(ChartView View, string Name, ValueAxis Axis = ValueAxis.Primary);

/// <summary>
/// Combines several single-metric views into one multi-series chart — goals as columns, minutes as a line
/// on a second axis, goals per 90 as a line — keeping each part's chart kind, giving every series its own
/// colour and a name ("Goals", or "Goals · player 7" when a part has a series per entity), and building one
/// value axis per side from the parts on it. Status colours on marks are kept.
/// </summary>
public static class ChartComposer
{
    public static ChartView Compose(IReadOnlyList<ChartPart> parts, ITheme theme)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) throw new ArgumentException("A chart needs at least one series.", nameof(parts));

        var xAxis = parts[0].View.Axes[0];
        foreach (var part in parts)
        {
            var x = part.View.Axes[0];
            if (x.Kind != xAxis.Kind || x.Title != xAxis.Title || x.Time != xAxis.Time || x.Grain != xAxis.Grain || x.TimeZone != xAxis.TimeZone)
            {
                throw new ArgumentException(
                    $"Series '{part.Name}' has a different x-axis ({x.Kind} {x.Title} {x.Grain}) from '{parts[0].Name}' " +
                    $"({xAxis.Kind} {xAxis.Title} {xAxis.Grain}); a combined chart needs one x-axis.", nameof(parts));
            }
        }

        bool secondary = parts.Any(p => p.Axis == ValueAxis.Secondary);
        var series = new List<SeriesView>();
        foreach (var part in parts)
        {
            foreach (var s in part.View.Series)
            {
                var color = theme.ColorForSeries(series.Count);
                var name = part.View.Series.Count == 1 ? part.Name : $"{part.Name} · {s.Name}";
                // Marks drawn in their series colour take the new one; status colours stay.
                var marks = s.Marks.Select(m => m.ColorToken == s.ColorToken ? m with { ColorToken = color } : m).ToList();
                series.Add(new SeriesView(name, color, marks, part.View.Kind, part.Axis == ValueAxis.Secondary ? 2 : 1));
            }
        }

        List<AxisView> axes = [xAxis, ValueAxisFor(parts.Where(p => p.Axis == ValueAxis.Primary).ToList())];
        if (secondary) axes.Add(ValueAxisFor(parts.Where(p => p.Axis == ValueAxis.Secondary).ToList()));

        return new ChartView(parts[0].View.Kind, series, axes,
            new LegendView(series.Select(s => s.Name).ToList()),
            [.. parts.SelectMany(p => p.View.Annotations)]);
    }

    private static AxisView ValueAxisFor(List<ChartPart> parts)
    {
        if (parts.Count == 0) return new AxisView(AxisKind.Linear, "Value");
        var axes = parts.Select(p => p.View.Axes[1]).ToList();
        var units = axes.Select(a => a.Unit).Distinct().ToList();
        var mins = axes.Where(a => a.Min is not null).Select(a => a.Min!.Value).ToList();
        var maxes = axes.Where(a => a.Max is not null).Select(a => a.Max!.Value).ToList();
        return new AxisView(
            AxisKind.Linear,
            string.Join(" · ", parts.Select(p => p.Name)),
            Min: mins.Count > 0 ? mins.Min() : null,
            Max: maxes.Count > 0 ? maxes.Max() : null,
            Unit: units.Count == 1 ? units[0] : null);
    }
}
