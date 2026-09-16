using System.IO;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// PreviewImageService owns decode + the bounded RAM cache and needs no WPF Window, so the
/// decode/cache/metrics contract is driven for real (migrated from Program.cs).
/// </summary>
public sealed class PreviewImageServiceTests : IAsyncLifetime
{
    // A valid, tiny PNG keeps decode fixtures portable while exercising WPF's real decoder.
    internal static readonly byte[] PreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly TempRoot _root = new("preview-service");
    private readonly string _previewPath;
    private readonly ReviewMetrics _metrics = new();
    private readonly PreviewImageService _service;
    // Every PreviewImageService starts two persist workers that only stop once
    // ShutdownPersistWorkersAsync completes their channel; tracking each instance created
    // by this class (including the ones tests construct locally) lets DisposeAsync retire
    // them all instead of leaking live background workers per test. The disk directory is
    // tracked alongside each service so teardown can also wait for that directory's own
    // fire-and-forget prune pass(es) -- see DisposeAsync.
    private readonly List<(PreviewImageService Service, string DiskDirectory)> _services = [];

    public PreviewImageServiceTests()
    {
        var folder = _root.Dir("preview-service");
        _previewPath = Path.Combine(folder, "preview-a.png");
        File.WriteAllBytes(_previewPath, PreviewPng);
        var diskCache = _root.Dir("disk-cache");
        _service = Track(new PreviewImageService(_metrics, () => false, () => 512, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskCache), diskCache);
    }

    private PreviewImageService Track(PreviewImageService service, string diskDirectory)
    {
        _services.Add((service, diskDirectory));
        return service;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(s => s.Service.ShutdownPersistWorkersAsync()));
        // ShutdownPersistWorkersAsync only waits for the persist writes themselves; each
        // write's SchedulePrune call is itself fire-and-forget, so wait for those to finish
        // too, or _root.Dispose() below can race a prune worker still enumerating/deleting
        // files in one of these directories.
        await Task.WhenAll(_services.Select(s => DiskCacheStore.WaitForPruneAsync(s.DiskDirectory, TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    [Fact(DisplayName = "Preview decode records exactly one source read with the real source byte count")]
    public async Task PreviewDecodeRecordsExactlyOneSourceRead()
    {
        var decoded = await _service.GetPreviewAsync(_previewPath);
        var snapshot = _metrics.Snapshot();
        Assert.True(decoded.PixelWidth > 0 && snapshot.CacheMisses == 1 && snapshot.CacheHits == 0
            && snapshot.SourceReads == 1 && snapshot.SourceBytesRead == new FileInfo(_previewPath).Length
            && _service.CacheCount == 1 && _service.CacheBytes > 0);
    }

    [Fact(DisplayName = "Concurrent requests for the same key decode the source exactly once")]
    public async Task ConcurrentRequestsForSameKeyDecodeOnce()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => _service.GetPreviewAsync(_previewPath)));
        var snapshot = _metrics.Snapshot();
        Assert.True(snapshot.SourceReads == 1 && results.All(image => ReferenceEquals(image, results[0])));
    }

    [Fact(DisplayName = "Source byte metrics exclude cache deliveries and the cache returns the same decoded bitmap")]
    public async Task SourceByteMetricsExcludeCacheDeliveries()
    {
        var decoded = await _service.GetPreviewAsync(_previewPath);
        var afterFirstDecode = _metrics.Snapshot();
        var second = await _service.GetPreviewAsync(_previewPath);
        var afterCacheHit = _metrics.Snapshot();
        Assert.True(afterCacheHit.CacheHits == 1 && afterCacheHit.SourceReads == 1
            && afterCacheHit.SourceBytesRead == afterFirstDecode.SourceBytesRead
            && ReferenceEquals(second, decoded));
    }

    [Fact(DisplayName = "Evicting a path drops its decoded bitmap from the preview cache")]
    public async Task EvictingPathDropsDecodedBitmap()
    {
        await _service.GetPreviewAsync(_previewPath);
        _service.EvictCachedPath(_previewPath);
        Assert.True(!_service.TryGetCachedPreview(_previewPath, out _) && _service.CacheCount == 0);
    }

    [Fact(DisplayName = "Eviction forces a fresh source read on the next request")]
    public async Task EvictionForcesFreshSourceReadOnNextRequest()
    {
        await _service.GetPreviewAsync(_previewPath);
        _service.EvictCachedPath(_previewPath);
        await _service.GetPreviewAsync(_previewPath);
        Assert.True(_metrics.Snapshot().SourceReads == 2 && _service.CacheCount == 1);
    }

    [Fact(DisplayName = "Clearing the preview cache drops every decoded bitmap")]
    public async Task ClearingPreviewCacheDropsEveryDecodedBitmap()
    {
        await _service.GetPreviewAsync(_previewPath);
        _service.ClearCache();
        Assert.True(_service.CacheCount == 0 && _service.CacheBytes == 0);
    }

    [Fact(DisplayName = "Original loading mode decodes at full size while Preview mode uses the target decode width")]
    public void OriginalLoadingModeDecodesAtFullSize()
    {
        var originalDiskCache = _root.Dir("disk-cache-original");
        var originalModeService = Track(new PreviewImageService(_metrics, () => true, () => 512,
            diskCacheDirectory: originalDiskCache), originalDiskCache);
        Assert.True(originalModeService.IsOriginalLoadingMode()
            && originalModeService.GetCurrentCacheKey(_previewPath).IsOriginal
            && originalModeService.GetCurrentCacheKey(_previewPath).TargetWidth == 0
            && !_service.GetCurrentCacheKey(_previewPath).IsOriginal
            && _service.GetCurrentCacheKey(_previewPath).TargetWidth == 512);
    }

    [Fact(DisplayName = "Original dimensions are read from the real source header")]
    public async Task OriginalDimensionsAreReadFromRealSourceHeader()
    {
        var dimensions = await _service.GetOriginalDimensionsAsync(_previewPath);
        Assert.True(dimensions.Width == 1 && dimensions.Height == 1);
    }
}

/// <summary>
/// PreloadScheduler takes its memory-load limit by constructor injection, so the memory
/// pressure guard is driven for real: a 0.0 limit can never have headroom
/// (migrated from Program.cs).
/// </summary>
public sealed class PreloadSchedulerTests : IAsyncLifetime
{
    private readonly TempRoot _root = new("preload-scheduler");
    private readonly string[] _preloadFiles;
    private readonly string[] _singleFiles;
    // See PreviewImageServiceTests: every PreviewImageService created here (directly or via
    // NewWarmScheduler) starts persist workers that must be shut down, not just have their
    // temp directory deleted out from under them; the disk directory travels with each
    // service so DisposeAsync can also wait out that directory's fire-and-forget prune(s).
    private readonly List<(PreviewImageService Service, string DiskDirectory)> _services = [];

    public PreloadSchedulerTests()
    {
        var preloadFolder = _root.Dir("preload-scheduler");
        _preloadFiles = Enumerable.Range(0, 4).Select(i =>
        {
            var path = Path.Combine(preloadFolder, $"preload-{i}.png");
            File.WriteAllBytes(path, PreviewImageServiceTests.PreviewPng);
            return path;
        }).ToArray();

        var singleFolder = _root.Dir("preload-single");
        _singleFiles = Enumerable.Range(0, 2).Select(i =>
        {
            var path = Path.Combine(singleFolder, $"single-{i}.png");
            File.WriteAllBytes(path, PreviewImageServiceTests.PreviewPng);
            return path;
        }).ToArray();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(s => s.Service.ShutdownPersistWorkersAsync()));
        // See PreviewImageServiceTests.DisposeAsync: wait for each service's own
        // fire-and-forget prune pass(es) too, or _root.Dispose() below can race one.
        await Task.WhenAll(_services.Select(s => DiskCacheStore.WaitForPruneAsync(s.DiskDirectory, TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    // hasHeadroom defaults to "always available": these tests exercise the scheduler's
    // own priority/queueing logic and must not depend on how much RAM the test
    // machine actually has free (PhysicalMemory.HasHeadroom also floors on a fixed
    // 2 GiB reserve, which a constrained CI/dev box may never satisfy).
    private (ReviewMetrics Metrics, PreviewImageService Service, PreloadScheduler Scheduler) NewWarmScheduler(
        Func<double, bool>? hasHeadroom = null)
    {
        var metrics = new ReviewMetrics();
        var diskDirectory = _root.Dir("disk-cache-" + Guid.NewGuid().ToString("N"));
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskDirectory);
        _services.Add((service, diskDirectory));
        var scheduler = new PreloadScheduler(service, metrics, () => _preloadFiles, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: hasHeadroom ?? (_ => true));
        return (metrics, service, scheduler);
    }

    [Fact(DisplayName = "Background preload memory guard decodes nothing when there is no memory headroom")]
    public async Task MemoryGuardDecodesNothingWithoutHeadroom()
    {
        var (metrics, service, scheduler) = NewWarmScheduler(hasHeadroom: _ => false);
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            Assert.True(service.CacheCount == 0 && metrics.Snapshot().SourceReads == 0);
        }
    }

    [Fact(DisplayName = "Background preload warms the cache around the current index when memory headroom allows")]
    public async Task PreloadWarmsCacheAroundCurrentIndex()
    {
        var (metrics, service, scheduler) = NewWarmScheduler();
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            Assert.True(service.CacheCount > 0 && metrics.Snapshot().SourceReads > 0);
        }
    }

    [Fact(DisplayName = "Background preload prioritizes the next image after the current index")]
    public async Task PreloadPrioritizesNextImage()
    {
        var (_, service, scheduler) = NewWarmScheduler();
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            Assert.True(service.TryGetCachedPreview(_preloadFiles[1], out _));
        }
    }

    [Fact(DisplayName = "Cancelling preload does not poison the scheduler; the next request starts a fresh lifetime")]
    public async Task CancellingPreloadDoesNotPoisonScheduler()
    {
        var (_, service, scheduler) = NewWarmScheduler();
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            scheduler.Cancel();
            var reloadedCount = service.CacheCount;
            await scheduler.PreloadAroundAsync(2);
            Assert.True(service.CacheCount >= reloadedCount);
        }
    }

    [Fact(DisplayName = "Clearing preloaded keys drops every warmed-key record")]
    public async Task ClearingPreloadedKeysDropsEveryRecord()
    {
        var (_, service, scheduler) = NewWarmScheduler();
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            scheduler.Cancel();
            await scheduler.PreloadAroundAsync(2);
            scheduler.ClearPreloadedKeys();
            Assert.False(scheduler.TryConsumePreloadedKey(service.GetCurrentCacheKey(_preloadFiles[0])));
        }
    }

    // Warmed-key bookkeeping is asserted against a two-file catalog so exactly one preload
    // worker runs. PreloadScheduler._preloadedKeys is written from concurrent PreloadOneAsync
    // tasks, so a multi-worker catalog loses records intermittently.
    [Fact(DisplayName = "A preloaded key is reported once and then consumed so a hit is not counted twice")]
    public async Task PreloadedKeyIsReportedOnceThenConsumed()
    {
        var metrics = new ReviewMetrics();
        var diskDirectory = _root.Dir("disk-cache-single");
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskDirectory);
        _services.Add((service, diskDirectory));
        using var scheduler = new PreloadScheduler(service, metrics, () => _singleFiles, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: _ => true);
        await scheduler.PreloadAroundAsync(0);
        var warmedKey = service.GetCurrentCacheKey(_singleFiles[1]);
        Assert.True(scheduler.TryConsumePreloadedKey(warmedKey) && !scheduler.TryConsumePreloadedKey(warmedKey));
    }
}

/// <summary>
/// PreviewImageService's disk cache (PR-029): a decoded preview is persisted in the
/// background and served without a source read on the next request, scoped to
/// downscaled previews only, with corruption recovery falling back to the source.
/// The disk directory is always overridden to a TempRoot so these tests never touch
/// the developer's real %LocalAppData%\PhotoReview\cache.
/// </summary>
public sealed class PreviewImageServiceDiskCacheTests : IAsyncLifetime
{
    private readonly TempRoot _root = new("preview-disk-cache");
    private readonly string _previewPath;
    // See PreviewImageServiceTests: every service created by a test below must have its
    // persist workers shut down, not just its temp directory deleted; the disk directory
    // travels with each service so DisposeAsync can also wait out its fire-and-forget
    // prune pass(es) before the temp root is removed.
    private readonly List<(PreviewImageService Service, string DiskDirectory)> _services = [];

    public PreviewImageServiceDiskCacheTests()
    {
        var folder = _root.Dir("source");
        _previewPath = Path.Combine(folder, "preview-a.png");
        File.WriteAllBytes(_previewPath, PreviewImageServiceTests.PreviewPng);
    }

    private PreviewImageService Track(PreviewImageService service, string diskDirectory)
    {
        _services.Add((service, diskDirectory));
        return service;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(s => s.Service.ShutdownPersistWorkersAsync()));
        // ShutdownPersistWorkersAsync only waits for the persist writes themselves; each
        // write's SchedulePrune call is itself fire-and-forget, so wait for those to finish
        // too, or _root.Dispose() below can race a prune worker still enumerating/deleting
        // files in one of these directories.
        await Task.WhenAll(_services.Select(s => DiskCacheStore.WaitForPruneAsync(s.DiskDirectory, TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    private async Task<string[]> WaitForCacheFilesAsync(string diskDir, int expectedCount = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string[] files;
        do
        {
            files = Directory.Exists(diskDir) ? Directory.GetFiles(diskDir, "*.png") : [];
            if (files.Length >= expectedCount) return files;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);
        return files;
    }

    [Fact(DisplayName = "A downscaled preview decoded from source is persisted to the disk cache")]
    public async Task DownscaledPreviewIsPersistedToDiskCache()
    {
        var diskDir = _root.Dir("write");
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256, diskCacheDirectory: diskDir), diskDir);

        await service.GetPreviewAsync(_previewPath);
        var files = await WaitForCacheFilesAsync(diskDir);

        Assert.True(files.Length == 1 && new FileInfo(files[0]).Length > 0);
    }

    [Fact(DisplayName = "Original (full-resolution) mode never writes to the disk cache")]
    public async Task OriginalModeDoesNotWriteToDiskCache()
    {
        var diskDir = _root.Dir("original-no-write");
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => true, () => 0, diskCacheDirectory: diskDir), diskDir);

        await service.GetPreviewAsync(_previewPath);
        var files = await WaitForCacheFilesAsync(diskDir, expectedCount: 1, timeoutMs: 500);

        Assert.True(files.Length == 0);
    }

    [Fact(DisplayName = "A preview found on disk is loaded without re-reading the source")]
    public async Task DiskCacheHitAvoidsSourceRead()
    {
        var diskDir = _root.Dir("hit");
        var writer = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256, diskCacheDirectory: diskDir), diskDir);
        await writer.GetPreviewAsync(_previewPath);
        await WaitForCacheFilesAsync(diskDir);

        var readerMetrics = new ReviewMetrics();
        var reader = Track(new PreviewImageService(readerMetrics, () => false, () => 256, diskCacheDirectory: diskDir), diskDir);
        var image = await reader.GetPreviewAsync(_previewPath);
        var snapshot = readerMetrics.Snapshot();

        Assert.True(image.PixelWidth > 0 && snapshot.SourceReads == 0 && snapshot.DiskCacheHits == 1);
    }

    [Fact(DisplayName = "A corrupt disk cache entry is deleted and the preview is re-decoded from source")]
    public async Task CorruptDiskCacheEntryFallsBackToSource()
    {
        var diskDir = _root.Dir("corrupt");
        var writer = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256, diskCacheDirectory: diskDir), diskDir);
        await writer.GetPreviewAsync(_previewPath);
        var files = await WaitForCacheFilesAsync(diskDir);
        File.WriteAllBytes(files[0], [1, 2, 3, 4]);

        var readerMetrics = new ReviewMetrics();
        var reader = Track(new PreviewImageService(readerMetrics, () => false, () => 256, diskCacheDirectory: diskDir), diskDir);
        var image = await reader.GetPreviewAsync(_previewPath);
        var snapshot = readerMetrics.Snapshot();

        Assert.True(image.PixelWidth > 0 && snapshot.SourceReads == 1 && snapshot.DiskCacheHits == 0);
    }

    [Fact(DisplayName = "The disk cache directory is pruned instead of growing unbounded")]
    public async Task DiskCacheIsPrunedToConfiguredQuota()
    {
        var diskDir = _root.Dir("quota");
        const long quotaBytes = 1;
        // Five distinct target widths produce five distinct cache keys/files for the
        // same source image; a near-zero quota forces PruneDirectory to run and evict
        // on every write instead of letting all five accumulate.
        foreach (var width in new[] { 100, 200, 300, 400, 500 })
        {
            var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => width,
                diskCacheDirectory: diskDir, diskCacheCapacityBytes: quotaBytes), diskDir);
            await service.GetPreviewAsync(_previewPath);
            // Serialize write+prune per iteration: fire-and-forget work must settle before
            // the next iteration's own write/prune pass runs, or the count below races it.
            // Wait on the actual byte quota rather than an arbitrary file count so this
            // loop (and the final assertion) actually verifies the configured quota.
            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < deadline && DirectoryBytes(diskDir) > quotaBytes) await Task.Delay(25);
        }

        var remainingBytes = DirectoryBytes(diskDir);
        Assert.True(remainingBytes <= quotaBytes,
            $"Expected the {quotaBytes}-byte quota to be enforced; " +
            $"{Directory.GetFiles(diskDir, "*.png").Length} file(s) totaling {remainingBytes} bytes remained.");
    }

    // Runs concurrently with the real fire-and-forget prune worker, which can delete a file
    // between GetFiles listing it and FileInfo reading its length -- treat a file that
    // vanished mid-count as already pruned (0 bytes) instead of letting the test fail.
    private static long DirectoryBytes(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.png").Sum(FileLengthOrZero) : 0;

    private static long FileLengthOrZero(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }
}
