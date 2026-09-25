using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

[Trait("Category", "Slow")]
public sealed class PreloadSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-PreloadSafety", Guid.NewGuid().ToString("N"));
    private readonly string[] _files;
    private readonly PhotoReview.Core.Catalog.CatalogEntry[] _entries;

    public PreloadSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _files = Enumerable.Range(0, 8).Select(i =>
        {
            var path = Path.Combine(_root, $"image-{i}.jpg");
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return path;
        }).ToArray();
        _entries = Array.ConvertAll(_files, f => new PhotoReview.Core.Catalog.CatalogEntry(f));
    }

    [Fact]
    public async Task MemoryPressure_DoesNotStartDecode()
    {
        using var target = new RecordingTarget();
        using var scheduler = Create(target, new FakeMemoryProbe(false), workerCount: 2);

        await scheduler.PreloadAroundAsync(0);

        Assert.Equal(0, target.StartedCount);
    }

    [Fact]
    public async Task AvailableMemory_ContinuesAcrossMultipleBatches()
    {
        using var target = new RecordingTarget();
        using var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 2);

        await scheduler.PreloadAroundAsync(0);

        Assert.True(target.StartedCount > 2, $"Expected more than one worker batch, got {target.StartedCount} decodes.");
    }

    [Fact]
    public async Task HeadroomLost_MidBatch_StopsNewStarts()
    {
        using var target = new RecordingTarget();
        // Headroom disappears as soon as the first decode has started; with 8 workers a per-batch
        // check would still start up to 7 more decodes on the stale answer (IMG-03).
        var probe = new DelegateMemoryProbe(() => target.StartedCount < 1);
        using var scheduler = Create(target, probe, workerCount: 8);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(target.StartedCount, 1, 2);
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsActiveWorkersBeforeReturning()
    {
        using var target = new RecordingTarget(blockUntilCancellation: true);
        var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 2);
        var preload = scheduler.PreloadAroundAsync(0);
        await target.WaitForStartsAsync(2);

        await Task.Run(scheduler.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        await preload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, target.CancelledCount);
        Assert.Equal(target.StartedCount, target.CompletedCount);
        Assert.True(scheduler.PreloadAroundAsync(0).IsCompletedSuccessfully);
        scheduler.Dispose(); // idempotent
    }

    [Fact(DisplayName = "Dispose does not hold its caller for a worker that ignores cancellation")]
    public async Task Dispose_WorkerIgnoresCancellation_ReturnsWithinDrainTimeout()
    {
        using var gate = new SemaphoreSlim(0);
        using var target = new RecordingTarget(uncancellableGate: gate);
        var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 2);
        scheduler.DisposeDrainTimeout = TimeSpan.FromMilliseconds(200);
        _ = scheduler.PreloadAroundAsync(0);
        await target.WaitForStartsAsync(1);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(scheduler.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Dispose blocked for {stopwatch.Elapsed}.");
        gate.Release(8); // let the abandoned workers finish
    }

    [Fact]
    public async Task CancelThenRestart_UsesFreshLifetime_AndDisposeStopsIt()
    {
        using var target = new RecordingTarget(blockUntilCancellation: true);
        var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 1);
        var first = scheduler.PreloadAroundAsync(0);
        await target.WaitForStartsAsync(1);
        scheduler.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        var second = scheduler.PreloadAroundAsync(1);
        await target.WaitForStartsAsync(2);
        await Task.Run(scheduler.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, target.CancelledCount);
        Assert.Equal(target.StartedCount, target.CompletedCount);
    }

    [Fact]
    public async Task CancelThenRestartBeforeDrain_DisposeWaitsForBothLifetimes()
    {
        using var target = new RecordingTarget(blockUntilCancellation: true);
        var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 1);
        var first = scheduler.PreloadAroundAsync(0);
        await target.WaitForStartsAsync(1);

        scheduler.Cancel();
        // Start the replacement lifetime before the cancelled worker has necessarily
        // unwound. Dispose must retain ownership of both scheduler tasks.
        var second = scheduler.PreloadAroundAsync(1);
        await target.WaitForStartsAsync(2);

        await Task.Run(scheduler.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, target.CancelledCount);
        Assert.Equal(target.StartedCount, target.CompletedCount);
    }

    private PreloadScheduler Create(IPreloadTarget target, IMemoryProbe probe, int workerCount) =>
        new(target, new ReviewMetrics(), () => _entries, () => 0,
            new PreloadOptions(WorkerCount: workerCount), probe, ImmediateUiScheduler.Instance);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class DelegateMemoryProbe(Func<bool> hasHeadroom) : IMemoryProbe
    {
        public bool HasHeadroom(double maximumLoad, long reserveBytes) => hasHeadroom();
        public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
        public bool IsMemoryPressureHigh() => !hasHeadroom();
        public long GetAvailableMemoryBytes() => hasHeadroom() ? 16L * 1024 * 1024 * 1024 : 0;
    }

    private sealed class RecordingTarget(bool blockUntilCancellation = false, SemaphoreSlim? uncancellableGate = null) : IPreloadTarget, IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _startedSignal = new(0);
        private int _started;
        private int _cancelled;
        private int _completed;

        public int StartedCount => Volatile.Read(ref _started);
        public int CancelledCount => Volatile.Read(ref _cancelled);
        public int CompletedCount => Volatile.Read(ref _completed);
        public int CacheCount => _cached.Count;
        public long CacheBytes => 0;

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => ImageCacheKey.Create(path, false, 100);
        public ImageCacheKey GetCurrentCacheKey(PhotoReview.Core.Catalog.CatalogEntry entry) => GetCurrentCacheKey(entry.Path);

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            _startedSignal.Release();
            try
            {
                if (uncancellableGate is not null)
                    await uncancellableGate.WaitAsync(CancellationToken.None); // deliberately ignores cancellation: a decode already running
                else if (blockUntilCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                else
                    await Task.Delay(10, cancellationToken);
                _cached.TryAdd(path, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally { Interlocked.Increment(ref _completed); }
        }

        public async Task WaitForStartsAsync(int count)
        {
            while (StartedCount < count)
                await _startedSignal.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => _startedSignal.Dispose();
    }
}

