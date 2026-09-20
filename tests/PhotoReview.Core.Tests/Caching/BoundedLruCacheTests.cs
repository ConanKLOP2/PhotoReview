using PhotoReview.Core.Caching;

namespace PhotoReview.Core.Tests.Caching;

/// <summary>Bounded LRU cache contracts.</summary>
[Trait("Category", "HotPath")]
public sealed class BoundedLruCacheTests
{
    private static BoundedLruCache<string, string> Seeded()
    {
        var cache = new BoundedLruCache<string, string>(4, value => value.Length, StringComparer.Ordinal);
        cache.Set("a", "aa");
        cache.Set("b", "b");
        return cache;
    }

    [Fact(DisplayName = "LRU retains recently accessed entry")]
    public void LruRetainsRecentlyAccessedEntry() => Assert.True(Seeded().TryGet("a", out _));

    [Fact(DisplayName = "LRU evicts least recently used entry")]
    public void LruEvictsLeastRecentlyUsedEntry()
    {
        var cache = Seeded();
        cache.TryGet("a", out _);
        cache.Set("c", "cc");
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact(DisplayName = "LRU enforces byte capacity")]
    public void LruEnforcesByteCapacity()
    {
        var cache = Seeded();
        cache.TryGet("a", out _);
        cache.Set("c", "cc");
        Assert.True(cache.CurrentSize <= 4);
    }

    [Fact(DisplayName = "LRU rejects a null removal predicate")]
    public void RemoveWhereNullPredicateThrows() =>
        Assert.Throws<ArgumentNullException>(() => Seeded().RemoveWhere(null!));
}
