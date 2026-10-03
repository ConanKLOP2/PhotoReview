using System.Collections.Concurrent;
using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// RV-T28: <see cref="PreloadControllerAdapter"/> forwards every member to the scheduler returned by the lazy
/// <c>getScheduler</c> delegate (it is re-read on every call, never captured), with the real scheduler's results.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadControllerAdapterTests
{
    private static CatalogEntry[] MakeEntries(int count)
    {
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new CatalogEntry($@"C:\adapter-test\img-{i:D3}.jpg").WithMetadata(1, written))
            .ToArray();
    }

    private static PreloadScheduler NewScheduler(RecordingTarget target) =>
        new(target, new ReviewMetrics(), () => target.Entries,
            new PreloadOptions(WorkerCount: 2, FullFolderThresholdBytes: 0), new FakeMemoryProbe());

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PreloadControllerAdapter(null!));
    }

    [Fact]
    public void EveryMember_ReadsTheSchedulerFromTheLazyFactoryOnEachCall()
    {
        var target = new RecordingTarget(2);
        using var scheduler = NewScheduler(target);
        var reads = 0;
        var adapter = new PreloadControllerAdapter(() => { reads++; return scheduler; });
        Assert.Equal(0, reads); // lazy: construction does not touch the scheduler
        var key = target.GetCurrentCacheKey(target.Entries[0].Path);

        adapter.Cancel();
        adapter.ClearPreloadedKeys();
        adapter.RemovePreloadedKeysForPath(key.Path);
        _ = adapter.TryConsumePreloadedKey(key);
        _ = adapter.IsIdle;
        _ = adapter.GetViewerDecodeDelay();
        adapter.NotifyNavigation(0);

        Assert.Equal(7, reads);
    }

    [Fact]
    public async Task PreloadAroundAsync_DelegatesToTheSchedulerAndWarmsTheNeighbour()
    {
        var target = new RecordingTarget(2);
        using var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);

        await adapter.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(target.Entries[1].Path, target.Preloaded);
        Assert.True(adapter.IsIdle);
    }

    [Fact]
    public async Task TryConsumePreloadedKey_AfterAWarmedPreload_ConsumesTheKeyExactlyOnce()
    {
        var target = new RecordingTarget(2);
        using var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);
        await adapter.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));
        var key = target.GetCurrentCacheKey(target.Entries[1]);

        Assert.True(adapter.TryConsumePreloadedKey(key));
        Assert.False(adapter.TryConsumePreloadedKey(key));
    }

    [Fact]
    public async Task RemovePreloadedKeysForPath_DropsTheWarmedKeyOfThatPathOnly()
    {
        var target = new RecordingTarget(3);
        using var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);
        await adapter.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));
        var removed = target.GetCurrentCacheKey(target.Entries[1]);
        var kept = target.GetCurrentCacheKey(target.Entries[2]);

        adapter.RemovePreloadedKeysForPath(removed.Path);

        Assert.False(adapter.TryConsumePreloadedKey(removed));
        Assert.True(adapter.TryConsumePreloadedKey(kept));
    }

    [Fact]
    public async Task ClearPreloadedKeys_DropsEveryWarmedKey()
    {
        var target = new RecordingTarget(3);
        using var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);
        await adapter.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));

        adapter.ClearPreloadedKeys();

        Assert.False(adapter.TryConsumePreloadedKey(target.GetCurrentCacheKey(target.Entries[1])));
        Assert.False(adapter.TryConsumePreloadedKey(target.GetCurrentCacheKey(target.Entries[2])));
    }

    [Fact]
    public async Task CancelAndIsIdle_ReflectTheSchedulersInFlightPreload()
    {
        var target = new RecordingTarget(2) { Block = true };
        using var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);
        Assert.True(adapter.IsIdle); // a fresh scheduler is idle

        var lifetime = adapter.PreloadAroundAsync(0);
        await target.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(adapter.IsIdle);
        Assert.False(target.LastToken.IsCancellationRequested);

        adapter.Cancel();

        Assert.True(target.LastToken.IsCancellationRequested);
        await lifetime.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(adapter.IsIdle);
    }

    [Fact]
    public async Task Dispose_DisposesTheSchedulerSoALaterPreloadStartsNothing()
    {
        var target = new RecordingTarget(2);
        var scheduler = NewScheduler(target);
        var adapter = new PreloadControllerAdapter(() => scheduler);

        adapter.Dispose();
        await adapter.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(target.Preloaded);
    }

    private sealed class FakeMemoryProbe : IMemoryProbe
    {
        public bool HasHeadroom(double maximumLoad, long reserveBytes) => true;
        public MemorySnapshot? GetSnapshot() => null;
    }

    private sealed class RecordingTarget : IPreloadTarget
    {
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _preloaded = new();

        public RecordingTarget(int count) => Entries = MakeEntries(count);

        public CatalogEntry[] Entries { get; }
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken { get; private set; }
        public IReadOnlyCollection<string> Preloaded => _preloaded;
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);

        public bool TryGetCachedPreview(ImageCacheKey key) =>
            _cached.Keys.Any(p => string.Equals(Path.GetFullPath(p).ToUpperInvariant(), key.Path, StringComparison.Ordinal));

        public Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            _preloaded.Enqueue(path);
            LastToken = cancellationToken;
            Started.TrySetResult();
            if (Block)
            {
                var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken));
                return blocked.Task;
            }
            _cached.TryAdd(path, 0);
            return Task.CompletedTask;
        }

        public ImageCacheKey GetCurrentCacheKey(string path) =>
            GetCurrentCacheKey(Array.Find(Entries, entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))!);

        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) =>
            ImageCacheKey.Create(entry, false, new DecodeBox(100, 100));
    }
}
