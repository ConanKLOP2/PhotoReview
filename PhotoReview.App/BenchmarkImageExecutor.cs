using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

/// <summary>
/// Production-compatible image executor for benchmark runs. Routes every decode through
/// a real <see cref="PreviewImageService"/>/<see cref="PreloadScheduler"/> pair — the same
/// RAM cache, disk-cache probe/persist path and preload-priority scheduling the viewer
/// uses — instead of a benchmark-only cache, so P50/P95 reflect what the app actually does.
/// The disk cache lives in a dedicated scratch directory per run so a benchmark never
/// reads from or writes into the app's real %LocalAppData% preview cache.
/// </summary>
public sealed class BenchmarkImageExecutor : IAsyncDisposable
{
    private readonly BenchmarkProfile _profile;
    private readonly ReviewMetrics _metrics = new();
    private readonly PreviewImageService _previewService;
    private readonly PreloadScheduler _preloadScheduler;
    private readonly string _diskCacheDirectory;
    private Task? _lastPreloadTask;

    // hasHeadroom defaults to the real OS memory check (production behavior), exactly like
    // PreloadScheduler's own constructor -- tests inject a fixed answer so preload-warming
    // assertions don't depend on how much RAM the machine running them has free.
    public BenchmarkImageExecutor(BenchmarkProfile profile, string[] files, long totalSourceBytes,
        Func<double, bool>? hasHeadroom = null)
    {
        _profile = profile;
        _diskCacheDirectory = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Cache", Guid.NewGuid().ToString("N"));
        var isOriginal = string.Equals(profile.LoadingMode, "Original", StringComparison.OrdinalIgnoreCase);
        _previewService = new PreviewImageService(_metrics, () => isOriginal, () => profile.TargetWidth(),
            AppConstants.ImageCacheCapacityBytes, _diskCacheDirectory);
        _preloadScheduler = new PreloadScheduler(_previewService, _metrics, () => files, () => totalSourceBytes,
            AppConstants.ImageCacheCapacityBytes, AppConstants.PreloadMemoryLoadLimit, hasHeadroom);
    }

    // FirstFrame profiles always decode files[0] regardless of iteration index, to
    // measure cold first-decode latency. RAM eviction alone is no longer enough now
    // that a real disk cache is in play: a prior iteration's persisted PNG could
    // satisfy the next "cold" decode from disk instead of source, so the scratch
    // disk cache is cleared too (cheap: at most one small file for this path).
    public void EvictForColdDecode(string path)
    {
        _previewService.EvictCachedPath(path, normalized => _preloadScheduler.RemovePreloadedKeysForPath(normalized));
        _previewService.ClearDisk();
    }

    // GetPreviewAsync has no cancellation parameter (the production decode loop doesn't
    // either — WPF's synchronous BitmapDecoder can't be interrupted mid-frame), so
    // cancellation here only takes effect between iterations.
    public Task<BitmapImage> DecodeAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Mirrors MainWindow.ShowImageAsync: a preload hit only counts when the image was
        // already sitting in the RAM cache before this request (i.e. a prior
        // WarmPreloadAround call actually warmed it), not on every decode. Without this
        // check Preload/WarmNext reports always show zero hits, no matter how well the
        // real PreloadScheduler warmed the cache.
        var key = _previewService.GetCurrentCacheKey(path);
        if (_previewService.TryGetCachedPreview(key, out _) && _preloadScheduler.TryConsumePreloadedKey(key))
            _metrics.RecordPreloadHit();
        return _previewService.GetPreviewAsync(path, key);
    }

    /// <summary>Snapshot of source reads, cache hits, disk hits and preload events recorded
    /// so far, so callers can attach it to a <see cref="BenchmarkReport"/> instead of the
    /// report always carrying no metrics.</summary>
    public ReviewMetricsSnapshot Metrics => _metrics.Snapshot();

    /// <summary>
    /// Kicks off real background preload around this navigation center, exactly as
    /// MainWindow does right after presenting an image, so a later <see cref="DecodeAsync"/>
    /// for a neighboring index can land as a genuine preload hit instead of a cold decode.
    /// </summary>
    public void WarmPreloadAround(int center) => _lastPreloadTask = _preloadScheduler.PreloadAroundAsync(center);

    public async ValueTask DisposeAsync()
    {
        // Cancel first so the last WarmPreloadAround call (fire-and-forget by design —
        // production doesn't await it either) unwinds via its own OperationCanceledException
        // handling instead of still running when the scheduler/semaphore below are disposed.
        _preloadScheduler.Cancel();
        if (_lastPreloadTask is not null) { try { await _lastPreloadTask; } catch { } }
        _preloadScheduler.Dispose();
        // Only after every persist write has actually finished is it safe to delete the
        // scratch directory without racing a worker that's still writing into it.
        await _previewService.ShutdownPersistWorkersAsync();
        // ShutdownPersistWorkersAsync guarantees every write has issued its SchedulePrune
        // call, but that call is itself fire-and-forget -- wait for the prune pass(es) it
        // started to actually finish, or the delete below can race a worker still
        // enumerating/deleting files in this same directory.
        var pruneSettled = await DiskCacheStore.WaitForPruneAsync(_diskCacheDirectory, TimeSpan.FromSeconds(5));
        if (!pruneSettled)
        {
            // A timeout is not success: a prune worker may still be touching this
            // directory, so deleting it now could race that worker instead of just
            // leaving scratch data behind. Leave the directory for the OS temp cleaner
            // rather than risk a racy delete.
            AppLog.Error($"Benchmark disk cache prune did not finish in time; leaving scratch directory: {_diskCacheDirectory}");
            return;
        }
        try { if (Directory.Exists(_diskCacheDirectory)) Directory.Delete(_diskCacheDirectory, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
