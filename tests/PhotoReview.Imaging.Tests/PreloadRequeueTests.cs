using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Review round 7: a preview preloaded earlier in a scheduler lifetime and evicted since (other decodes sharing the
/// LRU) is queued again by the next order pass; a path whose preload failed is not retried in that lifetime.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadRequeueTests
{
    private const int ImageCount = 40;
    // Last index of the 32-ahead window around 0: held inside PreloadAsync so the lifetime stays open.
    private const int BlockedIndex = PreloadOrderService.ForwardLookahead;

    [Fact(DisplayName = "Evicted preview is preloaded again on the next order pass of the same lifetime")]
    public async Task EvictedPreview_IsRequeued_WithinSameLifetime()
    {
        var target = new GatedTarget(blockedIndex: BlockedIndex);
        using var scheduler = Create(target);

        var lifetime = scheduler.PreloadAroundAsync(0);
        await target.BlockedEntered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, target.PreloadCount(1));

        target.Evict(1); // e.g. a compare/zoom decode pushed it out of the LRU
        _ = scheduler.PreloadAroundAsync(0); // navigation: same lifetime, new order pass
        target.ReleaseBlocked();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, target.PreloadCount(1));
        Assert.True(target.IsCached(1));
    }

    [Fact(DisplayName = "A failed preload is not retried by the next order pass of the same lifetime")]
    public async Task FailedPreload_IsNotRetried_WithinSameLifetime()
    {
        var target = new GatedTarget(blockedIndex: BlockedIndex, failingIndex: 2);
        using var scheduler = Create(target);

        var lifetime = scheduler.PreloadAroundAsync(0);
        await target.BlockedEntered.WaitAsync(TimeSpan.FromSeconds(10));
        _ = scheduler.PreloadAroundAsync(0);
        target.ReleaseBlocked();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, target.PreloadCount(2));
    }

    private static PreloadScheduler Create(GatedTarget target) =>
        new(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance);

    /// <summary>File-free preview cache: one index blocks inside PreloadAsync, one can fail with an IOException.</summary>
    private sealed class GatedTarget : IPreloadTarget
    {
        private readonly int _blockedIndex;
        private readonly int _failingIndex;
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, int> _preloads = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedTarget(int blockedIndex, int failingIndex = -1)
        {
            _blockedIndex = blockedIndex;
            _failingIndex = failingIndex;
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\r7-requeue\img-{i:D3}.jpg").WithMetadata(1000 + i, written))
                .ToArray();
            for (var i = 0; i < Entries.Length; i++) _indexByPath[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public Task BlockedEntered => _entered.Task;
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public int PreloadCount(int index) => _preloads.GetValueOrDefault(index);
        public bool IsCached(int index) => _cached.ContainsKey(Entries[index].Path);
        public void Evict(int index) => _cached.TryRemove(Entries[index].Path, out _);
        public void ReleaseBlocked() => _release.TrySetResult();

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(Entries[_indexByPath[path]]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            var index = _indexByPath[path];
            _preloads.AddOrUpdate(index, 1, (_, count) => count + 1);
            if (index == _failingIndex) throw new IOException("unreadable");
            if (index == _blockedIndex)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
        }
    }
}
