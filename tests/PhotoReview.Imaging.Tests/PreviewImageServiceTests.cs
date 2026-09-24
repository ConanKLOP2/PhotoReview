using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// PreviewImageService owns decode + the bounded RAM cache and needs no WPF Window, so the
/// decode/cache/metrics contract is driven for real.
/// </summary>
[Trait("Category", "Slow")]
#pragma warning disable CA1001 // _root (TempRoot) is disposed in DisposeAsync below via IAsyncLifetime, which xUnit invokes automatically; CA1001 doesn't recognize that async disposal pattern
public sealed class PreviewImageServiceTests : IAsyncLifetime
#pragma warning restore CA1001
{
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
        File.WriteAllBytes(_previewPath, TestImages.PreviewPng);
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
        await Task.WhenAll(_services.Select(s => s.Service.WaitForPruneAsync(TimeSpan.FromSeconds(5))));
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

    [Fact(DisplayName = "A source open is recorded once per decode from source, not per cache hit")]
    public async Task SourceOpenIsRecordedOncePerSourceDecode()
    {
        await _service.GetPreviewAsync(_previewPath);
        await _service.GetPreviewAsync(_previewPath);

        var snapshot = _metrics.Snapshot();

        Assert.Equal(1, snapshot.SourceOpenCount);
        var top = Assert.Single(snapshot.TopSourceOpens);
        Assert.Equal(_previewPath, top.Path);
        Assert.Equal(1, top.Count);
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
        // EvictCachedPath only drops the RAM-cached bitmap (see
        // "Evicting a path drops its decoded bitmap from the preview cache" above); it never
        // touches the disk cache. The shared _service is in downscaled mode, so its first
        // GetPreviewAsync below fires a background persist to disk (PR-029); if that
        // fire-and-forget write wins its race against the second GetPreviewAsync call, the
        // "fresh" request is served from the disk cache instead of the source, and
        // SourceReads stays at 1 -- flaky under parallel test execution. Original-loading-mode
        // services never persist to disk at all (see
        // "Original (full-resolution) mode never writes to the disk cache" in
        // PreviewImageServiceDiskCacheTests), so a dedicated original-mode service here
        // removes that race entirely and keeps the assertion exact.
        var diskCache = _root.Dir("disk-cache-eviction");
        var metrics = new ReviewMetrics();
        var service = Track(new PreviewImageService(metrics, () => true, () => 512, diskCacheDirectory: diskCache), diskCache);

        await service.GetPreviewAsync(_previewPath);
        service.EvictCachedPath(_previewPath);
        await service.GetPreviewAsync(_previewPath);
        Assert.True(metrics.Snapshot().SourceReads == 2 && service.CacheCount == 1);
    }

    [Fact(DisplayName = "Eviction in downscaled mode forces fresh source read when disk cache is cleared")]
    public async Task EvictionInDownscaledModeForcesFreshSourceReadWhenDiskCacheCleared()
    {
        var diskCache = _root.Dir("disk-cache-eviction-downscaled");
        var metrics = new ReviewMetrics();
        var service = Track(new PreviewImageService(metrics, () => false, () => 512, diskCacheDirectory: diskCache), diskCache);

        await service.GetPreviewAsync(_previewPath);
        // Wait for background persistence to finish writing to disk cache
        await service.ShutdownPersistWorkersAsync();
        await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));

        // Delete disk cache files and evict RAM cache entry
        service.DiskStore.ClearDirectory();
        service.EvictCachedPath(_previewPath);

        // Second request must re-read from source since both RAM and disk cache are gone
        await service.GetPreviewAsync(_previewPath);
        Assert.True(metrics.Snapshot().SourceReads == 2 && service.CacheCount == 1);
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

    [Fact(DisplayName = "After a preview decode, GetOriginalDimensionsAsync for the same source needs no extra source open")]
    public async Task GetOriginalDimensionsAsyncAfterDecodeNeedsNoExtraSourceOpen()
    {
        // perf(dims): the decode below already learns the source's original (post-orientation)
        // dimensions for free (IDecodedImage.OriginalWidth/Height, seeded into
        // PreviewImageService's _originalDimensions cache by DecodeAndCacheAsync). A later
        // GetOriginalDimensionsAsync for the same source must be served from that seed instead of
        // opening the file again via decoder.ReadInfo -- SourceOpenCount must not increase.
        var key = _service.GetCurrentCacheKey(_previewPath);
        var decoded = await _service.GetPreviewAsync(_previewPath, key);
        var opensAfterDecode = _metrics.Snapshot().SourceOpenCount;

        var dimensions = await _service.GetOriginalDimensionsAsync(_previewPath, key);

        Assert.Equal(opensAfterDecode, _metrics.Snapshot().SourceOpenCount);
        Assert.Equal(decoded.OriginalWidth, dimensions.Width);
        Assert.Equal(decoded.OriginalHeight, dimensions.Height);
    }

    [Fact(DisplayName = "ClearCache also drops seeded original dimensions (next query reads the source again)")]
    public async Task ClearCacheDropsSeededOriginalDimensions()
    {
        var key = _service.GetCurrentCacheKey(_previewPath);
        await _service.GetPreviewAsync(_previewPath, key);

        _service.ClearCache();
        var opensBefore = _metrics.Snapshot().SourceOpenCount;
        await _service.GetOriginalDimensionsAsync(_previewPath, key);

        Assert.Equal(opensBefore + 1, _metrics.Snapshot().SourceOpenCount);
    }
}

/// <summary>
/// PreloadScheduler takes its memory-load limit by constructor injection, so the memory
/// pressure guard is driven for real: a 0.0 limit can never have headroom
/// </summary>
#pragma warning disable CA1001 // _root (TempRoot) is disposed in DisposeAsync below via IAsyncLifetime, which xUnit invokes automatically; CA1001 doesn't recognize that async disposal pattern
public sealed class PreloadSchedulerTests : IAsyncLifetime
#pragma warning restore CA1001
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
            File.WriteAllBytes(path, TestImages.PreviewPng);
            return path;
        }).ToArray();

        var singleFolder = _root.Dir("preload-single");
        _singleFiles = Enumerable.Range(0, 2).Select(i =>
        {
            var path = Path.Combine(singleFolder, $"single-{i}.png");
            File.WriteAllBytes(path, TestImages.PreviewPng);
            return path;
        }).ToArray();
    }

    private static CatalogEntry[] ToEntries(string[] files) => Array.ConvertAll(files, f => new CatalogEntry(f));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_services.Select(s => s.Service.ShutdownPersistWorkersAsync()));
        // See PreviewImageServiceTests.DisposeAsync: wait for each service's own
        // fire-and-forget prune pass(es) too, or _root.Dispose() below can race one.
        await Task.WhenAll(_services.Select(s => s.Service.WaitForPruneAsync(TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    // hasHeadroom defaults to "always available": these tests exercise the scheduler's
    // own priority/queueing logic and must not depend on how much RAM the test
    // machine actually has free (PhysicalMemory.HasHeadroom also floors on a fixed
    // 2 GiB reserve, which a constrained CI/dev box may never satisfy).
    private (ReviewMetrics Metrics, PreviewImageService Service, PreloadScheduler Scheduler) NewWarmScheduler(
        Func<double, bool>? hasHeadroom = null,
        int? workerCount = null)
    {
        var metrics = new ReviewMetrics();
        var diskDirectory = _root.Dir("disk-cache-" + Guid.NewGuid().ToString("N"));
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskDirectory);
        _services.Add((service, diskDirectory));
        var scheduler = new PreloadScheduler(service, metrics, () => ToEntries(_preloadFiles), () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: hasHeadroom ?? (_ => true),
            workerCountOverride: workerCount,
            uiScheduler: ImmediateUiScheduler.Instance);
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

    [Fact(DisplayName = "Navigating to a preloaded image needs 0 extra source opens to learn its original dimensions")]
    public async Task NavigatingToPreloadedImageNeedsNoExtraSourceOpenForOriginalDimensions()
    {
        // perf(dims): reproduces ImagePresenter's real sequence -- a background preload decodes
        // the next image (via IPreloadTarget.PreloadAsync -> the same DecodeAndCacheAsync a live
        // navigation uses), then "navigating" to it asks for its original dimensions (as
        // PresentAsync does for the status bar). That must be served from the seed
        // DecodeAndCacheAsync already recorded, with zero additional decoder.ReadInfo opens.
        var (metrics, service, scheduler) = NewWarmScheduler();
        using (scheduler)
        {
            await scheduler.PreloadAroundAsync(0);
            var preloadedPath = _preloadFiles[1];
            Assert.True(service.TryGetCachedPreview(preloadedPath, out var preloaded));
            var opensAfterPreload = metrics.Snapshot().SourceOpenCount;

            var key = service.GetCurrentCacheKey(preloadedPath);
            var dimensions = await service.GetOriginalDimensionsAsync(preloadedPath, key);

            Assert.Equal(opensAfterPreload, metrics.Snapshot().SourceOpenCount);
            Assert.Equal(preloaded.OriginalWidth, dimensions.Width);
            Assert.Equal(preloaded.OriginalHeight, dimensions.Height);
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
        using var scheduler = new PreloadScheduler(service, metrics, () => ToEntries(_singleFiles), () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: _ => true, uiScheduler: ImmediateUiScheduler.Instance);
        await scheduler.PreloadAroundAsync(0);
        var warmedKey = service.GetCurrentCacheKey(_singleFiles[1]);
        Assert.True(scheduler.TryConsumePreloadedKey(warmedKey) && !scheduler.TryConsumePreloadedKey(warmedKey));
    }

    [Fact(DisplayName = "Preload without Dispatcher continues beyond first batch and loads all files")]
    public async Task PreloadWithoutDispatcherContinuesBeyondFirstBatchAndLoadsAllFiles()
    {
        // Issue detected in D10: when no Dispatcher was present, Dispatcher.Yield() threw
        // and stopped preload after the first batch of workers. ImmediateUiScheduler fixes this.
        var batchFiles = Enumerable.Range(0, 6).Select(i =>
        {
            var path = Path.Combine(_root.Dir("preload-batch"), $"batch-{i}.png");
            File.WriteAllBytes(path, TestImages.PreviewPng);
            return path;
        }).ToArray();

        var metrics = new ReviewMetrics();
        var diskDirectory = _root.Dir("disk-cache-batch");
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: diskDirectory);
        _services.Add((service, diskDirectory));

        // 2 workers, 6 files -> requires multiple batch yields
        var options = new PreloadOptions(WorkerCount: 2, MemoryLoadLimit: 1.0);
        using var scheduler = new PreloadScheduler(service, metrics, () => ToEntries(batchFiles), () => 0L,
            options: options, memoryProbe: new FakeMemoryProbe(true), uiScheduler: ImmediateUiScheduler.Instance);

        await scheduler.PreloadAroundAsync(0);

        // Files around center 0 should be cached across multiple batches
        Assert.True(service.CacheCount >= 5,
            $"Expected at least 5 cached files, but got {service.CacheCount}");
    }
}

/// <summary>
/// PreviewImageService's disk cache (PR-029): a decoded preview is persisted in the
/// background and served without a source read on the next request, scoped to
/// downscaled previews only, with corruption recovery falling back to the source.
/// The disk directory is always overridden to a TempRoot so these tests never touch
/// the developer's real %LocalAppData%\PhotoReview\cache.
/// </summary>
[Trait("Category", "Slow")]
#pragma warning disable CA1001 // _root (TempRoot) is disposed in DisposeAsync below via IAsyncLifetime, which xUnit invokes automatically; CA1001 doesn't recognize that async disposal pattern
public sealed class PreviewImageServiceDiskCacheTests : IAsyncLifetime
#pragma warning restore CA1001
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
        File.WriteAllBytes(_previewPath, TestImages.OpaquePng); // opaque: alpha previews are (correctly) never disk-cached, see IMG-01
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
        await Task.WhenAll(_services.Select(s => s.Service.WaitForPruneAsync(TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    private static async Task<string[]> WaitForCacheFilesAsync(string diskDir, int expectedCount = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string[] files;
        // TC09: poll a condition with a deadline instead of asserting after a fixed delay.
        do
        {
            files = Directory.Exists(diskDir) ? Directory.GetFiles(diskDir, "*.pv4") : [];
            if (files.Length >= expectedCount) return files;
            await Task.Delay(10); // not Task.Yield: a yield loop busy-spins a pool thread and starved the code under test on 2-vCPU CI
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

    [Fact(DisplayName = "A transparent PNG preview is never persisted to the disk cache and keeps its alpha")]
    public async Task TransparentPng_IsNotPersistedToDiskCache()
    {
        var transparentPath = Path.Combine(_root.Dir("source-alpha"), "transparent.png");
        File.WriteAllBytes(transparentPath, TestImages.TransparentPng);
        var diskDir = _root.Dir("alpha-no-write");
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: diskDir), diskDir);

        var first = await service.GetPreviewAsync(transparentPath);
        await service.ShutdownPersistWorkersAsync(); // drains every queued persist deterministically
        var written = Directory.GetFiles(diskDir, "*", SearchOption.AllDirectories);

        service.ClearCache(); // RAM tier only; re-fetching must still yield alpha (from source, since disk is empty)
        var second = await service.GetPreviewAsync(transparentPath);

        Assert.True(first.Downscaled && second.Downscaled);
        Assert.True(PreviewCacheFile.HasAlpha(second));
        Assert.Empty(written.Select(f => Path.GetRelativePath(diskDir, f)));
    }

    [Fact(DisplayName = "An opaque PNG preview is still persisted to the disk cache")]
    public async Task OpaquePng_IsStillPersisted()
    {
        var diskDir = _root.Dir("opaque-write");
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: diskDir), diskDir);

        var image = await service.GetPreviewAsync(_previewPath);
        var files = await WaitForCacheFilesAsync(diskDir);

        Assert.True(image.Downscaled);
        Assert.Single(files);
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

    [Fact(DisplayName = "A disk cache entry with a mismatched format version is a miss and is re-decoded from source")]
    public async Task DiskCacheEntryWithMismatchedVersionIsMiss()
    {
        // perf(cache) v4: the whole point of the header's version byte is that bumping the cache
        // key/file version (e.g. a future v5) never needs to parse or migrate an older layout --
        // every entry stamped with a different version is simply ignored as if it didn't exist.
        var diskDir = _root.Dir("cache-version");
        var writer = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256, diskCacheDirectory: diskDir), diskDir);
        await writer.GetPreviewAsync(_previewPath);
        var files = await WaitForCacheFilesAsync(diskDir);
        var bytes = File.ReadAllBytes(files[0]);
        bytes[4] = unchecked((byte)(PreviewCacheFile.CurrentVersion + 1)); // offset 4 = version byte
        File.WriteAllBytes(files[0], bytes);

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
            // Each iteration's service is short-lived: shut its persist worker down so the
            // write itself is guaranteed complete (ShutdownPersistWorkersAsync only returns
            // after the worker has processed the write and called SchedulePrune for it -- see
            // RunPersistWorkerAsync), then wait out the prune that write scheduled, before
            // moving on to the next iteration's write. A fixed poll timeout here instead would
            // race the next iteration's write under heavy parallel test load, where a slow
            // prune pass can still be mid-flight when the timeout trips, transiently leaving
            // the directory over quota when the loop moves on.
            var service = new PreviewImageService(new ReviewMetrics(), () => false, () => width,
                diskCacheDirectory: diskDir, diskCacheCapacityBytes: quotaBytes);
            await service.GetPreviewAsync(_previewPath);
            await service.ShutdownPersistWorkersAsync();
            Assert.True(await service.WaitForPruneAsync(TimeSpan.FromSeconds(10)),
                $"Prune for width={width} did not complete within the wait timeout.");
        }

        var remainingBytes = DirectoryBytes(diskDir);
        Assert.True(remainingBytes <= quotaBytes,
            $"Expected the {quotaBytes}-byte quota to be enforced; " +
            $"{Directory.GetFiles(diskDir, "*.pv4").Length} file(s) totaling {remainingBytes} bytes remained.");
    }

    // Runs concurrently with the real fire-and-forget prune worker, which can delete a file
    // between GetFiles listing it and FileInfo reading its length -- treat a file that
    // vanished mid-count as already pruned (0 bytes) instead of letting the test fail.
    private static long DirectoryBytes(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.pv4").Sum(FileLengthOrZero) : 0;

    private static long FileLengthOrZero(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }
}
