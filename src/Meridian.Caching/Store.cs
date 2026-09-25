using System.Collections.Concurrent;
using Meridian.Core;

namespace Meridian.Caching;

/// <summary>A cached value. Holds the compact <see cref="PointBlock"/> — never the projected view.</summary>
public sealed record CacheEntry(PointBlock Block);

/// <summary>Expiry options. Absolute expiry only for now; sliding expiry is a store detail to add later.</summary>
public readonly record struct CacheEntryOptions(DateTimeOffset? AbsoluteExpiration = null)
{
    public static readonly CacheEntryOptions None = new();
}

/// <summary>
/// The minimal backend contract — the Hangfire trick. A backend author implements only this plain
/// key/value store; tagging is layered on for free by <see cref="TaggedStoreDecorator"/>.
/// </summary>
public interface IKeyValueStore
{
    ValueTask<CacheEntry?> GetAsync(CacheKey key, CancellationToken ct = default);
    ValueTask SetAsync(CacheKey key, CacheEntry entry, CacheEntryOptions options, CancellationToken ct = default);
    ValueTask RemoveAsync(CacheKey key, CancellationToken ct = default);
}

/// <summary>The full cache-store contract: a key/value store plus tag-group eviction.</summary>
public interface IPointCacheStore : IKeyValueStore
{
    ValueTask SetAsync(CacheKey key, CacheEntry entry, IReadOnlyCollection<CacheTag> tags,
        CacheEntryOptions options, CancellationToken ct = default);
    ValueTask RemoveByTagAsync(CacheTag tag, CancellationToken ct = default);
}

/// <summary>A plain in-memory key/value store — the simplest possible backend. Redis/SQL implement the
/// same interface; nothing above the store changes.</summary>
public sealed class InMemoryKeyValueStore(Func<DateTimeOffset>? clock = null) : IKeyValueStore
{
    private readonly ConcurrentDictionary<CacheKey, (CacheEntry Entry, DateTimeOffset? ExpiresAt)> _map = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);

    public ValueTask<CacheEntry?> GetAsync(CacheKey key, CancellationToken ct = default)
    {
        if (_map.TryGetValue(key, out var slot))
        {
            if (slot.ExpiresAt is { } exp && exp <= _clock())
            {
                _map.TryRemove(key, out _);
                return ValueTask.FromResult<CacheEntry?>(null);
            }
            return ValueTask.FromResult<CacheEntry?>(slot.Entry);
        }
        return ValueTask.FromResult<CacheEntry?>(null);
    }

    public ValueTask SetAsync(CacheKey key, CacheEntry entry, CacheEntryOptions options, CancellationToken ct = default)
    {
        _map[key] = (entry, options.AbsoluteExpiration);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(CacheKey key, CancellationToken ct = default)
    {
        _map.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Adds tag-group eviction to ANY <see cref="IKeyValueStore"/> by maintaining its own tag→keys index.
/// This is what lets a minimal backend (Get/Set/Remove only) inherit <c>RemoveByTag</c> — the
/// batteries-included pattern from the plan's §9.
/// </summary>
public sealed class TaggedStoreDecorator(IKeyValueStore inner) : IPointCacheStore
{
    private readonly ConcurrentDictionary<CacheTag, HashSet<CacheKey>> _tagToKeys = new();
    private readonly ConcurrentDictionary<CacheKey, HashSet<CacheTag>> _keyToTags = new();
    private readonly Lock _gate = new();

    public ValueTask<CacheEntry?> GetAsync(CacheKey key, CancellationToken ct = default) => inner.GetAsync(key, ct);

    public ValueTask SetAsync(CacheKey key, CacheEntry entry, CacheEntryOptions options, CancellationToken ct = default) =>
        SetAsync(key, entry, [], options, ct);

    public async ValueTask SetAsync(CacheKey key, CacheEntry entry, IReadOnlyCollection<CacheTag> tags,
        CacheEntryOptions options, CancellationToken ct = default)
    {
        await inner.SetAsync(key, entry, options, ct).ConfigureAwait(false);
        lock (_gate)
        {
            Untrack(key);
            if (tags.Count > 0)
            {
                _keyToTags[key] = [.. tags];
                foreach (var tag in tags)
                {
                    _tagToKeys.GetOrAdd(tag, static _ => []).Add(key);
                }
            }
        }
    }

    public async ValueTask RemoveAsync(CacheKey key, CancellationToken ct = default)
    {
        await inner.RemoveAsync(key, ct).ConfigureAwait(false);
        lock (_gate) Untrack(key);
    }

    public async ValueTask RemoveByTagAsync(CacheTag tag, CancellationToken ct = default)
    {
        CacheKey[] keys;
        lock (_gate)
        {
            keys = _tagToKeys.TryGetValue(tag, out var set) ? [.. set] : [];
        }
        foreach (var key in keys)
        {
            await inner.RemoveAsync(key, ct).ConfigureAwait(false);
            lock (_gate) Untrack(key);
        }
    }

    private void Untrack(CacheKey key)
    {
        if (_keyToTags.TryRemove(key, out var tags))
        {
            foreach (var tag in tags)
            {
                if (_tagToKeys.TryGetValue(tag, out var set))
                {
                    set.Remove(key);
                    if (set.Count == 0) _tagToKeys.TryRemove(tag, out _);
                }
            }
        }
    }
}

/// <summary>
/// Composes ordered layers (e.g. in-process L1 over distributed L2). Reads probe layers in order and
/// back-fill faster layers on a hit; writes and evictions fan out to all. "Injectable layers" from §9.
/// </summary>
public sealed class LayeredPointCacheStore(params IPointCacheStore[] layers) : IPointCacheStore
{
    private readonly IPointCacheStore[] _layers = layers.Length > 0
        ? layers
        : throw new ArgumentException("At least one layer is required.", nameof(layers));

    public async ValueTask<CacheEntry?> GetAsync(CacheKey key, CancellationToken ct = default)
    {
        for (int i = 0; i < _layers.Length; i++)
        {
            var entry = await _layers[i].GetAsync(key, ct).ConfigureAwait(false);
            if (entry is not null)
            {
                // Back-fill faster layers that missed.
                for (int j = 0; j < i; j++)
                {
                    await _layers[j].SetAsync(key, entry, CacheEntryOptions.None, ct).ConfigureAwait(false);
                }
                return entry;
            }
        }
        return null;
    }

    public ValueTask SetAsync(CacheKey key, CacheEntry entry, CacheEntryOptions options, CancellationToken ct = default) =>
        SetAsync(key, entry, [], options, ct);

    public async ValueTask SetAsync(CacheKey key, CacheEntry entry, IReadOnlyCollection<CacheTag> tags,
        CacheEntryOptions options, CancellationToken ct = default)
    {
        foreach (var layer in _layers)
        {
            await layer.SetAsync(key, entry, tags, options, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveAsync(CacheKey key, CancellationToken ct = default)
    {
        foreach (var layer in _layers) await layer.RemoveAsync(key, ct).ConfigureAwait(false);
    }

    public async ValueTask RemoveByTagAsync(CacheTag tag, CancellationToken ct = default)
    {
        foreach (var layer in _layers) await layer.RemoveByTagAsync(tag, ct).ConfigureAwait(false);
    }
}
