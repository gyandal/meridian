using Meridian.Core;

namespace Meridian.Caching;

/// <summary>
/// A deterministic cache key: a canonical string plus its stable 64-bit hash. Two logically-identical
/// requests always produce an equal key, across processes and restarts — the property a per-instance
/// <c>Guid</c> key destroys. Equality is on the canonical string (hash is for bucketing).
/// </summary>
public readonly record struct CacheKey(string Canonical)
{
    public ulong Hash => StableHash.Fnv1a64(Canonical);
    public override string ToString() => Canonical;
}

/// <summary>A group handle for invalidation. Every entry is written with a set of tags; evicting a tag
/// drops every entry carrying it, without enumerating keys.</summary>
public readonly record struct CacheTag(string Value)
{
    public static CacheTag Tenant(string tenant) => new($"tenant:{tenant}");
    public static CacheTag Metric(string metric) => new($"metric:{metric}");
    public static CacheTag Entity(EntityRef entity) => new($"entity:{entity.Dimension.Name}:{entity.Id}");

    public override string ToString() => Value;
}

/// <summary>Builds deterministic keys from a scope + entity + data version. Centralised so every caller
/// produces identical keys — the single source of truth for cache identity.</summary>
public static class CacheKeyBuilder
{
    public static CacheKey ForEntity(CacheScope scope, EntityRef entity, long version)
    {
        // Culture-invariant, order-fixed. Version is part of the key so a bump is a natural miss.
        var canonical = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"t={scope.Tenant}|m={scope.Metric}|dim={scope.EntityDimension.Name}|" +
            $"s={scope.Timeframe.Start.UtcTicks}|e={scope.Timeframe.End.UtcTicks}|" +
            $"sig={scope.Signature}|ent={entity.Id}|v={version}");
        return new CacheKey(canonical);
    }
}
