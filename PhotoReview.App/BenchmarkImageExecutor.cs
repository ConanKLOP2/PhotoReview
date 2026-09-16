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

    // The raw decode is shared with the viewer through PreviewImageService.DecodeSource,
    // so there is exactly one BitmapImage decode implementation in the app.
    //
    // The benchmark deliberately does NOT reuse PreviewImageService.GetPreviewAsync itself:
    // that path adds the disk-cache probe, ReviewMetrics recording and the cache-epoch
    // lifecycle, all of which would change what a benchmark run measures, and it has no
    // CancellationToken in its decode loop.  Merging them would require reworking the
    // service's threading/metrics contract, which is out of scope for this refactor.
    private static BitmapImage DecodeOnWorker(ImageCacheKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bitmap = PreviewImageService.DecodeSource(key.Path, key.IsOriginal ? 0 : key.TargetWidth);
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    private static long ImageBytes(BitmapImage image) => Math.Max(1, image.PixelWidth * (long)image.PixelHeight * 4);
}
