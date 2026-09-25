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

/// <summary>
/// A transform that combines values with an aggregator (resample, rolling, group-by, total, reduce). The
/// engine swaps the aggregator when it runs a derived metric's inputs, so that each side of a ratio is
/// totalled the way the formula says before dividing.
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
