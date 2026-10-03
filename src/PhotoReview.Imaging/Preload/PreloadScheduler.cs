using System.Diagnostics;
using System.Globalization;
using System.IO;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

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
    // CatalogEntry (not string) so candidates can be filtered against the RAM cache via
    // GetCurrentCacheKey(entry), which reuses the folder scan's Length/LastWriteUtc instead of
    // stat-ing every candidate examined during a preload scan.
    private readonly Func<CatalogEntry[]> _snapshotEntries;
    // RV-I13: cheap change counter of the list behind _snapshotEntries (ReviewCatalog.StructuralVersion); null = never changes.
    private readonly Func<int>? _snapshotVersion;
    private readonly PreloadOptions _options;
    private readonly IMemoryProbe _memoryProbe;
    private readonly ILog _log;
    private readonly Func<string, CancellationToken, Task>? _prefetchSourceBytes;
    // perf(preload): direction + key-rate tracking; see NotifyNavigation and PreloadOrderService.Build.
    private readonly NavigationPace _pace;
    // Q-R17: measured preview sizes behind the whole-folder estimate.
    private readonly PreviewSizeSampler _sizes = new();
    // Paths the decoder refused as busy: cooldown (in order passes) + retry cap, see PreloadBusyBackoff.
    private readonly PreloadBusyBackoff _busyBackoff = new();
    // Concurrent preload decodes allowed to start while a viewer decode is running.
    private readonly int _viewerBusyWorkerLimit = Math.Max(2, Environment.ProcessorCount / 3);
    // Q-R29 option C-2: above this observed source-read wall time (ms, DecodeMillisecondsEwma), the link is
    // treated as slow enough that preload's own concurrency while the viewer's own decode is in flight drops
    // to SlowLinkViewerBusyWorkerLimit (see the comment at its use site). Local disk stays 15-50 ms P50
    // (R01-R04); a capped/slow link measured 1000+ ms per read (docs/refactoring/decisions/Q-R29-C2.md) --
    // comfortably on either side of this threshold.
    private const double SlowLinkDecodeMsThreshold = 200;
    // At most one preload read shares the link with the viewer's own read on a slow link (not 0: the
    // scheduler's own exit condition needs `running.Count < limit` reachable from 0 to keep making progress
    // once the viewer decode ends without waiting for a fresh navigation -- see the use site's comment).
    private const int SlowLinkViewerBusyWorkerLimit = 1;

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
    // Finished lifetimes (and their disposed CTS) are dropped again by PruneFinishedLifetimes, so a session of many
    // navigations does not accumulate one CTS and one task per navigation.
    private readonly List<CancellationTokenSource> _preloadLifetimes = [];
    private readonly List<(CancellationTokenSource Cts, Task Task)> _preloadLifetimeTasks = [];
    private int _preloadCenter;
    private long _preloadPriorityVersion;
    // Written from concurrent PreloadOneAsync worker tasks (PreloadWorkerCount at once)
    // and read/cleared from the caller's thread; a plain HashSet is not thread-safe
    // against that, so every access goes through _preloadedKeysGate.
    private readonly HashSet<ImageCacheKey> _preloadedKeys = [];
    private readonly object _preloadedKeysGate = new();
    // RV-I15: target box of the keys in _preloadedKeys (guarded by _preloadedKeysGate). Keys embed the box, so once
    // previews are cached at another box (window resize) the old keys can never match again: they are dropped.
    private DecodeBox _preloadedKeysBox;
    private readonly object _preloadCtsGate = new();
    // RV-I12: id of the newest scheduler run, and whether that run already passed its final exit check (it will never
    // look at the priority version again, so PreloadAroundAsync must start a new run instead of joining it).
    // RV-I13: the _snapshotVersion value that run's entries snapshot was taken at. All guarded by _preloadCtsGate.
    private long _schedulerRunId;
    private bool _schedulerRunExiting;
    private int _schedulerSnapshotVersion;
    private bool _disposed;

    public PreloadScheduler(
        IPreloadTarget target,
        ReviewMetrics metrics,
        Func<CatalogEntry[]> snapshotEntries,
        PreloadOptions? options = null,
        IMemoryProbe? memoryProbe = null,
        ILog? log = null,
        Func<string, CancellationToken, Task>? prefetchSourceBytes = null,
        NavigationPace? pace = null,
        Func<int>? snapshotVersion = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _pace = pace ?? new NavigationPace();
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _snapshotEntries = snapshotEntries ?? throw new ArgumentNullException(nameof(snapshotEntries));
        _options = options ?? new PreloadOptions();
        _memoryProbe = memoryProbe ?? throw new ArgumentNullException(nameof(memoryProbe));
        _log = log ?? NullLog.Instance;
        _prefetchSourceBytes = prefetchSourceBytes;
        _snapshotVersion = snapshotVersion;

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
        Func<CatalogEntry[]> snapshotEntries,
        long fullFolderRamThresholdBytes,
        double memoryLoadLimit,
        Func<double, bool>? hasHeadroom = null,
        IMemoryProbe? memoryProbe = null,
        int? workerCountOverride = null,
        ILog? log = null,
        Func<string, CancellationToken, Task>? prefetchSourceBytes = null,
        PreloadWindow? window = null,
        Func<int>? snapshotVersion = null)
        : this(target, metrics, snapshotEntries,
            new PreloadOptions(
                WorkerCount: workerCountOverride ?? DiagOptionsWorkers() ?? 8,
                MemoryLoadLimit: memoryLoadLimit,
                FullFolderThresholdBytes: fullFolderRamThresholdBytes)
            { Window = window ?? PreloadWindow.Default },
            memoryProbe ?? (hasHeadroom is not null
                ? new DelegateMemoryProbe(hasHeadroom)
                : throw new ArgumentNullException(nameof(memoryProbe), "A real memory probe or an explicit test override is required.")),
            log,
            prefetchSourceBytes,
            pace: null,
            snapshotVersion)
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
        WakeScheduler();
        if (PhotoReviewPerf.Log.IsEnabled()) PhotoReviewPerf.Log.PreloadCancel("cancel");
    }

    /// <summary>Drops the warmed-key set (folder reload / cache clear).</summary>
    public void ClearPreloadedKeys()
    {
        lock (_preloadedKeysGate) _preloadedKeys.Clear();
        _busyBackoff.Clear();
    }

    /// <summary>Removes warmed keys for one normalized (full, upper-invariant) path.</summary>
    public void RemovePreloadedKeysForPath(string normalizedPath)
    {
        lock (_preloadedKeysGate)
            _preloadedKeys.RemoveWhere(key => string.Equals(key.Path, normalizedPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when the current preload lifetime has no decode work in flight or queued (the scheduler
    /// loop only returns when its running set is empty; see <see cref="RunPreloadSchedulerAsync"/>).
    /// Best-effort: used by diagnostics/the perf harness to know a navigation has fully settled, not
    /// for correctness. A fresh scheduler (or one paused for memory headroom) reports idle too.
    /// </summary>
    public bool IsIdle
    {
        get
        {
            lock (_preloadCtsGate)
                return _preloadSchedulerTask is null || _preloadSchedulerTask.IsCompleted;
        }
    }

    /// <summary>True when this key was warmed by preload; consumes the entry.</summary>
    public bool TryConsumePreloadedKey(ImageCacheKey key) { lock (_preloadedKeysGate) return _preloadedKeys.Remove(key); }

    /// <summary>
    /// perf(preload): called at the START of every navigation (before its own decode), unlike
    /// <see cref="PreloadAroundAsync"/> which runs after the image is presented. Records the key
    /// timing/direction and moves the preload center immediately, so a running scheduler re-prioritizes
    /// on its next pass instead of preloading around a position the user already left. During a burst
    /// (lead &gt; 0) navigations are rarely presented, so this also (re)starts the scheduler itself.
    /// </summary>
    public void NotifyNavigation(int index)
    {
        if (_workerCount == 0) return;
        _pace.Record(index);
        lock (_preloadCtsGate)
        {
            if (_disposed) return;
            Volatile.Write(ref _preloadCenter, index);
            Interlocked.Increment(ref _preloadPriorityVersion);
        }
        WakeScheduler();
        if (CurrentShape().Lead > 0) _ = PreloadAroundAsync(index);
    }

    /// <summary>
    /// perf(preload): how long the viewer should wait before starting its own decode of a
    /// not-yet-cached image (zero outside a burst); see <see cref="NavigationPace.GetViewerStartDelay"/>.
    /// </summary>
    public TimeSpan GetViewerDecodeDelay() => _workerCount == 0
        ? TimeSpan.Zero
        : _pace.GetViewerStartDelay(_metrics.DecodeMillisecondsEwma);

    // perf(preload): completed (and replaced) whenever the priority version changes, so a scheduler
    // loop waiting for a worker to finish also wakes up to re-prioritize and fill idle slots at once.
    // Before, a loop with one long decode in flight ignored every navigation until that decode ended,
    // leaving the other workers idle for a whole decode time at the start of a burst.
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void WakeScheduler() =>
        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    // Direction of travel and burst lead (images to skip ahead) as of now.
    private (int Direction, int Lead) CurrentShape() => (_pace.Direction, _pace.GetLead(_metrics.DecodeMillisecondsEwma));

    public Task PreloadAroundAsync(int center)
    {
        // Not an async method: a throw of the snapshot callbacks (the catalog is being replaced while a window closes) would reach
        // the UI-thread caller synchronously. Preload is best effort -- log and do nothing, the next navigation re-kicks it.
        try
        {
            return PreloadAroundCore(center);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Preload kick failed; skipped", ex);
            return Task.CompletedTask;
        }
    }

    private Task PreloadAroundCore(int center)
    {
        // Disposed schedulers must stay dead: without this check, a call here
        // would resurrect a new CancellationTokenSource and background loop.
        // D10: PHOTOREVIEW_DIAG_PRELOAD_WORKERS=0 means no preload at all. Returning here
        // (before touching _preloadCts/_preloadCenter/_preloadPriorityVersion) keeps this a
        // true no-op: no scheduler task is created and _preloadSlots is never waited on.
        if (_workerCount == 0) return Task.CompletedTask;
        // No-op when NotifyNavigation already recorded this index (the normal App path); keeps
        // direction tracking working for callers that only ever call PreloadAroundAsync.
        _pace.Record(center);
        // RV-I13: read before the snapshot below is taken, so a change in between only costs one extra restart.
        var snapshotVersion = _snapshotVersion?.Invoke() ?? 0;
        // Navigation changes priority, but an already running decode is useful
        // and must remain available to ShowImageAsync through the in-flight map.
        CancellationTokenSource cts;
        lock (_preloadCtsGate)
        {
            if (_disposed) return Task.CompletedTask;
            if (snapshotVersion != _schedulerSnapshotVersion && _preloadSchedulerTask is { IsCompleted: false }
                && ReferenceEquals(_preloadSchedulerCts, _preloadCts) && !_preloadCts.IsCancellationRequested)
            {
                // RV-I13: the entries changed without a Cancel() (e.g. an undo re-inserted a file): the running loop
                // iterates a stale snapshot whose indices now name other files. Retire it exactly like Cancel() does.
                _preloadCts.Cancel();
                WakeScheduler();
            }
            if (_preloadCts.IsCancellationRequested)
            {
                PruneFinishedLifetimes();
                _preloadCts = new CancellationTokenSource();
                _preloadLifetimes.Add(_preloadCts);
            }
            cts = _preloadCts;
            Volatile.Write(ref _preloadCenter, center);
            Interlocked.Increment(ref _preloadPriorityVersion);
            if (_preloadSchedulerTask is { IsCompleted: false } &&
                ReferenceEquals(_preloadSchedulerCts, cts) && !_schedulerRunExiting)
            {
                WakeScheduler();
                return _preloadSchedulerTask;
            }
            _preloadSchedulerCts = cts;
            // ADR 0005: callers (ImagePresenter) are on the UI thread, so the loop's synchronous
            // prefix (order build + at most one worker batch of candidate starts, each a cache
            // lookup/stat before its decode Task.Run) runs there -- deliberately kept synchronous
            // so the first candidates are queued before the caller moves on. Every await in the
            // loop uses ConfigureAwait(false), so no continuation is ever posted to the Dispatcher
            // (Dispose drains this task synchronously on the UI thread).
            _preloadLifetimeTasks.RemoveAll(entry => entry.Task.IsCompleted);
            var runId = ++_schedulerRunId;
            _schedulerRunExiting = false;
            _schedulerSnapshotVersion = snapshotVersion;
            _preloadSchedulerTask = RunPreloadSchedulerAsync(_snapshotEntries(), runId, cts.Token);
            _preloadLifetimeTasks.Add((cts, _preloadSchedulerTask));
            return _preloadSchedulerTask;
        }
    }

    /// <summary>
    /// Drops completed scheduler tasks and disposes every lifetime CTS that is no longer current and has no unfinished task.
    /// Caller holds <see cref="_preloadCtsGate"/>.
    /// </summary>
    private void PruneFinishedLifetimes()
    {
        _preloadLifetimeTasks.RemoveAll(entry => entry.Task.IsCompleted);
        for (var i = _preloadLifetimes.Count - 1; i >= 0; i--)
        {
            var lifetime = _preloadLifetimes[i];
            if (ReferenceEquals(lifetime, _preloadCts)) continue;
            if (_preloadLifetimeTasks.Exists(entry => ReferenceEquals(entry.Cts, lifetime))) continue;
            _preloadLifetimes.RemoveAt(i);
            lifetime.Dispose();
        }
    }

    /// <summary>
    /// RV-I12: the exit decision of run <paramref name="runId"/>, taken under the same gate <see cref="PreloadAroundAsync"/>
    /// joins a running loop under. False when a navigation bumped the priority version since <paramref name="seenVersion"/>
    /// (take another pass). True marks the run as exiting, so any later <see cref="PreloadAroundAsync"/> starts a new run
    /// instead of returning this one, whose loop will not read the version again. A run paused for memory may still be
    /// draining started workers after this: the new run then shares <see cref="_preloadSlots"/> with them (concurrency
    /// stays bounded) and may queue a path one of them still holds; the target's in-flight dedup joins that decode.
    /// </summary>
    private bool TryExitRun(long runId, long seenVersion, bool paused, CancellationToken cancellationToken)
    {
        lock (_preloadCtsGate)
        {
            if (!paused && !cancellationToken.IsCancellationRequested
                && Interlocked.Read(ref _preloadPriorityVersion) != seenVersion) return false;
            if (runId == _schedulerRunId) _schedulerRunExiting = true;
            return true;
        }
    }

    private void MarkRunExiting(long runId)
    {
        lock (_preloadCtsGate)
            if (runId == _schedulerRunId) _schedulerRunExiting = true;
    }

    private async Task RunPreloadSchedulerAsync(CatalogEntry[] entries, long runId, CancellationToken cancellationToken)
    {
        var workers = _workerCount;
        var running = new Dictionary<Task<PreloadOutcome>, string>();
        // Paths in flight, plus paths whose preload failed (corrupt/unreadable/not cached): never retried in
        // this lifetime. A path whose preview was cached leaves the set when its worker finishes, so if that
        // preview is evicted later (viewer/compare/zoom decodes sharing the LRU) the next order pass queues it
        // again. The order enumerator yields each index once, so this cannot re-queue within one pass.
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenVersion = -1L;
        var seenShape = (Direction: 1, Lead: 0);
        var seenBox = DecodeBox.Unbounded;
        var seenCalibrated = false;
        var seenWholeFolder = false;
        // The O(n) folder estimate only changes with the snapshot, the box or the calibration, not with every navigation.
        var estimateCache = new FolderEstimateCache();
        var orderCenter = 0;
        IEnumerator<int>? order = null;
        var examinedSinceYield = 0;
        var headroom = new HeadroomProbeState();
        var paused = false;
        // Q-R26: the caller's thread (the UI thread for ImagePresenter) runs this loop until its first await.
        var callerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Captured before the version is read: a navigation after this point completes this
                // very task, so the wait below can never miss it.
                var wake = Volatile.Read(ref _wake).Task;
                var currentVersion = Interlocked.Read(ref _preloadPriorityVersion);
                // perf(preload): the shape (direction, burst lead) is re-read on every pass, not only on
                // navigation: when a burst ends no further key arrives to bump the version, but the lead
                // must still drop back to 0 so the images right next to the stop position come first.
                var shape = CurrentShape();
                // Q-R17: the order is also rebuilt once enough previews were measured at the current box,
                // so the whole-folder decision moves from the box upper bound to the measured size
                // without waiting for the next navigation.
                var calibrated = _sizes.MeanBytes(seenBox) is not null;
                if (seenVersion != currentVersion || shape != seenShape || calibrated != seenCalibrated)
                {
                    order?.Dispose();
                    _busyBackoff.BeginPass();
                    var center = Volatile.Read(ref _preloadCenter);
                    var box = CurrentBox(entries, center);
                    var measured = _sizes.MeanBytes(box);
                    var estimated = estimateCache.GetOrCompute(entries, box, measured);
                    var wholeFolder = RamBudgetPolicy.ShouldPreloadWholeFolderEstimate(estimated,
                        _options.FullFolderThresholdBytes, _memoryProbe, _options.ReserveBytes, _options.MemoryLoadLimit);
                    if (_log.Enabled)
                        _log.Info($"Preload policy: images={entries.Length} box={box.Width}x{box.Height} measuredMeanBytes={measured?.ToString("F0", CultureInfo.InvariantCulture) ?? "none"} estimatedBytes={estimated} capacityBytes={_options.FullFolderThresholdBytes} wholeFolder={wholeFolder} center={center} direction={shape.Direction} lead={shape.Lead}");
                    order = PreloadOrderService.Build(center, entries.Length,
                        wholeFolder, shape.Direction, shape.Lead, _options.Window).GetEnumerator();
                    seenVersion = currentVersion;
                    seenShape = shape;
                    seenBox = box;
                    seenCalibrated = measured is not null;
                    seenWholeFolder = wholeFolder;
                    orderCenter = center;
                }
                // perf(preload): viewer priority -- while the viewer is decoding the image on screen, preload
                // does not ramp up to its full worker count against it (e.g. right after a burst stops on a
                // not-yet-decoded image). Decodes already running are left alone: they can't be interrupted
                // and their results are kept. (Capping preload during the whole burst was measured too: it
                // showed fewer images and did not make the stop image faster, so bursts use every worker.)
                //
                // Q-R29 option C-2: on local disk this narrower limit was already enough (R04: no software
                // sync gap found). On a slow/bandwidth-capped link it is not -- measured (F4/fixture500,
                // simulated 10 ms + 20 MB/s link, see docs/refactoring/decisions/Q-R29-C2.md): even
                // _viewerBusyWorkerLimit's few concurrent preload readers can nearly saturate a capped link,
                // so the viewer's own read still starved (key-settle P50 1507-1511 ms / 100% of a 1500 ms cap
                // hit, vs 893 ms / 0 timeouts with preload paused). A slow link shows up here as source reads
                // taking far longer than any local decode does (DecodeMillisecondsEwma reuses the exact signal
                // PreloadScheduler already tracks for burst pacing) -- past SlowLinkDecodeMsThresholdMs, the
                // limit drops to SlowLinkViewerBusyWorkerLimit (1, not 0: `running.Count < limit` must stay
                // reachable from 0, or the "nothing queued, nothing running" branch below reads that as this
                // pass being entirely done and exits the scheduler task -- with limit 0 and no navigation to
                // wake it, preload would never resume filling the rest of the folder once the viewer decode
                // ends). A decode already running is still left alone, unchanged from before. This never
                // changes behavior on local disk (DecodeMillisecondsEwma there stays under the threshold, per
                // R01-R04's own measured 15-50 ms figures), so it does not touch the already-validated
                // fast-disk case, only the slow-link one this measurement added evidence for.
                var slowLink = _metrics.DecodeMillisecondsEwma > SlowLinkDecodeMsThreshold;
                var limit = _target.ActiveViewerDecodes > 0
                    ? (slowLink ? SlowLinkViewerBusyWorkerLimit : Math.Min(workers, _viewerBusyWorkerLimit))
                    : workers;
                while (running.Count < limit && order!.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // perf(Q-R26): only the 32/8 window is examined on the caller's thread. Once the whole folder
                    // is cached, a pass queues nothing and so never reached the yield below: every navigation
                    // then ran one cache lookup per image in the folder on the UI thread before the frame
                    // (F4, 1895 images: preloadKick P50 2-2.8 ms vs 0.3 ms in window mode). The rest of the
                    // folder continues on the thread pool (ForceYielding without the captured context).
                    if (Environment.CurrentManagedThreadId == callerThreadId
                        && !InPreloadWindow(order.Current, orderCenter, seenShape, _options.Window))
                        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    // IMG-03: GlobalMemoryStatusEx is a syscall, so the probe is cached; it is re-run
                    // when a decode was queued since the last check (memory use changed) or after 50 ms,
                    // so a burst never commits up to `workers` decodes past the limit on a stale answer.
                    if (!HasPreloadHeadroomCached(ref headroom))
                    {
                        // GlobalMemoryStatusEx is a syscall: only taken when either the plain
                        // log or the perf trace will actually consume it.
                        var perfEnabled = PhotoReviewPerf.Log.IsEnabled();
                        var memory = _log.Enabled || perfEnabled ? _memoryProbe.GetSnapshot() : null;
                        if (_log.Enabled)
                            _log.Info($"Preload paused for memory: queued={queued.Count} cacheCount={_target.CacheCount} cacheBytes={_target.CacheBytes} availableBytes={memory?.AvailableBytes} loadPercent={memory?.LoadPercent}");
                        if (perfEnabled)
                        {
                            PhotoReviewPerf.Log.PreloadPaused(memory is { } m ? (int)m.LoadPercent : -1,
                                memory is { } a ? (long)(a.AvailableBytes / (1024 * 1024)) : -1);
                        }
                        paused = true;
                        break;
                    }
                    // Beyond the 32/8 window a whole-folder pass stops once the cache is nearly full: past
                    // that point each far image would only evict an older one, and the LRU drops the
                    // earliest preloaded -- the images right next to the user (estimate off, or other
                    // decodes such as compare/zoom sharing the cache).
                    if (seenWholeFolder && _target.CacheBytes >= _options.FullFolderThresholdBytes * WholeFolderCacheFillLimit
                        && !InPreloadWindow(order.Current, orderCenter, seenShape, _options.Window)) break;
                    var entry = entries[order.Current];
                    var path = entry.Path;
                    if (queued.Contains(path)) continue;
                    // Reuses the folder scan's Length/LastWriteUtc: no stat for candidates
                    // already warm (the common case once preload has caught up).
                    ImageCacheKey key;
                    try { key = _target.GetCurrentCacheKey(entry); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _log.Warn($"Preload skipped, cannot stat: {path}"); // deleted between scan and stat
                        continue;
                    }
                    if (_target.TryGetCachedPreview(key)) { _busyBackoff.RecordSuccess(path); continue; } // decoded by another route (viewer): its busy history no longer applies
                    // Busy recently (decoder queue full): not retried on every order rebuild, see PreloadBusyBackoff.
                    if (_busyBackoff.ShouldSkip(path, key)) continue;
                    queued.Add(path);
                    headroom.DecodeQueuedSinceCheck = true;
                    var work = PreloadOneAsync(order.Current, path, key, cancellationToken);
                    // A call that finished synchronously (already cached, or superseded, before any await
                    // yielded) can return a runtime-cached, shared Task instance for its result,
                    // so it cannot key `running`: a second one threw and ended the whole scheduler loop.
                    if (work.IsCompletedSuccessfully) ForgetUnlessFailed(queued, path, work.Result);
                    else running.Add(work, path);
                    // Yield only after actual queue work; give input/rendering a
                    // chance without limiting every batch to two decodes.
                    if (++examinedSinceYield >= workers)
                    {
                        examinedSinceYield = 0;
                        // GlobalMemoryStatusEx is a syscall taken on every batch here; skip it
                        // entirely (this runs on the common, non-paused preload path) when
                        // nothing will read the result.
                        if (_log.Enabled)
                        {
                            var memory = _memoryProbe.GetSnapshot();
                            _log.Info($"Preload progress: queued={queued.Count} active={running.Count} cacheCount={_target.CacheCount} cacheBytes={_target.CacheBytes} availableBytes={memory?.AvailableBytes}");
                        }
                        // Never yield through a UI Dispatcher here. Dispose is called by the
                        // window's Closed handler and synchronously drains this task; a queued
                        // Dispatcher continuation would deadlock against that same UI thread.
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    }
                }
                // Q-R17: the window may run out right as its decodes finish calibrating the estimate;
                // take one more pass so a now-affordable whole folder still gets queued.
                if (!paused && running.Count == 0 && (_sizes.MeanBytes(seenBox) is not null) != seenCalibrated) continue;
                if (paused || running.Count == 0)
                {
                    // A navigation that landed after this pass read the version bumped it and woke nobody (there is
                    // nothing to wait on): take another pass for the new centre instead of exiting. Checked under the
                    // gate (RV-I12): a navigation after this point starts a new run instead of joining this one.
                    if (!TryExitRun(runId, seenVersion, paused, cancellationToken)) continue;
                    BeforeSchedulerExitForTests?.Invoke();
                    break;
                }
                var signalled = await Task.WhenAny(running.Keys.Append<Task>(wake)).ConfigureAwait(false);
                if (ReferenceEquals(signalled, wake)) continue; // woken by a navigation: re-prioritize
                var finished = (Task<PreloadOutcome>)signalled;
                running.Remove(finished, out var finishedPath);
                ForgetUnlessFailed(queued, finishedPath!, await finished.ConfigureAwait(false));
            }
            // Cancelled (the wake signal can end the wait before any worker finished) or paused for
            // memory: still report completion only once every started worker has unwound, so IsIdle
            // and Dispose never see this lifetime as finished while a worker still holds a slot.
            await DrainWorkersAsync(running.Keys).ConfigureAwait(false);
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
        // failure in PreloadOrderService/the memory probe) would otherwise escape
        // unobserved once the discarded fire-and-forget task
        // (`_ = PreloadAroundAsync(...)`) is garbage collected.
        catch (Exception ex)
        {
            MarkRunExiting(runId); // RV-I12: the next navigation starts a new run instead of joining this dying one
            _log.Error("Preload scheduler failed", ex);
            await DrainWorkersAsync(running.Keys).ConfigureAwait(false);
        }
        finally { order?.Dispose(); }
    }

    // Superseded (dropped before decoding because the user moved past it) and Cached (the preview is in the
    // RAM cache now) are forgotten, so a later order -- the user turning back, or the preview having been
    // evicted meanwhile -- can queue the path again. Failed stays: retrying a corrupt file on every
    // navigation would re-read it from disk each time.
    private static void ForgetUnlessFailed(HashSet<string> queued, string path, PreloadOutcome outcome)
    {
        if (outcome != PreloadOutcome.Failed) queued.Remove(path);
    }

    private enum PreloadOutcome
    {
        Cached,
        Superseded,
        Failed,
        /// <summary>The decoder refused the background decode because its queue is full: transient, retried by a later pass.</summary>
        Busy,
    }

    private static async Task DrainWorkersAsync(IEnumerable<Task> workers)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private struct HeadroomProbeState
    {
        public bool Value;
        public long Tick;
        public bool Checked;
        public bool DecodeQueuedSinceCheck;
    }

    private const long HeadroomRecheckMs = 50;

    private bool HasPreloadHeadroomCached(ref HeadroomProbeState state)
    {
        var now = Environment.TickCount64;
        if (!state.Checked || state.DecodeQueuedSinceCheck || now - state.Tick > HeadroomRecheckMs)
        {
            state.Value = HasPreloadHeadroom();
            state.Tick = now;
            state.Checked = true;
            state.DecodeQueuedSinceCheck = false;
        }
        return state.Value;
    }

    private bool HasPreloadHeadroom() => _memoryProbe.HasHeadroom(_options.MemoryLoadLimit, _options.ReserveBytes);

    /// <summary>
    /// perf(preload): false when <paramref name="index"/> no longer deserves a preload slot as of now --
    /// it is the current image (the viewer's own, priority decode handles it), or a burst already
    /// carried the user past it. Checked when a queued item finally gets its slot, i.e. the last point
    /// before its decode starts (a decode already inside the decoder cannot be interrupted).
    /// </summary>
    private bool IsStillWanted(int index)
    {
        var center = Volatile.Read(ref _preloadCenter);
        var (direction, lead) = CurrentShape();
        var ahead = direction * (index - center);
        if (ahead == 0) return false;
        return lead == 0 || ahead > 0;
    }

    /// <summary>Share of the preview budget a whole-folder pass may fill before it stops adding far images.</summary>
    public const double WholeFolderCacheFillLimit = 0.9;

    // The directional window PreloadOrderService.Build queues first (before the rest of the folder).
    private static bool InPreloadWindow(int index, int center, (int Direction, int Lead) shape, PreloadWindow window)
    {
        var ahead = (shape.Direction < 0 ? -1 : 1) * (index - center);
        return ahead > 0 ? ahead <= shape.Lead + window.Forward
            : ahead < 0 && -ahead <= window.Backward;
    }

    // Decode box previews are cached at right now, from the key of the image at the preload center
    // (reuses the folder scan's Length/LastWriteUtc: no stat). Unbounded when unknown.
    private DecodeBox CurrentBox(CatalogEntry[] entries, int center)
    {
        if (center < 0 || center >= entries.Length) return DecodeBox.Unbounded;
        try { return _target.GetCurrentCacheKey(entries[center]).TargetBox; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DecodeBox.Unbounded; }
    }

    /// <returns><see cref="PreloadOutcome.Superseded"/> when the item was dropped (see <see cref="IsStillWanted"/>),
    /// <see cref="PreloadOutcome.Cached"/> when its preview is in the RAM cache afterwards, <see cref="PreloadOutcome.Busy"/> when the
    /// decoder refused the work for now (retry later), otherwise
    /// <see cref="PreloadOutcome.Failed"/>.</returns>
    private async Task<PreloadOutcome> PreloadOneAsync(int index, string path, ImageCacheKey key, CancellationToken cancellationToken)
    {
        // D04 perf: preload work is not tied to a navigation. Setting the AsyncLocal here only
        // affects calls made downstream from this method (DecodeAndCacheAsync), not the caller
        // of PreloadAroundAsync or the scheduler loop above.
        var perf = PhotoReviewPerf.Log.IsEnabled();
        if (perf) PhotoReviewPerf.NavContext = -1;
        long perfEnqueue = perf ? Stopwatch.GetTimestamp() : 0;
        int slot;
        await _preloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            slot = _workerCount - 1 - _preloadSlots.CurrentCount;
            var stopwatch = Stopwatch.StartNew();
            var queueWaitMs = perf ? PhotoReviewPerf.Ms(perfEnqueue) : 0;
            var pathId = perf ? PhotoReviewPerf.PathId(path) : "";
            // If another task already decoded it while this one waited in the semaphore queue,
            // skip. Reuses the key built when this candidate was queued -- no stat.
            if (_target.TryGetCachedPreview(key))
            {
                stopwatch.Stop();
                if (perf) PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, "skipped", stopwatch.Elapsed.TotalMilliseconds);
                return PreloadOutcome.Cached;
            }
            if (!IsStillWanted(index))
            {
                stopwatch.Stop();
                if (perf) PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, "superseded", stopwatch.Elapsed.TotalMilliseconds);
                return PreloadOutcome.Superseded;
            }
            try
            {
                // A preview already in the disk cache decodes from there without touching the original:
                // prefetching the whole source file would be a read nobody uses.
                var isRawSource = ImageFileTypes.RawExtensions.Contains(Path.GetExtension(path));
                if (!isRawSource && _prefetchSourceBytes is not null && !_target.HasDiskCachedPreview(key))
                    await _prefetchSourceBytes(path, cancellationToken).ConfigureAwait(false);
                // Snapshot() copies/sorts the per-path open table: only pay for it when tracing.
                var beforeReads = perf ? _metrics.Snapshot().SourceReads : 0;
                await _target.PreloadAsync(path, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                // Must re-stat: the identity actually stored by PreloadAsync's decode is
                // whatever GetCurrentCacheKey(path) resolved to at decode time, which can
                // differ from the pre-decode `key` above if the source changed mid-flight.
                var freshKey = _target.GetCurrentCacheKey(path);
                var isHit = _target.TryGetCachedPreview(freshKey);
                if (isHit)
                {
                    lock (_preloadedKeysGate)
                    {
                        // RV-I15: a new box (window resized) makes every key of the old box unmatchable: drop them.
                        // The set only feeds the preload-hit metric (TryConsumePreloadedKey), never the queue logic,
                        // so a box flip back and forth can at worst under-count hits; it can no longer grow unbounded.
                        if (freshKey.TargetBox != _preloadedKeysBox)
                        {
                            _preloadedKeys.Clear();
                            _preloadedKeysBox = freshKey.TargetBox;
                        }
                        _preloadedKeys.Add(freshKey);
                    }
                }
                if (isHit && _target.CachedPreviewBytes(freshKey) is { } previewBytes)
                    _sizes.Record(freshKey.TargetBox, previewBytes);
                if (perf)
                {
                    var sourceRead = _metrics.Snapshot().SourceReads > beforeReads;
                    var kind = sourceRead ? "decoded" : isHit ? "hit" : "miss";
                    PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, kind, stopwatch.Elapsed.TotalMilliseconds);
                }
                if (isHit) _busyBackoff.RecordSuccess(path);
                return isHit ? PreloadOutcome.Cached : PreloadOutcome.Failed;
            }
            catch (DecoderBusyException)
            {
                // Not a failure: the decoder's bounded queue was full. Leave the path retryable (ForgetUnlessFailed forgets it).
                stopwatch.Stop();
                // The first busy of a path is worth one Info line; repeats (bounded by PreloadBusyBackoff) only go to the debugger.
                if (_busyBackoff.NoteBusy(path, key) == 1) _log.Info($"Preload deferred, decoder busy: {path}");
                else Debug.WriteLine($"Preload deferred again, decoder busy: {path}");
                BusyNoted?.Invoke(path);
                if (perf) PhotoReviewPerf.Log.PreloadItem(slot, pathId, queueWaitMs, "busy", stopwatch.Elapsed.TotalMilliseconds);
                return PreloadOutcome.Busy;
            }
            catch (IOException ex)
            {
                _log.Error($"Preload failed: {path}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error($"Preload failed: {path}", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // RV-I14: cancelled by someone else's token (not this lifetime): only this item failed. Letting it escape
                // would reach the scheduler loop as an unexpected exception and end preload for the whole lifetime.
                // Failed (not retried this lifetime) on purpose: a source that keeps cancelling must not be re-read every pass.
                _log.Warn($"Preload cancelled by another source: {path} ({ex.Message})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A corrupt/unsupported file (FileFormatException, NotSupportedException, ...) must skip only itself:
                // rethrowing would end the whole scheduler loop and stall preload at the bad file on every navigation.
                _log.Error($"Preload failed: {path}", ex);
            }
            return PreloadOutcome.Failed;
        }
        finally
        {
            _preloadSlots.Release();
        }
    }

    /// <summary>Test seam (RV-I12): runs on the loop's exit path, after its final version check, before the run completes.</summary>
    internal Action? BeforeSchedulerExitForTests { get; set; }

    /// <summary>Test seam (RV-I15): number of warmed keys currently tracked.</summary>
    internal int PreloadedKeyCountForTests { get { lock (_preloadedKeysGate) return _preloadedKeys.Count; } }

    /// <summary>Test seam: invoked with the path right AFTER a busy preload outcome was recorded in the backoff (set before the first pass).</summary>
    internal Action<string>? BusyNoted { get; set; }

    /// <summary>Cancellation lifetimes and scheduler tasks this scheduler still tracks for Dispose (test seam: must stay bounded over a long session).</summary>
    internal (int Lifetimes, int Tasks) TrackedLifetimeCounts
    {
        get { lock (_preloadCtsGate) return (_preloadLifetimes.Count, _preloadLifetimeTasks.Count); }
    }

    /// <summary>Longest Dispose blocks its caller (the UI thread at window close) for workers that ignore cancellation, e.g. a decode already running.</summary>
    internal TimeSpan DisposeDrainTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        Task[] lifetimeTasks;
        CancellationTokenSource[] lifetimeCts;
        lock (_preloadCtsGate)
        {
            if (_disposed) return;
            _disposed = true;
            _preloadCts.Cancel();
            lifetimeTasks = _preloadLifetimeTasks.Select(entry => entry.Task).ToArray();
            lifetimeCts = _preloadLifetimes.ToArray();
        }
        WakeScheduler();

        // The scheduler and workers use ConfigureAwait(false), so draining cannot require the
        // caller's UI context. Keep synchronization primitives alive until every waiter/holder
        // has observed cancellation and released its slot.
        try
        {
            if (!Task.WaitAll(lifetimeTasks, DisposeDrainTimeout))
            {
                // A decode that cannot be cancelled is still running. Do not hold the caller for it, and do not dispose the
                // slots/CTS it will still release/observe; they are unreferenced afterwards and reclaimed by the GC.
                _log.Warn("Preload scheduler did not drain in time; leaving in-flight decodes to finish on their own");
                // The slots/CTS are still in use by those decodes: release them once the last lifetime task has finished.
                _ = Task.WhenAll(lifetimeTasks).ContinueWith(_ =>
                {
                    foreach (var lifetimeCtsSource in lifetimeCts) lifetimeCtsSource.Dispose();
                    _preloadSlots.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
        }
        catch (AggregateException ex)
        {
            // WaitAll observed every task complete; only genuine failures (not cancellation) are worth a log line.
            foreach (var inner in ex.InnerExceptions)
                if (inner is not OperationCanceledException) _log.Error("Preload scheduler failed during disposal", inner);
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
}

public sealed class FakeMemoryProbe : IMemoryProbe
{
    private readonly bool _hasHeadroom;
    public FakeMemoryProbe(bool hasHeadroom = true) => _hasHeadroom = hasHeadroom;
    public bool HasHeadroom(double maximumLoad, long reserveBytes) => _hasHeadroom;
    public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
}
