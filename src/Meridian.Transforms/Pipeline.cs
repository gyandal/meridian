using Meridian.Core;
using Meridian.Time;

namespace Meridian.Transforms;

/// <summary>Ambient state a transform may need — the calendar for time-aware stages, etc.</summary>
public sealed record TransformContext(CalendarContext Calendar)
{
    public static TransformContext Default { get; } = new(CalendarContext.Default);
}

/// <summary>
/// The algebra's atom: points in, points out. Closed under composition, so any transform chains with
/// any other. No chart type needs to reimplement the
/// pipeline.
/// </summary>
public interface ITransform
{
    PointBlock Apply(PointBlock input, TransformContext ctx);
}

/// <summary>A transform that reads key dimensions (grouping or filtering by venue): a report using it must
/// keep those dimensions, or every point would look the same to it.</summary>
public interface IDimensionalTransform : ITransform
{
    IReadOnlyCollection<DimensionId> Dimensions { get; }
}

/// <summary>A filter on what a point is (its key), not its value — so it gives the same result before or after
/// any per-key time bucketing, and the engine may move bucketing ahead of it to push it down.</summary>
public interface IKeyFilter : IDimensionalTransform;

/// <summary>A filter on a point's value. It depends on what has been aggregated so far, so it's never moved.</summary>
public interface IValueFilter : ITransform;

/// <summary>A transform that divides each value by a total of values (a share), so it needs values that add up.</summary>
public interface IShareTransform : IDimensionalTransform;

/// <summary>
/// A transform that combines values with an aggregator (resample, rolling, group-by, total, reduce). The
/// engine swaps the aggregator when it runs a derived metric's inputs, so that each input is totalled the
/// way the formula says before they're combined.
/// </summary>
public interface IAggregatingTransform : ITransform
{
    IAggregator Aggregator { get; }

    ITransform WithAggregator(IAggregator aggregator);
}

/// <summary>An ordered composition of transforms. <c>Run</c> threads the block through each stage.</summary>
public sealed class Pipeline
{
    private readonly ITransform[] _stages;

    private Pipeline(ITransform[] stages) => _stages = stages;

    public static Pipeline Of(params ITransform[] stages) => new(stages);

    public Pipeline Then(ITransform stage) => new([.. _stages, stage]);

    public PointBlock Run(PointBlock input, TransformContext ctx)
    {
        var current = input;
        foreach (var stage in _stages)
        {
            current = stage.Apply(current, ctx);
        }
        return current;
    }
}
