using Meridian.Caching;
using Meridian.Core;
using Meridian.Time;
using Xunit;

namespace Meridian.Caching.Tests;

public class PointCacheTests
{
    private static readonly DimensionId Player = new("player");

    private static CacheScope Scope() => new(
        Tenant: "football",
        Metric: "training-load",
        EntityDimension: Player,
        Timeframe: new DateInterval(
            Instant.FromUtc(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
            Instant.FromUtc(new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc))),
        Signature: "week|mean|leavemissing");

    private static EntityRef E(long id) => new(Player, id);

    /// <summary>A loader that records which entities it was asked for and fabricates one point each.</summary>
    private sealed class RecordingLoader
    {
        public List<IReadOnlyList<EntityRef>> Calls { get; } = [];

        public PointLoader Delegate => (missing, ct) =>
        {
            Calls.Add(missing);
            var builder = new PointBlock.Builder(new Unit("au"));
            foreach (var e in missing)
            {
                builder.Add(PointKey.Of(KeyPart.Entity(Player, e.Id)), Measurement.Of(e.Id * 10.0), null);
            }
            return Task.FromResult(builder.Build());
        };
    }

    private static PointCache NewCache(out IDataVersionStore versions, out IPointCacheStore store)
    {
        versions = new InMemoryDataVersionStore();
        store = new TaggedStoreDecorator(new InMemoryKeyValueStore());
        return new PointCache(store, versions);
    }

    [Fact]
    public void CacheKey_is_deterministic_for_the_same_logical_request()
    {
        var a = CacheKeyBuilder.ForEntity(Scope(), E(5), version: 3);
        var b = CacheKeyBuilder.ForEntity(Scope(), E(5), version: 3);
        Assert.Equal(a, b);
        Assert.Equal(a.Hash, b.Hash);

        var different = CacheKeyBuilder.ForEntity(Scope(), E(5), version: 4);
        Assert.NotEqual(a, different); // a version bump is a different key
    }

    [Fact]
    public async Task Partial_hit_only_loads_the_missing_entities()
    {
        var cache = NewCache(out _, out _);
        var loader = new RecordingLoader();

        var first = await cache.GetOrLoadAsync(Scope(), [E(1), E(2)], loader.Delegate);
        var second = await cache.GetOrLoadAsync(Scope(), [E(1), E(2), E(3)], loader.Delegate);

        // First call loads {1,2}; second call must load ONLY {3} — the crown-jewel merge behaviour.
        Assert.Equal([E(1), E(2)], loader.Calls[0]);
        Assert.Equal([E(3)], loader.Calls[1]);

        // Merged result covers all three requested entities, in order.
        Assert.Equal([10.0, 20.0, 30.0], second.Values.ToArray());
    }

    [Fact]
    public async Task Concurrent_identical_cold_requests_load_once()
    {
        var cache = NewCache(out _, out _);
        var loader = new SlowRecordingLoader();

        var entities = new[] { E(1), E(2) };
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrLoadAsync(Scope(), entities, loader.Delegate))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, loader.CallCount); // single-flight: eight readers, one load
    }

    [Fact]
    public async Task Version_bump_invalidates_without_active_eviction()
    {
        var cache = NewCache(out var versions, out _);
        var loader = new RecordingLoader();
        var invalidator = new VersionBumpInvalidator(versions);

        await cache.GetOrLoadAsync(Scope(), [E(1)], loader.Delegate);         // loads {1}
        await cache.GetOrLoadAsync(Scope(), [E(1)], loader.Delegate);         // cached — no new load
        Assert.Single(loader.Calls);

        // A metric write for player 1 bumps the version → next read must miss and reload (no stale data).
        await invalidator.InvalidateAsync(new ChangeScope("football", "training-load", E(1)));
        await cache.GetOrLoadAsync(Scope(), [E(1)], loader.Delegate);
        Assert.Equal(2, loader.Calls.Count);
    }

    [Fact]
    public async Task Tag_eviction_invalidates_immediately()
    {
        var cache = NewCache(out _, out var store);
        var loader = new RecordingLoader();
        var invalidator = new TagEvictionInvalidator(store);

        await cache.GetOrLoadAsync(Scope(), [E(1), E(2)], loader.Delegate); // loads {1,2}
        await invalidator.InvalidateAsync(new ChangeScope("football", "training-load", E(1)));

        // Only player 1 was invalidated → player 2 still cached, player 1 reloads.
        await cache.GetOrLoadAsync(Scope(), [E(1), E(2)], loader.Delegate);
        Assert.Equal([E(1)], loader.Calls[1]);
    }

    private sealed class SlowRecordingLoader
    {
        private int _calls;
        public int CallCount => _calls;

        public PointLoader Delegate => async (missing, ct) =>
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(30, ct); // widen the window so a stampede would show up as >1 call
            var builder = new PointBlock.Builder(new Unit("au"));
            foreach (var e in missing)
            {
                builder.Add(PointKey.Of(KeyPart.Entity(Player, e.Id)), Measurement.Of(e.Id), null);
            }
            return builder.Build();
        };
    }
}
