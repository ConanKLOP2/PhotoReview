using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Catalog;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// Owns preview decode, the bounded RAM cache, in-flight decode de-duplication and
/// the cache invalidation epoch. Extracted from MainWindow so the decode/cache
/// contract can be exercised without constructing a WPF window.
/// The current loading mode and target decode box are supplied by the caller
/// (they are UI/settings state) instead of being read from MainWindow.
/// </summary>
public sealed class PreviewImageService : IPreloadTarget
{
    private readonly BoundedLruCache<ImageCacheKey, IDecodedImage> _cache;
    private readonly ConcurrentDictionary<(ImageCacheKey Key, long Epoch), Lazy<Task<IDecodedImage>>> _previewLoads = new();
    // IMG-02: bounded (entry-count LRU; each entry is a key plus two ints) so a multi-day session over
    // very large libraries cannot grow this for the life of the process.
    private readonly BoundedLruCache<ImageCacheKey, (int Width, int Height)> _originalDimensions;
    /// <summary>Default entry cap for the original-dimensions cache.</summary>
    public const int DefaultOriginalDimensionsCapacity = 200_000;
    private readonly object _cacheLifecycleGate = new();
    private long _cacheEpoch;
    private readonly ReviewMetrics _metrics;
    private readonly Func<bool> _isOriginalLoadingMode;
    private readonly Func<DecodeBox> _targetDecodeBox;
    private readonly string _diskCacheDirectory;
    private readonly long _diskCacheCapacityBytes;
    private readonly DiskCacheStore _diskStore;
    private readonly Func<DecoderBackend> _currentBackend;
    private readonly IImageDecoderFactory? _decoderFactory;
    private readonly IImageDecoder _decoder;
    // Perf: decoders (WpfBitmapImageDecoder, WicDirectDecoder, TurboJpegDecoder, and the
    // FallbackImageDecoder wrapping them) hold no mutable instance state -- every field is
    // readonly and every COM/native handle is scoped to a single Decode() call -- so they are
    // safe to share across concurrent decodes. Caching one instance per backend avoids
    // reconstructing (and, for TurboJpeg, Activator.CreateInstance-ing) a fresh
    // FallbackImageDecoder + primary/fallback pair on every single preview decode.
    private readonly ConcurrentDictionary<DecoderBackend, IImageDecoder> _decodersByBackend = new();
    private readonly ILog _log;
    // D10: precedence is the explicit test parameter, then the diagnostic environment
    // variable, then "disk cache enabled" (unset behavior). Read once in the constructor so
    // a mid-process environment change never makes DecodeAndCacheAsync and PersistToDiskCache
    // disagree about whether the disk cache is on.
    private readonly bool _disableDiskCache;
    private readonly SourceBytesCache? _sourceBytesCache;

    public DiskCacheStore DiskStore => _diskStore;
    public string DiskDirectory => _diskCacheDirectory;
    public IImageDecoder Decoder => GetDecoder(_currentBackend());
    public IImageDecoderFactory? DecoderFactory => _decoderFactory;

    // Persistence (PNG-encode + write + prune) runs outside the decode semaphore, so it
    // needs its own bound: without one, a preload burst spawns one Task.Run per decoded
    // preview with no cap, competing with live decodes for CPU/disk and keeping each
    // bitmap's closure (and the RAM it references) alive until its write finishes.
    // A small fixed worker pool with a bounded, drop-when-full queue caps that instead.
    private const int PersistWorkerCount = 2;
    private const int PersistQueueCapacity = 32;
    private readonly Channel<(BitmapSource Bitmap, string CachePath, long Epoch, DecoderBackend Backend, int Orientation, int OriginalWidth, int OriginalHeight)> _persistQueue =
        Channel.CreateBounded<(BitmapSource, string, long, DecoderBackend, int, int, int)>(
            new BoundedChannelOptions(PersistQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task[] _persistWorkers;

    /// <summary>
    /// Width-only overload (height unconstrained), kept for benchmark profiles and tests that pin
    /// a single decode width. The app wires the viewport box overload instead.
    /// </summary>
    public PreviewImageService(
        ReviewMetrics metrics,
        Func<bool> isOriginalLoadingMode,
        Func<int> targetDecodeWidth,
        long capacityBytes = 16L * 1024 * 1024 * 1024,
        string? diskCacheDirectory = null,
        long diskCacheCapacityBytes = 4L * 1024 * 1024 * 1024,
        bool? disableDiskCacheOverride = null,
        IImageDecoder? decoder = null,
        ILog? log = null,
        Func<DecoderBackend>? currentBackend = null,
        IImageDecoderFactory? decoderFactory = null,
        SourceBytesCache? sourceBytesCache = null,
        int originalDimensionsCapacity = DefaultOriginalDimensionsCapacity)
        : this(metrics, isOriginalLoadingMode, WidthOnly(targetDecodeWidth), capacityBytes, diskCacheDirectory,
            diskCacheCapacityBytes, disableDiskCacheOverride, decoder, log, currentBackend, decoderFactory, sourceBytesCache,
            originalDimensionsCapacity)
    {
    }

    private static Func<DecodeBox> WidthOnly(Func<int> targetDecodeWidth)
    {
        ArgumentNullException.ThrowIfNull(targetDecodeWidth);
        return () => new DecodeBox(targetDecodeWidth(), 0);
    }

    /// <param name="targetDecodeBox">
    /// Preview decode box (device pixels) read on every key build; previews decode to the largest
    /// size inside it (see <see cref="DecodeBox.Fit"/>). <see cref="DecodeBox.Unbounded"/> = full size.
    /// </param>
    public PreviewImageService(
        ReviewMetrics metrics,
        Func<bool> isOriginalLoadingMode,
        Func<DecodeBox> targetDecodeBox,
        long capacityBytes = 16L * 1024 * 1024 * 1024,
        string? diskCacheDirectory = null,
        long diskCacheCapacityBytes = 4L * 1024 * 1024 * 1024,
        bool? disableDiskCacheOverride = null,
        IImageDecoder? decoder = null,
        ILog? log = null,
        Func<DecoderBackend>? currentBackend = null,
        IImageDecoderFactory? decoderFactory = null,
        SourceBytesCache? sourceBytesCache = null,
        int originalDimensionsCapacity = DefaultOriginalDimensionsCapacity)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _isOriginalLoadingMode = isOriginalLoadingMode ?? throw new ArgumentNullException(nameof(isOriginalLoadingMode));
        _targetDecodeBox = targetDecodeBox ?? throw new ArgumentNullException(nameof(targetDecodeBox));
        _currentBackend = currentBackend ?? (() => DecoderBackend.Wpf);
        _diskCacheDirectory = diskCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache");
        _diskCacheCapacityBytes = diskCacheCapacityBytes;
        _log = log ?? NullLog.Instance;
        // perf(cache) v4: single-file entries (see PreviewCacheFile), no ".meta" companion.
        _diskStore = new DiskCacheStore(_diskCacheDirectory, "*.pv4", _diskCacheCapacityBytes, _log, companionSuffix: null);
        _disableDiskCache = disableDiskCacheOverride ?? (Environment.GetEnvironmentVariable("PHOTOREVIEW_DIAG_DISABLE_DISKCACHE") == "1");
        _decoderFactory = decoderFactory;
        _sourceBytesCache = sourceBytesCache;
        _decoder = decoder ?? (_decoderFactory?.Create(_currentBackend()) ?? new WpfBitmapImageDecoder());
        _originalDimensions = new BoundedLruCache<ImageCacheKey, (int Width, int Height)>(
            originalDimensionsCapacity, _ => 1);
        // IMG-11: clamp to half of physical RAM and log what is actually in effect.
        var requestedCapacity = capacityBytes;
        capacityBytes = RamBudgetPolicy.ClampToPhysicalMemory(capacityBytes, RamBudgetPolicy.GetPhysicalMemoryBytes());
        _log.Info($"Memory budgets: preview cache {capacityBytes / (1024 * 1024)} MiB"
            + (capacityBytes != requestedCapacity ? $" (clamped from {requestedCapacity / (1024 * 1024)} MiB to 50% of physical RAM)" : "")
            + (sourceBytesCache is null ? ", source-bytes cache off" : $", source-bytes cache {sourceBytesCache.CapacityBytes / (1024 * 1024)} MiB")
            + ".");
        _cache = new BoundedLruCache<ImageCacheKey, IDecodedImage>(
            capacityBytes, image => image.EstimatedBytes);
        // Two workers: enough to keep the disk-cache warm without letting persistence
        // saturate CPU/disk against live decodes. The production singleton in MainWindow
        // runs these for the process lifetime and never shuts them down (each write is
        // already atomic, so there's nothing to lose on process exit); a short-lived
        // instance (e.g. one benchmark run) should call ShutdownPersistWorkersAsync
        // instead of leaking these tasks and everything their closures hold alive.
        _persistWorkers = new Task[PersistWorkerCount];
        for (var i = 0; i < PersistWorkerCount; i++) _persistWorkers[i] = RunPersistWorkerAsync();
        ScheduleLegacyCacheCleanup();
    }

    // perf(cache): bumping the cache key/file version (preview-v3 -> v4, ".png"+".meta" ->
    // ".pv4") leaves any old-version files behind as dead weight instead of naturally aging out
    // through DiskCacheStore's own quota prune (which only ever scans "*.pv4" now). This sweeps
    // them once, lazily (fire-and-forget from the constructor, never blocks startup) and bounded
    // (a single non-recursive directory listing, capped at LegacyCleanupMaxFiles per extension)
    // so a huge leftover pile from a very old install can't turn this into an unbounded scan.
    private const int LegacyCleanupMaxFiles = 5000;

    private void ScheduleLegacyCacheCleanup()
    {
        if (_disableDiskCache) return;
        var directory = _diskCacheDirectory;
        var log = _log;
        _ = Task.Run(() => CleanupLegacyCacheFiles(directory, log));
    }

    private static void CleanupLegacyCacheFiles(string directory, ILog log)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.png").Take(LegacyCleanupMaxFiles))
                DiskCacheStore.TryDelete(path, log);
            foreach (var path in Directory.EnumerateFiles(directory, "*.png.meta").Take(LegacyCleanupMaxFiles))
                DiskCacheStore.TryDelete(path, log);
        }
        catch (IOException ex) { log.Error($"Legacy preview cache cleanup failed: {directory}", ex); }
        catch (UnauthorizedAccessException ex) { log.Error($"Legacy preview cache cleanup failed: {directory}", ex); }
    }

    /// <summary>
    /// Stops accepting new persist requests and waits for in-flight writes to finish.
    /// Only for short-lived instances (benchmark runs); the production singleton never
    /// calls this — see the constructor's comment on why that's intentional.
    /// </summary>
    public Task ShutdownPersistWorkersAsync()
    {
        _persistQueue.Writer.TryComplete();
        return Task.WhenAll(_persistWorkers);
    }

    private async Task RunPersistWorkerAsync()
    {
        await foreach (var request in _persistQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // The folder/cache may have moved on since this was queued (folder switch,
            // Clear Cache): the RAM-cache epoch check upstream only stopped the bitmap
            // from entering _cache, not this write, so re-check before doing any I/O.
            if (request.Epoch != Volatile.Read(ref _cacheEpoch)) continue;
            try
            {
                await PreviewCacheFile.WriteAtomicallyAsync(request.Bitmap, request.Backend, request.Orientation,
                        request.OriginalWidth, request.OriginalHeight, request.CachePath)
                    .ConfigureAwait(false);
                if (request.Epoch != Volatile.Read(ref _cacheEpoch))
                {
                    // Went stale mid-write (e.g. Clear Cache ran concurrently): don't leave
                    // a freshly-written file for a cache generation that was just cleared.
                    DiskCacheStore.TryDelete(request.CachePath);
                    continue;
                }
                // Coalesced per directory in DiskCacheStore: concurrent preload workers
                // persisting several previews at once must not each scan the whole directory.
                _diskStore.NoteWritten(request.CachePath);
                _diskStore.SchedulePrune();
            }
            catch (Exception ex)
            {
                // Only 2 workers are started for the process lifetime (never restarted):
                // an uncaught exception here would silently and permanently disable disk
                // persistence instead of just failing this one write.
                _log.Error($"Preview disk cache write failed: {request.CachePath}", ex);
            }
        }
    }

    /// <summary>Entries in the original-dimensions LRU (test/diagnostic only).</summary>
    public int KnownOriginalDimensionsCount => _originalDimensions.Count;

    public int CacheCount => _cache.Count;
    public long CacheBytes => _cache.CurrentSize;

    public bool IsOriginalLoadingMode() => _isOriginalLoadingMode();

    public ImageCacheKey GetCurrentCacheKey(string path)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(path, isOriginal, isOriginal ? DecodeBox.Unbounded : _targetDecodeBox(), orientationApplied: true, backend: _currentBackend());
    }

    /// <summary>Reuses a FileInfo the caller already fetched instead of stat-ing the path again.</summary>
    public ImageCacheKey GetCurrentCacheKey(FileInfo info)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(info, isOriginal, isOriginal ? DecodeBox.Unbounded : _targetDecodeBox(), orientationApplied: true, backend: _currentBackend());
    }

    public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(entry, isOriginal, isOriginal ? DecodeBox.Unbounded : _targetDecodeBox(), orientationApplied: true, backend: _currentBackend());
    }

    public Task<IDecodedImage> GetPreviewAsync(string path) => GetPreviewAsync(path, GetCurrentCacheKey(path));

    public Task<IDecodedImage> GetPreviewAsync(string path, ImageCacheKey key) =>
        GetPreviewCoreAsync(path, key, viewer: false, TimeSpan.Zero, CancellationToken.None);

    /// <summary>
    /// perf(preload): the viewer's decode of the image currently being navigated to. Unlike
    /// <see cref="GetPreviewAsync(string, ImageCacheKey)"/> (preload / compare), a new decode started
    /// here (a) never waits for thread-pool threads or preload workers -- it runs on a dedicated,
    /// above-normal-priority thread in one of <see cref="ViewerDecodeSlots"/> viewer slots -- and
    /// (b) is dropped, before it starts, once <paramref name="cancellationToken"/> is cancelled
    /// (the navigation was superseded). <paramref name="startDelay"/> (a key-held burst, see
    /// NavigationPace) waits a little first, so a decode the very next key supersedes is never started.
    /// A decode that has already started can't be interrupted inside the decoder: it runs to completion
    /// and its result is kept in the cache like a preloaded image. Joining an in-flight or cached
    /// preview behaves exactly as <see cref="GetPreviewAsync(string, ImageCacheKey)"/>.
    /// </summary>
    public Task<IDecodedImage> GetViewerPreviewAsync(string path, ImageCacheKey key, CancellationToken cancellationToken, TimeSpan startDelay = default) =>
        GetPreviewCoreAsync(path, key, viewer: true, startDelay, cancellationToken);

    /// <summary>Concurrent viewer decodes: one for the current image plus one spare, so a superseded
    /// viewer decode that is already inside the decoder never blocks the next image's decode.</summary>
    public const int ViewerDecodeSlots = 2;
    // A channel pre-filled with one token per slot is an async, cancellable counting gate (read =
    // acquire, write = release) that, unlike SemaphoreSlim, owns nothing disposable -- this service is
    // a process-lifetime singleton that is not IDisposable.
    private readonly Channel<byte> _viewerSlots = CreateSlotTokens(ViewerDecodeSlots);
    private int _activeViewerDecodes;

    /// <inheritdoc cref="IPreloadTarget.ActiveViewerDecodes"/>
    public int ActiveViewerDecodes => Volatile.Read(ref _activeViewerDecodes);

    private static Channel<byte> CreateSlotTokens(int count)
    {
        var channel = Channel.CreateBounded<byte>(count);
        for (var i = 0; i < count; i++) channel.Writer.TryWrite(0);
        return channel;
    }

    private async Task<IDecodedImage> GetPreviewCoreAsync(string path, ImageCacheKey key, bool viewer, TimeSpan startDelay, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // null: the in-flight decode this call joined belonged to a superseded viewer navigation
            // and was dropped before it started; this caller still wants the image, so start its own.
            var image = await GetPreviewOnceAsync(path, key, viewer, startDelay, cancellationToken).ConfigureAwait(false);
            if (image is not null) return image;
        }
    }

    private async Task<IDecodedImage?> GetPreviewOnceAsync(string path, ImageCacheKey key, bool viewer, TimeSpan startDelay, CancellationToken cancellationToken)
    {
        // Read WPF layout/DPI only on the UI thread. The decode below runs on a worker thread.
        var targetBox = key.TargetBox;
        if (_cache.TryGet(key, out var cached)) { _metrics.RecordCacheHit(); return cached; }
        var cacheEpoch = Volatile.Read(ref _cacheEpoch);
        var loadKey = (key, cacheEpoch);
        // GetOrAdd + Lazy makes "is anyone already loading this key" atomic: a plain
        // TryGetValue-then-set-indexer left a window where two concurrent misses (a
        // preload worker and the viewer, say) could each start their own decode, and
        // whichever's Task.Run finished first could remove the OTHER's still-running
        // entry from the dictionary via a bare TryRemove(key), letting a third caller
        // start yet another redundant decode.
        // GetOrAdd(key, factory) can invoke the factory more than once when callers race,
        // discarding every result but the winner's — so a closure flag set inside the
        // factory (e.g. "isNewLoad = true") can report CacheMiss for a caller that actually
        // lost the race and joined someone else's in-flight decode. Constructing the
        // candidate up front and comparing it by reference to what GetOrAdd returns
        // identifies the true winner instead.
        var candidate = new Lazy<Task<IDecodedImage>>(() => viewer
                ? DecodeForViewerAsync(path, key, targetBox, cacheEpoch, startDelay, cancellationToken)
                : DecodeAndCacheAsync(path, key, targetBox, cacheEpoch),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _previewLoads.GetOrAdd(loadKey, candidate);
        if (ReferenceEquals(lazy, candidate)) _metrics.RecordCacheMiss(); else _metrics.RecordInflightJoin();
        // D04 perf: a joiner records JoinStart/JoinEnd under its own nav; the shared decode
        // itself is traced once, under the nav of whoever created the Lazy (see DecodeAndCacheAsync).
        long perfJoin = 0; long perfNav = 0; var perfPathId = "";
        if (PhotoReviewPerf.Log.IsEnabled() && !ReferenceEquals(lazy, candidate))
        {
            perfNav = PhotoReviewPerf.NavContext; perfPathId = PhotoReviewPerf.PathId(path);
            PhotoReviewPerf.Log.JoinStart(perfNav, perfPathId);
            perfJoin = Stopwatch.GetTimestamp();
        }
        try { return await lazy.Value.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ReferenceEquals(lazy, candidate) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _previewLoads.TryRemove(new KeyValuePair<(ImageCacheKey, long), Lazy<Task<IDecodedImage>>>(loadKey, lazy));
            if (perfJoin != 0) PhotoReviewPerf.Log.JoinEnd(perfNav, perfPathId, PhotoReviewPerf.Ms(perfJoin));
        }
    }

    // D04 perf: Task.Run captures the ExecutionContext, so PhotoReviewPerf.NavContext read inside
    // the lambda is the nav of the caller that created the Lazy (viewer token or -1 for preload).
    private Task<IDecodedImage> DecodeAndCacheAsync(string path, ImageCacheKey key, DecodeBox targetBox, long cacheEpoch) =>
        Task.Run(() => DecodeAndCache(path, key, targetBox, cacheEpoch));

    // perf(preload): see GetViewerPreviewAsync. The 1.3 s "thumbnail shown, preview late" outlier at
    // the end of a key-held burst was every superseded navigation's decode still running: each nav
    // Task.Run-ed its own decode with no bound and no cancellation, so ~30 of them (plus 8 preload
    // workers) shared 12 cores, the thread pool had to grow to fit them, and the image the burst
    // stopped on decoded ~5x slower than alone. Here a superseded navigation's decode is dropped
    // unless it already started, at most ViewerDecodeSlots run at once, and they run on dedicated
    // threads so neither preload workers nor thread-pool growth delays them.
    private async Task<IDecodedImage> DecodeForViewerAsync(string path, ImageCacheKey key, DecodeBox targetBox, long cacheEpoch,
        TimeSpan startDelay, CancellationToken cancellationToken)
    {
        if (startDelay > TimeSpan.Zero) await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
        await _viewerSlots.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var decoding = false;
        try
        {
            // Last point where superseded work can be dropped: once DecodeAndCache starts it runs to
            // completion (the decoder can't be interrupted) and its result is cached for a later visit.
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _activeViewerDecodes);
            decoding = true;
            return await Task.Factory.StartNew(() =>
            {
                // LongRunning = a dedicated thread (not a pool thread), so raising its priority is
                // safe; it lets the current image win the CPU against the (normal-priority) preload
                // decodes without slowing the UI thread, which stays idle while this runs.
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                return DecodeAndCache(path, key, targetBox, cacheEpoch);
            }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                .ConfigureAwait(false);
        }
        finally
        {
            if (decoding) Interlocked.Decrement(ref _activeViewerDecodes);
            _viewerSlots.Writer.TryWrite(0);
        }
    }

    private IDecodedImage DecodeAndCache(string path, ImageCacheKey key, DecodeBox targetBox, long cacheEpoch)
    {
        var perf = PhotoReviewPerf.Log.IsEnabled();
        var perfNav = perf ? PhotoReviewPerf.NavContext : 0;
        var perfPathId = perf ? PhotoReviewPerf.PathId(path) : "";
        long perfT0 = 0;
        var stopwatch = Stopwatch.StartNew();
        var sourceRead = false;
        var cachePath = GetDiskCachePath(key);
        IDecodedImage decodedImage;

        // D10: PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1 measures decode/contention without the
        // disk cache muddying the numbers. Treat the cache as if the file didn't exist -- this
        // never deletes an existing entry, it just skips reading (and, below, persisting) it.
        if (!_disableDiskCache && File.Exists(cachePath))
        {
            try
            {
                if (perf) perfT0 = Stopwatch.GetTimestamp();
                // perf(cache) v4: one file open, one header read, one decode -- no separate
                // ".meta" companion (see PreviewCacheFile). ActualBackend comes from the header,
                // not key.Backend: a fallback-decoded preview (see the persist condition below)
                // is stored under the requested backend's path but its header truthfully records
                // whichever backend actually produced the pixels.
                var cacheEntry = PreviewCacheFile.Read(cachePath);
                decodedImage = new WpfDecodedImage(cacheEntry.Bitmap, downscaled: true, orientation: cacheEntry.Orientation, actualBackend: cacheEntry.ActualBackend,
                    originalWidth: cacheEntry.OriginalWidth, originalHeight: cacheEntry.OriginalHeight);
                _metrics.RecordDiskCacheHit();
                if (perf) PhotoReviewPerf.Log.DiskCacheRead(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0), cacheEntry.FileBytes);
            }
            // A background prune can delete cachePath between the Exists check above and
            // here; re-checking filesystem state in the catch filter (as this used to do)
            // lets that race turn a plain cache miss into an escaping FileNotFoundException
            // instead of the source fallback below. Catch the actual expected read/decode
            // failure types instead of re-querying state that can change mid-catch.
            // InvalidDataException covers PreviewCacheFile.Read's own header validation (bad
            // magic, a version other than PreviewCacheFile.CurrentVersion, or any other
            // structurally invalid header) -- a version bump makes every older entry take this
            // same "corrupt: delete and re-decode" path instead of needing a migration.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidDataException)
            {
                try { File.Delete(cachePath); } catch { /* best-effort: entry is re-decoded from source below */ }
                sourceRead = true;
                decodedImage = DecodeFromSource(path, key.Backend, targetBox, perf, perfNav, perfPathId);
            }
        }
        else
        {
            sourceRead = true;
            decodedImage = DecodeFromSource(path, key.Backend, targetBox, perf, perfNav, perfPathId);
        }

        // A path can be replaced while decode is in flight. Never publish
        // the old pixels under the new source's identity.
        // D04 perf: Verify is only emitted when the check passes (the throw path is not traced).
        if (perf) perfT0 = Stopwatch.GetTimestamp();
        if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed during decode: {path}");
        if (perf) PhotoReviewPerf.Log.Verify(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0));
        lock (_cacheLifecycleGate)
            if (cacheEpoch == _cacheEpoch) _cache.Set(key, decodedImage);

        // Perf: every decode through this method (viewer-triggered or preload-triggered -- both
        // share this same path, see IPreloadTarget.PreloadAsync) already knows the source's
        // original (post-orientation) dimensions for free, whether that came from a fresh decode
        // header or a disk-cache hit's stored header. Seeding _originalDimensions here means a
        // later GetOriginalDimensionsAsync for the same source (e.g. ImagePresenter showing
        // "WxH" in the status bar) never needs its own decoder ReadInfo call/file open, as long
        // as the image was already decoded once -- including by a background preload.
        _originalDimensions.Set(ImageCacheKey.CreateOriginal(key), (decodedImage.OriginalWidth, decodedImage.OriginalHeight));

        // Only cache previews that actually decoded at the downscaled target width: a
        // fallback to full-resolution (see DecodeWithFallback) must never be persisted under the
        // downscaled cache key, and Original-mode's full-resolution decode is slower to persist
        // than just re-decoding the source JPEG, so it would cost more than it saves.
        //
        // perf(cache): this used to also require decodedImage.ActualBackend == key.Backend,
        // which meant a preview whose *backend-level* decode fell back (e.g. an ICC JPEG the
        // configured TurboJpeg backend can't handle, decoded via FallbackImageDecoder's inner
        // Wpf/WicDirect decoder instead) was never disk-cached at all -- every future open re-ran
        // the same fallback chain from source. GetDiskCachePath already hashes key.Backend (the
        // *requested* backend), so this path is still per-requested-backend; the header just
        // records which backend actually produced the pixels (PreviewCacheFile.ReadResult.ActualBackend
        // above), so a disk-cache hit correctly reports the same ActualBackend a fresh fallback
        // decode would have.
        if (!_disableDiskCache && sourceRead &&
            decodedImage.Downscaled && decodedImage.PlatformImage is BitmapSource bmp &&
            !PreviewCacheFile.HasAlpha(bmp)) // IMG-01/Q-R1: the JPEG cache would flatten transparency to black
            PersistToDiskCache(bmp, cachePath, cacheEpoch, decodedImage.ActualBackend, decodedImage.Orientation, decodedImage.OriginalWidth, decodedImage.OriginalHeight);
        stopwatch.Stop();
        // key.Length is the stat already taken to build the cache key (validated above by
        // MatchesCurrentSource); reusing it avoids a redundant stat just for metrics.
        if (sourceRead) _metrics.RecordSourceRead(key.Length, stopwatch.ElapsedMilliseconds);
        return decodedImage;
    }

    public bool TryGetCachedPreview(string path, out IDecodedImage image)
    {
        try
        {
            return TryGetCachedPreview(GetCurrentCacheKey(path), out image);
        }
        catch (IOException) { image = default!; return false; }
        catch (UnauthorizedAccessException) { image = default!; return false; }
    }

    public bool TryGetCachedPreview(ImageCacheKey key, out IDecodedImage image)
    {
        try
        {
            return _cache.TryGet(key, out image);
        }
        catch (IOException) { image = default!; return false; }
        catch (UnauthorizedAccessException) { image = default!; return false; }
    }

    public bool HasInflightPreview(string path)
    {
        try
        {
            return HasInflightPreview(GetCurrentCacheKey(path));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Reuses a key the caller already built instead of stat-ing the path again.</summary>
    public bool HasInflightPreview(ImageCacheKey key) => _previewLoads.ContainsKey((key, Volatile.Read(ref _cacheEpoch)));

    /// <param name="alsoInvalidate">
    /// Runs inside the cache lifecycle lock with the normalized path, so callers can drop
    /// their own key-indexed state (e.g. preloaded keys) atomically with the eviction.
    /// </param>
    public void EvictCachedPath(string path, Action<string>? alsoInvalidate = null)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        lock (_cacheLifecycleGate)
        {
            _cacheEpoch++;
            _cache.RemoveWhere(key => string.Equals(key.Path, normalized, StringComparison.Ordinal));
            alsoInvalidate?.Invoke(normalized);
        }
    }

    /// <summary>Bumps the cache epoch and drops every cached bitmap.</summary>
    public void ClearCache()
    {
        lock (_cacheLifecycleGate) { _cacheEpoch++; _cache.Clear(); }
        _originalDimensions.Clear();
    }

    /// <summary>
    /// Deletes the on-disk preview cache. Bumping the epoch first (also done by
    /// <see cref="ClearCache"/>, harmless to do twice) makes any in-flight persist worker
    /// re-check and skip its write instead of recreating a file this just deleted.
    /// </summary>
    public void ClearDisk()
    {
        lock (_cacheLifecycleGate) { _cacheEpoch++; }
        try
        {
            _diskStore.ClearDirectory();
            // An explicit user action ("Clear Cache"), not a background pass: also sweep any
            // leftover pre-v4 files (".png" + ".png.meta") right away instead of waiting for
            // ScheduleLegacyCacheCleanup's bounded, once-per-process pass.
            CleanupLegacyCacheFiles(_diskCacheDirectory, _log);
        }
        catch (IOException ex) { _log.Error($"Preview disk cache clear failed: {_diskCacheDirectory}", ex); }
        catch (UnauthorizedAccessException ex) { _log.Error($"Preview disk cache clear failed: {_diskCacheDirectory}", ex); }
    }

    public Task<bool> WaitForPruneAsync(TimeSpan timeout) => _diskStore.WaitForPruneAsync(timeout);

    public void ClearOriginalDimensions() => _originalDimensions.Clear();

    public void ClearSourceBytesCache() => _sourceBytesCache?.Clear();

    private IImageDecoder GetDecoder(DecoderBackend backend) =>
        _decoderFactory is null ? _decoder : _decodersByBackend.GetOrAdd(backend, _decoderFactory.Create);

    private IDecodedImage DecodeFromSource(string path, DecoderBackend backend, DecodeBox targetBox, bool perf, long perfNav, string perfPathId)
    {
        ReadOnlyMemory<byte>? preReadBytes = null;
        if (_sourceBytesCache is not null)
        {
            preReadBytes = _sourceBytesCache.GetOrRead(path);
        }
        else if (Environment.GetEnvironmentVariable("PHOTOREVIEW_DIAG_PREREAD") == "1")
        {
            long readStart = perf ? Stopwatch.GetTimestamp() : 0;
            byte[] bytes;
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            {
                bytes = new byte[fileStream.Length];
                var offset = 0;
                int read;
                while (offset < bytes.Length && (read = fileStream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            }
            if (perf) PhotoReviewPerf.Log.SourceRead(PhotoReviewPerf.NavContext, PhotoReviewPerf.PathId(path), PhotoReviewPerf.Ms(readStart), bytes.LongLength);
            preReadBytes = bytes;
        }

        _metrics.RecordSourceOpen(path);

        long perfT0 = perf ? Stopwatch.GetTimestamp() : 0;
        var decoded = GetDecoder(backend).Decode(new DecodeRequest(path, targetBox, bytes: preReadBytes));
        if (perf) PhotoReviewPerf.Log.Decode(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0), targetBox.Width, decoded.Downscaled, !targetBox.IsUnbounded && !decoded.Downscaled);
        return decoded;
    }

    /// <summary>
    /// Original (post-orientation) dimensions already known for <paramref name="currentKey"/>'s source
    /// -- seeded by any earlier decode, viewer or preload -- without touching the file.
    /// </summary>
    public bool TryGetKnownOriginalDimensions(ImageCacheKey currentKey, out (int Width, int Height) dimensions) =>
        _originalDimensions.TryGet(ImageCacheKey.CreateOriginal(currentKey), out dimensions);

    /// <summary>
    /// feat(zoom) (option A for #43): full-resolution decode of one source for the zoomed viewer.
    /// Unlike <see cref="GetPreviewAsync(string, ImageCacheKey)"/> with an original key, the result is
    /// neither put in the RAM LRU nor the disk cache: a 24 MP original is ~96 MB, so the caller holds at
    /// most the current image's original and drops it on navigation (it must not displace dozens of
    /// preloaded previews from the LRU). The decode runs on a dedicated above-normal-priority thread
    /// (not a pool thread, not a viewer slot -- the next navigation's preview must never wait for it)
    /// and is dropped before it starts once <paramref name="cancellationToken"/> is cancelled; a started
    /// decode cannot be interrupted and simply completes unobserved. An original already in the RAM
    /// cache (Original loading mode) is returned as is.
    /// </summary>
    /// <param name="sourceKey">Any key for the source (typically the displayed preview's); its
    /// original-mode twin is derived without re-stating the file.</param>
    public Task<IDecodedImage> DecodeOriginalAsync(string path, ImageCacheKey sourceKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ImageCacheKey.CreateOriginal(sourceKey);
        if (_cache.TryGet(key, out var cached)) return Task.FromResult(cached);
        return Task.Factory.StartNew(() =>
        {
            // Last point where a superseded request can be dropped (see summary).
            cancellationToken.ThrowIfCancellationRequested();
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            var perf = PhotoReviewPerf.Log.IsEnabled();
            var stopwatch = Stopwatch.StartNew();
            var decoded = DecodeFromSource(path, key.Backend, DecodeBox.Unbounded, perf,
                perf ? PhotoReviewPerf.NavContext : 0, perf ? PhotoReviewPerf.PathId(path) : "");
            if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed during decode: {path}");
            _originalDimensions.Set(key, (decoded.OriginalWidth, decoded.OriginalHeight));
            _metrics.RecordSourceRead(key.Length, stopwatch.ElapsedMilliseconds);
            return decoded;
            // RunContinuationsAsynchronously: the dedicated thread exits right after the decode
            // instead of running the awaiting caller's continuation on its own (above-normal) stack.
        }, CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach | TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    public Task<(int Width, int Height)> GetOriginalDimensionsAsync(string path) =>
        GetOriginalDimensionsAsync(path, ImageCacheKey.Create(path, true, 0, orientationApplied: true, backend: _currentBackend()));

    /// <summary>Reuses a key the caller already built for this navigation instead of stat-ing the path again.</summary>
    public async Task<(int Width, int Height)> GetOriginalDimensionsAsync(string path, ImageCacheKey currentKey)
    {
        var key = ImageCacheKey.CreateOriginal(currentKey);
        if (_originalDimensions.TryGet(key, out var dimensions)) return dimensions;
        // Only reached when nothing decoded so far (viewer or preload) has told us this source's
        // original dimensions -- see the seeding in DecodeAndCacheAsync. ReadInfo below genuinely
        // opens the file (a header-only read), so it counts as a source open like any other.
        _metrics.RecordSourceOpen(path);
        var info = await Task.Run(() => GetDecoder(key.Backend).ReadInfo(path)).ConfigureAwait(false);
        dimensions = (info.Width, info.Height);
        if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed while reading dimensions: {path}");
        _originalDimensions.Set(key, dimensions);
        return dimensions;
    }

    private string GetDiskCachePath(ImageCacheKey key)
    {
        // v4 (PreviewCacheFile): a single file carries its own header (backend, orientation,
        // size, version) -- no atomic metadata companion. Bumping "preview-v3" to "preview-v4"
        // here means every old-version file misses on lookup by construction (this hash never
        // matches one); ScheduleLegacyCacheCleanup/ClearDisk sweep the orphaned files themselves.
        // perf(decode) "preview-v5-box": two independent format bumps landed together -- the key
        // is a width x height decode box now (was width-only, "preview-v4-box"), and the on-disk
        // header itself grew original-source-dimension fields (PreviewCacheFile.CurrentVersion
        // 4 -> 5). Either change alone would make old entries miss (a different hash) or fail the
        // header version check (see PreviewCacheFile.Read) and get deleted/re-decoded, but bumping
        // the key prefix past both means every pre-merge entry (box or non-box, v4 or v5 header)
        // misses on lookup by construction instead of taking the slower throw/catch/delete path;
        // ScheduleLegacyCacheCleanup/ClearDisk sweep the orphaned ".pv4" files themselves.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"preview-v5-box|{key.Path}|{key.Length}|{key.LastWriteUtcTicks}|{key.IsOriginal}|{key.TargetWidth}x{key.TargetHeight}|{key.OrientationApplied}|{key.Backend}"))));
        return Path.Combine(_diskCacheDirectory, hash + ".pv4");
    }

    /// <summary>Queues the decoded preview for background persistence; drops it if the bounded queue is full.</summary>
    private void PersistToDiskCache(BitmapSource bitmap, string cachePath, long cacheEpoch, DecoderBackend backend, int orientation, int originalWidth, int originalHeight)
    {
        if (cacheEpoch != Volatile.Read(ref _cacheEpoch)) return;
        // Best-effort: a full queue means persistence is falling behind decode, so this
        // preview is dropped rather than growing the backlog or blocking the caller.
        _persistQueue.Writer.TryWrite((bitmap, cachePath, cacheEpoch, backend, orientation, originalWidth, originalHeight));
    }

    bool IPreloadTarget.TryGetCachedPreview(string path) => TryGetCachedPreview(path, out _);
    bool IPreloadTarget.TryGetCachedPreview(ImageCacheKey key) => TryGetCachedPreview(key, out _);
    Task IPreloadTarget.PreloadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetPreviewAsync(path);
    }
}
