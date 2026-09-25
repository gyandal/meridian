using System.Collections.Concurrent;
using Meridian.Core;

namespace Meridian.Caching;

/// <summary>Loads data for the entities that missed the cache. Called with ONLY the missing set.</summary>
public delegate Task<PointBlock> PointLoader(IReadOnlyList<EntityRef> missing, CancellationToken ct);

/// <summary>Loads several scopes (typically several metrics) for the same missing entities in one call,
/// returning a block per scope. A scope absent from the result is treated as having no data.</summary>
public delegate Task<IReadOnlyDictionary<CacheScope, PointBlock>> BatchPointLoader(
    IReadOnlyList<CacheScope> scopes, IReadOnlyList<EntityRef> missing, CancellationToken ct);

/// <summary>
/// The value layer of the cache: given a scope and a set of entities, serve what's cached per-entity
/// and load only the misses, then merge. The per-entity partial-hit merge sits on top of a clean, taggable store,
/// with deterministic versioned keys and single-flight stampede protection.
/// </summary>
public sealed class PointCache(IPointCacheStore store, IDataVersionStore versions)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<long, PointBlock>>>> _inflight = new();

    public async Task<PointBlock> GetOrLoadAsync(
        CacheScope scope,
        IReadOnlyList<EntityRef> entities,
        PointLoader loader,
        CancellationToken ct = default)
    {
        var (hits, misses) = await ProbeAsync(scope, entities, ct).ConfigureAwait(false);

        IReadOnlyDictionary<long, PointBlock> loaded = misses.Count == 0
            ? new Dictionary<long, PointBlock>()
            : await LoadMissesCoalesced(scope, misses, loader, ct).ConfigureAwait(false);

        return Merge(entities, hits, loaded);
    }

    /// <summary>The current data version of each entity in <paramref name="scope"/>, in order — anything
    /// derived from those slices is valid exactly while this stamp is unchanged.</summary>
    public string VersionStamp(CacheScope scope, IReadOnlyList<EntityRef> entities) =>
        string.Join(',', entities.Select(e => $"{e.Id}v{versions.Current(new ChangeScope(scope.Tenant, scope.Metric, e))}"));

    /// <summary>
    /// The batched form of <see cref="GetOrLoadAsync"/>: several scopes over the same entities (e.g. several
    /// metrics of one request). Each scope is probed per entity as usual; scopes missing the same entities
    /// are loaded together in ONE loader call, so a cold multi-metric request costs one source round trip,
    /// and slices already cached are never refetched. Returns a merged block per requested scope.
    /// </summary>
    public async Task<IReadOnlyDictionary<CacheScope, PointBlock>> GetOrLoadManyAsync(
        IReadOnlyList<CacheScope> scopes,
        IReadOnlyList<EntityRef> entities,
        BatchPointLoader loader,
        CancellationToken ct = default)
    {
        var distinct = scopes.Distinct().ToList();
        var probes = new Dictionary<CacheScope, (Dictionary<long, PointBlock> Hits, List<EntityRef> Misses)>();
        foreach (var scope in distinct)
        {
            probes[scope] = await ProbeAsync(scope, entities, ct).ConfigureAwait(false);
        }

        // One loader call per distinct missing set: normally one (all cold) or none (all warm).
        var groups = distinct
            .Where(s => probes[s].Misses.Count > 0)
            .GroupBy(s => string.Join(',', probes[s].Misses.Select(e => e.Id).Order()))
            .Select(g => (Scopes: g.ToList(), Missing: probes[g.First()].Misses.OrderBy(e => e.Id).ToList()))
            .ToList();

        var loadedGroups = await Task.WhenAll(groups.Select(async g =>
        {
            var blocks = await loader(g.Scopes, g.Missing, ct).ConfigureAwait(false);
            var stored = new List<(CacheScope Scope, IReadOnlyDictionary<long, PointBlock> Slices)>();
            foreach (var scope in g.Scopes)
            {
                var block = blocks.TryGetValue(scope, out var b) ? b : PointBlock.Empty(Unit.None);
                stored.Add((scope, await StoreAsync(scope, g.Missing, block, ct).ConfigureAwait(false)));
            }
            return stored;
        })).ConfigureAwait(false);

        var loaded = loadedGroups.SelectMany(x => x).ToDictionary(x => x.Scope, x => x.Slices);
        var result = new Dictionary<CacheScope, PointBlock>();
        foreach (var scope in distinct)
        {
            result[scope] = Merge(entities, probes[scope].Hits,
                loaded.TryGetValue(scope, out var l) ? l : new Dictionary<long, PointBlock>());
        }
        return result;
    }

    private async Task<(Dictionary<long, PointBlock> Hits, List<EntityRef> Misses)> ProbeAsync(
        CacheScope scope, IReadOnlyList<EntityRef> entities, CancellationToken ct)
    {
        var hits = new Dictionary<long, PointBlock>();
        var misses = new List<EntityRef>();
        foreach (var entity in entities)
        {
            long version = versions.Current(new ChangeScope(scope.Tenant, scope.Metric, entity));
            var key = CacheKeyBuilder.ForEntity(scope, entity, version);
            var entry = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (entry is not null)
            {
                hits[entity.Id] = entry.Block;
            }
            else
            {
                misses.Add(entity);
            }
        }
        return (hits, misses);
    }

    /// <summary>Merge cached and freshly loaded slices, preserving the requested entity order.</summary>
    private static PointBlock Merge(
        IReadOnlyList<EntityRef> entities, Dictionary<long, PointBlock> hits, IReadOnlyDictionary<long, PointBlock> loaded)
    {
        var merged = Template(hits, loaded);
        foreach (var entity in entities)
        {
            var slice = hits.TryGetValue(entity.Id, out var h) ? h
                : loaded.TryGetValue(entity.Id, out var l) ? l
                : null;
            if (slice is null) continue;
            for (int i = 0; i < slice.Count; i++) merged.Add(slice.Row(i));
        }
        return merged.Build();
    }

    private async Task<IReadOnlyDictionary<long, PointBlock>> LoadMissesCoalesced(
        CacheScope scope, List<EntityRef> misses, PointLoader loader, CancellationToken ct)
    {
        // Coalesce identical concurrent miss-sets so the loader runs once (stampede protection).
        misses.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var coalesceKey = BuildCoalesceKey(scope, misses);

        var lazy = _inflight.GetOrAdd(coalesceKey,
            _ => new Lazy<Task<IReadOnlyDictionary<long, PointBlock>>>(
                () => LoadAndStore(scope, misses, loader, ct)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(coalesceKey, out _);
        }
    }

    private async Task<IReadOnlyDictionary<long, PointBlock>> LoadAndStore(
        CacheScope scope, List<EntityRef> misses, PointLoader loader, CancellationToken ct)
    {
        var block = await loader(misses, ct).ConfigureAwait(false);
        return await StoreAsync(scope, misses, block, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<long, PointBlock>> StoreAsync(
        CacheScope scope, IReadOnlyList<EntityRef> misses, PointBlock block, CancellationToken ct)
    {
        var byEntity = SplitByEntity(block, scope.EntityDimension);

        var result = new Dictionary<long, PointBlock>();
        foreach (var entity in misses)
        {
            // Store a slice for every requested miss — an empty one caches the "no data" result so we
            // don't re-hit the source for a genuinely empty entity.
            var slice = byEntity.TryGetValue(entity.Id, out var s) ? s : PointBlock.Empty(block.Unit, block.Time);
            long version = versions.Current(new ChangeScope(scope.Tenant, scope.Metric, entity));
            var key = CacheKeyBuilder.ForEntity(scope, entity, version);
            var tags = new[] { CacheTag.Entity(entity), CacheTag.Metric(scope.Metric), CacheTag.Tenant(scope.Tenant) };

            await store.SetAsync(key, new CacheEntry(slice), tags, CacheEntryOptions.None, ct).ConfigureAwait(false);
            result[entity.Id] = slice;
        }
        return result;
    }

    private static Dictionary<long, PointBlock> SplitByEntity(PointBlock block, DimensionId entityDimension)
    {
        var builders = new Dictionary<long, PointBlock.Builder>();
        for (int i = 0; i < block.Count; i++)
        {
            if (!block.Keys[i].TryGet(entityDimension, out var part)) continue;
            long id = part.Numeric;
            if (!builders.TryGetValue(id, out var b))
            {
                b = PointBlock.Builder.Like(block);
                builders[id] = b;
            }
            b.Add(block.Row(i));
        }
        return builders.ToDictionary(kv => kv.Key, kv => kv.Value.Build());
    }

    /// <summary>A builder with the unit and time axis of the slices being merged (non-empty ones first,
    /// since an empty slice for a data-less entity carries no real information).</summary>
    private static PointBlock.Builder Template(Dictionary<long, PointBlock> hits, IReadOnlyDictionary<long, PointBlock> loaded)
    {
        var all = hits.Values.Concat(loaded.Values).ToList();
        var like = all.FirstOrDefault(b => b.Count > 0) ?? all.FirstOrDefault();
        return like is null ? new PointBlock.Builder(Unit.None) : PointBlock.Builder.Like(like);
    }

    private string BuildCoalesceKey(CacheScope scope, List<EntityRef> misses)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(scope.Tenant).Append('|').Append(scope.Metric).Append('|').Append(scope.Signature)
          .Append('|').Append(scope.Timeframe.Start.UtcTicks).Append('|').Append(scope.Timeframe.End.UtcTicks)
          .Append("|e=");
        foreach (var m in misses)
        {
            long version = versions.Current(new ChangeScope(scope.Tenant, scope.Metric, m));
            sb.Append(m.Id).Append('v').Append(version).Append(',');
        }
        return sb.ToString();
    }
}
