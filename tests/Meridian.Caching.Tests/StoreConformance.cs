using Meridian.Caching;
using Meridian.Core;
using Xunit;

namespace Meridian.Caching.Tests;

/// <summary>
/// The Hangfire trick: one conformance suite every <see cref="IPointCacheStore"/> backend must pass.
/// InMemory runs it below; Redis/SQL would subclass this and pass the same tests — that is the whole
/// promise of the pluggable-backend design.
/// </summary>
public abstract class StoreConformance
{
    protected abstract IPointCacheStore CreateStore();

    private static CacheKey Key(string s) => new(s);
    private static CacheEntry Entry(double v) =>
        new(PointBlock.FromRows([new Point(PointKey.Empty, v, null)]));

    [Fact]
    public async Task Get_missing_returns_null()
    {
        var store = CreateStore();
        Assert.Null(await store.GetAsync(Key("nope")));
    }

    [Fact]
    public async Task Set_then_Get_returns_the_entry()
    {
        var store = CreateStore();
        await store.SetAsync(Key("a"), Entry(42), [], CacheEntryOptions.None);
        var got = await store.GetAsync(Key("a"));
        Assert.NotNull(got);
        Assert.Equal(42.0, got!.Block.Values[0]);
    }

    [Fact]
    public async Task Set_overwrites()
    {
        var store = CreateStore();
        await store.SetAsync(Key("a"), Entry(1), [], CacheEntryOptions.None);
        await store.SetAsync(Key("a"), Entry(2), [], CacheEntryOptions.None);
        Assert.Equal(2.0, (await store.GetAsync(Key("a")))!.Block.Values[0]);
    }

    [Fact]
    public async Task Remove_deletes()
    {
        var store = CreateStore();
        await store.SetAsync(Key("a"), Entry(1), [], CacheEntryOptions.None);
        await store.RemoveAsync(Key("a"));
        Assert.Null(await store.GetAsync(Key("a")));
    }

    [Fact]
    public async Task RemoveByTag_removes_all_tagged_and_nothing_else()
    {
        var store = CreateStore();
        var tag = new CacheTag("group");
        await store.SetAsync(Key("a"), Entry(1), [tag], CacheEntryOptions.None);
        await store.SetAsync(Key("b"), Entry(2), [tag], CacheEntryOptions.None);
        await store.SetAsync(Key("c"), Entry(3), [new CacheTag("other")], CacheEntryOptions.None);

        await store.RemoveByTagAsync(tag);

        Assert.Null(await store.GetAsync(Key("a")));
        Assert.Null(await store.GetAsync(Key("b")));
        Assert.NotNull(await store.GetAsync(Key("c"))); // untouched
    }

    [Fact]
    public async Task Overwriting_with_new_tags_drops_the_old_tag_association()
    {
        var store = CreateStore();
        await store.SetAsync(Key("a"), Entry(1), [new CacheTag("t1")], CacheEntryOptions.None);
        await store.SetAsync(Key("a"), Entry(2), [new CacheTag("t2")], CacheEntryOptions.None);

        await store.RemoveByTagAsync(new CacheTag("t1")); // should no longer match key "a"
        Assert.NotNull(await store.GetAsync(Key("a")));

        await store.RemoveByTagAsync(new CacheTag("t2"));
        Assert.Null(await store.GetAsync(Key("a")));
    }
}

public sealed class InMemoryStoreConformanceTests : StoreConformance
{
    protected override IPointCacheStore CreateStore() =>
        new TaggedStoreDecorator(new InMemoryKeyValueStore());
}

public sealed class LayeredStoreConformanceTests : StoreConformance
{
    protected override IPointCacheStore CreateStore() =>
        new LayeredPointCacheStore(
            new TaggedStoreDecorator(new InMemoryKeyValueStore()),
            new TaggedStoreDecorator(new InMemoryKeyValueStore()));
}
