namespace Meridian.Core;

/// <summary>
/// A columnar (struct-of-arrays) batch of points — the execution unit transforms operate over.
/// Parallel arrays keep aggregation cache-friendly and SIMD-able on the hot path; <see cref="Point"/>
/// is the row view you materialise for authoring. This spike keeps the API honest about the layout
/// without yet doing the SIMD pass (that lands with the benchmarks).
/// </summary>
public sealed class PointBlock
{
    public const long NoAt = long.MinValue;

    private readonly PointKey[] _keys;
    private readonly double[] _values;
    private readonly MeasureFlags[] _flags;
    private readonly long[] _at;

    public Unit Unit { get; }
    public int Count { get; }

    internal PointBlock(Unit unit, PointKey[] keys, double[] values, MeasureFlags[] flags, long[] at, int count)
    {
        Unit = unit;
        _keys = keys;
        _values = values;
        _flags = flags;
        _at = at;
        Count = count;
    }

    public ReadOnlySpan<PointKey> Keys => _keys.AsSpan(0, Count);
    public ReadOnlySpan<double> Values => _values.AsSpan(0, Count);
    public ReadOnlySpan<MeasureFlags> Flags => _flags.AsSpan(0, Count);
    public ReadOnlySpan<long> AtTicks => _at.AsSpan(0, Count);

    public Point Row(int i) => new(
        _keys[i],
        new Measurement(_values[i], _flags[i]),
        _at[i] == NoAt ? null : new Instant(_at[i]),
        Unit);

    public IEnumerable<Point> Rows()
    {
        for (int i = 0; i < Count; i++) yield return Row(i);
    }

    public static PointBlock FromRows(IEnumerable<Point> rows, Unit? unit = null)
    {
        var builder = new Builder(unit ?? Unit.None);
        foreach (var r in rows) builder.Add(r);
        return builder.Build();
    }

    public static PointBlock Empty(Unit unit) =>
        new(unit, Array.Empty<PointKey>(), Array.Empty<double>(), Array.Empty<MeasureFlags>(), Array.Empty<long>(), 0);

    public sealed class Builder(Unit unit)
    {
        private readonly List<PointKey> _keys = [];
        private readonly List<double> _values = [];
        private readonly List<MeasureFlags> _flags = [];
        private readonly List<long> _at = [];

        public Builder Add(Point p) => Add(p.Key, p.Measure, p.At);

        public Builder Add(PointKey key, Measurement measure, Instant? at)
        {
            _keys.Add(key);
            _values.Add(measure.Value);
            _flags.Add(measure.Flags);
            _at.Add(at?.UtcTicks ?? NoAt);
            return this;
        }

        public PointBlock Build() =>
            new(unit, [.. _keys], [.. _values], [.. _flags], [.. _at], _keys.Count);
    }
}
