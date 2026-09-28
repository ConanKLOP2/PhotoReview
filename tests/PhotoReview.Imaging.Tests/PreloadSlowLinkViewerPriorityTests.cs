using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Q-R29 option C-2: measured (docs/refactoring/decisions/Q-R29-C2.md) that on a slow/bandwidth-capped
/// link, the existing viewer-priority worker cap (<c>_viewerBusyWorkerLimit</c>, several concurrent
/// preload readers) still let preload nearly saturate the link and starve the viewer's own read. When
/// the observed source-read time (<see cref="ReviewMetrics.DecodeMillisecondsEwma"/>) shows a slow link,
/// <see cref="PreloadScheduler"/> now caps concurrent preload decodes to
/// <c>SlowLinkViewerBusyWorkerLimit</c> (1) instead while a viewer decode is in flight.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadSlowLinkViewerPriorityTests
{
    private const int ImageCount = 40;

    [Fact(DisplayName = "On a slow link (high DecodeMillisecondsEwma), at most one preload decode runs while a viewer decode is in flight")]
    public async Task SlowLink_ViewerDecodeInFlight_CapsPreloadToOne()
    {
        var metrics = new ReviewMetrics();
        // Pushes DecodeMillisecondsEwma well past SlowLinkDecodeMsThreshold (200ms): a single sample sets
        // the EWMA directly to that value (see ReviewMetrics.UpdateDecodeEwma: "old <= 0 ? milliseconds").
        metrics.RecordSourceRead(1, 5000);

        var target = new GatedTarget(activeViewerDecodes: 1);
        using var scheduler = new PreloadScheduler(target, metrics, () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 8, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance);

        var lifetime = scheduler.PreloadAroundAsync(0);
        // The scheduler only starts a new candidate while running.Count < limit: with limit == 1 it
        // cannot admit a second one until the first is removed from `running` (i.e. after ReleaseAll
        // below), so MaxConcurrent cannot exceed 1 by the time the first has started -- no race window
        // to wait out. With the old (mutated) wider limit, several candidates start synchronously in
        // the same pass before this wait even polls once, so the mutation still shows up here.
        await Wait.UntilAsync(() => target.StartedCount >= 1, "first preload decode to start");

        Assert.True(target.MaxConcurrent <= 1, $"expected at most 1 concurrent preload decode while a viewer decode is in flight on a slow link, saw {target.MaxConcurrent}");
        Assert.True(target.StartedCount >= 1, "expected at least one preload decode to still make progress (not fully paused)");

        target.ReleaseAll();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "On a fast link (no samples yet), several preload decodes run concurrently while a viewer decode is in flight, same as before")]
    public async Task FastLink_ViewerDecodeInFlight_UsesTheWiderLimit()
    {
        var metrics = new ReviewMetrics(); // no samples: DecodeMillisecondsEwma stays 0 (not a slow link)
        var target = new GatedTarget(activeViewerDecodes: 1);
        using var scheduler = new PreloadScheduler(target, metrics, () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 8, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance);

        var lifetime = scheduler.PreloadAroundAsync(0);
        await Wait.UntilAsync(() => target.MaxConcurrent >= 2, "at least 2 concurrent preload decodes");

        Assert.True(target.MaxConcurrent >= 2, $"expected the existing (unreduced-by-this-change) viewer-busy limit to still allow >=2 concurrent preload decodes, saw {target.MaxConcurrent}");

        target.ReleaseAll();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class GatedTarget : IPreloadTarget
    {
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maxConcurrent;
        private int _started;

        public GatedTarget(int activeViewerDecodes)
        {
            ActiveViewerDecodes = activeViewerDecodes;
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\qr29c2-slowlink\img-{i:D3}.jpg").WithMetadata(1000 + i, written))
                .ToArray();
            for (var i = 0; i < Entries.Length; i++) _indexByPath[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public int ActiveViewerDecodes { get; }
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public int StartedCount => Volatile.Read(ref _started);
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public void ReleaseAll() => _release.TrySetResult();

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(Entries[_indexByPath[path]]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxConcurrent, now);
            try { await _release.Task.ConfigureAwait(false); }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
            }
        }

        private static void InterlockedMax(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (value <= current) return;
                if (Interlocked.CompareExchange(ref location, value, current) == current) return;
            }
        }
    }
}
