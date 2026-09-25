using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>PreloadScheduler keeps every cancellation lifetime and scheduler task so Dispose can drain them; a session of many navigations must not grow that bookkeeping without bound.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadLifetimeTrackingTests
{
    private sealed class AllCachedTarget : IPreloadTarget
    {
        public bool TryGetCachedPreview(string path) => true;
        public bool TryGetCachedPreview(ImageCacheKey key) => true;
        public Task PreloadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, 100);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => GetCurrentCacheKey(entry.Path);
        public int CacheCount => 0;
        public long CacheBytes => 0;
    }

    private sealed class AlwaysHeadroom : IMemoryProbe
    {
        public bool HasHeadroom(double maximumLoad, long reserveBytes) => true;
        public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
        public bool IsMemoryPressureHigh() => false;
        public long GetAvailableMemoryBytes() => 16L * 1024 * 1024 * 1024;
    }

    [Fact(DisplayName = "Thousands of navigations (each cancelling the previous preload) leave a bounded number of tracked lifetimes and tasks")]
    public async Task ManyNavigations_KeepTrackingBounded()
    {
        using var root = new TempRoot("PreloadLifetimes");
        var entries = Enumerable.Range(0, 8).Select(i => new CatalogEntry(root.File($"i{i}.jpg", 1, 2, 3))).ToArray();
        using var scheduler = new PreloadScheduler(new AllCachedTarget(), new ReviewMetrics(), () => entries, () => 0,
            new PreloadOptions(WorkerCount: 2), new AlwaysHeadroom());

        for (var i = 0; i < 1_000; i++)
        {
            await scheduler.PreloadAroundAsync(i % entries.Length);
            if (i % 2 == 0) scheduler.Cancel();
        }

        var (lifetimes, tasks) = scheduler.TrackedLifetimeCounts;
        Assert.True(lifetimes <= 8, $"{lifetimes} cancellation lifetimes still tracked after 1000 navigations");
        Assert.True(tasks <= 8, $"{tasks} scheduler tasks still tracked after 1000 navigations");
    }
}
