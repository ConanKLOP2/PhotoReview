using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>A decoder refusing background work because its queue is full is a transient skip: no error, and the path is retried by a later pass.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadDecoderBusyTests
{
    private const int ImageCount = 40;
    private const int BlockedIndex = PreloadOrderService.ForwardLookahead;
    private const int BusyIndex = 2;

    [Fact(DisplayName = "A DecoderBusyException preload is logged as Info, not Error, and retried by the next order pass")]
    public async Task BusyPreload_IsSkippedWithoutErrorAndRetriedOnTheNextPass()
    {
        var log = new RecordingLog();
        var target = new BusyTarget();
        using var scheduler = new PreloadScheduler(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: 1),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance, log);

        var lifetime = scheduler.PreloadAroundAsync(0);
        await target.BlockedEntered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, target.PreloadCount(BusyIndex));
        Assert.False(target.IsCached(BusyIndex));

        _ = scheduler.PreloadAroundAsync(0); // navigation: same lifetime, new order pass
        target.ReleaseBlocked();
        await lifetime.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, target.PreloadCount(BusyIndex)); // a Failed outcome would never be retried in this lifetime
        Assert.True(target.IsCached(BusyIndex));
        Assert.Empty(log.Errors);
        Assert.Contains(log.Infos, message => message.Contains("busy", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<string> Infos { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool Enabled => true;
        public void Info(string message) => Infos.Enqueue(message);
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) => Errors.Enqueue(message);
    }

    /// <summary>File-free cache: index <see cref="BusyIndex"/> is refused as busy on its first preload only; one index blocks to keep the lifetime open.</summary>
    private sealed class BusyTarget : IPreloadTarget
    {
        private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, int> _preloads = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BusyTarget()
        {
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\busy-preload\img-{i:D3}.cr2").WithMetadata(1000 + i, written))
                .ToArray();
            for (var i = 0; i < Entries.Length; i++) _indexByPath[Entries[i].Path] = i;
        }

        public CatalogEntry[] Entries { get; }
        public Task BlockedEntered => _entered.Task;
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public int PreloadCount(int index) => _preloads.GetValueOrDefault(index);
        public bool IsCached(int index) => _cached.ContainsKey(Entries[index].Path);
        public void ReleaseBlocked() => _release.TrySetResult();

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(Entries[_indexByPath[path]]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            var index = _indexByPath[path];
            var attempt = _preloads.AddOrUpdate(index, 1, (_, count) => count + 1);
            if (index == BusyIndex && attempt == 1) throw new DecoderBusyException();
            if (index == BlockedIndex)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }
            _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
        }
    }
}
