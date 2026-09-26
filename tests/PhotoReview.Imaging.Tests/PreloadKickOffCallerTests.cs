using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Q-R26: with the whole folder cached, a navigation's preload kick examines only the 32/8 window on the caller's
/// (UI) thread; the rest of the folder is checked on the thread pool. Before, the pass queued nothing, never
/// yielded, and ran one cache lookup per image in the folder on the UI thread before the frame.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadKickOffCallerTests
{
    private const int ImageCount = 500;

    [Fact(DisplayName = "Whole folder cached: the kick examines only the preload window on the caller's thread")]
    public async Task WholeFolderCached_CallerThreadExaminesOnlyTheWindow()
    {
        var target = new AllCachedTarget();
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => ImageCount,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1L << 40), new FakeMemoryProbe(true));

        target.TrackCaller(Environment.CurrentManagedThreadId);
        var lifetime = scheduler.PreloadAroundAsync(ImageCount / 2);
        var onCaller = target.StopTracking();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        // The window plus the center image (the decode box is read from its key).
        var window = PreloadOrderService.ForwardLookahead + PreloadOrderService.BackwardLookahead + 1;
        Assert.InRange(onCaller, 1, window);
        Assert.Equal(ImageCount, target.Examined.Count); // the far images are still checked, off the caller
    }

    private sealed class AllCachedTarget : IPreloadTarget
    {
        private int _callerThreadId = -1;
        private int _onCaller;

        public AllCachedTarget()
        {
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\q-r26-kick\img-{i:D3}.jpg").WithMetadata(1, written))
                .ToArray();
        }

        public CatalogEntry[] Entries { get; }
        public ConcurrentDictionary<string, byte> Examined { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CacheCount => ImageCount;
        public long CacheBytes => 0;

        public void TrackCaller(int threadId) => Volatile.Write(ref _callerThreadId, threadId);

        public int StopTracking()
        {
            Volatile.Write(ref _callerThreadId, -1);
            return Volatile.Read(ref _onCaller);
        }

        public bool TryGetCachedPreview(string path) => true;
        public bool TryGetCachedPreview(ImageCacheKey key) => true;
        public Task PreloadAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(new CatalogEntry(path));

        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry)
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref _callerThreadId)) Interlocked.Increment(ref _onCaller);
            Examined.TryAdd(entry.Path, 0);
            return ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
        }
    }
}
