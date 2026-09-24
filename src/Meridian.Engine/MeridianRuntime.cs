using Meridian.Caching;
using Meridian.Semantics;
using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// A ready-wired engine plus the invalidation handle the write path calls. The default composition:
/// tagged in-memory store, version + tag invalidation, the shared projector. Swap the store for
/// Redis and nothing else changes.
/// </summary>
public sealed record MeridianRuntime(
    ReportEngine Engine,
    ICacheInvalidator Invalidator,
    IDataVersionStore Versions,
    IPointCacheStore Store)
{
    public static MeridianRuntime InMemory(IMetricCatalog catalog, IPointSource source)
    {
        var versions = new InMemoryDataVersionStore();
        var store = new TaggedStoreDecorator(new InMemoryKeyValueStore());
        var cache = new PointCache(store, versions);
        var engine = new ReportEngine(catalog, source, cache, ChartProjector.Instance);
        var invalidator = new CompositeInvalidator(
            new VersionBumpInvalidator(versions),   // correctness backbone
            new TagEvictionInvalidator(store));      // immediate reclaim
        return new MeridianRuntime(engine, invalidator, versions, store);
    }
}
