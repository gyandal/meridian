namespace Meridian.Caching;

/// <summary>
/// The one invalidation hook the write path calls. The metric-write path raises a
/// <see cref="ChangeScope"/>; the strategy behind this decides how to make stale entries disappear.
/// The write path never sees a cache key.
/// </summary>
public interface ICacheInvalidator
{
    ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct = default);
}

/// <summary>Correctness backbone: bump the data version. Entries keyed at the old version become
/// unreachable and age out — works on any backend, even one that cannot actively evict.</summary>
public sealed class VersionBumpInvalidator(IDataVersionStore versions) : ICacheInvalidator
{
    public ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct = default)
    {
        versions.Bump(scope);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Immediate reclaim: actively evict every entry tagged for the changed entity.</summary>
public sealed class TagEvictionInvalidator(IPointCacheStore store) : ICacheInvalidator
{
    public ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct = default) =>
        store.RemoveByTagAsync(CacheTag.Entity(scope.Entity), ct);
}

/// <summary>Run several strategies together — the recommended default is version-bump (correctness)
/// plus tag-eviction (immediate reclaim).</summary>
public sealed class CompositeInvalidator(params ICacheInvalidator[] strategies) : ICacheInvalidator
{
    public async ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct = default)
    {
        foreach (var strategy in strategies)
        {
            await strategy.InvalidateAsync(scope, ct).ConfigureAwait(false);
        }
    }
}
