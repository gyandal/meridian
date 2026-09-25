using System.Text;

namespace Meridian.Core;

/// <summary>Stable identity of a dimension (e.g. "player", "venue", "month"). Ordinal-compared.</summary>
public readonly record struct DimensionId(string Name) : IComparable<DimensionId>
{
    public int CompareTo(DimensionId other) => string.CompareOrdinal(Name, other.Name);
    public override string ToString() => Name;
}

public enum KeyPartKind : byte
{
    Entity,   // an entity reference: dimension = entity type, value = id  (player:42)
    Temporal, // a bucketed instant: value = bucket-start ticks           (month:@2026-08)
    Ordinal,  // an ordered discrete value: value = long                  (matchTimeBand:3)
    Category, // a named category: value = text                           (venue:"Home")
}

/// <summary>
/// One typed component of a composite key. Equality and ordering are on the TYPED value, never a
/// formatted display string — so two points that render to the same
/// name never collide, and a month bucket sorts by instant, not by
/// the localised string "August".
/// </summary>
public readonly struct KeyPart : IEquatable<KeyPart>, IComparable<KeyPart>
{
    public DimensionId Dimension { get; }
    public KeyPartKind Kind { get; }
    private readonly long _num;
    private readonly string? _text;

    private KeyPart(DimensionId dimension, KeyPartKind kind, long num, string? text)
    {
        Dimension = dimension;
        Kind = kind;
        _num = num;
        _text = text;
    }

    public static KeyPart Entity(DimensionId dimension, long id) =>
        new(dimension, KeyPartKind.Entity, id, null);

    public static KeyPart Temporal(DimensionId dimension, Instant bucketStart) =>
        new(dimension, KeyPartKind.Temporal, bucketStart.UtcTicks, null);

    public static KeyPart Ordinal(DimensionId dimension, long value) =>
        new(dimension, KeyPartKind.Ordinal, value, null);

    public static KeyPart Category(DimensionId dimension, string value) =>
        new(dimension, KeyPartKind.Category, 0L, value ?? throw new ArgumentNullException(nameof(value)));

    public long Numeric => _num;
    public string Text => _text ?? string.Empty;
    public Instant AsInstant => new(_num);

    public bool Equals(KeyPart other) =>
        Dimension.Equals(other.Dimension)
        && Kind == other.Kind
        && _num == other._num
        && string.Equals(_text, other._text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is KeyPart other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Dimension, Kind, _num,
            _text is null ? 0 : string.GetHashCode(_text, StringComparison.Ordinal));

    public int CompareTo(KeyPart other)
    {
        int c = Dimension.CompareTo(other.Dimension);
        if (c != 0) return c;
        c = ((byte)Kind).CompareTo((byte)other.Kind);
        if (c != 0) return c;
        c = _num.CompareTo(other._num);
        if (c != 0) return c;
        return string.CompareOrdinal(_text, other._text);
    }

    /// <summary>Appends a canonical, culture-invariant token used to build deterministic cache keys.</summary>
    public void AppendCanonical(StringBuilder sb)
    {
        sb.Append(Dimension.Name).Append('=');
        switch (Kind)
        {
            case KeyPartKind.Category:
                sb.Append('"').Append(_text).Append('"');
                break;
            case KeyPartKind.Temporal:
                sb.Append('@').Append(_num);
                break;
            default:
                sb.Append(_num);
                break;
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendCanonical(sb);
        return sb.ToString();
    }
}

/// <summary>
/// A composite key: the set of typed dimensions that identify a point. Parts are canonicalised
/// (sorted by dimension) on construction, so <c>{player, venue}</c> and <c>{venue, player}</c> are
/// the same key and produce the same deterministic hash — order-independence by construction.
/// </summary>
public readonly struct PointKey : IEquatable<PointKey>, IComparable<PointKey>
{
    private readonly KeyPart[]? _parts;

    public static readonly PointKey Empty = default;

    private PointKey(KeyPart[] canonical) => _parts = canonical;

    public static PointKey Of(params KeyPart[] parts)
    {
        if (parts is null || parts.Length == 0) return Empty;
        var copy = (KeyPart[])parts.Clone();
        Array.Sort(copy);
        return new PointKey(copy);
    }

    public ReadOnlySpan<KeyPart> Parts => _parts ?? ReadOnlySpan<KeyPart>.Empty;
    public int Count => _parts?.Length ?? 0;

    public bool TryGet(DimensionId dimension, out KeyPart part)
    {
        foreach (var p in Parts)
        {
            if (p.Dimension.Equals(dimension))
            {
                part = p;
                return true;
            }
        }
        part = default;
        return false;
    }

    /// <summary>Projection used by grouping: drop a dimension (e.g. collapse the temporal axis).</summary>
    public PointKey Without(DimensionId dimension)
    {
        if (_parts is null) return Empty;
        var kept = _parts.Where(p => !p.Dimension.Equals(dimension)).ToArray();
        return kept.Length == 0 ? Empty : new PointKey(kept); // already canonical order preserved
    }

    /// <summary>Projection used by grouping: keep only the parts in <paramref name="dimensions"/>.</summary>
    public PointKey Only(IReadOnlyCollection<DimensionId> dimensions)
    {
        if (_parts is null) return Empty;
        bool all = true;
        foreach (var p in _parts)
        {
            if (!dimensions.Contains(p.Dimension)) { all = false; break; }
        }
        if (all) return this; // the common case: nothing to drop, no allocation
        var kept = _parts.Where(p => dimensions.Contains(p.Dimension)).ToArray();
        return kept.Length == 0 ? Empty : new PointKey(kept); // canonical order preserved
    }

    /// <summary>Return a new key with <paramref name="part"/> added or replacing its dimension.</summary>
    public PointKey With(KeyPart part)
    {
        var others = (_parts ?? Array.Empty<KeyPart>()).Where(p => !p.Dimension.Equals(part.Dimension));
        return Of(others.Append(part).ToArray());
    }

    public bool Equals(PointKey other)
    {
        var a = Parts;
        var b = other.Parts;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is PointKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in Parts) hash.Add(p);
        return hash.ToHashCode();
    }

    public int CompareTo(PointKey other)
    {
        var a = Parts;
        var b = other.Parts;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public void AppendCanonical(StringBuilder sb)
    {
        var parts = Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('&');
            parts[i].AppendCanonical(sb);
        }
    }

    public string ToCanonicalString()
    {
        var sb = new StringBuilder();
        AppendCanonical(sb);
        return sb.ToString();
    }

    /// <summary>Deterministic 64-bit key suitable for a cross-node cache. See <see cref="StableHash"/>.</summary>
    public ulong StableHash64() => StableHash.Fnv1a64(ToCanonicalString());

    public override string ToString() => ToCanonicalString();
}
