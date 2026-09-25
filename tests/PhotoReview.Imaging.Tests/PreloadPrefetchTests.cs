using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Review round 7: the optional source-bytes prefetch is skipped for images whose preview is already in the disk
/// cache (the decode reads that entry, never the original).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadPrefetchTests
{
    private const int ImageCount = 20;

    [Fact(DisplayName = "Source-bytes prefetch runs only for images without a disk-cached preview")]
    public async Task Prefetch_SkipsImagesWithDiskCachedPreview()
    {
        var target = new DiskAwareTarget(onDisk: i => i % 2 == 0);
        var prefetched = new ConcurrentBag<string>();
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance,
            prefetchSourceBytes: (path, _) => { prefetched.Add(path); return Task.CompletedTask; });

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ImageCount - 1, target.PreloadCount); // every image is still preloaded
        var expected = Enumerable.Range(1, ImageCount - 1).Where(i => i % 2 != 0).Select(i => target.Entries[i].Path);
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), prefetched.Order(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class DiskAwareTarget : IPreloadTarget
    {
        private readonly Func<int, bool> _onDisk;
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private int _preloads;

        public DiskAwareTarget(Func<int, bool> onDisk)
        {
            _onDisk = onDisk;
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\r7-prefetch\img-{i:D3}.jpg").WithMetadata(1000 + i, written))
                .ToArray();
            for (var i = 0; i < Entries.Length; i++) _indexByPath[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public int PreloadCount => Volatile.Read(ref _preloads);
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(Entries[_indexByPath[path]]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
        public bool HasDiskCachedPreview(ImageCacheKey key) =>
            _onDisk(Array.FindIndex(Entries, e => string.Equals(GetCurrentCacheKey(e).Path, key.Path, StringComparison.Ordinal)));

        public Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _preloads);
            _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
            return Task.CompletedTask;
        }
    }
}
