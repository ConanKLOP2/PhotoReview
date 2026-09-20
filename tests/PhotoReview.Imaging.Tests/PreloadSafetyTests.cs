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

    public PreloadSafetyTests()
    {
        Directory.CreateDirectory(_root);
        _files = Enumerable.Range(0, 8).Select(i =>
        {
            var path = Path.Combine(_root, $"image-{i}.jpg");
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return path;
        }).ToArray();
    }

    [Fact]
    public async Task MemoryPressure_DoesNotStartDecode()
    {
        var target = new RecordingTarget();
        using var scheduler = Create(target, new FakeMemoryProbe(false), workerCount: 2);

        await scheduler.PreloadAroundAsync(0);

        Assert.Equal(0, target.StartedCount);
    }

    [Fact]
    public async Task AvailableMemory_ContinuesAcrossMultipleBatches()
    {
        var target = new RecordingTarget();
        using var scheduler = Create(target, new FakeMemoryProbe(true), workerCount: 2);

        await scheduler.PreloadAroundAsync(0);

        Assert.True(target.StartedCount > 2, $"Expected more than one worker batch, got {target.StartedCount} decodes.");
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsActiveWorkersBeforeReturning()
    {
        var target = new RecordingTarget(blockUntilCancellation: true);
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

    [Fact]
    public async Task CancelThenRestart_UsesFreshLifetime_AndDisposeStopsIt()
    {
        var target = new RecordingTarget(blockUntilCancellation: true);
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
        var target = new RecordingTarget(blockUntilCancellation: true);
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
        new(target, new ReviewMetrics(), () => _files, () => 0,
            new PreloadOptions(WorkerCount: workerCount), probe, ImmediateUiScheduler.Instance);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RecordingTarget(bool blockUntilCancellation = false) : IPreloadTarget
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

        public async Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            _startedSignal.Release();
            try
            {
                if (blockUntilCancellation)
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
    }
}

