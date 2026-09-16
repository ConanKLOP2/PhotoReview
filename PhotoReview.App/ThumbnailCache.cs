using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

/// <summary>
/// Thread-safe thumbnail cache for fast folder review. The source image is never modified.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    public const int MaxThumbnailWidth = 800;
    public const long DefaultMaxDiskBytes = 1L * 1024 * 1024 * 1024;

    private readonly string _diskDirectory;
    private readonly long _maxRamBytes;
    private readonly long _maxDiskBytes;
    private readonly bool _persistNewThumbnails;
    private readonly BoundedLruCache<string, BitmapSource> _ramCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _lifecycleGate = new();
    private long _cacheGeneration;
    private bool _disposed;

    public ThumbnailCache(string? diskDirectory = null, long maxRamBytes = 256L * 1024 * 1024, long maxDiskBytes = DefaultMaxDiskBytes, bool persistNewThumbnails = true)
    {
        if (maxRamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRamBytes));
        if (maxDiskBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDiskBytes));
        _diskDirectory = diskDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoReview", "thumbnails");
        _maxRamBytes = maxRamBytes;
        _maxDiskBytes = maxDiskBytes;
        _persistNewThumbnails = persistNewThumbnails;
        _ramCache = new BoundedLruCache<string, BitmapSource>(_maxRamBytes, EstimateBytes);
    }

    public Task<BitmapSource> GetAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
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

        if (_ramCache.TryGet(key, out var cached)) return Task.FromResult(cached);

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<BitmapSource>>(
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

    private async Task<BitmapSource> AwaitAndCacheAsync(
        string key,
        Lazy<Task<BitmapSource>> lazy,
        CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _cacheGeneration);
        var underlyingTask = lazy.Value;
        _ = underlyingTask.ContinueWith(
            _ => _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource>>>(key, lazy)),
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

    private async Task<BitmapSource> LoadOrCreateAsync(string sourcePath, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cachePath = Path.Combine(_diskDirectory, key + ".png");
        if (File.Exists(cachePath))
        {
            try { return await DecodeAsync(cachePath, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { TryDelete(cachePath); }
            catch (NotSupportedException) { TryDelete(cachePath); }
            catch (InvalidDataException) { TryDelete(cachePath); }
        }

        var image = await DecodeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!_persistNewThumbnails) return image;
        try { await WriteAtomicallyAsync(image, cachePath, cancellationToken).ConfigureAwait(false); _ = Task.Run(() => PruneDiskCache()); }
        catch (IOException) { /* The RAM result remains usable when disk cache is unavailable. */ }
        catch (UnauthorizedAccessException) { /* Same fallback for read-only locations. */ }
        return image;
    }

    public void ClearDisk()
    {
        lock (_lifecycleGate) _cacheGeneration++;
        if (!Directory.Exists(_diskDirectory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_diskDirectory, "*.png")) TryDelete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void PruneDiskCache()
    {
        if (!Directory.Exists(_diskDirectory)) return;
        try
        {
            var files = Directory.EnumerateFiles(_diskDirectory, "*.png")
                .Select(path => new FileInfo(path)).Where(info => info.Exists)
                .OrderBy(info => info.LastAccessTimeUtc).ThenBy(info => info.CreationTimeUtc).ToList();
            var total = files.Sum(info => info.Length);
            foreach (var info in files)
            {
                if (total <= _maxDiskBytes) break;
                if (TryDelete(info.FullName)) total -= info.Length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Task<BitmapSource> DecodeAsync(string path, CancellationToken cancellationToken)
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
            return (BitmapSource)bitmap;
        }, cancellationToken);
    }

    private static async Task WriteAtomicallyAsync(BitmapSource image, string cachePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally { TryDelete(temporaryPath); }
    }

    private static string BuildKey(string path)
    {
        var info = new FileInfo(path);
        var stamp = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{MaxThumbnailWidth}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))).ToLowerInvariant();
    }

    private static long EstimateBytes(BitmapSource image) => (long)image.PixelWidth * image.PixelHeight * 4;

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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
