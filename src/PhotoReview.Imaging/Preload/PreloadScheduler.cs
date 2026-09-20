using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Imaging.Preload;

/// <summary>
/// Owns background preload: the cancellation lifetime, the worker slots, the priority
/// version/center used to rebuild the preload order, and the set of keys this scheduler
/// warmed (so the viewer can record a preload hit).
/// </summary>
public sealed class PreloadScheduler : IDisposable
{
    private readonly IPreloadTarget _target;
    private readonly ReviewMetrics _metrics;
    private readonly Func<string[]> _snapshotFiles;
    private readonly Func<long> _totalSourceBytes;
    private readonly PreloadOptions _options;
    private readonly IMemoryProbe _memoryProbe;
    private readonly IUiScheduler _ui;
    private readonly ILog _log;
    private readonly Func<string, CancellationToken, Task>? _prefetchSourceBytes;

    private readonly int _workerCount;
    private CancellationTokenSource _preloadCts = new();
    // D10: PHOTOREVIEW_DIAG_PRELOAD_WORKERS overrides how many decodes may run at once.
    // SemaphoreSlim requires maxCount > 0, so when _workerCount is 0 (no preload at all)
    // this is sized 1 but is never touched: PreloadAroundAsync below returns immediately
    // for _workerCount == 0, so RunPreloadSchedulerAsync/PreloadOneAsync never run and
    // never call WaitAsync/Release/CurrentCount on it.
    private readonly SemaphoreSlim _preloadSlots;
    private Task? _preloadSchedulerTask;
    private CancellationTokenSource? _preloadSchedulerCts;
    // A cancelled lifetime can still be draining while navigation starts a fresh one.
    // Keep every lifetime owned by this scheduler so Dispose waits for all of them.
    private readonly List<CancellationTokenSource> _preloadLifetimes = [];
    private readonly List<Task> _preloadLifetimeTasks = [];
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
        IPreloadTarget target,
        ReviewMetrics metrics,
        Func<string[]> snapshotFiles,
        Func<long> totalSourceBytes,
        PreloadOptions? options = null,
        IMemoryProbe? memoryProbe = null,
        IUiScheduler? uiScheduler = null,
        ILog? log = null,
        Func<string, CancellationToken, Task>? prefetchSourceBytes = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _snapshotFiles = snapshotFiles ?? throw new ArgumentNullException(nameof(snapshotFiles));
        _totalSourceBytes = totalSourceBytes ?? throw new ArgumentNullException(nameof(totalSourceBytes));
        _options = options ?? new PreloadOptions();
        _memoryProbe = memoryProbe ?? throw new ArgumentNullException(nameof(memoryProbe));
        _ui = uiScheduler ?? ImmediateUiScheduler.Instance;
        _log = log ?? NullLog.Instance;
        _prefetchSourceBytes = prefetchSourceBytes;

        // D10: precedence is the explicit option/parameter, then the diagnostic environment variable
        var diagOverride = Environment.GetEnvironmentVariable("PHOTOREVIEW_DIAG_PRELOAD_WORKERS");
        if (int.TryParse(diagOverride, out var parsedWorkers) && parsedWorkers >= 0 && parsedWorkers <= 16)
        {
            _workerCount = parsedWorkers;
        }
        else
        {
            _workerCount = _options.WorkerCount;
        }

        _preloadSlots = new SemaphoreSlim(Math.Max(1, _workerCount), Math.Max(1, _workerCount));
        _preloadLifetimes.Add(_preloadCts);
    }

    public PreloadScheduler(
        IPreloadTarget target,
        ReviewMetrics metrics,
        Func<string[]> snapshotFiles,
        Func<long> totalSourceBytes,
        long fullFolderRamThresholdBytes,
        double memoryLoadLimit,
        Func<double, bool>? hasHeadroom = null,
        IMemoryProbe? memoryProbe = null,
        int? workerCountOverride = null,
        IUiScheduler? uiScheduler = null,
        ILog? log = null,
        Func<string, CancellationToken, Task>? prefetchSourceBytes = null)
        : this(target, metrics, snapshotFiles, totalSourceBytes,
            new PreloadOptions(
                WorkerCount: workerCountOverride ?? DiagOptionsWorkers() ?? 8,
                MemoryLoadLimit: memoryLoadLimit,
                FullFolderThresholdBytes: fullFolderRamThresholdBytes),
            memoryProbe ?? (hasHeadroom is not null
                ? new DelegateMemoryProbe(hasHeadroom)
                : throw new ArgumentNullException(nameof(memoryProbe), "A real memory probe or an explicit test override is required.")),
            uiScheduler,
            log,
            prefetchSourceBytes)
    {
    }

    private static int? DiagOptionsWorkers()
    {
        var diagOverride = Environment.GetEnvironmentVariable("PHOTOREVIEW_DIAG_PRELOAD_WORKERS");
        return int.TryParse(diagOverride, out var parsed) && parsed >= 0 && parsed <= 16 ? parsed : null;
    }

    /// <summary>Cancels in-flight preload work. The next <see cref="PreloadAroundAsync"/> starts a fresh lifetime.</summary>
    public void Cancel()
    {
        lock (_preloadCtsGate)
        {
            if (_disposed) return;
            _preloadCts.Cancel();
        }
        if (PhotoReviewPerf.Log.IsEnabled()) PhotoReviewPerf.Log.PreloadCancel("cancel");
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
        // D10: PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0 means no preload at all. Returning here
        // (before touching _preloadCts/_preloadCenter/_preloadPriorityVersion) keeps this a
        // true no-op: no scheduler task is created and _preloadSlots is never waited on.
        if (_workerCount == 0) return Task.CompletedTask;
        // Navigation changes priority, but an already running decode is useful
        // and must remain available to ShowImageAsync through the in-flight map.
        CancellationTokenSource cts;
        lock (_preloadCtsGate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_preloadCts.IsCancellationRequested)
            {
                _preloadCts = new CancellationTokenSource();
                _preloadLifetimes.Add(_preloadCts);
            }
            cts = _preloadCts;
            Volatile.Write(ref _preloadCenter, center);
            Interlocked.Increment(ref _preloadPriorityVersion);
            if (_preloadSchedulerTask is { IsCompleted: false } &&
                ReferenceEquals(_preloadSchedulerCts, cts)) return _preloadSchedulerTask;
            _preloadSchedulerCts = cts;
            _preloadSchedulerTask = RunPreloadSchedulerAsync(_snapshotFiles(), cts.Token);
            _preloadLifetimeTasks.Add(_preloadSchedulerTask);
            return _preloadSchedulerTask;
        }
    }

    private async Task RunPreloadSchedulerAsync(string[] files, CancellationToken cancellationToken)
    {
        var workers = _workerCount;
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
                    var sourceBytes = _totalSourceBytes();
                    var wholeFolder = RamBudgetPolicy.ShouldPreloadWholeFolder(sourceBytes,
                        _options.FullFolderThresholdBytes, _memoryProbe, _options.ReserveBytes);
                    _log.Info($"Preload policy: sourceBytes={sourceBytes} capacityBytes={_options.FullFolderThresholdBytes} wholeFolder={wholeFolder}");
                    order = PreloadOrderService.Build(Volatile.Read(ref _preloadCenter), files.Length,
                        wholeFolder).GetEnumerator();
                    seenVersion = currentVersion;
                }
                while (running.Count < workers && order!.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // GlobalMemoryStatusEx is a syscall; only re-check on the same
                    // cadence as the progress log below, not on every candidate.
                    if (examinedSinceYield == 0 && !HasPreloadHeadroom())
                    {
                        var memory = _memoryProbe.GetSnapshot();
                        _log.Info($"Preload paused for memory: queued={queued.Count} cacheCount={_target.CacheCount} cacheBytes={_target.CacheBytes} availableBytes={memory?.AvailableBytes} loadPercent={memory?.LoadPercent}");
                        // GlobalMemoryStatusEx is a syscall: only taken while tracing (-1 = unavailable).
                        if (PhotoReviewPerf.Log.IsEnabled())
                        {
                            PhotoReviewPerf.Log.PreloadPaused(memory is { } m ? (int)m.LoadPercent : -1,
                                memory is { } a ? (long)(a.AvailableBytes / (1024 * 1024)) : -1);
                        }
                        return;
                    }
                    var path = files[order.Current];
                    if (queued.Contains(path) || _target.TryGetCachedPreview(path)) continue;
                    queued.Add(path);
                    running.Add(PreloadOneAsync(path, cancellationToken), path);
                    // Yield only after actual queue work; give input/rendering a
                    // chance without limiting every batch to two decodes.
                    if (++examinedSinceYield >= workers)
                    {
                        examinedSinceYield = 0;
                        var memory = _memoryProbe.GetSnapshot();
                        _log.Info($"Preload progress: queued={queued.Count} active={running.Count} cacheCount={_target.CacheCount} cacheBytes={_target.CacheBytes} availableBytes={memory?.AvailableBytes}");
                        // Never yield through a UI Dispatcher here. Dispose is called by the
                        // window's Closed handler and synchronously drains this task; a queued
                        // Dispatcher continuation would deadlock against that same UI thread.
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (running.Count == 0) return;
                var finished = await Task.WhenAny(running.Keys);
                running.Remove(finished);
                await finished;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation can arrive while WhenAny/await is between worker
            // completions. Drain every task still tracked by this scheduler before
            // reporting completion so Dispose() does not return while a worker is
            // still unwinding its target cancellation/finally path.
            await DrainWorkersAsync(running.Keys).ConfigureAwait(false);
        }
        // Any other exception (unexpected cancellation source, or a genuine
        // failure in PreloadOrderService/PhysicalMemory) would otherwise escape
        // unobserved once the discarded fire-and-forget task
        // (`_ = PreloadAroundAsync(...)`) is garbage collected.
        catch (Exception ex)
        {
            _log.Error("Preload scheduler failed", ex);
            await DrainWorkersAsync(running.Keys).ConfigureAwait(false);
        }
        finally { order?.Dispose(); }
    }

    private static async Task DrainWorkersAsync(IEnumerable<Task> workers)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private bool HasPreloadHeadroom() => _memoryProbe.HasHeadroom(_options.MemoryLoadLimit, _options.ReserveBytes);

    private async Task PreloadOneAsync(string path, CancellationToken cancellationToken)
    {
        // D04 perf: preload work is not tied to a navigation. Setting the AsyncLocal here only
        // affects calls made downstream from this method (DecodeAndCacheAsync), not the caller
        // of PreloadAroundAsync or the scheduler loop above.
        var perf = PhotoReviewPerf.Log.IsEnabled();
        if (perf) PhotoReviewPerf.NavContext = -1;
        long perfEnqueue = perf ? Stopwatch.GetTimestamp() : 0;
        var slot = -1;
        await _preloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            slot = _workerCount - 1 - _preloadSlots.CurrentCount;
            var stopwatch = Stopwatch.StartNew();
            var queueWaitMs = perf ? PhotoReviewPerf.Ms(perfEnqueue) : 0;
            var pathId = perf ? PhotoReviewPerf.PathId(path) : "";
            // If another task already decoded it while this one waited in the semaphore queue, skip.
            if (_target.TryGetCachedPreview(path))
            {
                stopwatch.Stop();
                if (perf) PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, "skipped", stopwatch.Elapsed.TotalMilliseconds);
                return;
            }
            try
            {
                if (_prefetchSourceBytes is not null)
                    await _prefetchSourceBytes(path, cancellationToken).ConfigureAwait(false);
                var beforeReads = _metrics.Snapshot().SourceReads;
                await _target.PreloadAsync(path, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                var key = _target.GetCurrentCacheKey(path);
                var isHit = _target.TryGetCachedPreview(key);
                if (isHit) lock (_preloadedKeysGate) _preloadedKeys.Add(key);
                if (perf)
                {
                    var sourceRead = _metrics.Snapshot().SourceReads > beforeReads;
                    var kind = sourceRead ? "decoded" : isHit ? "hit" : "miss";
                    PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, kind, stopwatch.Elapsed.TotalMilliseconds);
                }
            }
            catch (IOException ex)
            {
                _log.Error($"Preload failed: {path}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error($"Preload failed: {path}", ex);
            }
        }
        finally
        {
            _preloadSlots.Release();
        }
    }

    public void Dispose()
    {
        Task[] lifetimeTasks;
        CancellationTokenSource[] lifetimeCts;
        lock (_preloadCtsGate)
        {
            if (_disposed) return;
            _disposed = true;
            _preloadCts.Cancel();
            lifetimeTasks = _preloadLifetimeTasks.ToArray();
            lifetimeCts = _preloadLifetimes.ToArray();
        }

        // The scheduler and workers use ConfigureAwait(false), so draining cannot require the
        // caller's UI context. Keep synchronization primitives alive until every waiter/holder
        // has observed cancellation and released its slot.
        foreach (var schedulerTask in lifetimeTasks)
        {
            try { schedulerTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error("Preload scheduler failed during disposal", ex); }
        }

        foreach (var lifetimeCtsSource in lifetimeCts)
            lifetimeCtsSource.Dispose();
        _preloadSlots.Dispose();
    }
}

public sealed class DelegateMemoryProbe : IMemoryProbe
{
    private readonly Func<double, bool> _hasHeadroom;
    public DelegateMemoryProbe(Func<double, bool> hasHeadroom) => _hasHeadroom = hasHeadroom;
    public bool HasHeadroom(double maximumLoad, long reserveBytes) => _hasHeadroom(maximumLoad);
    public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
    public bool IsMemoryPressureHigh() => false;
    public long GetAvailableMemoryBytes() => 16L * 1024 * 1024 * 1024;
}

public sealed class FakeMemoryProbe : IMemoryProbe
{
    private readonly bool _hasHeadroom;
    public FakeMemoryProbe(bool hasHeadroom = true) => _hasHeadroom = hasHeadroom;
    public bool HasHeadroom(double maximumLoad, long reserveBytes) => _hasHeadroom;
    public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
    public bool IsMemoryPressureHigh() => !_hasHeadroom;
    public long GetAvailableMemoryBytes() => _hasHeadroom ? 16L * 1024 * 1024 * 1024 : 0;
}
