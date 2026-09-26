using PhotoReview.Platform.Windows;
using PhotoReview.Core.Settings;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Benchmarking;

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
    private readonly WindowedPreloadTarget _preloadTarget;
    private Task? _lastPreloadTask;

    // hasHeadroom defaults to the real OS memory check (production behavior), exactly like
    // PreloadScheduler's own constructor -- tests inject a fixed answer so preload-warming
    // assertions don't depend on how much RAM the machine running them has free.
    public BenchmarkImageExecutor(BenchmarkProfile profile, string[] files, long totalSourceBytes,
        Func<double, bool>? hasHeadroom = null)
    {
        _profile = profile;
        _diskCacheDirectory = Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Cache", Guid.NewGuid().ToString("N"));
        var isOriginal = profile.LoadingMode == LoadingMode.Original;
        _previewService = new PreviewImageService(_metrics, () => isOriginal, () => profile.TargetWidth(),
            PerformanceOptions.ImageCacheCapacityBytes, _diskCacheDirectory,
            disableDiskCacheOverride: !profile.DiskCache);
        var catalogEntries = Array.ConvertAll(files, f => new PhotoReview.Core.Catalog.CatalogEntry(f));
        // PERF-01: the profile's NextWindow/PreviousWindow/FullFolder must reach the scheduler, otherwise every
        // named profile ran the same production window policy regardless of what it claims to measure.
        _preloadTarget = new WindowedPreloadTarget(_previewService, files, profile);
        _preloadScheduler = new PreloadScheduler(_preloadTarget, _metrics, () => catalogEntries, () => totalSourceBytes,
            options: new PreloadOptions(
                WorkerCount: profile.Workers,
                MemoryLoadLimit: PerformanceOptions.PreloadMemoryLoadLimit,
                ReserveBytes: profile.MemoryReserveBytes,
                // A profile without FullFolder must never escalate to a whole-folder pass, however small the folder.
                FullFolderThresholdBytes: profile.FullFolder ? _previewService.CapacityBytes : 0),
            memoryProbe: hasHeadroom is null ? WindowsMemoryProbe.Instance : new DelegateMemoryProbe(hasHeadroom),
            uiScheduler: ImmediateUiScheduler.Instance);
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
    public Task<IDecodedImage> DecodeAsync(string path, CancellationToken token = default)
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
    public void WarmPreloadAround(int center)
    {
        _preloadTarget.SetCenter(center);
        _lastPreloadTask = _preloadScheduler.PreloadAroundAsync(center);
    }

    /// <summary>The scheduler/logging settings this executor really applies for its profile (PERF-01).</summary>
    public BenchmarkEffectiveConfig EffectiveConfig => new(_profile.Workers, _profile.NextWindow, _profile.PreviousWindow,
        _profile.FullFolder, _profile.DetailedLogging, _profile.DiskCache);

    /// <summary>Test seam: whether the RAM cache currently holds a preview for this path.</summary>
    internal bool IsPreviewCached(string path) => _previewService.TryGetCachedPreview(_previewService.GetCurrentCacheKey(path), out _);

    /// <summary>Test seam: completes once the pass started by the last <see cref="WarmPreloadAround"/> has finished.</summary>
    internal Task WhenPreloadSettledAsync() => _lastPreloadTask ?? Task.CompletedTask;

    /// <summary>Test seam: the scratch disk-cache directory this executor writes into, so a teardown test can
    /// assert it is actually removed once <see cref="TeardownBackgroundTask"/> settles.</summary>
    internal string DiskCacheDirectory => _diskCacheDirectory;

    /// <summary>
    /// perf(bench-window): the background prune-then-delete pass <see cref="DisposeAsync"/> starts (never
    /// awaited by production code, which must not block the next profile on a slow prune). Tests await this
    /// to observe the scratch directory actually being removed once the prune settles.
    /// </summary>
    internal Task TeardownBackgroundTask { get; private set; } = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Cancel first so the last WarmPreloadAround call (fire-and-forget by design —
        // production doesn't await it either) unwinds via its own OperationCanceledException
        // handling instead of still running when the scheduler/semaphore below are disposed.
        _preloadScheduler.Cancel();
        if (_lastPreloadTask is not null) { try { await _lastPreloadTask; } catch { /* a failed background preload must not fail the benchmark teardown */ } }
        _preloadScheduler.Dispose();
        // Only after every persist write has actually finished is it safe to delete the
        // scratch directory without racing a worker that's still writing into it.
        await _previewService.ShutdownPersistWorkersAsync();
        // perf(bench-window): ShutdownPersistWorkersAsync guarantees every write has issued its
        // SchedulePrune call, but that call is itself fire-and-forget, and the prune pass it
        // started can take up to a few seconds on a large/slow disk. The OLD code awaited that
        // pass (bounded to 5 s) right here, so a slow prune held up the NEXT profile's startup
        // (the window runs profiles sequentially -- see BenchmarkWindow.RunAsync). Instead, wait
        // for the pass and delete the directory on a background task that this call does not
        // await: teardown itself returns as soon as persisted writes are flushed. The next
        // profile is safe to start immediately because every executor gets its own fresh
        // Guid-named scratch directory (see the constructor), so a still-running prune here can
        // never race a directory another profile is using.
        TeardownBackgroundTask = DeleteScratchDirectoryAfterPruneAsync();
    }

    private async Task DeleteScratchDirectoryAfterPruneAsync()
    {
        // Generous bound (not the old 5 s): this task no longer blocks anything, so there is no
        // reason to give up early. A prune that somehow never settles just means the scratch
        // directory is left for the OS temp cleaner, exactly like the old timeout path.
        var pruneSettled = await _previewService.WaitForPruneAsync(TimeSpan.FromMinutes(2));
        if (!pruneSettled)
        {
            FileLog.Default.Error($"Benchmark disk cache prune did not finish in time; leaving scratch directory: {_diskCacheDirectory}");
            return;
        }
        try { if (Directory.Exists(_diskCacheDirectory)) Directory.Delete(_diskCacheDirectory, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Effective per-profile settings the executor applies (recorded so a report can prove what was measured).</summary>
public sealed record BenchmarkEffectiveConfig(int Workers, int NextWindow, int PreviousWindow, bool FullFolder,
    bool DetailedLogging, bool DiskCache);

/// <summary>
/// Applies a profile's per-run process state (currently detailed logging) around a benchmark run. Shared by the
/// WPF benchmark window and the CLI so <c>logging-on</c>/<c>logging-off</c> differ in both front ends.
/// </summary>
public static class BenchmarkProfileScope
{
    /// <summary>Sets logging to <paramref name="profile"/>'s DetailedLogging; disposing restores the previous state.</summary>
    public static IDisposable ApplyLogging(BenchmarkProfile profile, Func<bool> getEnabled, Action<bool> setEnabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(getEnabled);
        ArgumentNullException.ThrowIfNull(setEnabled);
        var previous = getEnabled();
        setEnabled(profile.DetailedLogging);
        return new Restore(() => setEnabled(previous));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

/// <summary>
/// Restricts preload to the profile's window around the navigation center by reporting out-of-window
/// paths as already cached, so the scheduler never queues them. FullFolder profiles are not restricted.
/// The scheduler's own production window still caps the effective range (the smaller of the two applies).
/// </summary>
internal sealed class WindowedPreloadTarget : IPreloadTarget
{
    private readonly IPreloadTarget _inner;
    private readonly Dictionary<string, int> _indexByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _fullFolder;
    private readonly int _next;
    private readonly int _previous;
    private int _center;

    public WindowedPreloadTarget(IPreloadTarget inner, string[] files, BenchmarkProfile profile)
    {
        _inner = inner;
        _fullFolder = profile.FullFolder;
        _next = profile.NextWindow;
        _previous = profile.PreviousWindow;
        for (var i = 0; i < files.Length; i++) _indexByPath[System.IO.Path.GetFullPath(files[i])] = i;
    }

    public void SetCenter(int center) => Volatile.Write(ref _center, center);

    private bool OutsideWindow(string path)
    {
        if (_fullFolder || !_indexByPath.TryGetValue(path, out var index)) return false;
        var delta = index - Volatile.Read(ref _center);
        return delta > _next || -delta > _previous;
    }

    public bool TryGetCachedPreview(string path) => OutsideWindow(path) || _inner.TryGetCachedPreview(path);
    public bool TryGetCachedPreview(ImageCacheKey key) => OutsideWindow(key.Path) || _inner.TryGetCachedPreview(key);
    public Task PreloadAsync(string path, CancellationToken cancellationToken = default) => _inner.PreloadAsync(path, cancellationToken);
    public ImageCacheKey GetCurrentCacheKey(string path) => _inner.GetCurrentCacheKey(path);
    public ImageCacheKey GetCurrentCacheKey(PhotoReview.Core.Catalog.CatalogEntry entry) => _inner.GetCurrentCacheKey(entry);
    public int CacheCount => _inner.CacheCount;
    public long CacheBytes => _inner.CacheBytes;
    public int ActiveViewerDecodes => _inner.ActiveViewerDecodes;
    public long? CachedPreviewBytes(ImageCacheKey key) => _inner.CachedPreviewBytes(key);
    public bool HasDiskCachedPreview(ImageCacheKey key) => _inner.HasDiskCachedPreview(key);
}
