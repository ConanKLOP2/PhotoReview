using System.Diagnostics;
using System.IO;

namespace PhotoReview.App;

/// <summary>
/// Owns background preload: the cancellation lifetime, the worker slots, the priority
/// version/center used to rebuild the preload order, and the set of keys this scheduler
/// warmed (so the viewer can record a preload hit).  Extracted from MainWindow; it takes
/// its catalog snapshot, folder size and configuration from the caller instead of
/// reaching into MainWindow fields.
/// </summary>
public sealed class PreloadScheduler : IDisposable
{
    private readonly PreviewImageService _previewService;
    private readonly ReviewMetrics _metrics;
    private readonly Func<string[]> _snapshotFiles;
    private readonly Func<long> _totalSourceBytes;
    private readonly long _fullFolderRamThresholdBytes;
    private readonly double _memoryLoadLimit;

    private CancellationTokenSource _preloadCts = new();
    private readonly SemaphoreSlim _preloadSlots = new(AppConstants.PreloadWorkerCount, AppConstants.PreloadWorkerCount);
    private Task? _preloadSchedulerTask;
    private CancellationTokenSource? _preloadSchedulerCts;
    private int _preloadCenter;
    private long _preloadPriorityVersion;
    // Written from concurrent PreloadOneAsync worker tasks (PreloadWorkerCount at once)
    // and read/cleared from the caller's thread; a plain HashSet is not thread-safe
    // against that, so every access goes through _preloadedKeysGate.
    private readonly HashSet<ImageCacheKey> _preloadedKeys = [];
    private readonly object _preloadedKeysGate = new();
    private readonly object _preloadCtsGate = new();
    private bool _disposed;

    public PreloadScheduler(
        PreviewImageService previewService,
        ReviewMetrics metrics,
        Func<string[]> snapshotFiles,
        Func<long> totalSourceBytes,
        long fullFolderRamThresholdBytes,
        double memoryLoadLimit)
    {
        _previewService = previewService;
        _metrics = metrics;
        _snapshotFiles = snapshotFiles;
        _totalSourceBytes = totalSourceBytes;
        _fullFolderRamThresholdBytes = fullFolderRamThresholdBytes;
        _memoryLoadLimit = memoryLoadLimit;
    }

    /// <summary>Cancels in-flight preload work. The next <see cref="PreloadAroundAsync"/> starts a fresh lifetime.</summary>
    public void Cancel()
    {
        if (Volatile.Read(ref _disposed)) return;
        lock (_preloadCtsGate) _preloadCts.Cancel();
    }

    /// <summary>Drops the warmed-key set (folder reload / cache clear).</summary>
    public void ClearPreloadedKeys() { lock (_preloadedKeysGate) _preloadedKeys.Clear(); }

    /// <summary>Removes warmed keys for one normalized (full, upper-invariant) path.</summary>
    public void RemovePreloadedKeysForPath(string normalizedPath)
    {
        lock (_preloadedKeysGate)
            _preloadedKeys.RemoveWhere(key => string.Equals(key.Path, normalizedPath, StringComparison.Ordinal));
    }

    /// <summary>True when this key was warmed by preload; consumes the entry.</summary>
    public bool TryConsumePreloadedKey(ImageCacheKey key) { lock (_preloadedKeysGate) return _preloadedKeys.Remove(key); }

    public Task PreloadAroundAsync(int center)
    {
        // Disposed schedulers must stay dead: without this check, a call here
        // would resurrect a new CancellationTokenSource and background loop.
        if (Volatile.Read(ref _disposed)) return Task.CompletedTask;
        // Navigation changes priority, but an already running decode is useful
        // and must remain available to ShowImageAsync through the in-flight map.
        CancellationTokenSource cts;
        lock (_preloadCtsGate)
        {
            if (_preloadCts.IsCancellationRequested)
            {
                _preloadCts.Dispose();
                _preloadCts = new CancellationTokenSource();
            }
            cts = _preloadCts;
        }
        Volatile.Write(ref _preloadCenter, center);
        Interlocked.Increment(ref _preloadPriorityVersion);
        if (_preloadSchedulerTask is { IsCompleted: false } &&
            ReferenceEquals(_preloadSchedulerCts, cts)) return _preloadSchedulerTask;
        _preloadSchedulerCts = cts;
        _preloadSchedulerTask = RunPreloadSchedulerAsync(_snapshotFiles(), cts.Token);
        return _preloadSchedulerTask;
    }

    private async Task RunPreloadSchedulerAsync(string[] files, CancellationToken cancellationToken)
    {
        const int workers = AppConstants.PreloadWorkerCount; // must match _preloadSlots capacity above
        var running = new Dictionary<Task, string>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenVersion = -1L;
        IEnumerator<int>? order = null;
        var examinedSinceYield = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var currentVersion = Interlocked.Read(ref _preloadPriorityVersion);
                if (seenVersion != currentVersion)
                {
                    order?.Dispose();
                    order = PreloadOrderService.Build(Volatile.Read(ref _preloadCenter), files.Length,
                        _totalSourceBytes() < _fullFolderRamThresholdBytes).GetEnumerator();
                    seenVersion = currentVersion;
                }
                while (running.Count < workers && order!.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!HasPreloadHeadroom())
                    {
                        if (AppLog.Enabled)
                        {
                            var memory = PhysicalMemory.GetSnapshot();
                            AppLog.Info($"Preload paused for memory: queued={queued.Count} cacheCount={_previewService.CacheCount} cacheBytes={_previewService.CacheBytes} availableBytes={memory?.AvailableBytes} loadPercent={memory?.LoadPercent}");
                        }
                        return;
                    }
                    var path = files[order.Current];
                    if (queued.Contains(path) || _previewService.TryGetCachedPreview(path, out _)) continue;
                    queued.Add(path);
                    running.Add(PreloadOneAsync(path, cancellationToken), path);
                    // Yield only after actual queue work; give input/rendering a
                    // chance without limiting every batch to two decodes.
                    if (++examinedSinceYield >= workers)
                    {
                        examinedSinceYield = 0;
                        if (AppLog.Enabled)
                        {
                            var memory = PhysicalMemory.GetSnapshot();
                            AppLog.Info($"Preload progress: queued={queued.Count} active={running.Count} cacheCount={_previewService.CacheCount} cacheBytes={_previewService.CacheBytes} availableBytes={memory?.AvailableBytes}");
                        }
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                if (running.Count == 0) return;
                var finished = await Task.WhenAny(running.Keys);
                running.Remove(finished);
                await finished;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        // Any other exception (unexpected cancellation source, or a genuine
        // failure in PreloadOrderService/PhysicalMemory) would otherwise escape
        // unobserved once the discarded fire-and-forget task
        // (`_ = PreloadAroundAsync(...)`) is garbage collected.
        catch (Exception ex) { AppLog.Error("Preload scheduler failed", ex); }
        finally { order?.Dispose(); }
    }

    private bool HasPreloadHeadroom()
    {
        return PhysicalMemory.HasHeadroom(_memoryLoadLimit);
    }

    private async Task PreloadOneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var queueWait = Stopwatch.StartNew();
            await _preloadSlots.WaitAsync(cancellationToken);
            _metrics.RecordQueueWait(queueWait.ElapsedMilliseconds);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!HasPreloadHeadroom()) return;
                var key = _previewService.GetCurrentCacheKey(path);
                await _previewService.GetPreviewAsync(path, key);
                if (_previewService.TryGetCachedPreview(key, out _)) lock (_preloadedKeysGate) _preloadedKeys.Add(key);
            }
            finally { _preloadSlots.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
        catch (Exception ex) { AppLog.Error($"Preload failed: {path}", ex); }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        lock (_preloadCtsGate)
        {
            _preloadCts.Cancel();
            _preloadCts.Dispose();
        }
        _preloadSlots.Dispose();
    }
}
