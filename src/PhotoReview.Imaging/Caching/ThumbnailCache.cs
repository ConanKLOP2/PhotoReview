using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Diagnostics;
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
    private readonly DiskCacheStore _diskStore;
    private readonly BoundedLruCache<string, IDecodedImage> _ramCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<IDecodedImage>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lifecycleGate = new();
    private long _cacheGeneration;
    private bool _disposed;

    public DiskCacheStore DiskStore => _diskStore;
    public string DiskDirectory => _diskDirectory;

    public ThumbnailCache(
        string? diskDirectory = null,
        long maxRamBytes = 256L * 1024 * 1024,
        long maxDiskBytes = DefaultMaxDiskBytes,
        bool persistNewThumbnails = true,
        ILog? log = null)
    {
        if (maxRamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRamBytes));
        if (maxDiskBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDiskBytes));
        _diskDirectory = diskDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoReview", "thumbnails");
        _maxRamBytes = maxRamBytes;
        _maxDiskBytes = maxDiskBytes;
        _persistNewThumbnails = persistNewThumbnails;
        _log = log ?? NullLog.Instance;
        _diskStore = new DiskCacheStore(_diskDirectory, "*.png", _maxDiskBytes, _log);
        _ramCache = new BoundedLruCache<string, IDecodedImage>(_maxRamBytes, EstimateBytes, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IDecodedImage> GetAsync(string sourcePath, CancellationToken cancellationToken = default)
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
        var fullPath = Path.GetFullPath(sourcePath);
        var key = BuildKey(fullPath);

        if (_ramCache.TryGet(key, out var cached))
        {
            if (perfT0 != 0) PhotoReviewPerf.Log.ThumbEnd(PhotoReviewPerf.NavContext, PhotoReviewPerf.PathId(fullPath), "ram", PhotoReviewPerf.Ms(perfT0));
            return Task.FromResult(cached);
        }

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<IDecodedImage>>(
            () => LoadOrCreateAsync(fullPath, key, disposeToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitAndCacheAsync(key, lazy, cancellationToken);
    }

    public void ClearMemory()
    {
        lock (_lifecycleGate)
        {
            _cacheGeneration++;
            _ramCache.Clear();
        }
    }

    private async Task<IDecodedImage> AwaitAndCacheAsync(
        string key,
        Lazy<Task<IDecodedImage>> lazy,
        CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _cacheGeneration);
        var underlyingTask = lazy.Value;
        _ = underlyingTask.ContinueWith(
            _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<IDecodedImage>>>(key, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var image = await underlyingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lifecycleGate)
        {
            if (!_disposed && generation == _cacheGeneration) _ramCache.Set(key, image);
        }
        return image;
    }

    private async Task<IDecodedImage> LoadOrCreateAsync(string sourcePath, string key, CancellationToken cancellationToken)
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
                if (perf) PhotoReviewPerf.Log.ThumbEnd(perfNav, perfPathId, "disk", PhotoReviewPerf.Ms(perfT0));
                return fromDisk;
            }
            // WPF raises FileFormatException (not just IOException/NotSupportedException) for
            // invalid image bytes, so a corrupt cached PNG must be caught here too or it would
            // escape instead of being deleted and regenerated from the source below.
            // UnauthorizedAccessException is included too: a transiently ACL-blocked cache
            // file must fall back to the source and regenerate, not break loading entirely.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or InvalidDataException or FileFormatException)
            {
                _log.Error($"Disk thumbnail read failed: {cachePath}", ex);
                DiskCacheStore.TryDelete(cachePath, "Thumbnail delete failed");
            }
        }

        var generationBeforeDecode = Volatile.Read(ref _cacheGeneration);
        var image = await DecodeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        // Includes a failed disk-cache attempt, if any; excludes persisting the new thumbnail.
        if (perf) PhotoReviewPerf.Log.ThumbEnd(perfNav, perfPathId, "decode", PhotoReviewPerf.Ms(perfT0));
        if (!_persistNewThumbnails) return image;
        // ClearDisk() bumps _cacheGeneration and wipes the directory; without this check an
        // in-flight decode that started before the clear can still recreate a PNG right after
        // the user asked for the disk cache to be emptied.
        if (Volatile.Read(ref _cacheGeneration) != generationBeforeDecode) return image;
        try
        {
            if (image.PlatformImage is BitmapSource bmp)
            {
                await _diskStore.WriteAtomicallyAsync(bmp, cachePath, cancellationToken).ConfigureAwait(false);
            }
            if (Volatile.Read(ref _cacheGeneration) != generationBeforeDecode)
            {
                // Went stale mid-write (ClearDisk ran concurrently): don't leave a
                // freshly-written file for a cache generation that was just cleared.
                DiskCacheStore.TryDelete(cachePath);
            }
            else PruneDiskCache();
        }
        catch (IOException ex) { _log.Error($"Disk thumbnail write failed: {cachePath}", ex); }
        catch (UnauthorizedAccessException ex) { _log.Error($"Disk thumbnail write failed: {cachePath}", ex); }
        return image;
    }

    public void ClearDisk()
    {
        lock (_lifecycleGate) _cacheGeneration++;
        try { _diskStore.ClearDirectory(); }
        catch (IOException ex) { _log.Error($"Thumbnail disk cache clear failed: {_diskDirectory}", ex); }
        catch (UnauthorizedAccessException ex) { _log.Error($"Thumbnail disk cache clear failed: {_diskDirectory}", ex); }
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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = MaxThumbnailWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return (IDecodedImage)new WpfDecodedImage(bitmap, downscaled: true);
        }, cancellationToken);
    }

    private static string BuildKey(string path)
    {
        var info = new FileInfo(path);
        var stamp = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{MaxThumbnailWidth}";
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
