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

    private readonly string _diskDirectory;
    private readonly long _maxRamBytes;
    private readonly BoundedLruCache<string, BitmapSource> _ramCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();

    public ThumbnailCache(string? diskDirectory = null, long maxRamBytes = 256L * 1024 * 1024)
    {
        if (maxRamBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRamBytes));
        _diskDirectory = diskDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoReview", "thumbnails");
        _maxRamBytes = maxRamBytes;
        _ramCache = new BoundedLruCache<string, BitmapSource>(_maxRamBytes, EstimateBytes);
    }

    public Task<BitmapSource> GetAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var key = BuildKey(fullPath);

        if (_ramCache.TryGet(key, out var cached)) return Task.FromResult(cached);

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<BitmapSource>>(
            () => LoadOrCreateAsync(fullPath, key, _disposeCts.Token),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitAndCacheAsync(key, lazy, cancellationToken);
    }

    public void ClearMemory() => _ramCache.Clear();

    private async Task<BitmapSource> AwaitAndCacheAsync(
        string key,
        Lazy<Task<BitmapSource>> lazy,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ramCache.Set(key, image);
            return image;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource>>>(key, lazy));
        }
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
        try { await WriteAtomicallyAsync(image, cachePath, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { /* The RAM result remains usable when disk cache is unavailable. */ }
        catch (UnauthorizedAccessException) { /* Same fallback for read-only locations. */ }
        return image;
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _ramCache.Clear();
    }
}
