using System.Collections.Concurrent;
using System.IO;
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

    /// <summary>
    /// feat/preload-window-setting: the caller-thread cap (Q-R26) must follow the configured window too, not the
    /// hard-coded ForwardLookahead/BackwardLookahead constants -- otherwise a small configured window would still
    /// examine the old, larger default window's worth of candidates on the UI thread.
    /// </summary>
    [Fact(DisplayName = "Whole folder cached with a small configured window: the kick examines only that window on the caller's thread")]
    public async Task WholeFolderCached_ConfiguredWindow_CallerThreadExaminesOnlyThatWindow()
    {
        var target = new AllCachedTarget();
        var window = new PreloadWindow(3, 1);
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => ImageCount,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1L << 40) { Window = window }, new FakeMemoryProbe(true));

        target.TrackCaller(Environment.CurrentManagedThreadId);
        var lifetime = scheduler.PreloadAroundAsync(ImageCount / 2);
        var onCaller = target.StopTracking();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(onCaller, 1, window.ImageCount);
        Assert.Equal(ImageCount, target.Examined.Count); // the far images are still checked, off the caller
    }

    /// <summary>feat/preload-window-setting: a user-configured window is honoured end to end by the scheduler.</summary>
    [Fact(DisplayName = "A configured window of (3,1) preloads exactly the 3 forward + 1 backward neighbours")]
    public async Task ConfiguredWindow_PreloadsExactlyForwardPlusBackwardNeighbours()
    {
        const int LocalImageCount = 50;
        var target = new AutoCacheTarget(LocalImageCount);
        var options = new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 0) { Window = new PreloadWindow(3, 1) };
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 0,
            options, new FakeMemoryProbe(true));

        await scheduler.PreloadAroundAsync(25).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, target.CacheCount);
        Assert.Equal(ExpectedNeighbours, target.CachedIndices.OrderBy(i => i));
    }

    private static readonly int[] ExpectedNeighbours = [24, 26, 27, 28];

    private sealed class AutoCacheTarget : IPreloadTarget
    {
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);

        public AutoCacheTarget(int count)
        {
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, count)
                .Select(i => new CatalogEntry($@"C:\preload-window-test\img-{i:D3}.jpg").WithMetadata(1, written))
                .ToArray();
        }

        public CatalogEntry[] Entries { get; }
        public int CacheCount => _cached.Count;

        public IEnumerable<int> CachedIndices => Entries
            .Select((entry, index) => (entry, index))
            .Where(t => _cached.ContainsKey(t.entry.Path))
            .Select(t => t.index);

        public long CacheBytes => 0;

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);

        public bool TryGetCachedPreview(ImageCacheKey key) =>
            _cached.Keys.Any(p => string.Equals(Path.GetFullPath(p).ToUpperInvariant(), key.Path, StringComparison.Ordinal));

        public Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            _cached.TryAdd(path, 0);
            return Task.CompletedTask;
        }

        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(new CatalogEntry(path));

        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) =>
            ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
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
