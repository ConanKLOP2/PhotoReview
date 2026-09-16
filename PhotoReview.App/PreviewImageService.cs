using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

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
    private readonly ConcurrentDictionary<(ImageCacheKey Key, long Epoch), Task<BitmapImage>> _previewLoads = new();
    private readonly ConcurrentDictionary<ImageCacheKey, (int Width, int Height)> _originalDimensions = new();
    private readonly object _cacheLifecycleGate = new();
    private long _cacheEpoch;
    private readonly ReviewMetrics _metrics;
    private readonly Func<bool> _isOriginalLoadingMode;
    private readonly Func<int> _targetDecodeWidth;

    public PreviewImageService(
        ReviewMetrics metrics,
        Func<bool> isOriginalLoadingMode,
        Func<int> targetDecodeWidth,
        long capacityBytes = AppConstants.ImageCacheCapacityBytes)
    {
        _metrics = metrics;
        _isOriginalLoadingMode = isOriginalLoadingMode;
        _targetDecodeWidth = targetDecodeWidth;
        _cache = new BoundedLruCache<ImageCacheKey, BitmapImage>(
            capacityBytes, bitmap => Math.Max(1, bitmap.PixelWidth * (long)bitmap.PixelHeight * 4));
    }

    public int CacheCount => _cache.Count;
    public long CacheBytes => _cache.CurrentSize;

    public bool IsOriginalLoadingMode() => _isOriginalLoadingMode();

    public ImageCacheKey GetCurrentCacheKey(string path)
    {
        var isOriginal = IsOriginalLoadingMode();
        return ImageCacheKey.Create(path, isOriginal, isOriginal ? 0 : _targetDecodeWidth());
    }

    public Task<BitmapImage> GetPreviewAsync(string path) => GetPreviewAsync(path, GetCurrentCacheKey(path));

    public async Task<BitmapImage> GetPreviewAsync(string path, ImageCacheKey key)
    {
        // Read WPF layout/DPI only on the UI thread. The decode below runs on a worker thread.
        var targetWidth = key.TargetWidth;
        if (_cache.TryGet(key, out var cached)) { _metrics.RecordCacheHit(); return cached; }
        var cacheEpoch = Volatile.Read(ref _cacheEpoch);
        var loadKey = (key, cacheEpoch);
        if (_previewLoads.TryGetValue(loadKey, out var pending))
        {
            _metrics.RecordInflightJoin();
            return await pending;
        }
        _metrics.RecordCacheMiss();
        var load = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var sourceRead = false;
            var bitmap = new BitmapImage();
            var cachePath = GetDiskCachePath(key);
            if (File.Exists(cachePath))
            {
                try
                {
                    using var cacheStream = File.OpenRead(cachePath);
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = cacheStream; bitmap.EndInit(); bitmap.Freeze();
                    _metrics.RecordDiskCacheHit();
                }
                catch (Exception) when (File.Exists(cachePath))
                {
                    try { File.Delete(cachePath); } catch { }
                    sourceRead = true;
                    bitmap = DecodeWithFallback(path, targetWidth);
                }
            }
            else
            {
                sourceRead = true;
                bitmap = DecodeWithFallback(path, targetWidth);
            }
            // A path can be replaced while decode is in flight. Never publish
            // the old pixels under the new source's identity.
            if (!key.MatchesCurrentSource()) throw new IOException($"Image source changed during decode: {path}");
            lock (_cacheLifecycleGate)
                if (cacheEpoch == _cacheEpoch) _cache.Set(key, bitmap);
            // Only cache downscaled previews to disk: PNG-encoding a full-resolution
            // Original-mode decode is slower than just re-decoding the source JPEG,
            // so it would cost more than it saves.
            if (sourceRead && targetWidth > 0) PersistToDiskCache(bitmap, cachePath);
            stopwatch.Stop();
            if (sourceRead) try { _metrics.RecordSourceRead(new FileInfo(path).Length, stopwatch.ElapsedMilliseconds); } catch { }
            return bitmap;
        });
        _previewLoads[loadKey] = load;
        try { return await load; }
        finally { _previewLoads.TryRemove(loadKey, out _); }
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
        // Allow an in-flight decode to coexist with Move/Delete. The action path
        // cancels future work and invalidates its result; Windows can still
        // complete the file operation without waiting for this read handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0) bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    public static BitmapImage DecodeWithFallback(string path, int targetWidth)
    {
        try { return DecodeSource(path, targetWidth); }
        catch when (targetWidth > 0) { return DecodeSource(path, 0); }
    }

    private static string GetDiskCachePath(ImageCacheKey key)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key.Path}|{key.Length}|{key.LastWriteUtcTicks}|{key.IsOriginal}|{key.TargetWidth}")));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "cache", hash + ".png");
    }

    /// <summary>Fire-and-forget: write the decoded preview to disk and prune the cache directory to quota.</summary>
    private static void PersistToDiskCache(BitmapImage bitmap, string cachePath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DiskCacheStore.WriteAtomicallyAsync(bitmap, cachePath).ConfigureAwait(false);
                DiskCacheStore.PruneDirectory(Path.GetDirectoryName(cachePath)!, "*.png",
                    AppConstants.PreviewDiskCacheCapacityBytes, "Preview disk cache delete failed");
            }
            catch (IOException ex) { AppLog.Error($"Preview disk cache write failed: {cachePath}", ex); }
            catch (UnauthorizedAccessException ex) { AppLog.Error($"Preview disk cache write failed: {cachePath}", ex); }
        });
    }
}
