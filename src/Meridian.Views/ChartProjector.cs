using Meridian.Core;

namespace Meridian.Views;

public interface IChartProjector
{
    ChartView Project(PointBlock block, ViewSpec spec, ProjectionOptions options);
}

/// <summary>
/// Data → view. This is the boundary the whole design is organised around: colour, label, epoch-millis,
/// and status→colour are applied HERE and only here, from a datum that carried none of them. Never inside
/// a transform.
/// </summary>
public sealed class ChartProjector : IChartProjector
{
    public static readonly ChartProjector Instance = new();

    public ChartView Project(PointBlock block, ViewSpec spec, ProjectionOptions options)
    {
        // Partition into series (preserving first-seen order), keyed by the SeriesBy dimension.
        var seriesOrder = new List<PointKey>();
        var seriesRows = new Dictionary<PointKey, List<int>>();
        var seriesParts = new Dictionary<PointKey, KeyPart?>();

        for (int i = 0; i < block.Count; i++)
        {
            KeyPart? seriesPart = null;
            var groupKey = PointKey.Empty;
            if (spec.SeriesBy is { } dim && block.Keys[i].TryGet(dim, out var part))
            {
                seriesPart = part;
                groupKey = PointKey.Of(part);
            }

            if (!seriesRows.TryGetValue(groupKey, out var rows))
            {
                rows = [];
                seriesRows[groupKey] = rows;
                seriesOrder.Add(groupKey);
                seriesParts[groupKey] = seriesPart;
            }
            rows.Add(i);
        }

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var series = new List<SeriesView>(seriesOrder.Count);

        for (int s = 0; s < seriesOrder.Count; s++)
        {
            var groupKey = seriesOrder[s];
            var seriesColor = options.Theme.ColorForSeries(s);
            var part = seriesParts[groupKey];
            var name = part is { } p ? options.Labels.SeriesName(p) : "series";

            var marks = new List<MarkView>();
            foreach (var i in seriesRows[groupKey])
            {
                bool present = (block.Flags[i] & MeasureFlags.Missing) == 0;
                double? value = present ? block.Values[i] : null;
                if (present)
                {
                    if (block.Values[i] < min) min = block.Values[i];
                    if (block.Values[i] > max) max = block.Values[i];
                }

                var status = present && spec.Status is { } rule ? rule.Evaluate(block.Values[i]) : SemanticStatus.Neutral;
                var color = status != SemanticStatus.Neutral ? options.Theme.ColorForStatus(status) : seriesColor;

                string label;
                long? at = null;
                switch (spec.XAxis)
                {
                    case TemporalAxisSource:
                        var instant = new Instant(block.AtTicks[i]);
                        at = block.AtTicks[i] == PointBlock.NoAt ? null : instant.ToUnixMilliseconds();
                        label = block.AtTicks[i] == PointBlock.NoAt ? "" : options.Labels.TimeLabel(instant, options.Calendar);
                        break;
                    case CategoryAxisSource cat:
                        label = block.Keys[i].TryGet(cat.Dimension, out var cp) ? options.Labels.CategoryLabel(cp) : "";
                        break;
                    default:
                        label = "";
                        break;
                }

                marks.Add(new MarkView(label, value, at, status, color));
            }

            // Temporal marks are ordered by instant; sorting by formatted name would get months wrong.
            if (spec.XAxis is TemporalAxisSource)
            {
                marks.Sort(static (a, b) => Nullable.Compare(a.At, b.At));
            }

            series.Add(new SeriesView(name, seriesColor, marks));
        }

        var axes = BuildAxes(spec, block.Unit, min, max);
        var legend = new LegendView(series.Select(v => v.Name).ToList());
        return new ChartView(spec.Kind, series, axes, legend, Annotations: []);
    }

    private static IReadOnlyList<AxisView> BuildAxes(ViewSpec spec, Unit dataUnit, double min, double max)
    {
        AxisView xAxis = spec.XAxis switch
        {
            TemporalAxisSource => new AxisView(AxisKind.Temporal, "Time"),
            CategoryAxisSource cat => new AxisView(AxisKind.Category, cat.Dimension.Name),
            _ => new AxisView(AxisKind.Category, ""),
        };

        var unit = spec.ValueUnit ?? dataUnit;
        var yAxis = new AxisView(
            AxisKind.Linear,
            spec.ValueAxisTitle ?? (string.IsNullOrEmpty(unit.Symbol) ? "Value" : unit.Symbol),
            Min: double.IsFinite(min) ? min : null,
            Max: double.IsFinite(max) ? max : null,
            Unit: string.IsNullOrEmpty(unit.Symbol) ? null : unit.Symbol);

        return [xAxis, yAxis];
    }
}
