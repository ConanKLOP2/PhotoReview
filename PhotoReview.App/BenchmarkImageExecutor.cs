using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

/// Production-compatible image executor for benchmark runs. It uses the same
/// fingerprinted ImageCacheKey and OnLoad/frozen BitmapImage semantics as the viewer.
public sealed class BenchmarkImageExecutor
{
    private readonly BoundedLruCache<ImageCacheKey, BitmapImage> _cache;
    public BenchmarkImageExecutor(long capacityBytes = AppConstants.ImageCacheCapacityBytes)
        => _cache = new BoundedLruCache<ImageCacheKey, BitmapImage>(capacityBytes, ImageBytes);

    public async Task<(BitmapImage Image, bool CacheHit)> DecodeAsync(string path, BenchmarkProfile profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var original = string.Equals(profile.LoadingMode, "Original", StringComparison.OrdinalIgnoreCase);
        var key = ImageCacheKey.Create(path, original, profile.TargetWidth());
        if (_cache.TryGet(key, out var cached)) return (cached, true);
        var image = await Task.Run(() => DecodeOnWorker(key, token), token).ConfigureAwait(false);
        _cache.Set(key, image);
        return (image, false);
    }

    public void Clear() => _cache.Clear();

    private static BitmapImage DecodeOnWorker(ImageCacheKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (!key.IsOriginal && key.TargetWidth > 0) bitmap.DecodePixelWidth = key.TargetWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    private static long ImageBytes(BitmapImage image) => Math.Max(1, image.PixelWidth * (long)image.PixelHeight * 4);
}
