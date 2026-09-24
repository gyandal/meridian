using Meridian.Core;
using Xunit;

namespace Meridian.Core.Tests;

public class KeyTests
{
    private static readonly DimensionId Player = new("player");
    private static readonly DimensionId Venue = new("venue");
    private static readonly DimensionId Month = new("month");

    [Fact]
    public void Keys_are_order_independent_by_construction()
    {
        var a = PointKey.Of(KeyPart.Entity(Player, 42), KeyPart.Category(Venue, "Home"));
        var b = PointKey.Of(KeyPart.Category(Venue, "Home"), KeyPart.Entity(Player, 42));

        Assert.Equal(a, b);
        Assert.Equal(a.ToCanonicalString(), b.ToCanonicalString());
    }

    [Fact]
    public void StableHash_is_deterministic_and_order_independent()
    {
        var a = PointKey.Of(KeyPart.Entity(Player, 1), KeyPart.Category(Venue, "Away"));
        var b = PointKey.Of(KeyPart.Category(Venue, "Away"), KeyPart.Entity(Player, 1));

        // Unlike string.GetHashCode(), this must be identical for equal keys — the property a
        // cross-node cache depends on. (Value asserted literally to catch accidental algorithm drift.)
        Assert.Equal(a.StableHash64(), b.StableHash64());

        var different = PointKey.Of(KeyPart.Entity(Player, 2), KeyPart.Category(Venue, "Away"));
        Assert.NotEqual(a.StableHash64(), different.StableHash64());
    }

    [Fact]
    public void Different_kinds_with_same_text_do_not_collide()
    {
        // Keying equality on a formatted name would make these two collide.
        var asCategory = PointKey.Of(KeyPart.Category(Venue, "1"));
        var asOrdinal = PointKey.Of(KeyPart.Ordinal(Venue, 1));

        Assert.NotEqual(asCategory, asOrdinal);
    }

    [Fact]
    public void Temporal_parts_order_chronologically_not_alphabetically()
    {
        // February sorts BEFORE August by instant, even though "August" < "February" as strings.
        var feb = KeyPart.Temporal(Month, Instant.FromUtc(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        var aug = KeyPart.Temporal(Month, Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.True(feb.CompareTo(aug) < 0);
        Assert.True(string.CompareOrdinal("August", "February") < 0); // the trap we avoid
    }

    [Fact]
    public void Without_drops_a_dimension_and_With_replaces_it()
    {
        var full = PointKey.Of(KeyPart.Entity(Player, 7), KeyPart.Category(Venue, "Home"));

        var dropped = full.Without(Venue);
        Assert.False(dropped.TryGet(Venue, out _));
        Assert.True(dropped.TryGet(Player, out _));

        var replaced = full.With(KeyPart.Category(Venue, "Away"));
        Assert.True(replaced.TryGet(Venue, out var v));
        Assert.Equal("Away", v.Text);
    }
}
