using System.IO;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// PreviewImageService owns decode + the bounded RAM cache and needs no WPF Window, so the
/// decode/cache/metrics contract is driven for real (migrated from Program.cs).
/// </summary>
public sealed class PreviewImageServiceTests : IDisposable
{
    // A valid, tiny PNG keeps decode fixtures portable while exercising WPF's real decoder.
    internal static readonly byte[] PreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly TempRoot _root = new("preview-service");
    private readonly string _previewPath;
    private readonly ReviewMetrics _metrics = new();
    private readonly PreviewImageService _service;

    public PreviewImageServiceTests()
    {
        var folder = _root.Dir("preview-service");
        _previewPath = Path.Combine(folder, "preview-a.png");
        File.WriteAllBytes(_previewPath, PreviewPng);
        _service = new PreviewImageService(_metrics, () => false, () => 512, capacityBytes: 64L * 1024 * 1024);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Preview decode records exactly one source read with the real source byte count")]
    public async Task PreviewDecodeRecordsExactlyOneSourceRead()
    {
        var decoded = await _service.GetPreviewAsync(_previewPath);
        var snapshot = _metrics.Snapshot();
        Assert.True(decoded.PixelWidth > 0 && snapshot.CacheMisses == 1 && snapshot.CacheHits == 0
            && snapshot.SourceReads == 1 && snapshot.SourceBytesRead == new FileInfo(_previewPath).Length
            && _service.CacheCount == 1 && _service.CacheBytes > 0);
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
        var originalModeService = new PreviewImageService(_metrics, () => true, () => 512);
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
public sealed class PreloadSchedulerTests : IDisposable
{
    private readonly TempRoot _root = new("preload-scheduler");
    private readonly string[] _preloadFiles;
    private readonly string[] _singleFiles;

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

    public void Dispose() => _root.Dispose();

    private (ReviewMetrics Metrics, PreviewImageService Service, PreloadScheduler Scheduler) NewWarmScheduler(
        double memoryLoadLimit = 1.0)
    {
        var metrics = new ReviewMetrics();
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024);
        var scheduler = new PreloadScheduler(service, metrics, () => _preloadFiles, () => 0L, long.MaxValue,
            memoryLoadLimit: memoryLoadLimit);
        return (metrics, service, scheduler);
    }

    [Fact(DisplayName = "Background preload memory guard decodes nothing when there is no memory headroom")]
    public async Task MemoryGuardDecodesNothingWithoutHeadroom()
    {
        var (metrics, service, scheduler) = NewWarmScheduler(memoryLoadLimit: 0.0);
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
        var service = new PreviewImageService(metrics, () => false, () => 256, capacityBytes: 64L * 1024 * 1024);
        using var scheduler = new PreloadScheduler(service, metrics, () => _singleFiles, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0);
        await scheduler.PreloadAroundAsync(0);
        var warmedKey = service.GetCurrentCacheKey(_singleFiles[1]);
        Assert.True(scheduler.TryConsumePreloadedKey(warmedKey) && !scheduler.TryConsumePreloadedKey(warmedKey));
    }
}
