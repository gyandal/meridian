using Meridian.Core;

namespace Meridian.Caching;

/// <summary>A reference to a cached entity (the thing whose data is fetched per-key and merged).</summary>
public readonly record struct EntityRef(DimensionId Dimension, long Id);

/// <summary>
/// The non-entity part of a request — the "bucket" a set of per-entity slices share. It is the request
/// shape minus the entity list, which is what lets the cache fetch only the missing entities and merge. <see cref="Signature"/> captures aggregation/period/gap so two
/// differently-shaped requests never collide.
/// </summary>
public sealed record CacheScope(
    string Tenant,
    string Metric,
    DimensionId EntityDimension,
    DateInterval Timeframe,
    string Signature);

/// <summary>What a write touched — resolved to tags (and version bumps) on invalidation.</summary>
public sealed record ChangeScope(string Tenant, string Metric, EntityRef Entity);

/// <summary>Per-(tenant,metric,entity) data version. Baked into the cache key so a bump is a clean miss
/// even on a backend that cannot actively evict — the correctness backbone.</summary>
public interface IDataVersionStore
{
    long Current(ChangeScope scope);
    void Bump(ChangeScope scope);
}

public sealed class InMemoryDataVersionStore : IDataVersionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _versions = new();

    private static string Key(ChangeScope s) => $"{s.Tenant}|{s.Metric}|{s.Entity.Dimension.Name}|{s.Entity.Id}";

    public long Current(ChangeScope scope) => _versions.TryGetValue(Key(scope), out var v) ? v : 0;

    public void Bump(ChangeScope scope) =>
        _versions.AddOrUpdate(Key(scope), 1, static (_, current) => current + 1);
}
