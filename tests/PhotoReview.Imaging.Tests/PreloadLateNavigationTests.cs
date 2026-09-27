using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>A navigation landing after a pass read the priority version but before the scheduler exits is not lost.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadLateNavigationTests
{
    private const int ImageCount = 200;
    private const int OldWindowLast = PreloadOrderService.ForwardLookahead;
    private const int NewCenter = 150;

    [Fact(DisplayName = "A navigation arriving during an all-cached pass is preloaded around, not dropped when the scheduler exits")]
    public async Task NavigationDuringAllCachedPass_IsServedByTheSameScheduler()
    {
        var target = new AllCachedTarget();
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 1, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance);
        target.OnCheck = index =>
        {
            // Re-entrant on the scheduler's own pass: the version is bumped after the pass read it.
            if (index == OldWindowLast && Interlocked.Exchange(ref target.Fired, 1) == 0)
                _ = scheduler.PreloadAroundAsync(NewCenter);
        };

        // One worker: pass 1 queues the single uncached image and waits for it; the pass after it finishes then
        // examines the rest of the (cached) window on a pool thread, while the scheduler task is already published.
        var lifetime = scheduler.PreloadAroundAsync(0);
        await target.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        target.Release();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(target.Checked.ContainsKey(NewCenter + 1), $"never examined; fired={target.Fired} checked={string.Join(",", target.Checked.Keys.Order())}");
    }

    private sealed class AllCachedTarget : IPreloadTarget
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _warm;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public int Fired;
        public Action<int>? OnCheck;
        public System.Collections.Concurrent.ConcurrentDictionary<int, byte> Checked { get; } = new();
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

        public AllCachedTarget()
        {
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\late-nav\img-{i:D3}.jpg").WithMetadata(1000 + i, written)).ToArray();
            for (var i = 0; i < Entries.Length; i++) _index[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public int CacheCount => 0;
        public long CacheBytes => 0;
        public bool TryGetCachedPreview(string path) => true;
        public bool TryGetCachedPreview(ImageCacheKey key)
        {
            var index = _index[key.Path];
            Checked.TryAdd(index, 0);
            OnCheck?.Invoke(index);
            return index != 1 || Volatile.Read(ref _warm) == 1;
        }
        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            Volatile.Write(ref _warm, 1);
        }
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(Entries[_index[path]]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
    }
}
