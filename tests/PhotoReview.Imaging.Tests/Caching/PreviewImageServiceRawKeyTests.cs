using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// RAW sources carry a distinct <see cref="ImageCacheKey.SourceKind"/>: the original-dimensions cache must be written and
/// read under the same key kind, and the on-disk preview identity must include the kind for non-standard sources.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceRawKeyTests : IDisposable
{
    private readonly TempRoot _root = new("raw-key");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task GetOriginalDimensionsAsync_ForARawPathAlreadyDecoded_ReusesTheSeededDimensionsWithoutReadInfo()
    {
        var raw = _root.File("shot.cr2", TestImages.OpaquePng); // a RAW extension over decodable bytes
        var decoder = new CountingDecoder(new PhotoReview.Imaging.Decoding.WpfBitmapImageDecoder());
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, decoder: decoder, disableDiskCacheOverride: true);
        try
        {
            await service.GetPreviewAsync(raw);

            var dimensions = await service.GetOriginalDimensionsAsync(raw, service.GetCurrentCacheKey(raw));

            Assert.True(dimensions.Width > 0);
            Assert.Equal(0, decoder.ReadInfoCalls);
        }
        finally { await service.ShutdownPersistWorkersAsync(); }
    }

    [Fact]
    public async Task DiskCacheIdentity_DistinguishesTheRawPreviewKindFromAStandardKeyOfTheSamePath()
    {
        var raw = _root.File("kind.cr2", TestImages.OpaquePng); // opaque: alpha previews are never persisted
        var dir = _root.Dir("disk");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir);
        try
        {
            var rawKey = service.GetCurrentCacheKey(raw);
            Assert.Equal(ImageSourceKind.RawPreview, rawKey.SourceKind);
            await service.GetPreviewAsync(raw, rawKey);
            await service.ShutdownPersistWorkersAsync();
            Assert.True(service.HasDiskCachedPreview(rawKey));

            var standardKey = ImageCacheKey.Create(raw, isOriginal: false, rawKey.TargetBox, rawKey.OrientationApplied, rawKey.Backend, ImageSourceKind.Standard);

            Assert.False(service.HasDiskCachedPreview(standardKey));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DiskCacheIdentity_OfAStandardKey_IsUnchangedByTheSourceKindField()
    {
        var jpg = _root.File("plain.png", TestImages.OpaquePng);
        var dir = _root.Dir("disk-standard");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 32, diskCacheDirectory: dir);
        try
        {
            var key = service.GetCurrentCacheKey(jpg);
            await service.GetPreviewAsync(jpg, key);
            await service.ShutdownPersistWorkersAsync();

            var entry = Assert.Single(Directory.GetFiles(dir, "*.pv4"));
            // Standard entries keep the pre-kind hash, so existing user caches are not orphaned.
            var identity = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"preview-v5-box|{key.Path}|{key.Length}|{key.LastWriteUtcTicks}|{key.IsOriginal}|{key.TargetWidth}x{key.TargetHeight}|{key.OrientationApplied}|{key.Backend}");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))) + ".pv4";
            Assert.Equal(expected, Path.GetFileName(entry));
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
            await service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class CountingDecoder(IImageDecoder inner) : IImageDecoder
    {
        private int _readInfoCalls;
        public int ReadInfoCalls => Volatile.Read(ref _readInfoCalls);
        public IDecodedImage Decode(DecodeRequest request) => inner.Decode(request);
        public ImageInfo ReadInfo(string path)
        {
            Interlocked.Increment(ref _readInfoCalls);
            return inner.ReadInfo(path);
        }
    }
}
