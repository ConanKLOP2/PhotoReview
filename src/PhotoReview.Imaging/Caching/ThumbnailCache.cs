using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// Thread-safe thumbnail cache for fast folder review. The source image is never modified.
/// Public API works with <see cref="IDecodedImage"/> (K-1).
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    public const int MaxThumbnailWidth = 800;
    public const long DefaultMaxDiskBytes = 1L * 1024 * 1024 * 1024;

    private readonly string _diskDirectory;
    private readonly long _maxRamBytes;
    private readonly long _maxDiskBytes;
    private readonly bool _persistNewThumbnails;
    private readonly ILog _log;
    private readonly Func<string, CancellationToken, Task<IDecodedImage?>> _embeddedThumbnailReader;
    private readonly DiskCacheStore _diskStore;
    private readonly BoundedLruCache<string, IDecodedImage> _ramCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<IDecodedImage?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lifecycleGate = new();
    private long _cacheGeneration;
    private bool _disposed;

    private const int StartupTempCleanupMaxFiles = 5000;

    /// <summary>The start-up stale temp-file sweep started by the constructor; test seam.</summary>
    internal Task StartupCleanup { get; }

    /// <summary>Test seam (RV-I02): replaces the PNG persist write of a new thumbnail; null uses <see cref="DiskCacheStore"/>.</summary>
    internal Func<BitmapSource, string, CancellationToken, Task>? PersistForTests { get; set; }

    /// <summary>Test seam (RV-I10): receives the shared (Lazy) load task each caller waits on.</summary>
    internal Action<Task>? SharedLoadForTests { get; set; }

    public DiskCacheStore DiskStore => _diskStore;
    public string DiskDirectory => _diskDirectory;

    public ThumbnailCache(
        string? diskDirectory = null,
        long maxRamBytes = 1L * 1024 * 1024 * 1024,
        long maxDiskBytes = DefaultMaxDiskBytes,
        bool persistNewThumbnails = true,
        ILog? log = null,
        Func<string, CancellationToken, Task<IDecodedImage?>>? embeddedThumbnailReader = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRamBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDiskBytes);
        _diskDirectory = diskDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoReview", "thumbnails");
        _maxRamBytes = maxRamBytes;
        _maxDiskBytes = maxDiskBytes;
        _persistNewThumbnails = persistNewThumbnails;
        _log = log ?? NullLog.Instance;
        _embeddedThumbnailReader = embeddedThumbnailReader ?? DefaultReadEmbeddedThumbnailAsync;
        _diskStore = new DiskCacheStore(_diskDirectory, "*.png", _maxDiskBytes, _log);
        // Atomic-write temp files orphaned by a killed process (see DiskCacheStore.TempFilePattern); off the caller's thread.
        var directory = _diskDirectory;
        var cleanupLog = _log;
        StartupCleanup = Task.Run(() => DiskCacheStore.DeleteStaleTempFiles(directory, DateTime.UtcNow, StartupTempCleanupMaxFiles, cleanupLog));
        _ramCache = new BoundedLruCache<string, IDecodedImage>(_maxRamBytes, EstimateBytes, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IDecodedImage?> GetAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        GetAsync(sourcePath, knownStat: null, cancellationToken);

    /// <summary>
    /// Q-R29 option C: as <see cref="GetAsync(string, CancellationToken)"/>, but the cache key is built from
    /// <paramref name="knownStat"/> -- a stat of <paramref name="sourcePath"/> the caller just took (off the UI
    /// thread) -- instead of a fresh <see cref="FileInfo"/> on the calling thread. Null falls back to that stat.
    /// Freshness is the caller's stat's: a key built from it names exactly that Length/LastWriteUtc version.
    /// </summary>
    public Task<IDecodedImage?> GetAsync(string sourcePath, FileStat? knownStat, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        // D04 perf: ThumbEnd(source=ram) covers the key build (one stat) + RAM lookup.
        long perfT0 = PhotoReviewPerf.Log.IsEnabled() ? Stopwatch.GetTimestamp() : 0;
        CancellationToken disposeToken;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Captured in the same critical section as the disposed-check: reading
            // _disposeCts.Token later, unlocked, could race a concurrent Dispose()
            // and throw ObjectDisposedException even though the check above passed.
            disposeToken = _disposeCts.Token;
        }
        string fullPath;
        string key;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
            key = knownStat is null ? BuildKey(fullPath) : BuildKey(fullPath, knownStat.Length, knownStat.LastWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            // A missing/unreadable source (the stat for the key) or a malformed path is a per-image failure: report it through
            // the returned task like every other load error, not as a synchronous throw from a Task-returning method.
            return Task.FromException<IDecodedImage?>(ex);
        }

        if (_ramCache.TryGet(key, out var cached))
        {
            if (perfT0 != 0) PhotoReviewPerf.Log.ThumbEnd(PhotoReviewPerf.NavContext, PhotoReviewPerf.PathId(fullPath), "ram", PhotoReviewPerf.Ms(perfT0));
            return Task.FromResult<IDecodedImage?>(cached);
        }

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<IDecodedImage?>>(
            () => LoadOrCreateAsync(fullPath, key, disposeToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitAndCacheAsync(key, lazy, cancellationToken);
    }

    private static Task<IDecodedImage?> DefaultReadEmbeddedThumbnailAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EmbeddedThumbnailReader.TryRead(path);
        }, cancellationToken);

    public void ClearMemory()
    {
        lock (_lifecycleGate)
        {
            _cacheGeneration++;
            _ramCache.Clear();
        }
    }

    private async Task<IDecodedImage?> AwaitAndCacheAsync(
        string key,
        Lazy<Task<IDecodedImage?>> lazy,
        CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _cacheGeneration);
        var underlyingTask = lazy.Value;
        SharedLoadForTests?.Invoke(underlyingTask);
        _ = underlyingTask.ContinueWith(
            t =>
            {
                // RV-I10: every caller may have stopped waiting (WaitAsync cancelled) before the shared load faulted;
                // observe the fault here so it never surfaces as an UnobservedTaskException once the entry is dropped.
                _ = t.Exception;
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<IDecodedImage?>>>(key, lazy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var image = await underlyingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (image is not null)
        {
            lock (_lifecycleGate)
            {
                if (!_disposed && generation == _cacheGeneration) _ramCache.Set(key, image);
            }
        }
        return image;
    }

    private async Task<IDecodedImage?> LoadOrCreateAsync(string sourcePath, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // D04 perf: runs under the nav of whoever started this shared (Lazy) load; the AsyncLocal is
        // read before the first await, although it would flow across ConfigureAwait(false) too.
        var perf = PhotoReviewPerf.Log.IsEnabled();
        long perfT0 = perf ? Stopwatch.GetTimestamp() : 0;
        var perfNav = perf ? PhotoReviewPerf.NavContext : 0;
        var perfPathId = perf ? PhotoReviewPerf.PathId(sourcePath) : "";
        var cachePath = Path.Combine(_diskDirectory, key + ".png");
        if (File.Exists(cachePath))
        {
            try
            {
                var fromDisk = await DecodeAsync(cachePath, cancellationToken).ConfigureAwait(false);
                _diskStore.NoteAccessed(cachePath);
                if (perf) PhotoReviewPerf.Log.ThumbEnd(perfNav, perfPathId, "disk", PhotoReviewPerf.Ms(perfT0));
                return fromDisk;
            }
            // WPF raises FileFormatException (not just IOException/NotSupportedException) for
            // invalid image bytes, so a corrupt cached PNG must be caught here too or it would
            // escape instead of being deleted and regenerated from the source below.
            // UnauthorizedAccessException is included too: a transiently ACL-blocked cache
            // file must fall back to the source and regenerate, not break loading entirely.
            // Damaged metadata in an otherwise well-formed PNG surfaces as ArgumentException / InvalidOperationException /
            // COMException etc. (DiskCacheStore.IsCacheEntryFailure): also a miss.
            catch (Exception ex) when (DiskCacheStore.IsCacheEntryFailure(ex))
            {
                _log.Error($"Disk thumbnail read failed: {cachePath}", ex);
                DiskCacheStore.TryDelete(cachePath, _log);
            }
        }

        // Perf: on a disk-cache miss, never fall back to a full source decode -- that was the
        // original open-folder slow path (decoding the same JPEG twice, once here and once for
        // the preview, serialized behind each other). Read only the JPEG's embedded EXIF
        // thumbnail (APP1); if the source has none (or isn't a JPEG), return null and let the
        // preview decode that ImagePresenter already started concurrently supply the image.
        var generationBeforeDecode = Volatile.Read(ref _cacheGeneration);
        // A RAW has no EXIF-thumbnail APP1 to read here; opening it through WIC would re-read the file the preview decode already
        // reads by byte ranges (and a codec thumbnail carries the sensor size, not the preview size). The preview is the first frame.
        if (ImageFileTypes.IsRawPath(sourcePath)) return null;
        var embedded = await _embeddedThumbnailReader(sourcePath, cancellationToken).ConfigureAwait(false);
        // Includes a failed disk-cache attempt, if any; excludes persisting the new thumbnail.
        if (perf) PhotoReviewPerf.Log.ThumbEnd(perfNav, perfPathId, embedded is not null ? "embedded" : "none", PhotoReviewPerf.Ms(perfT0));
        if (embedded is null) return null;
        if (!_persistNewThumbnails) return embedded;
        // ClearDisk() bumps _cacheGeneration and wipes the directory; without this check an
        // in-flight decode that started before the clear can still recreate a PNG right after
        // the user asked for the disk cache to be emptied.
        if (Volatile.Read(ref _cacheGeneration) != generationBeforeDecode) return embedded;
        try
        {
            if (embedded.PlatformImage is BitmapSource bmp)
            {
                await (PersistForTests?.Invoke(bmp, cachePath, cancellationToken)
                    ?? _diskStore.WriteAtomicallyAsync(bmp, cachePath, cancellationToken)).ConfigureAwait(false);
            }
            if (Volatile.Read(ref _cacheGeneration) != generationBeforeDecode)
            {
                // Went stale mid-write (ClearDisk ran concurrently): don't leave a
                // freshly-written file for a cache generation that was just cleared.
                DiskCacheStore.TryDelete(cachePath);
            }
            else
            {
                _diskStore.NoteWritten(cachePath);
                PruneDiskCache();
            }
        }
        // RV-I02: the PNG encoder / WIC can also throw InvalidOperationException, NotSupportedException, ArgumentException or
        // COMException (IOException/UnauthorizedAccessException come from the temp file and File.Move). A failed persist must
        // never lose the thumbnail that was already read: log and return it (same policy as PreviewImageService's persist
        // worker). Cancellation still propagates.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"Disk thumbnail write failed: {cachePath}", ex);
        }
        return embedded;
    }

    public void ClearDisk()
    {
        lock (_lifecycleGate) _cacheGeneration++;
        try { _diskStore.ClearDirectory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { _log.Error($"Thumbnail disk cache clear failed: {_diskDirectory}", ex); }
    }

    public Task<bool> WaitForPruneAsync(TimeSpan timeout) => _diskStore.WaitForPruneAsync(timeout);

    // Coalesced per directory in DiskCacheStore: concurrent thumbnail writes (folder scan
    // pre-generating many at once) must not each spawn their own full directory scan.
    private void PruneDiskCache() => _diskStore.SchedulePrune();

    private static Task<IDecodedImage> DecodeAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only ever called with the .png disk-cache path (RV-I11): the source bytes cache never holds these.
            return WpfBitmapImageDecoder.DecodeWithFallback(new DecodeRequest(path, MaxThumbnailWidth, ApplyOrientation: true));
        }, cancellationToken);
    }

    private static string BuildKey(string path)
    {
        var info = new FileInfo(path);
        return BuildKey(path, info.Length, info.LastWriteTimeUtc);
    }

    private static string BuildKey(string path, long length, DateTime lastWriteUtc)
    {
        var stamp = $"{path}|{length}|{lastWriteUtc.Ticks}|{MaxThumbnailWidth}|orient=1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))).ToLowerInvariant();
    }

    private static long EstimateBytes(IDecodedImage image) => image.EstimatedBytes;

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _cacheGeneration++;
            _ramCache.Clear();
        }
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
