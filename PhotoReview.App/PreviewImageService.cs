using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using PhotoReview.App.Diagnostics;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

/// <summary>
/// Owns preview decode, the bounded RAM cache, in-flight decode de-duplication and
/// the cache invalidation epoch.  Extracted from MainWindow so the decode/cache
/// contract can be exercised without constructing a WPF window.
/// The current loading mode and target decode width are supplied by the caller
/// (they are UI/settings state) instead of being read from MainWindow.
/// </summary>
public sealed class PreviewImageService
{
    private readonly BoundedLruCache<ImageCacheKey, BitmapImage> _cache;
    private readonly ConcurrentDictionary<(ImageCacheKey Key, long Epoch), Lazy<Task<BitmapImage>>> _previewLoads = new();
    private readonly ConcurrentDictionary<ImageCacheKey, (int Width, int Height)> _originalDimensions = new();
    private readonly object _cacheLifecycleGate = new();
    private long _cacheEpoch;
    private readonly ReviewMetrics _metrics;
    private readonly Func<bool> _isOriginalLoadingMode;
    private readonly Func<int> _targetDecodeWidth;
    private readonly string _diskCacheDirectory;
    private readonly long _diskCacheCapacityBytes;
    // D10: precedence is the explicit test parameter, then the diagnostic environment
    // variable, then "disk cache enabled" (unset behavior). Read once in the constructor so
    // a mid-process environment change never makes DecodeAndCacheAsync and PersistToDiskCache
    // disagree about whether the disk cache is on.
    private readonly bool _disableDiskCache;
    // Persistence (PNG-encode + write + prune) runs outside the decode semaphore, so it
    // needs its own bound: without one, a preload burst spawns one Task.Run per decoded
    // preview with no cap, competing with live decodes for CPU/disk and keeping each
    // bitmap's closure (and the RAM it references) alive until its write finishes.
    // A small fixed worker pool with a bounded, drop-when-full queue caps that instead.
    private const int PersistWorkerCount = 2;
    private const int PersistQueueCapacity = 32;
    private readonly Channel<(BitmapImage Bitmap, string CachePath, long Epoch)> _persistQueue =
        Channel.CreateBounded<(BitmapImage, string, long)>(
            new BoundedChannelOptions(PersistQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task[] _persistWorkers;

    public PreviewImageService(
        ReviewMetrics metrics,
        Func<bool> isOriginalLoadingMode,
        Func<int> targetDecodeWidth,
        long capacityBytes = AppConstants.ImageCacheCapacityBytes,
        string? diskCacheDirectory = null,
        long diskCacheCapacityBytes = AppConstants.PreviewDiskCacheCapacityBytes,
        bool? disableDiskCacheOverride = null)
    {
        _metrics = metrics;
        _isOriginalLoadingMode = isOriginalLoadingMode;
        _targetDecodeWidth = targetDecodeWidth;
        _diskCacheDirectory = diskCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache");
        _diskCacheCapacityBytes = diskCacheCapacityBytes;
        _disableDiskCache = disableDiskCacheOverride ?? DiagOptions.DisableDiskCache;
        _cache = new BoundedLruCache<ImageCacheKey, BitmapImage>(
            capacityBytes, bitmap => Math.Max(1, bitmap.PixelWidth * (long)bitmap.PixelHeight * 4));
        // Two workers: enough to keep the disk-cache warm without letting persistence
        // saturate CPU/disk against live decodes. The production singleton in MainWindow
        // runs these for the process lifetime and never shuts them down (each write is
        // already atomic, so there's nothing to lose on process exit); a short-lived
        // instance (e.g. one benchmark run) should call ShutdownPersistWorkersAsync
        // instead of leaking these tasks and everything their closures hold alive.
        _persistWorkers = new Task[PersistWorkerCount];
        for (var i = 0; i < PersistWorkerCount; i++) _persistWorkers[i] = RunPersistWorkerAsync();
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
        await foreach (var request in _persistQueue.Reader.ReadAllAsync())
        {
            // The folder/cache may have moved on since this was queued (folder switch,
            // Clear Cache): the RAM-cache epoch check upstream only stopped the bitmap
            // from entering _cache, not this write, so re-check before doing any I/O.
            if (request.Epoch != Volatile.Read(ref _cacheEpoch)) continue;
            try
            {
                await DiskCacheStore.WriteAtomicallyAsync(request.Bitmap, request.CachePath).ConfigureAwait(false);
                if (request.Epoch != Volatile.Read(ref _cacheEpoch))
                {
                    // Went stale mid-write (e.g. Clear Cache ran concurrently): don't leave
                    // a freshly-written file for a cache generation that was just cleared.
                    DiskCacheStore.TryDelete(request.CachePath, logContext: null);
                    continue;
                }
                // Coalesced per directory in DiskCacheStore: concurrent preload workers
                // persisting several previews at once must not each scan the whole directory.
                DiskCacheStore.SchedulePrune(Path.GetDirectoryName(request.CachePath)!,
                    "*.png", _diskCacheCapacityBytes, "Preview disk cache delete failed");
            }
            catch (Exception ex)
            {
                // Only 2 workers are started for the process lifetime (never restarted):
                // an uncaught exception here would silently and permanently disable disk
                // persistence instead of just failing this one write.
                AppLog.Error($"Preview disk cache write failed: {request.CachePath}", ex);
            }
        }
    }

    public int CacheCount => _cache.Count;
    public long CacheBytes => _cache.CurrentSize;

    public bool IsOriginalLoadingMode() => _isOriginalLoadingMode();

    public ImageCacheKey GetCurrentCacheKey(string path)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(path, isOriginal, isOriginal ? 0 : _targetDecodeWidth());
    }

    /// <summary>Reuses a FileInfo the caller already fetched instead of stat-ing the path again.</summary>
    public ImageCacheKey GetCurrentCacheKey(FileInfo info)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(info, isOriginal, isOriginal ? 0 : _targetDecodeWidth());
    }

    public Task<BitmapImage> GetPreviewAsync(string path) => GetPreviewAsync(path, GetCurrentCacheKey(path));

    public async Task<BitmapImage> GetPreviewAsync(string path, ImageCacheKey key)
    {
        // Read WPF layout/DPI only on the UI thread. The decode below runs on a worker thread.
        var targetWidth = key.TargetWidth;
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
        var candidate = new Lazy<Task<BitmapImage>>(() => DecodeAndCacheAsync(path, key, targetWidth, cacheEpoch),
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
        try { return await lazy.Value; }
        finally
        {
            _previewLoads.TryRemove(new KeyValuePair<(ImageCacheKey, long), Lazy<Task<BitmapImage>>>(loadKey, lazy));
            if (perfJoin != 0) PhotoReviewPerf.Log.JoinEnd(perfNav, perfPathId, PhotoReviewPerf.Ms(perfJoin));
        }
    }

    // D04 perf: Task.Run captures the ExecutionContext, so PhotoReviewPerf.NavContext read inside
    // the lambda is the nav of the caller that created the Lazy (viewer token or -1 for preload).
    private Task<BitmapImage> DecodeAndCacheAsync(string path, ImageCacheKey key, int targetWidth, long cacheEpoch) => Task.Run(() =>
    {
        var perf = PhotoReviewPerf.Log.IsEnabled();
        var perfNav = perf ? PhotoReviewPerf.NavContext : 0;
        var perfPathId = perf ? PhotoReviewPerf.PathId(path) : "";
        long perfT0 = 0;
        var stopwatch = Stopwatch.StartNew();
        var sourceRead = false;
        // DecodeWithFallback silently returns a full-resolution decode when the requested
        // downscaled decode fails; this must be tracked so the fallback bitmap is never
        // persisted under the downscaled preview's cache key below.
        var downscaled = false;
        var bitmap = new BitmapImage();
        var cachePath = GetDiskCachePath(key);
        // D10: PHOTOREVIEW_DIAG_DISABLE_DISKCACHE=1 measures decode/contention without the
        // disk cache muddying the numbers. Treat the cache as if the file didn't exist -- this
        // never deletes an existing entry, it just skips reading (and, below, persisting) it.
        if (!_disableDiskCache && File.Exists(cachePath))
        {
            try
            {
                if (perf) perfT0 = Stopwatch.GetTimestamp();
                using var cacheStream = File.OpenRead(cachePath);
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = cacheStream; bitmap.EndInit(); bitmap.Freeze();
                _metrics.RecordDiskCacheHit();
                if (perf) PhotoReviewPerf.Log.DiskCacheRead(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0), PerfStreamLength(cacheStream));
            }
            // A background prune can delete cachePath between the Exists check above and
            // here; re-checking filesystem state in the catch filter (as this used to do)
            // lets that race turn a plain cache miss into an escaping FileNotFoundException
            // instead of the source fallback below. Catch the actual expected read/decode
            // failure types instead of re-querying state that can change mid-catch.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                try { File.Delete(cachePath); } catch { }
                sourceRead = true;
                if (perf) perfT0 = Stopwatch.GetTimestamp();
                (bitmap, downscaled) = DecodeWithFallback(path, targetWidth);
                if (perf) PhotoReviewPerf.Log.Decode(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0), targetWidth, downscaled, targetWidth > 0 && !downscaled);
            }
        }
        else
        {
            sourceRead = true;
            if (perf) perfT0 = Stopwatch.GetTimestamp();
            (bitmap, downscaled) = DecodeWithFallback(path, targetWidth);
            if (perf) PhotoReviewPerf.Log.Decode(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0), targetWidth, downscaled, targetWidth > 0 && !downscaled);
        }
        // A path can be replaced while decode is in flight. Never publish
        // the old pixels under the new source's identity.
        // D04 perf: Verify is only emitted when the check passes (the throw path is not traced).
        if (perf) perfT0 = Stopwatch.GetTimestamp();
        if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed during decode: {path}");
        if (perf) PhotoReviewPerf.Log.Verify(perfNav, perfPathId, PhotoReviewPerf.Ms(perfT0));
        lock (_cacheLifecycleGate)
            if (cacheEpoch == _cacheEpoch) _cache.Set(key, bitmap);
        // Only cache previews that actually decoded at the downscaled target width: a
        // fallback to full-resolution (see DecodeWithFallback) must never be PNG-encoded
        // under the downscaled cache key, and Original-mode's full-resolution decode is
        // slower to persist than just re-decoding the source JPEG, so it would cost more
        // than it saves.
        if (!_disableDiskCache && sourceRead && downscaled) PersistToDiskCache(bitmap, cachePath, cacheEpoch);
        stopwatch.Stop();
        if (sourceRead) try { _metrics.RecordSourceRead(new FileInfo(path).Length, stopwatch.ElapsedMilliseconds); } catch { }
        return bitmap;
    });

    // D04 perf (tracing only): must never throw into the disk-cache catch above, which would turn a
    // successful cache read into a delete + source decode.
    private static long PerfStreamLength(Stream stream)
    {
        try { return stream.Length; }
        catch { return -1; }
    }

    public bool TryGetCachedPreview(string path, out BitmapImage bitmap)
    {
        try
        {
            return TryGetCachedPreview(GetCurrentCacheKey(path), out bitmap);
        }
        catch (IOException) { bitmap = default!; return false; }
        catch (UnauthorizedAccessException) { bitmap = default!; return false; }
    }

    public bool TryGetCachedPreview(ImageCacheKey key, out BitmapImage bitmap)
    {
        try
        {
            return _cache.TryGet(key, out bitmap);
        }
        catch (IOException) { bitmap = default!; return false; }
        catch (UnauthorizedAccessException) { bitmap = default!; return false; }
    }

    public bool HasInflightPreview(string path)
    {
        try
        {
            var key = GetCurrentCacheKey(path);
            return _previewLoads.ContainsKey((key, Volatile.Read(ref _cacheEpoch)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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
    }

    /// <summary>
    /// Deletes the on-disk preview cache. Bumping the epoch first (also done by
    /// <see cref="ClearCache"/>, harmless to do twice) makes any in-flight persist worker
    /// re-check and skip its write instead of recreating a file this just deleted.
    /// </summary>
    public void ClearDisk()
    {
        lock (_cacheLifecycleGate) { _cacheEpoch++; }
        try { DiskCacheStore.ClearDirectory(_diskCacheDirectory, "*.png", "Preview disk cache delete failed"); }
        catch (IOException ex) { AppLog.Error($"Preview disk cache clear failed: {_diskCacheDirectory}", ex); }
        catch (UnauthorizedAccessException ex) { AppLog.Error($"Preview disk cache clear failed: {_diskCacheDirectory}", ex); }
    }

    public void ClearOriginalDimensions() => _originalDimensions.Clear();

    public async Task<(int Width, int Height)> GetOriginalDimensionsAsync(string path)
    {
        var key = ImageCacheKey.Create(path, true, 0);
        if (_originalDimensions.TryGetValue(key, out var dimensions)) return dimensions;
        dimensions = await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            // PixelWidth/Height only need the image header. OnLoad forced WIC
            // to read/decode the source a second time on every warm Next.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        });
        if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed while reading dimensions: {path}");
        _originalDimensions[key] = dimensions;
        return dimensions;
    }

    public static BitmapImage DecodeSource(string path, int targetWidth)
    {
        // D05 perf: PHOTOREVIEW_DIAG_PREREAD=1 reads the whole file into a MemoryStream first (timed
        // separately) and decodes from that instead of the FileStream, so t_read and t_decode can be
        // told apart. This changes the I/O pattern (one sequential read into RAM instead of WIC
        // streaming from the file handle) and exists only to measure R-4's potential benefit -- it
        // must stay off the hot path used everywhere else. When PreRead is false, behavior below is
        // byte-for-byte what it was before this file existed.
        if (DiagOptions.PreRead)
        {
            var perf = PhotoReviewPerf.Log.IsEnabled();
            long readStart = perf ? Stopwatch.GetTimestamp() : 0;
            byte[] bytes;
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            {
                bytes = new byte[fileStream.Length];
                var offset = 0;
                int read;
                while (offset < bytes.Length && (read = fileStream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            }
            // Only SourceRead is emitted here: the caller (DecodeAndCacheAsync) already wraps this
            // whole DecodeSource/DecodeWithFallback call in its own Decode event, so a second Decode
            // event here would double-count decode time for every PreRead navigation.
            if (perf) PhotoReviewPerf.Log.SourceRead(PhotoReviewPerf.NavContext, PhotoReviewPerf.PathId(path), PhotoReviewPerf.Ms(readStart), bytes.LongLength);
            using var memoryStream = new MemoryStream(bytes, writable: false);
            var bitmapFromMemory = new BitmapImage();
            bitmapFromMemory.BeginInit(); bitmapFromMemory.CacheOption = BitmapCacheOption.OnLoad;
            if (targetWidth > 0) bitmapFromMemory.DecodePixelWidth = targetWidth;
            bitmapFromMemory.StreamSource = memoryStream; bitmapFromMemory.EndInit(); bitmapFromMemory.Freeze();
            return bitmapFromMemory;
        }
        // Allow an in-flight decode to coexist with Move/Delete. The action path
        // cancels future work and invalidates its result; Windows can still
        // complete the file operation without waiting for this read handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0) bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    /// <summary>Decodes at <paramref name="targetWidth"/>, falling back to a full-resolution
    /// decode if that fails; the second value reports whether the target width was actually
    /// honored, so a caller never mistakes the full-resolution fallback for a downscaled decode.</summary>
    public static (BitmapImage Bitmap, bool Downscaled) DecodeWithFallback(string path, int targetWidth)
    {
        try { return (DecodeSource(path, targetWidth), targetWidth > 0); }
        catch when (targetWidth > 0) { return (DecodeSource(path, 0), false); }
    }

    private string GetDiskCachePath(ImageCacheKey key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key.Path}|{key.Length}|{key.LastWriteUtcTicks}|{key.IsOriginal}|{key.TargetWidth}")));
        return Path.Combine(_diskCacheDirectory, hash + ".png");
    }

    /// <summary>Queues the decoded preview for background persistence; drops it if the bounded queue is full.</summary>
    private void PersistToDiskCache(BitmapImage bitmap, string cachePath, long cacheEpoch)
    {
        if (cacheEpoch != Volatile.Read(ref _cacheEpoch)) return;
        // Best-effort: a full queue means persistence is falling behind decode, so this
        // preview is dropped rather than growing the backlog or blocking the caller.
        _persistQueue.Writer.TryWrite((bitmap, cachePath, cacheEpoch));
    }
}
