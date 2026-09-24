namespace Meridian.Core;

/// <summary>Unit of measure. Purely a data label; formatting lives in the presentation layer.</summary>
public readonly record struct Unit(string Symbol)
{
    public static readonly Unit None = new("");
    public override string ToString() => Symbol;
}

[Flags]
public enum MeasureFlags : byte
{
    None = 0,
    Missing = 1,
    Estimated = 2,
}

/// <summary>
/// A single measured value. "Missing" is an explicit flag, never a null-value smuggle — no
/// ambiguity between null, 0, and "no data".
/// </summary>
public readonly record struct Measurement(double Value, MeasureFlags Flags)
{
    public static readonly Measurement Missing = new(double.NaN, MeasureFlags.Missing);

    public bool IsPresent => (Flags & MeasureFlags.Missing) == 0;

    public static Measurement Of(double value) => new(value, MeasureFlags.None);

    public static implicit operator Measurement(double value) => Of(value);

    public override string ToString() => IsPresent ? Value.ToString("R") : "—";
}
